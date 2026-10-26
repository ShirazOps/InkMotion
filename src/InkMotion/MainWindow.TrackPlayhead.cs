using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace InkMotion;

public sealed partial class MainWindow
{
    private CanvasControl? _trackRulerDrag;
    private uint? _trackRulerPointerId;
    private double _audioTimelineSeconds = 1;

    private void AudioTrackArea_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutAudioClips();

    private void LayoutAudioClips()
    {
        if (AudioTrackArea is null || AudioTracksPanel is null)
        {
            return;
        }

        _audioTimelineSeconds = Math.Max(1, GetPlaybackEndSeconds());
        double width = AudioTrackArea.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        foreach (Button lane in AudioTracksPanel.Children.OfType<Button>())
        {
            if (lane.Tag is not int index || index < 0 || index >= _audioTracks.Count)
            {
                continue;
            }

            AudioTrackRuntime track = _audioTracks[index];
            double start = AudioClipEditing.GetClipStartSeconds(track.Project, _frameRate);
            double duration = AudioClipEditing.GetTimelineDuration(track.Project, track.SourceDurationSeconds);
            double left = Math.Clamp(start / _audioTimelineSeconds, 0, 1) * width;
            double right = double.IsFinite(duration)
                ? Math.Clamp((start + duration) / _audioTimelineSeconds, 0, 1) * width
                : width;
            lane.Margin = new Thickness(left, 0, 0, 0);
            lane.Width = Math.Max(0, right - left);
        }

        AudioTimelineRuler.Invalidate();
        AudioPlayheadSurface.Invalidate();
    }

    private void UpdateTrackPlayheads()
    {
        if (AudioPlayheadSurface is null || InkPlayheadSurface is null)
        {
            return;
        }

        LayoutAudioClips();
        AudioPlayheadSurface.Invalidate();
        InkPlayheadSurface.Invalidate();
        InkTimelineRuler.Invalidate();
        if (_isPlaying)
        {
            FollowTrackPlayhead();
        }
    }

    private void FollowTrackPlayhead()
    {
        ScrollViewer? scroller = _workspace switch
        {
            "Ink" => InkTimelineScroller,
            "Video" or "Export" => VideoTimelineScroller,
            _ => null,
        };
        if (scroller is null || scroller.ViewportWidth <= 0)
        {
            return;
        }

        double x = _workspace == "Ink" ? InkTimeline.GetPlayheadX(_frames, _playheadFramePosition)
            : _playheadFramePosition / _frameRate * _timelinePixelsPerSecond;
        double offset = scroller.HorizontalOffset;
        double viewport = scroller.ViewportWidth;
        if (x < offset || x > offset + viewport - 24)
        {
            scroller.ChangeView(Math.Max(0, x - viewport * 0.1), null, null, true);
        }
    }

    private static void DrawTrackPlayhead(CanvasDrawingSession session, double x, double width, double height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        float position = (float)Math.Clamp(x, 1, Math.Max(1, width - 1));
        var colour = Windows.UI.Color.FromArgb(255, 255, 77, 79);
        session.DrawLine(position, 0, position, (float)height, colour, 2);
        session.FillRectangle(position - 4, 0, 8, 7, colour);
    }

    private void AudioPlayheadSurface_Draw(CanvasControl sender, CanvasDrawEventArgs args) =>
        DrawTrackPlayhead(args.DrawingSession,
            _playheadFramePosition / _frameRate / _audioTimelineSeconds * sender.ActualWidth,
            sender.ActualWidth, sender.ActualHeight);

    private void AudioTimelineRuler_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (sender.ActualWidth <= 0)
        {
            return;
        }

        _timelineRulerFormat ??= new CanvasTextFormat { FontSize = 10, WordWrapping = CanvasWordWrapping.NoWrap };
        var session = args.DrawingSession;
        session.Clear(Windows.UI.Color.FromArgb(255, 31, 35, 43));
        double pixelsPerSecond = sender.ActualWidth / _audioTimelineSeconds;
        double step = GetRulerStep(pixelsPerSecond);
        for (double time = 0; time <= _audioTimelineSeconds; time += step)
        {
            float x = (float)(time * pixelsPerSecond);
            session.DrawLine(x, 16, x, 24, Colors.Gray);
            session.DrawText(FormatTimelineTime(time, step < 1), x + 3, 0, Colors.LightGray, _timelineRulerFormat);
        }
    }

    private void InkTimelineRuler_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        _timelineRulerFormat ??= new CanvasTextFormat { FontSize = 10, WordWrapping = CanvasWordWrapping.NoWrap };
        var session = args.DrawingSession;
        session.Clear(Windows.UI.Color.FromArgb(255, 31, 35, 43));
        double x = 0;
        int start = 0;
        foreach (EditorFrame frame in _frames)
        {
            double width = InkTimeline.GetCelWidth(frame);
            session.DrawText($"F{start + 1}", (float)x + 3, 0, Colors.LightGray, _timelineRulerFormat);
            session.DrawLine((float)x, 16, (float)x, 24, Colors.Gray);
            if (width / frame.ExposureFrames >= 4)
            {
                for (int offset = 1; offset < frame.ExposureFrames; offset++)
                {
                    float tick = (float)(x + offset / (double)frame.ExposureFrames * width);
                    session.DrawLine(tick, 20, tick, 24, Colors.DimGray);
                }
            }

            x += width + InkTimeline.CelSpacing;
            start += frame.ExposureFrames;
        }
    }

    private void TrackRuler_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not CanvasControl canvas || canvas.ActualWidth <= 0 ||
            (!e.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed &&
             e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Touch))
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        SeekTrackRuler(canvas, e.GetCurrentPoint(canvas).Position.X);
        if (canvas.CapturePointer(e.Pointer))
        {
            _trackRulerDrag = canvas;
            _trackRulerPointerId = e.Pointer.PointerId;
        }
        e.Handled = true;
    }

    private void TrackRuler_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (sender is CanvasControl canvas && canvas == _trackRulerDrag && e.Pointer.PointerId == _trackRulerPointerId)
        {
            SeekTrackRuler(canvas, e.GetCurrentPoint(canvas).Position.X);
            e.Handled = true;
        }
    }

    private void TrackRuler_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is CanvasControl canvas && canvas == _trackRulerDrag && e.Pointer.PointerId == _trackRulerPointerId)
        {
            SeekTrackRuler(canvas, e.GetCurrentPoint(canvas).Position.X);
            _trackRulerDrag = null;
            _trackRulerPointerId = null;
            canvas.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }

    private void TrackRuler_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (ReferenceEquals(sender, _trackRulerDrag) && e.Pointer.PointerId == _trackRulerPointerId)
        {
            _trackRulerDrag = null;
            _trackRulerPointerId = null;
        }
    }

    private void SeekTrackRuler(CanvasControl canvas, double x)
    {
        double seconds = canvas == InkTimelineRuler
            ? InkTimeline.GetFramePositionAtX(_frames, x) / _frameRate
            : Math.Clamp(x / canvas.ActualWidth, 0, 1) * _audioTimelineSeconds;
        MoveTimelinePlayhead(seconds);
    }
}
