using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Graphics.Canvas.UI.Xaml;

namespace InkMotion;

public sealed partial class MainWindow
{
    private uint? _inkTrackPointerId;

    private void LayoutAudioClips()
    {
        if (AudioTracksPanel is null)
        {
            return;
        }

        foreach (Grid lane in AudioTracksPanel.Children.OfType<Grid>())
        {
            if (lane.Tag is not int index || index < 0 || index >= _audioTracks.Count ||
                lane.Children.OfType<Button>().FirstOrDefault() is not { } clip)
            {
                continue;
            }

            AudioTrackRuntime track = _audioTracks[index];
            double left = AudioClipEditing.GetClipStartSeconds(track.Project, _frameRate) * _timelinePixelsPerSecond;
            double duration = AudioClipEditing.GetTimelineDuration(track.Project, track.SourceDurationSeconds);
            double width = double.IsFinite(duration)
                ? Math.Max(4, duration * _timelinePixelsPerSecond)
                : Math.Max(4, TrackLayoutContent.Width - left);
            clip.Margin = new Thickness(left, 0, 0, 0);
            clip.Width = width;
        }
    }

    private void UpdateTrackPlayheads()
    {
        if (TrackPlayheadLine is null)
        {
            return;
        }

        LayoutAudioClips();
        TrackPlayheadLine.Margin = new Thickness(
            Math.Max(0, _playheadFramePosition / _frameRate * _timelinePixelsPerSecond - 1),
            TimelineRulerHeight,
            0,
            0);
        InkTrackCanvas?.Invalidate();
        if (_isPlaying)
        {
            FollowTrackPlayhead();
        }
    }

    private void FollowTrackPlayhead()
    {
        ScrollViewer scroller = VideoTimelineScroller;
        if (scroller.ViewportWidth <= 0)
        {
            return;
        }

        double x = _playheadFramePosition / _frameRate * _timelinePixelsPerSecond;
        double offset = scroller.HorizontalOffset;
        double viewport = scroller.ViewportWidth;
        if (x < offset || x > offset + viewport - 24)
        {
            scroller.ChangeView(Math.Max(0, x - viewport * 0.1), null, null, true);
        }
    }

    private void InkTrackCanvas_RegionsInvalidated(CanvasVirtualControl sender, CanvasRegionsInvalidatedEventArgs args)
    {
        _timelineLabelFormat ??= new Microsoft.Graphics.Canvas.Text.CanvasTextFormat
        {
            FontSize = 11,
            WordWrapping = Microsoft.Graphics.Canvas.Text.CanvasWordWrapping.NoWrap,
            VerticalAlignment = Microsoft.Graphics.Canvas.Text.CanvasVerticalAlignment.Center,
            TrimmingGranularity = Microsoft.Graphics.Canvas.Text.CanvasTextTrimmingGranularity.Character,
            Options = Microsoft.Graphics.Canvas.Text.CanvasDrawTextOptions.Clip,
        };

        double pixelsPerFrame = _timelinePixelsPerSecond / _frameRate;
        foreach (Windows.Foundation.Rect region in args.InvalidatedRegions)
        {
            using var session = sender.CreateDrawingSession(region);
            double lastLabelEnd = region.Left;
            for (int celIndex = 0; celIndex < _frames.Count; celIndex++)
            {
                EditorFrame frame = _frames[celIndex];
                double left = InkTimeline.GetCelLeft(_frames, celIndex, pixelsPerFrame);
                double width = InkTimeline.GetCelWidth(frame, pixelsPerFrame);
                if (left + width < region.Left || left > region.Right)
                {
                    continue;
                }

                bool selected = celIndex == _selectedFrameIndex;
                Windows.UI.Color fill = selected
                    ? Windows.UI.Color.FromArgb(255, 0xD9, 0x73, 0x1A)
                    : frame.IsKeyframe
                        ? Windows.UI.Color.FromArgb(255, 0x4F, 0x62, 0x83)
                        : Windows.UI.Color.FromArgb(255, 0x3A, 0x42, 0x52);
                session.FillRectangle((float)left, 2, (float)Math.Max(1, width - 1), 30, fill);
                if (selected)
                {
                    session.DrawRectangle((float)left, 2, (float)Math.Max(1, width - 1), 30,
                        Windows.UI.Color.FromArgb(255, 255, 255, 255), 1);
                }

                string prefix = frame.DrawingLinkId.Length > 0 ? "L " : frame.IsKeyframe ? "K " :
                    _interpolatedFrameIndices.Contains(celIndex) ? "~" : "";
                string label = $"{prefix}{frame.Number:000}";
                double labelWidth = Math.Min(width - 6, 40);
                if (labelWidth >= 16 && (selected || left >= lastLabelEnd))
                {
                    session.DrawText(label, new Windows.Foundation.Rect(left + 3, 2, labelWidth, 30),
                        Windows.UI.Color.FromArgb(255, 236, 239, 244), _timelineLabelFormat);
                    lastLabelEnd = left + labelWidth + 4;
                }
            }
        }
    }

    private void InkTrackCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not CanvasVirtualControl canvas ||
            (!e.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed &&
             e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Touch))
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        SelectTimelineFrame(InkTimeline.GetFramePositionAtX(
            _frames, e.GetCurrentPoint(canvas).Position.X, _timelinePixelsPerSecond / _frameRate));
        if (canvas.CapturePointer(e.Pointer))
        {
            _inkTrackPointerId = e.Pointer.PointerId;
        }
        e.Handled = true;
    }

    private void InkTrackCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (sender is CanvasVirtualControl canvas && e.Pointer.PointerId == _inkTrackPointerId)
        {
            SelectTimelineFrame(InkTimeline.GetFramePositionAtX(
                _frames, e.GetCurrentPoint(canvas).Position.X, _timelinePixelsPerSecond / _frameRate));
            e.Handled = true;
        }
    }

    private void InkTrackCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is CanvasVirtualControl canvas && e.Pointer.PointerId == _inkTrackPointerId)
        {
            _inkTrackPointerId = null;
            canvas.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }

    private void InkTrackCanvas_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId == _inkTrackPointerId)
        {
            _inkTrackPointerId = null;
        }
    }
}
