using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace InkMotion;

public sealed partial class MainWindow
{
    private void HandleVideoEditingShortcut(KeyRoutedEventArgs e)
    {
        if (e.Handled || _workspace != "Video" || !VideoActionBar.IsEnabled)
            return;
        DependencyObject? focused = FocusManager.GetFocusedElement(Content.XamlRoot) as DependencyObject;
        for (DependencyObject? element = focused; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is TextBox or RichEditBox or PasswordBox or NumberBox or ComboBox or Slider or ContentDialog)
                return;
        }
        bool control = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) &
            Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        bool shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) &
            Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        bool alt = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu) &
            Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (alt)
            return;
        RoutedEventHandler? command = control ? e.Key switch
        {
            VirtualKey.C when !shift => CopyVideoClip_Click,
            VirtualKey.X when !shift => CutVideoClip_Click,
            VirtualKey.V when !shift => PasteVideoClip_Click,
            VirtualKey.B when !shift => SplitClip_Click,
            VirtualKey.Z when !shift => Undo_Click,
            VirtualKey.Z when shift => Redo_Click,
            VirtualKey.Y => Redo_Click,
            _ => null,
        } : e.Key switch
        {
            VirtualKey.Delete => DeleteClip_Click,
            VirtualKey.V => (_, args) => TimelineToolMode_Click(SelectTimelineToolButton, args),
            VirtualKey.B => (_, args) => TimelineToolMode_Click(BladeTimelineToolButton, args),
            _ => null,
        };
        if (command is null)
            return;
        PausePlayback();
        command(VideoTimelineFocusTarget, new RoutedEventArgs());
        e.Handled = true;
    }

    private void InkAlphaLock_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentInkLayer.IsLocked)
        {
            InkAlphaLockToggle.IsChecked = CurrentInkLayer.AlphaLock;
            StatusText.Text = "Unlock the drawing layer before changing alpha lock.";
            return;
        }
        PausePlayback(); FinishActiveEdit(); BeginEdit();
        CurrentInkLayer.AlphaLock = InkAlphaLockToggle.IsChecked == true;
        MarkEditChanged(); CommitEdit();
        StatusText.Text = CurrentInkLayer.AlphaLock
            ? "Alpha lock on: new painting recolours existing alpha. Erase and selection still edit original paths."
            : "Alpha lock off: new painting can extend artwork. Existing alpha-locked strokes retain their restriction.";
    }

    private bool EnsureInkLayerEditable()
    {
        if (CurrentInkLayer.LipSync is { Enabled: true })
        {
            StatusText.Text = "Turn lip-sync preview off before editing the layer drawing, or use Edit selected mouth in Lip-sync.";
            return false;
        }
        if (_characterRig is { Enabled: true } && CurrentInkLayer.RigBinding is not null)
        {
            StatusText.Text = "Turn animated rig preview off in Character rig before editing the original drawing; re-enable it to review poses.";
            return false;
        }
        if (CurrentInkLayer.ObjectTracking is { Enabled: true })
        {
            StatusText.Text = "Turn off Follow object before editing the original layer drawing; re-enable it to review the attached motion.";
            return false;
        }
        if (!CurrentInkLayer.IsLocked)
        {
            return true;
        }

        StatusText.Text = $"{CurrentInkLayer.Name} is drawing-locked. Unlock it in the Ink Editor inspector to edit its strokes.";
        return false;
    }

    private void InkLayerLock_Click(object sender, RoutedEventArgs e)
    {
        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        CurrentInkLayer.IsLocked = InkLayerLockButton.IsChecked == true;
        MarkEditChanged();
        CommitEdit();
        UpdateInkLayerControls();
        StatusText.Text = CurrentInkLayer.IsLocked
            ? "Drawing locked: strokes, opacity and layer order are protected. Cel timing/text and visibility remain editable."
            : "Drawing unlocked.";
    }

    private EditorFrame? _copiedCel;
    private AudioTrackProject? _copiedAudio;
    private float[] _copiedAudioPeaks = [];
    private TimelineClipProject? _copiedVideo;
    private TimelineTrackKind _copiedVideoKind;
    private AudioTrackProject? _copiedVideoAudio;
    private float[] _copiedVideoAudioPeaks = [];

    private void ClearEditingClipboard()
    {
        _isInkBucketFill = false;
        ClearInkSelection();
        _copiedInkSelection.Clear();
        _copiedCel = null;
        _copiedCelRange.Clear();
        PasteInkCelRangeButton.IsEnabled = false;
        _copiedAudio = null;
        _copiedAudioPeaks = [];
        _copiedVideo = null;
        _copiedVideoAudio = null;
        _copiedVideoAudioPeaks = [];
        PasteCelButton.IsEnabled = false;
        PasteAudioClipButton.IsEnabled = false;
        PasteVideoClipButton.IsEnabled = false;
    }

    private void CopyCel_Click(object sender, RoutedEventArgs e)
    {
        FinishActiveEdit();
        _copiedCel = InkEditing.Clone(CurrentFrame);
        PasteCelButton.IsEnabled = true;
        StatusText.Text = "Cel copied, including all ink layers, text and exposure.";
    }

    private void PasteCel_Click(object sender, RoutedEventArgs e)
    {
        if (_copiedCel is null)
        {
            StatusText.Text = "Copy a cel first.";
            return;
        }

        InsertCelCopy(_copiedCel);
    }

    private void InsertCelCopy(EditorFrame source)
    {
        if (source.LayerStrokes.Keys.Any(id => !_inkLayers.Any(layer => layer.Id == id)))
        {
            StatusText.Text = "This cel references removed or different-project layers. Copy a cel from the current project.";
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        int inserted = InkEditing.InsertCopy(_frames, _selectedFrameIndex, source);
        MarkEditChanged();
        CommitEdit();
        RefreshTimeline();
        SelectFrame(inserted);
        StatusText.Text = "Cel copy inserted after the selected cel.";
    }

    private void MoveInkLayer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !int.TryParse(tag, out int offset))
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        bool moved = InkEditing.MoveLayer(_inkLayers, _activeInkLayerId, offset);
        if (moved)
        {
            MarkEditChanged();
        }

        CommitEdit();
        RefreshInkLayers();
        RenderCurrentInk();
        StatusText.Text = moved ? "Ink layer order changed." : "Cannot move past the stack edge or a drawing-locked layer.";
    }

    private void CopyVideoClip_Click(object sender, RoutedEventArgs e)
    {
        FinishActiveEdit();
        if (SelectedTimelineClip is not { } selected)
        {
            StatusText.Text = "Select a video, image or title clip first.";
            return;
        }

        _copiedVideo = VideoTimelineEditing.Clone(selected.Clip);
        _copiedVideoKind = selected.Track.Kind;
        AudioTrackRuntime? linked = _audioTracks.FirstOrDefault(audio => audio.Project.LinkedVideoClipId == selected.Clip.Id);
        _copiedVideoAudio = linked is null ? null : AudioClipEditing.Clone(linked.Project);
        _copiedVideoAudioPeaks = linked?.WaveformPeaks ?? [];
        PasteVideoClipButton.IsEnabled = true;
        StatusText.Text = "Clip copied with its source range, properties and any linked audio.";
    }

    private void CutVideoClip_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetEditableClip(out _))
            return;
        CopyVideoClip_Click(sender, e);
        DeleteClip_Click(sender, e);
    }

    private void PasteVideoClip_Click(object sender, RoutedEventArgs e)
    {
        if (_copiedVideo is null)
        {
            StatusText.Text = "Copy a video, image or title clip first.";
            return;
        }

        PlaceVideoCopy(_copiedVideo, _copiedVideoKind, PlayheadFrameNumber, _copiedVideoAudio, _copiedVideoAudioPeaks);
    }

    private void DuplicateVideoClip_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetEditableClip(out var selected))
        {
            return;
        }

        int start = (int)Math.Ceiling(VideoTimelineEditing.GetEndSeconds(selected.Clip, selected.Track.Kind, _frameRate) * _frameRate - 1e-6) + 1;
        AudioTrackRuntime? linked = _audioTracks.FirstOrDefault(audio => audio.Project.LinkedVideoClipId == selected.Clip.Id);
        PlaceVideoCopy(selected.Clip, selected.Track.Kind, start, linked?.Project, linked?.WaveformPeaks);
    }

    private void PlaceVideoCopy(TimelineClipProject source, TimelineTrackKind kind, int startFrame,
        AudioTrackProject? linkedAudio = null, float[]? peaks = null)
    {
        PausePlayback();
        FinishActiveEdit();
        TimelineClipProject copy = VideoTimelineEditing.CopyForPlacement(source, startFrame);
        if (!LinkedAudioVideoEditing.TryCopyLinkedAudio(source, copy, linkedAudio, out AudioTrackProject? audioCopy))
        {
            StatusText.Text = "The linked audio would start outside the timeline. Paste later or unlink the clips before copying.";
            return;
        }
        BeginEdit();
        TimelineTrackProject track = VideoTimelineEditing.PlaceClip(_videoTracks, kind, SelectedTimelineTrack, copy, _frameRate);
        AudioTrackRuntime? runtime = null;
        if (audioCopy is not null)
        {
            runtime = new AudioTrackRuntime(audioCopy) { WaveformPeaks = peaks ?? [] };
            OpenAudioPlayer(runtime);
            _audioTracks.Add(runtime);
            RefreshAudioTracks();
            UpdateSelectedAudioTrack();
        }
        _selectedTimelineTrackId = track.Id;
        _selectedTimelineClipId = copy.Id;
        MarkEditChanged();
        CommitEdit();
        RefreshVideoTimeline();
        SeekToCurrentFrame();
        StatusText.Text = runtime?.LoadError is { } error
            ? $"Clip copy placed, but its linked audio could not be loaded: {error}"
            : "Clip copy placed with any linked audio; overlapping content is preserved on another track.";
    }

    private TimelineClipProject? SplitTimelineClip(TimelineClipLocation location, int frame)
    {
        _timelineClipActionWarning = null;
        AudioTrackRuntime? linked = _audioTracks.FirstOrDefault(audio => audio.Project.LinkedVideoClipId == location.Clip.Id);
        TimelineClipProject? right = LinkedAudioVideoEditing.SplitVideo(location.Track, location.Clip, frame,
            _frameRate, linked?.Project, linked?.SourceDurationSeconds ?? 0, out AudioTrackProject? rightAudio);
        if (right is null)
            return null;
        if (linked is not null)
        {
            UpdateAudioTrackPlayers(linked);
            if (rightAudio is not null)
            {
                var runtime = new AudioTrackRuntime(rightAudio) { WaveformPeaks = linked.WaveformPeaks };
                OpenAudioPlayer(runtime);
                _audioTracks.Insert(_audioTracks.IndexOf(linked) + 1, runtime);
                _timelineClipActionWarning = runtime.LoadError is { } rightError
                    ? $"Linked audio could not be loaded: {rightError}" : null;
            }
            _timelineClipActionWarning ??= linked.LoadError is { } leftError
                ? $"Linked audio is unavailable: {leftError}" : null;
            RefreshAudioTracks();
            UpdateSelectedAudioTrack();
        }
        return right;
    }

    private bool DeleteTimelineClip(TimelineClipLocation location, bool ripple)
    {
        List<AudioTrackProject> audio = _audioTracks.Select(runtime => runtime.Project).ToList();
        if (!LinkedAudioVideoEditing.DeleteVideo(location.Track, location.Clip, audio, _frameRate, ripple))
            return false;
        foreach (AudioTrackRuntime removed in _audioTracks.Where(runtime => !audio.Contains(runtime.Project)).ToArray())
        {
            _audioTracks.Remove(removed);
            removed.Dispose();
        }
        _selectedAudioTrackIndex = _audioTracks.Count == 0 ? -1 : Math.Clamp(_selectedAudioTrackIndex, 0, _audioTracks.Count - 1);
        RefreshAudioTracks();
        UpdateSelectedAudioTrack();
        return true;
    }

    private void CopyAudioClip_Click(object sender, RoutedEventArgs e)
    {
        FinishActiveEdit();
        if (!TryGetSelectedAudioClip(out AudioTrackRuntime? selected))
        {
            StatusText.Text = "Select an available audio clip to copy.";
            return;
        }

        _copiedAudio = AudioClipEditing.Clone(selected.Project);
        _copiedAudioPeaks = selected.WaveformPeaks;
        PasteAudioClipButton.IsEnabled = true;
        StatusText.Text = "Audio clip copied with its source range, processing and tags.";
    }

    private void PasteAudioClip_Click(object sender, RoutedEventArgs e)
    {
        if (_copiedAudio is null)
        {
            StatusText.Text = "Copy an audio clip first.";
            return;
        }

        PlaceAudioCopy(_copiedAudio, _copiedAudioPeaks, _playheadFramePosition + 1);
    }

    private void DuplicateAudioClip_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedAudioClip(out AudioTrackRuntime? selected))
        {
            StatusText.Text = "Select an available audio clip to duplicate.";
            return;
        }

        int end = selected.Project.StartFrame + (int)Math.Ceiling(
            AudioClipEditing.GetTimelineDuration(selected.Project, selected.SourceDurationSeconds) * _frameRate - 1e-6);
        PlaceAudioCopy(selected.Project, selected.WaveformPeaks, end);
    }

    private void PlaceAudioCopy(AudioTrackProject source, float[] peaks, int startFrame)
    {
        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        AudioTrackProject copy = AudioClipEditing.Clone(source);
        copy.LinkedVideoClipId = null;
        copy.StartFrame = startFrame;
        var runtime = new AudioTrackRuntime(copy) { WaveformPeaks = peaks };
        OpenAudioPlayer(runtime);
        _audioTracks.Add(runtime);
        _selectedAudioTrackIndex = _audioTracks.Count - 1;
        MarkEditChanged();
        CommitEdit();
        UpdateSelectedAudioTrack();
        RefreshAudioTracks();
        SeekToCurrentFrame();
        StatusText.Text = runtime.LoadError is null
            ? "Audio clip copy placed on a new track."
            : $"Audio clip copy placed, but its source could not be loaded: {runtime.LoadError}";
    }

    private void UpdateAudioVideoLinkControls()
    {
        bool hasAudio = TryGetSelectedAudioClip(out AudioTrackRuntime? audio);
        TimelineClipLocation? video = SelectedTimelineClip;
        bool canLink = hasAudio &&
            video is { Track.Kind: TimelineTrackKind.Video } &&
            audio!.Project.LinkedVideoClipId is null &&
            !_audioTracks.Any(track => track.Project.LinkedVideoClipId == video.Clip.Id);
        LinkAudioVideoButton.IsEnabled = canLink;
        UnlinkAudioVideoButton.IsEnabled = hasAudio && audio!.Project.LinkedVideoClipId is not null;
    }

    private void LinkSelectedAudioVideo_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not { Track.Kind: TimelineTrackKind.Video } video ||
            !TryGetSelectedAudioClip(out AudioTrackRuntime? audio))
        {
            StatusText.Text = "Select one video clip and one available audio clip to link.";
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        bool linked = LinkedAudioVideoEditing.TryLink(
            video.Track,
            video.Clip,
            audio.Project,
            _audioTracks.Select(track => track.Project));
        if (linked)
        {
            MarkEditChanged();
        }

        CommitEdit();
        UpdateSelectedAudioTrack();
        UpdateVideoInspector();
        StatusText.Text = linked
            ? "Audio and video linked. Moving either clip moves both; their current timing offset is preserved."
            : "Could not link these clips. Each video clip can have one linked audio clip.";
    }

    private void UnlinkSelectedAudioVideo_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedAudioClip(out AudioTrackRuntime? audio))
        {
            StatusText.Text = "Select a linked audio clip to unlink.";
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        bool unlinked = LinkedAudioVideoEditing.TryUnlink(audio.Project);
        if (unlinked)
        {
            MarkEditChanged();
        }

        CommitEdit();
        UpdateSelectedAudioTrack();
        UpdateVideoInspector();
        StatusText.Text = unlinked ? "Audio and video unlinked." : "The selected audio clip is not linked.";
    }

    private void ApplyCrossDissolve_Click(object sender, RoutedEventArgs e)
    {
        PausePlayback();
        FinishActiveEdit();
        if (!TryGetEditableClip(out TimelineClipLocation selected) ||
            selected.Track.Kind != TimelineTrackKind.Video)
        {
            return;
        }

        BeginEdit();
        bool applied = VideoTransitions.TrySetCrossDissolve(
            selected.Track,
            selected.Clip,
            CrossDissolveDurationBox.Value,
            _frameRate);
        if (applied)
        {
            MarkEditChanged();
        }

        CommitEdit();
        RefreshVideoTimeline();
        SeekToCurrentFrame();
        UpdateVideoInspector();
        StatusText.Text = applied
            ? $"Applied a {CrossDissolveDurationBox.Value:0.##} second cross-dissolve across the cut."
            : "A cross-dissolve needs an adjacent previous video clip, enough source handles on both clips, no cut-edge fades, and both clips at full opacity.";
    }

    private void RemoveCrossDissolve_Click(object sender, RoutedEventArgs e)
    {
        PausePlayback();
        FinishActiveEdit();
        if (!TryGetEditableClip(out TimelineClipLocation selected) ||
            selected.Track.Kind != TimelineTrackKind.Video)
        {
            return;
        }

        BeginEdit();
        bool removed = VideoTransitions.RemoveCrossDissolve(selected.Track, selected.Clip);
        if (removed)
        {
            MarkEditChanged();
        }

        CommitEdit();
        RefreshVideoTimeline();
        SeekToCurrentFrame();
        UpdateVideoInspector();
        StatusText.Text = removed ? "Cross-dissolve removed." : "The selected video clip has no incoming transition.";
    }

    private void DeleteAudioClip_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedAudioTrackIndex < 0 || _selectedAudioTrackIndex >= _audioTracks.Count)
        {
            StatusText.Text = "Select an audio track to remove.";
            return;
        }

        AudioTrackRuntime selected = _audioTracks[_selectedAudioTrackIndex];
        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        _audioTracks.Remove(selected);
        selected.Dispose();
        _selectedAudioTrackIndex = _audioTracks.Count == 0 ? -1 : Math.Min(_selectedAudioTrackIndex, _audioTracks.Count - 1);
        MarkEditChanged();
        CommitEdit();
        UpdateSelectedAudioTrack();
        RefreshAudioTracks();
        StatusText.Text = "Audio track removed from the project; the source file is unchanged.";
    }
}
