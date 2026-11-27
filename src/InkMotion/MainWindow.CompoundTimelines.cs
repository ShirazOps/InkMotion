using Microsoft.UI.Xaml;
using NAudio.Wave;

namespace InkMotion;

public sealed partial class MainWindow
{
    private readonly Stack<CompoundEditContext> _compoundContexts = [];
    private readonly List<CompoundPlayback> _compoundPlayback = [];
    private CancellationTokenSource? _compoundPreparation;
    private bool _compoundWindowClosed;
    private bool _switchingCompound;

    private sealed record CompoundEditContext(
        EditorProject Parent,
        string ClipId,
        string TrackId,
        EditorProject[] Undo,
        EditorProject[] Redo,
        int PlayheadFrame,
        bool WasUnsaved);

    private sealed class CompoundPlayback(string trackId, TimelineClipProject clip, CompoundAudioPreview source)
        : IDisposable
    {
        public string TrackId { get; } = trackId;
        public TimelineClipProject Clip { get; } = clip;
        public CompoundAudioPreview Source { get; } = source;
        public AudioPreviewInput? Input { get; set; }
        public string? LastWarning { get; set; }

        public void Dispose()
        {
            Input?.Dispose();
            Source.Dispose();
        }
    }

    private async Task<bool> PrepareCompoundPlaybackAsync(TimeSpan position)
    {
        if (IsOriginalPreview || HasSoloAudio ||
            !_videoTracks.Any(track => track.Kind == TimelineTrackKind.Compound && !track.IsMuted &&
            AudioBuses.IsAudible(_audioBuses, track.AudioBusId) &&
            track.Clips.Any(clip => VideoTimelineEditing.GetEndSeconds(clip, track.Kind, _frameRate) > position.TotalSeconds)))
        {
            return true;
        }
        EditorProject snapshot = CreateProjectSnapshot();
        TimelineTrackProject[] tracks = snapshot.VideoTracks
            .Where(track => track.Kind == TimelineTrackKind.Compound && !track.IsMuted &&
                AudioBuses.IsAudible(snapshot.AudioBuses, track.AudioBusId) &&
                track.Clips.Any(clip => VideoTimelineEditing.GetEndSeconds(clip, track.Kind, snapshot.FrameRate) > position.TotalSeconds))
            .ToArray();
        if (tracks.Length == 0)
        {
            return true;
        }

        string original = EditorProjectSerialiser.Serialise(snapshot);
        var cancellation = new CancellationTokenSource();
        _compoundPreparation = cancellation;
        StatusText.Text = "Preparing compound audio for playback. Select Play again to cancel.";
        List<CompoundPlayback>? prepared = null;
        try
        {
            prepared = await Task.Run(() =>
            {
                var result = new List<CompoundPlayback>();
                try
                {
                    foreach (TimelineTrackProject track in tracks)
                    {
                        foreach (TimelineClipProject clip in track.Clips)
                        {
                            double start = VideoTimelineEditing.GetStartSeconds(clip, snapshot.FrameRate);
                            if (start + clip.DurationSeconds <= position.TotalSeconds) continue;
                            cancellation.Token.ThrowIfCancellationRequested();
                            CompoundAudioPreview? source = CompoundAudioPreview.Prepare(
                                clip, Math.Max(0, position.TotalSeconds - start), cancellation.Token);
                            if (source is not null) result.Add(new CompoundPlayback(track.Id, clip, source));
                        }
                    }
                    return result;
                }
                catch
                {
                    foreach (CompoundPlayback playback in result) playback.Dispose();
                    throw;
                }
            }, cancellation.Token);

            if (cancellation.IsCancellationRequested || _compoundWindowClosed ||
                _timelinePosition != position ||
                EditorProjectSerialiser.Serialise(CreateProjectSnapshot()) != original)
            {
                if (!_compoundWindowClosed) StatusText.Text = "Compound playback preparation was cancelled because the timeline changed.";
                return false;
            }

            foreach (CompoundPlayback playback in prepared.ToArray())
            {
                TimelineTrackProject track = tracks.First(item => item.Id == playback.TrackId);
                ISampleProvider mapped = new ChannelMixingSampleProvider(playback.Source,
                    AudioChannelLayouts.CreateMixMatrix(playback.Source.Layout, _audioMixLayout));
                AudioPreviewSubmix? submix = GetPreviewSubmix(track.AudioBusId);
                if (submix is null)
                {
                    mapped = new ChannelMixingSampleProvider(mapped,
                        AudioChannelLayouts.CreateMixMatrix(_audioMixLayout, AudioChannelLayout.Stereo));
                }
                playback.Input = submix?.AddInput(mapped) ?? _audioPreviewBus.AddInput(mapped);
                _compoundPlayback.Add(playback);
                prepared.Remove(playback);
            }
            prepared = null;
            StatusText.Text = "Compound audio is ready.";
            return true;
        }
        catch (OperationCanceledException)
        {
            if (!_compoundWindowClosed) StatusText.Text = "Compound playback preparation cancelled.";
            return false;
        }
        catch (Exception ex) when (IsFileAccessException(ex) ||
            ex is InvalidDataException or InvalidOperationException or NotSupportedException or NAudio.MmException)
        {
            if (!_compoundWindowClosed) StatusText.Text = $"Could not prepare compound audio: {ex.Message}";
            return false;
        }
        finally
        {
            if (prepared is not null)
            {
                foreach (CompoundPlayback playback in prepared) playback.Dispose();
                DisposeCompoundPlayback();
            }
            if (ReferenceEquals(_compoundPreparation, cancellation)) _compoundPreparation = null;
            cancellation.Dispose();
        }
    }

