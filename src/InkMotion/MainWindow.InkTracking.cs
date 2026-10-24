using System.Text.Json;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace InkMotion;

public sealed partial class MainWindow
{
    private bool _selectInkTrackingBox;
    private Point? _inkTrackingBoxStart;
    private InkTrackingBox? _inkTrackingBox;
    private uint? _inkTrackingPointer;
    private CancellationTokenSource? _inkTrackingCancellation;
    private double _inkTrackingBoxSeconds;

    private void UpdateInkTrackingControls()
    {
        if (InkTrackingStatusText is null) return;
        InkObjectTracking? tracking = CurrentInkLayer.ObjectTracking;
        bool editable = !CurrentInkLayer.IsLocked && _inkTrackingCancellation is null;
        InkTrackingBoxButton.IsEnabled = editable;
        InkTrackObjectButton.IsEnabled = editable;
        InkTrackingEndBox.IsEnabled = editable;
        InkFollowObjectToggle.IsEnabled = editable && tracking is not null;
        InkFollowObjectToggle.IsChecked = tracking?.Enabled == true;
        ClearInkTrackingButton.IsEnabled = editable && tracking is not null;
        InkTrackingLostButton.IsEnabled = tracking?.LostSeconds is not null;
        CancelInkTrackingButton.IsEnabled = _inkTrackingCancellation is not null || _selectInkTrackingBox;
        InkTrackingStatusText.Text = !InkObjectMotion.IsSourceCurrent(tracking, _videoTracks) ?
            "Source clip changed or was removed. Clear tracking and analyse again; this layer is hidden while Follow object is enabled." :
            tracking is null ? "Off for this layer. Draw first, select an object box, then track." :
            $"{(tracking.Enabled ? "Following" : "Disabled")} · {tracking.StartSeconds:0.###}–{tracking.EndSeconds:0.###} s · " +
            $"{tracking.Keys.Count} motion keys" +
            (tracking.LostSeconds is double lost ? $" · STOP at {lost:0.###} s: correct the object box here and resume." : "");
    }

