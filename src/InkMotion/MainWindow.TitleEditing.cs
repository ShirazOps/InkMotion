using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.UI;

namespace InkMotion;

public sealed partial class MainWindow
{
    private TitleGesture? _titleGesture;
    private bool _titleDialogOpen;
    private double TitlePreviewSeconds => _playheadFramePosition / _frameRate;

    private sealed record TitleGesture(
        TimelineClipLocation Location, VideoTransformValue Original, Vector2 Start,
        string Operation, Vector2 Centre, float Radius, double Angle, uint PointerId)
    {
        public bool Started { get; set; }
    }

    private IReadOnlyList<TimelineClipLocation> VisibleViewerTitles() =>
        VideoTimelineEditing.GetVisibleLayers(_videoTracks, TitlePreviewSeconds, _frameRate, _videoMulticamGroups)
            .Where(location => location.Track.Kind == TimelineTrackKind.Text &&
                VideoTimelineEditing.GetOpacity(location, TitlePreviewSeconds, _frameRate) > 0 &&
                !string.IsNullOrWhiteSpace(location.Clip.Text) &&
                location.Clip.FontSize * VideoTransformKeyframes.GetValueAtTime(
                    location.Clip, TitlePreviewSeconds, _frameRate).Scale * TitleAdornerSurface.ActualHeight /
                    TimelineCompositor.TitleReferenceHeight >= 1).ToArray();

    private Rect ViewerTitleBounds(TimelineClipLocation location, VideoTransformValue value) =>
        TimelineCompositor.GetTitleBounds(TitleAdornerSurface, location.Clip,
            (float)TitleAdornerSurface.ActualWidth, (float)TitleAdornerSurface.ActualHeight, value);

    private Vector2 ViewerTitleCentre(VideoTransformValue value) => new(
        (float)(value.X * TitleAdornerSurface.ActualWidth), (float)(value.Y * TitleAdornerSurface.ActualHeight));

    private static Vector2 UnrotateTitlePoint(Vector2 point, Vector2 centre, double degrees) =>
        Vector2.Transform(point, Matrix3x2.CreateRotation((float)(-degrees * Math.PI / 180), centre));

    private static Vector2[] TitleCorners(Rect bounds) =>
    [
        new((float)bounds.Left, (float)bounds.Top), new((float)bounds.Right, (float)bounds.Top),
        new((float)bounds.Right, (float)bounds.Bottom), new((float)bounds.Left, (float)bounds.Bottom),
    ];

    private TimelineClipLocation? HitViewerTitle(Vector2 point)
    {
        foreach (TimelineClipLocation location in VisibleViewerTitles().Reverse())
        {
            VideoTransformValue value = VideoTransformKeyframes.GetValueAtTime(
                location.Clip, TitlePreviewSeconds, _frameRate);
            Vector2 local = UnrotateTitlePoint(point, ViewerTitleCentre(value), value.RotationDegrees);
            if (ViewerTitleBounds(location, value).Contains(new Point(local.X, local.Y)))
            {
                return location;
            }
        }
        return null;
    }