    private void SyncCompoundPlayback(TimeSpan position, bool startPlayback)
    {
        foreach (CompoundPlayback playback in _compoundPlayback)
        {
            TimelineTrackProject? track = _videoTracks.FirstOrDefault(item => item.Id == playback.TrackId);
            double start = VideoTimelineEditing.GetStartSeconds(playback.Clip, _frameRate);
            bool active = startPlayback && position.TotalSeconds >= start &&
                position.TotalSeconds < start + playback.Clip.DurationSeconds;
            bool audible = track is not null && !track.IsMuted && !HasSoloAudio && !IsOriginalPreview &&
                AudioBuses.IsAudible(_audioBuses, track.AudioBusId);
            playback.Source.SetAudible(audible);
            playback.Input?.SetEnabled(active);
            string? warning = playback.Source.Warnings.FirstOrDefault();
            if (warning is not null && warning != playback.LastWarning)
            {
                playback.LastWarning = warning;
                StatusText.Text = $"Compound audio: {warning}";
            }
        }
    }

    private void DisposeCompoundPlayback()
    {
        foreach (CompoundPlayback playback in _compoundPlayback) playback.Dispose();
        _compoundPlayback.Clear();
    }

    private void CreateCompound_Click(object sender, RoutedEventArgs e)
    {
        if (_switchingCompound || SelectedTimelineClip is not { } selected)
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        EditorProject snapshot = CreateProjectSnapshot();
        string? error = CompoundTimelines.TryCreateTrack(
            snapshot, selected.Track.Id, _compoundContexts.Count, out TimelineTrackProject? compound);
        if (error is not null || compound is null)
        {
            StatusText.Text = error ?? "Could not create the compound timeline.";
            return;
        }

        int index = _videoTracks.IndexOf(selected.Track);
        BeginEdit();
        _videoTracks[index] = compound;
        _selectedTimelineTrackId = compound.Id;
        _selectedTimelineClipId = compound.Clips[0].Id;
        MarkEditChanged();
        CommitEdit();
        RefreshVideoTimeline();
        SeekToCurrentFrame();
        StatusText.Text = "Track converted to a compound. Select Open compound to edit its timeline.";
    }

