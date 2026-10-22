using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InkMotion;

public sealed partial class MainWindow
{
    private bool EnsureInkLayerEditable()
    {
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

    private void ClearEditingClipboard()
    {
        ClearInkSelection();
        _copiedInkSelection.Clear();
        _copiedCel = null;
        _copiedAudio = null;
        _copiedAudioPeaks = [];
        _copiedVideo = null;
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
        PasteVideoClipButton.IsEnabled = true;
        StatusText.Text = "Clip copied with its source range and properties.";
    }

    private void PasteVideoClip_Click(object sender, RoutedEventArgs e)
    {
        if (_copiedVideo is null)
        {
            StatusText.Text = "Copy a video, image or title clip first.";
            return;
        }

        PlaceVideoCopy(_copiedVideo, _copiedVideoKind, PlayheadFrameNumber);
    }

    private void DuplicateVideoClip_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetEditableClip(out var selected))
        {
            return;
        }

        int start = (int)Math.Ceiling(VideoTimelineEditing.GetEndSeconds(selected.Clip, selected.Track.Kind, _frameRate) * _frameRate - 1e-6) + 1;
        PlaceVideoCopy(selected.Clip, selected.Track.Kind, start);
    }

    private void PlaceVideoCopy(TimelineClipProject source, TimelineTrackKind kind, int startFrame)
    {
        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        TimelineClipProject copy = VideoTimelineEditing.CopyForPlacement(source, startFrame);
        TimelineTrackProject track = VideoTimelineEditing.PlaceClip(_videoTracks, kind, SelectedTimelineTrack, copy, _frameRate);
        _selectedTimelineTrackId = track.Id;
        _selectedTimelineClipId = copy.Id;
        MarkEditChanged();
        CommitEdit();
        RefreshVideoTimeline();
        SeekToCurrentFrame();
        StatusText.Text = "Clip copy placed; overlapping content is preserved on another track.";
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
