using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InkMotion;

public sealed partial class MainWindow
{
    private AudioTrackRuntime? _audioRangeTrack;

    private void AudioRange_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_isInitialising)
        {
            RefreshAudioTracks();
        }
    }

    private void UpdateAudioRangeSelection()
    {
        AudioTrackRuntime? track = TryGetSelectedAudioClip(out var selected) ? selected : null;
        AudioRangeActions.IsEnabled = track is not null;
        if (!ReferenceEquals(track, _audioRangeTrack))
        {
            _audioRangeTrack = track;
            SelectWholeAudioRange();
        }
    }

    private void SelectWholeAudioRange()
    {
        if (_audioRangeTrack is not { } track)
        {
            AudioRangeStartBox.Value = 1;
            AudioRangeEndBox.Value = 2;
            return;
        }

        AudioRangeStartBox.Value = track.Project.StartFrame;
        AudioRangeEndBox.Value = Math.Ceiling(track.Project.StartFrame +
            AudioClipEditing.GetTimelineDuration(track.Project, track.SourceDurationSeconds) * _frameRate - 1e-8);
    }

    private void AudioRangeBoundary_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string boundary })
        {
            return;
        }

        if (boundary == "all")
        {
            SelectWholeAudioRange();
        }
        else if (boundary == "start")
        {
            AudioRangeStartBox.Value = _playheadFramePosition + 1;
        }
        else
        {
            AudioRangeEndBox.Value = _playheadFramePosition + 1;
        }
    }

    private void AudioRangeAction_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedAudioClip(out AudioTrackRuntime? selected) || sender is not Button { Tag: string action })
        {
            StatusText.Text = "Select an available audio clip first.";
            return;
        }

        double start = AudioRangeStartBox.Value;
        double end = AudioRangeEndBox.Value;
        if (!double.IsFinite(start) || !double.IsFinite(end) || start != Math.Truncate(start) ||
            end != Math.Truncate(end) || start < 1 || start > int.MaxValue || end < 1 || end > int.MaxValue)
        {
            StatusText.Text = "Audio range boundaries must be whole timeline frame numbers.";
            return;
        }

        AudioRangeParts parts;
        try
        {
            parts = AudioRangeEditing.Partition(selected.Project, (int)start, (int)end, _frameRate, selected.SourceDurationSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            StatusText.Text = "Select a non-empty range within this audio clip; the end frame is excluded.";
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        if (action is "copy" or "cut")
        {
            _copiedAudio = AudioClipEditing.Clone(parts.Range);
            _copiedAudioPeaks = selected.WaveformPeaks;
            PasteAudioClipButton.IsEnabled = true;
        }

        if (action == "copy")
        {
            StatusText.Text = "Audio range copied. Paste at the playhead to place it on a new track.";
            return;
        }

        BeginEdit();
        int insertion = _selectedAudioTrackIndex;
        float[] peaks = selected.WaveformPeaks;
        _audioTracks.RemoveAt(insertion);
        selected.Dispose();
        var replacements = new List<AudioTrackProject>();
        if (parts.Before is not null)
        {
            replacements.Add(parts.Before);
        }

        if (action == "silence")
        {
            parts.Range.Muted = true;
            replacements.Add(parts.Range);
        }

        if (parts.After is not null)
        {
            replacements.Add(parts.After);
        }

        string? loadError = null;
        foreach (AudioTrackProject project in replacements)
        {
            var runtime = new AudioTrackRuntime(project) { WaveformPeaks = peaks };
            OpenAudioPlayer(runtime);
            loadError ??= runtime.LoadError;
            _audioTracks.Insert(insertion++, runtime);
        }

        _selectedAudioTrackIndex = _audioTracks.Count == 0 ? -1 : Math.Min(_selectedAudioTrackIndex, _audioTracks.Count - 1);
        MarkEditChanged();
        CommitEdit();
        UpdateSelectedAudioTrack();
        RefreshAudioTracks();
        SeekToCurrentFrame();
        StatusText.Text = loadError is not null
            ? $"Audio range edited, but a source could not be loaded: {loadError}"
            : action == "silence"
                ? "Range silenced on a muted segment. Other audio stays at its original timeline position."
                : "Range removed without closing the gap. Surrounding audio and source files are unchanged.";
    }
}
