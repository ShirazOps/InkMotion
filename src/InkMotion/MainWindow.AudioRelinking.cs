using Microsoft.UI.Xaml;
using NAudio.Wave;

namespace InkMotion;

public sealed partial class MainWindow
{
    private async void RelinkMatchingAudioSources_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedAudioTrackIndex < 0 || _selectedAudioTrackIndex >= _audioTracks.Count ||
            string.IsNullOrWhiteSpace(_audioTracks[_selectedAudioTrackIndex].Project.AudioPath))
        {
            StatusText.Text = "Select an audio track with an existing source link, including a missing source.";
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        AudioTrackRuntime selected = _audioTracks[_selectedAudioTrackIndex];
        bool allowUnknownEnd = AudioRelinkUseReplacementEnd.IsChecked == true;
        string before = EditorProjectSerialiser.Serialise(CreateProjectSnapshot());
        var file = await PickTimelineFileAsync([".mp3", ".wav", ".m4a", ".aac", ".wma", ".flac"]);
        if (file is null)
        {
            return;
        }

        try
        {
            (double duration, int channels) = await Task.Run(() =>
            {
                using var reader = new AudioFileReader(file.Path);
                return (AudioSourcePosition.DurationSeconds(reader), reader.WaveFormat.Channels);
            });
            if (_audioMasterClosed)
            {
                return;
            }
            PausePlayback();
            FinishActiveEdit();
            if (_selectedAudioTrackIndex < 0 || _selectedAudioTrackIndex >= _audioTracks.Count ||
                !ReferenceEquals(_audioTracks[_selectedAudioTrackIndex], selected) ||
                before != EditorProjectSerialiser.Serialise(CreateProjectSnapshot()))
            {
                StatusText.Text = "The project changed while choosing a source. Relink again using the current selection.";
                return;
            }

            var tracks = _audioTracks.Select(track => track.Project).ToList();
            var durations = _audioTracks.Where(track => track.HasSource)
                .ToDictionary(track => track.Project, track => track.SourceDurationSeconds);
            string source = Path.GetFullPath(selected.Project.AudioPath!);
            AudioTrackRuntime[] affected = _audioTracks.Where(track =>
                !string.IsNullOrWhiteSpace(track.Project.AudioPath) &&
                string.Equals(Path.GetFullPath(track.Project.AudioPath), source, StringComparison.OrdinalIgnoreCase)).ToArray();
            BeginEdit();
            int count;
            try
            {
                count = AudioSourceRelinking.RelinkMatchingSources(tracks, selected.Project, file.Path,
                    duration, channels, durations, allowUnknownEnd);
                MarkEditChanged();
            }
            finally
            {
                CommitEdit();
            }

            ClearAudioSpectrum();
            foreach (AudioTrackRuntime track in affected)
            {
                track.WaveformPeaks = [];
                track.WaveformError = null;
                OpenAudioPlayer(track);
            }
            UpdateSelectedAudioTrack();
            RefreshAudioTracks();
            foreach (AudioTrackRuntime track in affected)
            {
                if (_audioMasterClosed || !_audioTracks.Contains(track))
                {
                    return;
                }
                if (track.HasSource)
                {
                    await LoadAudioWaveformAsync(track);
                }
            }
            if (_audioMasterClosed || affected.Any(track => !_audioTracks.Contains(track)))
            {
                return;
            }
            string[] errors = affected.Select(track => track.LoadError ?? track.WaveformError)
                .OfType<string>().ToArray();
            StatusText.Text = $"{count} audio source link(s) updated; placement, processing and tags retained." +
                (errors.Length > 0 ? " Reload warnings: " + string.Join("; ", errors) : string.Empty);
        }
        catch (Exception ex) when (IsFileAccessException(ex) ||
            ex is InvalidOperationException or NotSupportedException or FormatException or NAudio.MmException)
        {
            if (!_audioMasterClosed)
            {
                StatusText.Text = $"Could not relink audio sources: {ex.Message}";
            }
        }
    }
}