    private void TitleAdornerSurface_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_workspace != "Video" || IsOriginalPreview || _isPlaying ||
            sender.ActualWidth <= 0 || sender.ActualHeight <= 0 ||
            SelectedTimelineClip is not { Track.Kind: TimelineTrackKind.Text } selected ||
            !VisibleViewerTitles().Any(location => location.Clip.Id == selected.Clip.Id))
        {
            return;
        }
        VideoTransformValue value = VideoTransformKeyframes.GetValueAtTime(selected.Clip, TitlePreviewSeconds, _frameRate);
        Rect bounds = ViewerTitleBounds(selected, value);
        CanvasDrawingSession session = args.DrawingSession;
        session.Transform = Matrix3x2.CreateRotation(
            (float)(value.RotationDegrees * Math.PI / 180), ViewerTitleCentre(value));
        Color colour = Color.FromArgb(255, 90, 180, 255);
        session.DrawRectangle(bounds, colour, 1.5f);
        if (!selected.Track.IsLocked)
        {
            foreach (Vector2 corner in TitleCorners(bounds))
            {
                session.FillRectangle(corner.X - 5, corner.Y - 5, 10, 10, Microsoft.UI.Colors.White);
                session.DrawRectangle(corner.X - 5, corner.Y - 5, 10, 10, colour, 1);
            }
            float mid = (float)(bounds.Left + bounds.Width / 2);
            session.DrawLine(mid, (float)bounds.Top, mid, (float)bounds.Top - 28, colour, 1.5f);
            session.FillCircle(mid, (float)bounds.Top - 28, 7, colour);
        }
    }

    private void TitleAdornerSurface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_workspace != "Video" || IsOriginalPreview || _isPlaying || _titleGesture is not null)
        {
            return;
        }
        var point = e.GetCurrentPoint(TitleAdornerSurface);
        if (!point.Properties.IsLeftButtonPressed &&
            e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Touch)
        {
            return;
        }

        Vector2 position = new((float)point.Position.X, (float)point.Position.Y);
        TimelineClipLocation? target = null;
        string operation = "move";
        if (SelectedTimelineClip is { Track.Kind: TimelineTrackKind.Text } selected &&
            VisibleViewerTitles().Any(location => location.Clip.Id == selected.Clip.Id))
        {
            VideoTransformValue selectedValue = VideoTransformKeyframes.GetValueAtTime(selected.Clip, TitlePreviewSeconds, _frameRate);
            Rect bounds = ViewerTitleBounds(selected, selectedValue);
            Vector2 local = UnrotateTitlePoint(position, ViewerTitleCentre(selectedValue), selectedValue.RotationDegrees);
            Vector2 rotation = new((float)(bounds.Left + bounds.Width / 2), (float)bounds.Top - 28);
            if (Vector2.Distance(local, rotation) <= 15)
            {
                target = selected;
                operation = "rotate";
            }
            else if (TitleCorners(bounds).Any(corner => Vector2.Distance(local, corner) <= 15))
            {
                target = selected;
                operation = "resize";
            }
        }
        target ??= HitViewerTitle(position);
        if (target is null)
        {
            return;
        }
        PausePlayback();
        FinishActiveEdit();
        _selectedTimelineTrackId = target.Track.Id;
        _selectedTimelineClipId = target.Clip.Id;
        RefreshVideoTrackHeaders();
        UpdateVideoInspector();
        TitleAdornerSurface.Invalidate();
        if (target.Track.IsLocked)
        {
            StatusText.Text = "Unlock the title track before editing its text or transform.";
            return;
        }
        VideoTransformValue original = VideoTransformKeyframes.GetValueAtTime(target.Clip, TitlePreviewSeconds, _frameRate);
        Vector2 centre = ViewerTitleCentre(original);
        _titleGesture = new(target, original, position, operation, centre,
            Math.Max(1, Vector2.Distance(position, centre)), Math.Atan2(position.Y - centre.Y, position.X - centre.X),
            e.Pointer.PointerId);
        TitleAdornerSurface.CapturePointer(e.Pointer);
    }

    private void TitleAdornerSurface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_titleGesture is not TitleGesture gesture || gesture.PointerId != e.Pointer.PointerId)
        {
            return;
        }
        Point p = e.GetCurrentPoint(TitleAdornerSurface).Position;
        Vector2 position = new((float)p.X, (float)p.Y);
        if (!gesture.Started && Vector2.Distance(position, gesture.Start) < 3)
        {
            return;
        }
        VideoTransformValue value = gesture.Original;
        if (gesture.Operation == "move")
        {
            value = value with
            {
                X = Math.Clamp(value.X + (position.X - gesture.Start.X) / TitleAdornerSurface.ActualWidth, 0, 1),
                Y = Math.Clamp(value.Y + (position.Y - gesture.Start.Y) / TitleAdornerSurface.ActualHeight, 0, 1),
            };
        }
        else if (gesture.Operation == "resize")
        {
            value = value with { Scale = Math.Clamp(value.Scale * Vector2.Distance(position, gesture.Centre) / gesture.Radius,
                VideoTimelineEditing.MinimumScale, VideoTimelineEditing.MaximumScale) };
        }
        else
        {
            double angle = Math.Atan2(position.Y - gesture.Centre.Y, position.X - gesture.Centre.X);
            double degrees = value.RotationDegrees + (angle - gesture.Angle) * 180 / Math.PI;
            value = value with { RotationDegrees = ((degrees + 540) % 360) - 180 };
        }
        if (!gesture.Started)
        {
            BeginEdit();
            gesture.Started = true;
        }
        if (VideoTransformKeyframes.SetValueAtTime(
            gesture.Location.Clip, TimelineTrackKind.Text, TitlePreviewSeconds, _frameRate, value))
        {
            ApplyClipPreview();
            TitleAdornerSurface.Invalidate();
        }
        else
        {
            StatusText.Text = "Move the playhead inside the title clip before changing its animated transform.";
            FinishTitleGesture();
            TitleAdornerSurface.ReleasePointerCaptures();
        }
        e.Handled = true;
    }

    private void FinishTitleGesture()
    {
        TitleGesture? gesture = _titleGesture;
        _titleGesture = null;
        if (gesture?.Started == true)
        {
            CommitEdit();
            UpdateVideoInspector();
        }
        TitleAdornerSurface.Invalidate();
    }

    private void TitleAdornerSurface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_titleGesture?.PointerId == e.Pointer.PointerId)
        {
            FinishTitleGesture();
            TitleAdornerSurface.ReleasePointerCapture(e.Pointer);
        }
    }

    private void TitleAdornerSurface_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_titleGesture?.PointerId == e.Pointer.PointerId)
        {
            FinishTitleGesture();
        }
    }

    private void TitleAdornerSurface_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        Point point = e.GetPosition(TitleAdornerSurface);
        TimelineClipLocation? title = HitViewerTitle(new((float)point.X, (float)point.Y));
        if (title is null)
        {
            return;
        }
        FinishTitleGesture();
        _selectedTimelineTrackId = title.Track.Id;
        _selectedTimelineClipId = title.Clip.Id;
        EditSelectedTitle();
        e.Handled = true;
    }

    private void VideoTimelineCanvas_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        Point point = e.GetPosition(VideoTimelineCanvas);
        int index = GetTrackIndexAt(point.Y);
        if (index < 0 || _videoTracks[index].Kind != TimelineTrackKind.Text)
        {
            return;
        }
        EndTimelinePointer();
        TimelineTrackProject track = _videoTracks[index];
        TimelineClipLocation? location = VideoTimelineEditing.FindClipAt(
            track, Math.Max(0, point.X / _timelinePixelsPerSecond), _frameRate);
        if (location is null)
        {
            return;
        }
        _selectedTimelineTrackId = track.Id;
        _selectedTimelineClipId = location.Clip.Id;
        MoveTimelinePlayhead(Math.Max(VideoTimelineEditing.GetStartSeconds(location.Clip, _frameRate),
            point.X / _timelinePixelsPerSecond));
        EditSelectedTitle();
        e.Handled = true;
    }

    private void SelectTitleOnTrack(TimelineTrackProject track)
    {
        _selectedTimelineTrackId = track.Id;
        TimelineClipProject? clip = VideoTimelineEditing.FindClipAt(track, TitlePreviewSeconds, _frameRate)?.Clip ??
            track.Clips.FirstOrDefault(candidate => candidate.Id == _selectedTimelineClipId) ?? track.Clips.FirstOrDefault();
        _selectedTimelineClipId = clip?.Id;
        if (clip is not null)
        {
            MoveTimelinePlayhead(VideoTimelineEditing.GetStartSeconds(clip, _frameRate));
        }
        UpdateVideoInspector();
    }

    private async void EditSelectedTitle()
    {
        if (_titleDialogOpen || SelectedTimelineClip is not { Track.Kind: TimelineTrackKind.Text } selected)
        {
            return;
        }
        if (selected.Track.IsLocked)
        {
            StatusText.Text = "Unlock the title track before editing its text.";
            return;
        }
        PausePlayback();
        FinishActiveEdit();
        var input = new TextBox { Text = selected.Clip.Text, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, MaxLength = 500, MinWidth = 300, MinHeight = 120 };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot, Title = "Edit title text", Content = input,
            PrimaryButtonText = "Apply", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        };
        input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(input.Text);
        dialog.Opened += (_, _) => { input.Focus(FocusState.Programmatic); input.SelectAll(); };
        _titleDialogOpen = true;
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && !selected.Track.IsLocked &&
                _videoTracks.Contains(selected.Track) && selected.Track.Clips.Contains(selected.Clip) &&
                !string.IsNullOrWhiteSpace(input.Text) && selected.Clip.Text != input.Text)
            {
                BeginEdit();
                selected.Clip.Text = input.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
                ApplyClipPreview();
                CommitEdit();
                UpdateVideoInspector();
            }
        }
        finally
        {
            _titleDialogOpen = false;
        }
    }

    private void TitleBackgroundPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_isInitialising || _isRestoringProject || SelectedTimelineClip is not { Track.Kind: TimelineTrackKind.Text } selected)
        {
            return;
        }
        Color colour = args.NewColor;
        uint argb = ((uint)colour.A << 24) | ((uint)colour.R << 16) | ((uint)colour.G << 8) | colour.B;
        if (argb == selected.Clip.TextBackgroundColour || !TryBeginClipPropertyEdit(out _))
        {
            return;
        }
        selected.Clip.TextBackgroundColour = argb;
        ApplyClipPreview();
    }
}
