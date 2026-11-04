using System.Text.Json;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
    private InkLayerProject? _inkTrackingBoxLayer;
    private InkTextOverlay? _inkTrackingBoxText;
    private EditorFrame? _inkTrackingBoxFrame;
    private TimelineClipProject? _inkTrackingBoxTitle;
    private int _inkTrackingBoxTargetIndex;

    private InkTextOverlay? SelectedInkTrackingText =>
        ReferenceEquals(_textInspectorFrame, CurrentFrame) &&
        InkTextOverlaySelector.SelectedItem is ComboBoxItem { Tag: InkTextOverlay text } &&
        CurrentFrame.TextOverlays.Contains(text) ? text : null;

    private TimelineClipLocation? SelectedTimelineTitle =>
        SelectedTimelineClip is { Track.Kind: TimelineTrackKind.Text } title ? title : null;

    private int TrackingTargetIndex => InkTrackingTargetSelector?.SelectedIndex ?? 0;

    private InkObjectTracking? SelectedTracking
    {
        get => TrackingTargetIndex switch
        {
            1 => SelectedInkTrackingText?.ObjectTracking,
            2 => SelectedTimelineTitle?.Clip.ObjectTracking,
            _ => CurrentInkLayer.ObjectTracking,
        };
        set
        {
            switch (TrackingTargetIndex)
            {
                case 1:
                    if (SelectedInkTrackingText is { } text) text.ObjectTracking = value;
                    break;
                case 2:
                    if (SelectedTimelineTitle is { } title) title.Clip.ObjectTracking = value;
                    break;
                default:
                    CurrentInkLayer.ObjectTracking = value;
                    break;
            }
        }
    }

    private void UpdateInkTrackingControls()
    {
        if (InkTrackingStatusText is null) return;
        UpdateInkTextEditability();
        InkObjectTracking? tracking = SelectedTracking;
        bool targetAvailable = TrackingTargetIndex switch
        {
            1 => SelectedInkTrackingText is not null,
            2 => SelectedTimelineTitle is not null,
            _ => true,
        };
        bool targetLocked = TrackingTargetIndex switch
        {
            2 => SelectedTimelineTitle?.Track.IsLocked != false,
            1 => false,
            _ => CurrentInkLayer.IsLocked,
        };
        bool editable = targetAvailable && !targetLocked &&
            _inkTrackingCancellation is null;
        InkTrackingTargetSelector.IsEnabled = _inkTrackingCancellation is null && !_selectInkTrackingBox;
        InkTrackingBoxButton.IsEnabled = editable;
        InkTrackObjectButton.IsEnabled = editable;
        InkTrackingEndBox.IsEnabled = editable;
        InkFollowObjectToggle.IsEnabled = editable && tracking is not null;
        InkFollowObjectToggle.IsChecked = tracking?.Enabled == true;
        ClearInkTrackingButton.IsEnabled = editable && tracking is not null;
        InkTrackingLostButton.IsEnabled = tracking?.LostSeconds is not null;
        CancelInkTrackingButton.IsEnabled = _inkTrackingCancellation is not null || _selectInkTrackingBox;
        InkTrackingStatusText.Text = !targetAvailable ? TrackingTargetIndex switch
            {
                1 => "Select a text overlay in the Text Overlay list first.",
                2 => "Select a title clip on the video timeline first.",
                _ => "Select a tracking target first.",
            } :
            !InkObjectMotion.IsSourceCurrent(tracking, _videoTracks) ?
            "Source clip changed or was removed. Clear tracking and analyse again; this object is hidden while Follow object is enabled." :
            tracking is null ? TrackingTargetIndex switch
                {
                    1 => "Off for this text overlay. Select an object box and track.",
                    2 => "Off for this title clip. Select an object box and track.",
                    _ => "Off for this layer. Draw first, select an object box, then track.",
                } :
            $"{(tracking.Enabled ? "Following" : "Disabled")} · {tracking.StartSeconds:0.###}–{tracking.EndSeconds:0.###} s · " +
            $"{tracking.Keys.Count} motion keys" +
            (tracking.LostSeconds is double lost ? $" · STOP at {lost:0.###} s: correct the object box here and resume." : "");
    }

    private void InkTrackingTarget_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_inkTrackingCancellation is not null || _selectInkTrackingBox) return;
        UpdateInkTrackingControls();
    }

    private void SelectInkTrackingBox_Click(object sender, RoutedEventArgs e)
    {
        if (_inkTrackingCancellation is not null) return;
        int targetIndex = TrackingTargetIndex;
        if (targetIndex switch
            {
                1 => SelectedInkTrackingText is null,
                2 => SelectedTimelineTitle is null || SelectedTimelineTitle.Track.IsLocked,
                _ => CurrentInkLayer.IsLocked,
            }) return;
        if (IsOriginalPreview)
        {
            StatusText.Text = "Switch to Modified preview before selecting an object.";
            return;
        }
        PausePlayback();
        FinishActiveEdit();
        ClearInkSelection();
        CancelInkTrackingSelection();
        _inkTrackingBoxTargetIndex = targetIndex;
        _inkTrackingBoxLayer = targetIndex == 0 ? CurrentInkLayer : null;
        _inkTrackingBoxText = targetIndex == 1 ? SelectedInkTrackingText : null;
        _inkTrackingBoxFrame = targetIndex == 1 ? CurrentFrame : null;
        _inkTrackingBoxTitle = targetIndex == 2 ? SelectedTimelineTitle?.Clip : null;
        _selectInkTrackingBox = true;
        UpdateInkTrackingControls();
        StatusText.Text = targetIndex == 0
            ? "Drag a box around a textured part of the object in the Modified Ink Editor viewer."
            : targetIndex == 1
                ? "Drag a box around a textured part of the object for the selected text overlay in the Modified Ink Editor viewer."
                :
                "Drag a box around a textured part of the object for the selected title clip in the Modified Ink Editor viewer.";
    }

    private void CancelInkTrackingSelection()
    {
        _selectInkTrackingBox = false;
        _inkTrackingPointer = null;
        _inkTrackingBoxStart = null;
        _inkTrackingBox = null;
        _inkTrackingBoxLayer = null;
        _inkTrackingBoxText = null;
        _inkTrackingBoxFrame = null;
        _inkTrackingBoxTitle = null;
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
        if (_inkTrackingCancellation is not null) return;
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
        int targetIndex = _inkTrackingBoxTargetIndex;
        InkLayerProject? layer = targetIndex == 0 ? _inkTrackingBoxLayer : null;
        InkTextOverlay? text = targetIndex == 1 ? _inkTrackingBoxText : null;
        EditorFrame? textFrame = targetIndex == 1 ? _inkTrackingBoxFrame : null;
        TimelineClipProject? title = targetIndex == 2 ? _inkTrackingBoxTitle : null;
        if (targetIndex != TrackingTargetIndex ||
            (targetIndex == 1 && (text is null || textFrame is null || !ReferenceEquals(textFrame, CurrentFrame) ||
                !textFrame.TextOverlays.Contains(text) || !ReferenceEquals(text, SelectedInkTrackingText))) ||
            (targetIndex == 2 && (title is null || !ReferenceEquals(title, SelectedTimelineTitle?.Clip))) ||
            (targetIndex == 0 && (layer is null || !ReferenceEquals(layer, CurrentInkLayer))) ||
            (targetIndex == 2 && SelectedTimelineTitle?.Track.IsLocked != false))
        {
            StatusText.Text = "The tracking target changed. Select its object box again.";
            return;
        }
        var visible = VideoTimelineEditing.GetVisibleLayers(_videoTracks, start, _frameRate, _videoMulticamGroups)
            .LastOrDefault(location => location.Track.Kind == TimelineTrackKind.Video);
        if (visible is null)
        {
            StatusText.Text = "Place the playhead inside a visible video clip beneath your ink.";
            return;
        }
        if (targetIndex == 2 && !VideoTimelineEditing.GetVisibleLayers(_videoTracks, start, _frameRate, _videoMulticamGroups)
            .Any(location => location.Track.Kind == TimelineTrackKind.Text && location.Clip.Id == title!.Id))
        {
            StatusText.Text = "Place the playhead inside the selected title clip before tracking it.";
            return;
        }
        InkObjectTracking? original = targetIndex switch
        {
            1 => text!.ObjectTracking,
            2 => title!.ObjectTracking,
            _ => layer!.ObjectTracking,
        };
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
            StatusText.Text = "Clear the existing tracking before choosing a different source clip or starting range.";
            return;
        }
        bool hasTargetContent = targetIndex switch
        {
            1 => !string.IsNullOrWhiteSpace(text!.Text),
            2 => !string.IsNullOrWhiteSpace(title!.Text),
            _ => GetDisplayedLayerStrokes(_selectedFrameIndex, layer!.Id).Any(stroke => stroke.Count > 0),
        };
        if (!resume && !hasTargetContent)
        {
            StatusText.Text = targetIndex == 0 ?
                "Draw on this ink layer at the playhead before attaching it to an object." :
                "Add non-empty text/title before attaching it to an object.";
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
        object target = targetIndex switch { 1 => text!, 2 => title!, _ => layer! };
        string targetSignature = JsonSerializer.Serialize(target);
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
            bool targetStillCurrent = targetIndex switch
            {
                1 => textFrame is not null && text is not null && _frames.Contains(textFrame) &&
                    textFrame.TextOverlays.Contains(text) && ReferenceEquals(text, SelectedInkTrackingText) &&
                    JsonSerializer.Serialize(text) == targetSignature,
                2 => title is not null && SelectedTimelineTitle is { } selectedTitle &&
                    ReferenceEquals(title, selectedTitle.Clip) && !selectedTitle.Track.IsLocked &&
                    JsonSerializer.Serialize(title) == targetSignature,
                _ => layer is not null && _inkLayers.Contains(layer) && !layer.IsLocked &&
                    JsonSerializer.Serialize(layer) == targetSignature,
            };
            if (!targetStillCurrent || frameRate != _frameRate ||
                JsonSerializer.Serialize(CreateProjectSnapshot().VideoTracks) != sourceSignature)
            {
                StatusText.Text = "The tracking target or video changed during tracking; no motion was applied. Track again.";
                return;
            }
            BeginEdit();
            tracking.Keys.RemoveAll(key => key.Seconds >= start);
            tracking.Keys.AddRange(result.Keys);
            tracking.LostSeconds = result.LostSeconds;
            tracking.Enabled = true;
            switch (targetIndex)
            {
                case 1: text!.ObjectTracking = tracking; break;
                case 2: title!.ObjectTracking = tracking; break;
                default: layer!.ObjectTracking = tracking; break;
            }
            MarkEditChanged();
            CommitEdit();
            StatusText.Text = result.Message;
            CancelInkTrackingSelection();
            RenderCurrentInk();
            if (targetIndex == 2) CompositionSurface.Invalidate();
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
        if (SelectedTracking is not { } tracking ||
            (TrackingTargetIndex == 0 && CurrentInkLayer.IsLocked) ||
            (TrackingTargetIndex == 2 && SelectedTimelineTitle?.Track.IsLocked != false)) return;
        PausePlayback();
        FinishActiveEdit();
        ClearInkSelection();
        BeginEdit();
        tracking.Enabled = InkFollowObjectToggle.IsChecked == true;
        MarkEditChanged();
        CommitEdit();
        UpdateInkTrackingControls();
        RenderCurrentInk();
        if (TrackingTargetIndex == 2) CompositionSurface.Invalidate();
    }

    private void ClearInkTracking_Click(object sender, RoutedEventArgs e)
    {
        if (_inkTrackingCancellation is not null ||
            (TrackingTargetIndex == 0 && CurrentInkLayer.IsLocked) ||
            (TrackingTargetIndex == 2 && SelectedTimelineTitle?.Track.IsLocked != false)) return;
        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        SelectedTracking = null;
        MarkEditChanged();
        CommitEdit();
        CancelInkTrackingSelection();
        UpdateInkTrackingControls();
        RenderCurrentInk();
        if (TrackingTargetIndex == 2) CompositionSurface.Invalidate();
    }

    private void InkTrackingLost_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTracking?.LostSeconds is not double lost) return;
        PausePlayback();
        _playheadFramePosition = (int)Math.Round(lost * _frameRate);
        EnsureFrameExists(_playheadFramePosition);
        SelectFrame(InkTimeline.GetFrameIndexAtPosition(_frames, _playheadFramePosition), preservePlayhead: true);
        SeekToCurrentFrame();
        StatusText.Text = "Tracking stopped here. Select a corrected box around the same object, then resume.";
    }
}