    private void SelectInkTrackingBox_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentInkLayer.IsLocked || _inkTrackingCancellation is not null) return;
        if (IsOriginalPreview)
        {
            StatusText.Text = "Switch to Modified preview before selecting an object.";
            return;
        }
        PausePlayback();
        FinishActiveEdit();
        ClearInkSelection();
        CancelInkTrackingSelection();
        _selectInkTrackingBox = true;
        UpdateInkTrackingControls();
        StatusText.Text = "Drag a box around a textured part of the object in the Modified Ink Editor viewer.";
    }

    private void CancelInkTrackingSelection()
    {
        _selectInkTrackingBox = false;
        _inkTrackingPointer = null;
        _inkTrackingBoxStart = null;
        _inkTrackingBox = null;
        InkSurface.ReleasePointerCaptures();
        InkSurface.Invalidate();
    }

    private bool InkTrackingPointerPressed(PointerRoutedEventArgs e)
    {
        if (!_selectInkTrackingBox) return false;
        _inkTrackingBoxStart = e.GetCurrentPoint(InkSurface).Position;
        _inkTrackingPointer = e.Pointer.PointerId;
        _inkTrackingBoxSeconds = _playheadFramePosition / _frameRate;
        InkSurface.CapturePointer(e.Pointer);
        e.Handled = true;
        return true;
    }

    private bool InkTrackingPointerMoved(PointerRoutedEventArgs e)
    {
        if (!_selectInkTrackingBox) return false;
        if (_inkTrackingPointer == e.Pointer.PointerId && _inkTrackingBoxStart is Point start)
        {
            Point end = e.GetCurrentPoint(InkSurface).Position;
            double width = Math.Max(1, InkSurface.ActualWidth), height = Math.Max(1, InkSurface.ActualHeight);
            double left = Math.Clamp(Math.Min(start.X, end.X) / width, 0, 1);
            double top = Math.Clamp(Math.Min(start.Y, end.Y) / height, 0, 1);
            double right = Math.Clamp(Math.Max(start.X, end.X) / width, 0, 1);
            double bottom = Math.Clamp(Math.Max(start.Y, end.Y) / height, 0, 1);
            _inkTrackingBox = new((left + right) / 2, (top + bottom) / 2, right - left, bottom - top);
            InkSurface.Invalidate();
        }
        e.Handled = true;
        return true;
    }

    private bool InkTrackingPointerReleased(PointerRoutedEventArgs e)
    {
        if (!_selectInkTrackingBox) return false;
        if (_inkTrackingPointer != e.Pointer.PointerId) { e.Handled = true; return true; }
        InkTrackingPointerMoved(e);
        _selectInkTrackingBox = false;
        _inkTrackingPointer = null;
        InkSurface.ReleasePointerCapture(e.Pointer);
        StatusText.Text = _inkTrackingBox is { Width: >= 0.04, Height: >= 0.04 } ?
            "Object box selected. Choose Track / resume from playhead." : "Box too small. Select a larger object region.";
        UpdateInkTrackingControls();
        e.Handled = true;
        return true;
    }

    private void DrawInkTrackingBox(CanvasDrawingSession session, float width, float height)
    {
        if (_inkTrackingBox is not { } box || _isPlaying) return;
        session.DrawRectangle(new Rect((box.X - box.Width / 2) * width, (box.Y - box.Height / 2) * height,
            box.Width * width, box.Height * height), Microsoft.UI.Colors.DeepSkyBlue, 2);
    }

    private async void TrackInkObject_Click(object sender, RoutedEventArgs e)
    {
        if (_inkTrackingCancellation is not null || CurrentInkLayer.IsLocked) return;
        if (IsOriginalPreview)
        {
            StatusText.Text = "Switch to Modified preview before tracking an object.";
            return;
        }
        PausePlayback();
        FinishActiveEdit();
        ClearInkSelection();
        double start = _playheadFramePosition / _frameRate;
        InkTrackingBox? box = _inkTrackingBox;
        if (box is null || Math.Abs(start - _inkTrackingBoxSeconds) > 0.5 / _frameRate)
        {
            StatusText.Text = "Select the object box at the current playhead before starting or resuming.";
            return;
        }
        var visible = VideoTimelineEditing.GetVisibleLayers(_videoTracks, start, _frameRate, _videoMulticamGroups)
            .LastOrDefault(location => location.Track.Kind == TimelineTrackKind.Video);
        if (visible is null)
        {
            StatusText.Text = "Place the playhead inside a visible video clip beneath your ink.";
            return;
        }
        InkLayerProject layer = CurrentInkLayer;
        InkObjectTracking? original = layer.ObjectTracking;
        if (original is not null && !string.IsNullOrEmpty(original.SourceSignature) &&
            JsonSerializer.Serialize(visible.Clip) != original.SourceSignature)
        {
            StatusText.Text = "The source clip changed. Clear the old tracking and analyse again.";
            return;
        }
        bool resume = original is not null && original.SourceClipId == visible.Clip.Id &&
            start > original.StartSeconds && start < original.EndSeconds;
        if (resume && original!.LostSeconds is double lost && start > lost + 0.5 / _frameRate)
        {
            StatusText.Text = "Use Go to tracking stop and correct the box there; do not skip untracked footage.";
            return;
        }
        if (original is not null && !resume)
        {
            StatusText.Text = "Clear this layer's existing tracking before choosing a different source clip or starting range.";
            return;
        }
        if (!resume && !GetDisplayedLayerStrokes(_selectedFrameIndex, layer.Id).Any(stroke => stroke.Count > 0))
        {
            StatusText.Text = "Draw on this ink layer at the playhead before attaching it to an object.";
            return;
        }
        double clipEnd = VideoTimelineEditing.GetStartSeconds(visible.Clip, _frameRate) +
            VideoTimelineEditing.GetDurationSeconds(visible.Clip, TimelineTrackKind.Video);
        double end = resume ? original!.EndSeconds : InkTrackingEndBox.Value == 0 ? clipEnd : InkTrackingEndBox.Value;
        if (!double.IsFinite(end) || end > clipEnd + 0.0001 || end <= start + 1 / _frameRate)
        {
            StatusText.Text = "Set the end time after the playhead and within this video clip, or use 0 for the clip end.";
            return;
        }
        EditorProject snapshot = CreateProjectSnapshot();
        string sourceSignature = JsonSerializer.Serialize(snapshot.VideoTracks);
        string layerSignature = JsonSerializer.Serialize(layer);
        double frameRate = _frameRate;
        var tracking = original is null ? new InkObjectTracking
        {
            SourceClipId = visible.Clip.Id, StartSeconds = start, EndSeconds = end,
            SourceSignature = JsonSerializer.Serialize(visible.Clip),
            CanvasAspectRatio = InkSurface.ActualWidth / Math.Max(1, InkSurface.ActualHeight),
            ReferenceSeconds = start, AnchorX = box.X, AnchorY = box.Y, BoxWidth = box.Width, BoxHeight = box.Height,
        } : InkObjectMotion.Clone(original);
        InkObjectPose initial = resume ?
            new(start, box.X, box.Y,
                Math.Sqrt(box.Width * box.Height / (tracking.BoxWidth * tracking.BoxHeight)),
                InkObjectMotion.Evaluate(tracking, start).Rotation) : new(start, box.X, box.Y);
        using var cancellation = new CancellationTokenSource();
        _inkTrackingCancellation = cancellation;
        UpdateInkTrackingControls();
        var progress = new Progress<double>(value =>
        {
            if (_inkTrackingCancellation == cancellation)
                InkTrackingStatusText.Text = $"Analysing object motion · {value:P0}";
        });
        try
        {
            double aspect = InkSurface.ActualWidth / Math.Max(1, InkSurface.ActualHeight);
            InkTrackingResult result = await Task.Run(() => InkObjectTracker.Analyse(snapshot, tracking.SourceClipId,
                start, end, box, initial, aspect, progress, cancellation.Token, original));
            if (!_inkLayers.Contains(layer) || layer.IsLocked || frameRate != _frameRate ||
                JsonSerializer.Serialize(layer) != layerSignature ||
                JsonSerializer.Serialize(CreateProjectSnapshot().VideoTracks) != sourceSignature)
            {
                StatusText.Text = "The layer or video changed during tracking; no motion was applied. Track again.";
                return;
            }
            BeginEdit();
            tracking.Keys.RemoveAll(key => key.Seconds >= start);
            tracking.Keys.AddRange(result.Keys);
            tracking.LostSeconds = result.LostSeconds;
            tracking.Enabled = true;
            layer.ObjectTracking = tracking;
            MarkEditChanged();
            CommitEdit();
            StatusText.Text = result.Message;
            CancelInkTrackingSelection();
            RenderCurrentInk();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            StatusText.Text = "Object tracking cancelled; previous motion was preserved.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or
            System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Could not track this object: {ex.Message}";
        }
        finally
        {
            _inkTrackingCancellation = null;
            UpdateInkTrackingControls();
        }
    }

    private void CancelInkTracking_Click(object sender, RoutedEventArgs e)
    {
        _inkTrackingCancellation?.Cancel();
        CancelInkTrackingSelection();
        UpdateInkTrackingControls();
    }

    private void InkFollowObject_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentInkLayer.IsLocked || CurrentInkLayer.ObjectTracking is not { } tracking) return;
        PausePlayback();
        FinishActiveEdit();
        ClearInkSelection();
        BeginEdit();
        tracking.Enabled = InkFollowObjectToggle.IsChecked == true;
        MarkEditChanged();
        CommitEdit();
        UpdateInkTrackingControls();
        RenderCurrentInk();
    }

    private void ClearInkTracking_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentInkLayer.IsLocked || _inkTrackingCancellation is not null) return;
        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        CurrentInkLayer.ObjectTracking = null;
        MarkEditChanged();
        CommitEdit();
        CancelInkTrackingSelection();
        UpdateInkTrackingControls();
        RenderCurrentInk();
    }

    private void InkTrackingLost_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentInkLayer.ObjectTracking?.LostSeconds is not double lost) return;
        PausePlayback();
        _playheadFramePosition = (int)Math.Round(lost * _frameRate);
        EnsureFrameExists(_playheadFramePosition);
        SelectFrame(InkTimeline.GetFrameIndexAtPosition(_frames, _playheadFramePosition), preservePlayhead: true);
        SeekToCurrentFrame();
        StatusText.Text = "Tracking stopped here. Select a corrected box around the same object, then resume.";
    }
}