    private async void OpenCompound_Click(object sender, RoutedEventArgs e)
    {
        if (IsLanSessionActive)
        {
            StatusText.Text = "Leave the collaboration session before editing inside a compound timeline.";
            return;
        }
        if (_switchingCompound || SelectedTimelineClip is not { Track.Kind: TimelineTrackKind.Compound } selected ||
            selected.Track.IsLocked || selected.Clip.NestedProject is null)
        {
            return;
        }
        if (IsAudioRecordingInProgress || _screenRecorderWindow is not null)
        {
            StatusText.Text = "Stop audio recording and close the screen recorder before opening a compound timeline.";
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        EditorProject parent = CreateProjectSnapshot();
        TimelineClipLocation parentClip = VideoTimelineEditing.FindClip(parent.VideoTracks, selected.Clip.Id)!;
        var context = new CompoundEditContext(parent, selected.Clip.Id, selected.Track.Id,
            _undoHistory.ToArray(), _redoHistory.ToArray(), _playheadFramePosition,
            UnsavedIndicator.Visibility == Visibility.Visible);
        _compoundContexts.Push(context);
        _switchingCompound = true;
        ExitCompoundButton.IsEnabled = false;
        bool childActivated = false;
        try
        {
            ClearHistory();
            RestoreProjectState(parentClip.Clip.NestedProject!);
            childActivated = true;
            ExitCompoundButton.Visibility = Visibility.Visible;
            Title = $"InkMotion - {selected.Clip.NestedName}";
            string? warning = await ReloadCompoundSourcesAsync();
            StatusText.Text = $"Editing {selected.Clip.NestedName}. Use Return to parent when finished." +
                (warning is null ? string.Empty : $" {warning}");
        }
        catch (Exception ex) when (IsProjectFileException(ex) || ex is InvalidOperationException or NAudio.MmException)
        {
            if (!childActivated)
            {
                _compoundContexts.Pop();
                RestoreProjectState(parent);
                RestoreCompoundHistory(context.Undo, context.Redo);
                _playheadFramePosition = context.PlayheadFrame;
                _timelinePosition = TimeSpan.FromSeconds(context.PlayheadFrame / _frameRate);
                ExitCompoundButton.Visibility = _compoundContexts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                UpdateHistoryButtons();
            }
            StatusText.Text = $"Could not open the compound timeline: {ex.Message}";
        }
        finally
        {
            _switchingCompound = false;
            ExitCompoundButton.IsEnabled = true;
            if (!_compoundWindowClosed) UpdateVideoInspector();
        }
    }

    private async void ExitCompound_Click(object sender, RoutedEventArgs e)
    {
        if (_switchingCompound || _compoundContexts.Count == 0)
        {
            return;
        }
        if (IsAudioRecordingInProgress || _screenRecorderWindow?.IsRecording == true)
        {
            StatusText.Text = "Stop recording before returning to the parent timeline.";
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        CompoundEditContext context = _compoundContexts.Peek();
        EditorProject child = CreateProjectSnapshot();
        EditorProject[] childUndo = _undoHistory.ToArray();
        EditorProject[] childRedo = _redoHistory.ToArray();
        int childPlayhead = _playheadFramePosition;
        bool parentRestored = false;
        try
        {
            string childJson = EditorProjectSerialiser.Serialise(child);
            TimelineClipLocation original = VideoTimelineEditing.FindClip(context.Parent.VideoTracks, context.ClipId)!;
            bool changed = childJson != EditorProjectSerialiser.Serialise(original.Clip.NestedProject!);
            EditorProject updated = CompoundTimelines.Clone(context.Parent);
            TimelineClipLocation location = VideoTimelineEditing.FindClip(updated.VideoTracks, context.ClipId)!;
            double oldEnd = location.Clip.SourceDurationSeconds;
            double childEnd = GetPlaybackEndSeconds();
            if (!double.IsFinite(childEnd) || childEnd > CompoundTimelines.MaximumDurationSeconds)
            {
                StatusText.Text = "The compound timeline cannot be longer than 24 hours.";
                return;
            }

            location.Clip.NestedProject = child;
            location.Clip.SourceDurationSeconds = Math.Max(oldEnd, childEnd);
            if (Math.Abs(location.Clip.SourceInSeconds + location.Clip.DurationSeconds - oldEnd) < 1e-6)
            {
                double expanded = location.Clip.SourceDurationSeconds - location.Clip.SourceInSeconds;
                _ = VideoTimelineEditing.TrySetStillDuration(location.Track, location.Clip, expanded, updated.FrameRate);
            }
            EditorProjectValidation.Validate(updated);

            _switchingCompound = true;
            ExitCompoundButton.IsEnabled = false;
            RestoreProjectState(updated);
            _compoundContexts.Pop();
            parentRestored = true;
            RestoreCompoundHistory(context.Undo, context.Redo);
            if (changed)
            {
                PushHistory(_undoHistory, context.Parent);
                _redoHistory.Clear();
            }
            _selectedTimelineTrackId = context.TrackId;
            _selectedTimelineClipId = context.ClipId;
            _playheadFramePosition = context.PlayheadFrame;
            _timelinePosition = TimeSpan.FromSeconds(context.PlayheadFrame / _frameRate);
            UnsavedIndicator.Visibility = changed || context.WasUnsaved ? Visibility.Visible : Visibility.Collapsed;
            ExitCompoundButton.Visibility = _compoundContexts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            Title = _compoundContexts.Count > 0 ? "InkMotion - compound timeline" : "InkMotion";
            UpdateHistoryButtons();
            string? warning = await ReloadCompoundSourcesAsync();
            SeekToCurrentFrame();
            RefreshVideoTimeline();
            StatusText.Text = (changed
                ? "Compound changes applied. Extend its right edge if new material is beyond the current clip."
                : "Returned to the parent timeline.") + (warning is null ? string.Empty : $" {warning}");
        }
        catch (Exception ex) when (IsProjectFileException(ex) || ex is InvalidOperationException or NAudio.MmException)
        {
            if (_switchingCompound && !parentRestored)
            {
                RestoreProjectState(child);
                RestoreCompoundHistory(childUndo, childRedo);
                _playheadFramePosition = childPlayhead;
                _timelinePosition = TimeSpan.FromSeconds(childPlayhead / _frameRate);
                UpdateHistoryButtons();
            }
            StatusText.Text = $"Could not finish compound editing: {ex.Message}";
        }
        finally
        {
            _switchingCompound = false;
            ExitCompoundButton.IsEnabled = true;
            if (!_compoundWindowClosed) UpdateVideoInspector();
        }
    }

    private async Task<string?> ReloadCompoundSourcesAsync()
    {
        string? audioWarning = await LoadAudioTracksAsync(_audioTracks.Select(track => track.Project).ToArray());
        string? videoWarning = await LoadTimelineSourcesAsync();
        string warning = string.Join(" · ", new[] { audioWarning, videoWarning }.OfType<string>());
        return warning.Length == 0 ? null : warning;
    }

    private void RestoreCompoundHistory(EditorProject[] undo, EditorProject[] redo)
    {
        _undoHistory.Clear();
        _redoHistory.Clear();
        foreach (EditorProject snapshot in undo.Reverse()) _undoHistory.Push(snapshot);
        foreach (EditorProject snapshot in redo.Reverse()) _redoHistory.Push(snapshot);
    }
}
