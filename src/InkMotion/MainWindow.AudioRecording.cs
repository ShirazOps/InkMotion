using Microsoft.UI.Xaml;
using NAudio.Wave;

namespace InkMotion;

public sealed partial class MainWindow
{
    private readonly object _audioRecordingSync = new();
    private WaveInEvent? _audioRecordingCapture;
    private WaveFileWriter? _audioRecordingWriter;
    private string? _audioRecordingPartialPath;
    private string? _audioRecordingFinalPath;
    private int _audioRecordingStartFrame;
    private long _audioRecordingBytes;
    private Exception? _audioRecordingError;
    private Exception? _audioRecordingWriteError;
    private bool _audioRecordingInProgress;
    private bool _audioRecordingClosing;
    private int _audioRecordingFinalised;

    private bool IsAudioRecordingInProgress
    {
        get
        {
            lock (_audioRecordingSync)
            {
                return _audioRecordingInProgress;
            }
        }
    }

    private void InitialiseAudioRecordingControls()
    {
        AudioRecordingSampleRateSelector.ItemsSource = new[]
        {
            new AudioRecordingSampleRate(44100, "44.1 kHz"),
            new AudioRecordingSampleRate(48000, "48 kHz"),
        };
        AudioRecordingSampleRateSelector.DisplayMemberPath = nameof(AudioRecordingSampleRate.DisplayName);
        AudioRecordingSampleRateSelector.SelectedIndex = 1;

        try
        {
            AudioRecordingInput[] inputs = Enumerable.Range(0, WaveInEvent.DeviceCount)
                .Select(index =>
                {
                    WaveInCapabilities capabilities = WaveInEvent.GetCapabilities(index);
                    return new AudioRecordingInput(index, capabilities.ProductName, capabilities.Channels);
                })
                .Where(input => input.Channels > 0)
                .ToArray();
            AudioRecordingDeviceSelector.ItemsSource = inputs;
            AudioRecordingDeviceSelector.DisplayMemberPath = nameof(AudioRecordingInput.DisplayName);
            AudioRecordingDeviceSelector.SelectedIndex = inputs.Length > 0 ? 0 : -1;
            AudioRecordingButton.IsEnabled = inputs.Length > 0;
            AudioRecordingStatus.Text = inputs.Length > 0
                ? "Select an input and record at the playhead. Timeline playback may continue; input monitoring is off."
                : "No audio input devices were found.";
        }
        catch (NAudio.MmException ex)
        {
            AudioRecordingButton.IsEnabled = false;
            AudioRecordingStatus.Text = $"Audio inputs could not be listed: {ex.Message}";
        }
    }

    private void AudioRecordingButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsAudioRecordingInProgress)
        {
            StopAudioRecording();
            return;
        }

        StartAudioRecording();
    }

    private void StartAudioRecording()
    {
        if (_screenRecorderWindow?.IsRecording == true)
        {
            AudioRecordingStatus.Text = "Stop screen recording before starting a separate audio take.";
            return;
        }
        if (AudioRecordingDeviceSelector.SelectedItem is not AudioRecordingInput input ||
            AudioRecordingSampleRateSelector.SelectedItem is not AudioRecordingSampleRate sampleRate)
        {
            AudioRecordingStatus.Text = "Choose an available audio input and sample rate.";
            return;
        }

        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "InkMotion", "Recordings");
        string recordingId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        string partialPath = Path.Combine(directory, $"Recording-{recordingId}.wav.part");
        string finalPath = Path.Combine(directory, $"Recording-{recordingId}.wav");
        int channels = Math.Clamp(input.Channels, 1, 2);
        WaveFileWriter? writer = null;
        WaveInEvent? capture = null;
        try
        {
            Directory.CreateDirectory(directory);
            writer = new WaveFileWriter(partialPath, new WaveFormat(sampleRate.Value, 16, channels));
            capture = new WaveInEvent
            {
                DeviceNumber = input.DeviceNumber,
                WaveFormat = new WaveFormat(sampleRate.Value, 16, channels),
                BufferMilliseconds = 50,
            };
            capture.DataAvailable += AudioRecording_DataAvailable;
            capture.RecordingStopped += AudioRecording_RecordingStopped;
            lock (_audioRecordingSync)
            {
                _audioRecordingWriter = writer;
                _audioRecordingCapture = capture;
                _audioRecordingPartialPath = partialPath;
                _audioRecordingFinalPath = finalPath;
                _audioRecordingStartFrame = _playheadFramePosition + 1;
                _audioRecordingBytes = 0;
                _audioRecordingError = null;
                _audioRecordingWriteError = null;
                _audioRecordingInProgress = true;
                _audioRecordingClosing = false;
                _audioRecordingFinalised = 0;
            }

            capture.StartRecording();
            AudioRecordingButton.Content = "Stop recording";
            AudioRecordingDeviceSelector.IsEnabled = false;
            AudioRecordingSampleRateSelector.IsEnabled = false;
            AudioRecordingStatus.Text = $"Recording {channels} channel(s) at {sampleRate.DisplayName} from frame {_audioRecordingStartFrame}.";
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or NAudio.MmException or InvalidOperationException or ArgumentException)
        {
            string failure = ex.Message;
            if (capture is not null)
            {
                capture.DataAvailable -= AudioRecording_DataAvailable;
                capture.RecordingStopped -= AudioRecording_RecordingStopped;
                try
                {
                    capture.Dispose();
                }
                catch (Exception disposeError) when (disposeError is NAudio.MmException or InvalidOperationException)
                {
                    failure += $" Capture cleanup failed: {disposeError.Message}";
                }
            }

            try
            {
                writer?.Dispose();
            }
            catch (Exception disposeError) when (disposeError is IOException or UnauthorizedAccessException)
            {
                failure += $" WAV cleanup failed: {disposeError.Message}";
            }

            lock (_audioRecordingSync)
            {
                _audioRecordingWriter = null;
                _audioRecordingCapture = null;
                _audioRecordingPartialPath = null;
                _audioRecordingFinalPath = null;
                _audioRecordingInProgress = false;
                _audioRecordingFinalised = 1;
            }

            string? cleanupError = DeleteRecordingFile(partialPath);
            AudioRecordingButton.Content = "Record audio";
            AudioRecordingDeviceSelector.IsEnabled = true;
            AudioRecordingSampleRateSelector.IsEnabled = true;
            AudioRecordingButton.IsEnabled = AudioRecordingDeviceSelector.Items.Count > 0;
            AudioRecordingStatus.Text = $"Recording could not start: {failure}" +
                (cleanupError is null ? string.Empty : $" Temporary file could not be removed: {cleanupError}");
        }
    }

    private void StopAudioRecording()
    {
        WaveInEvent? capture;
        lock (_audioRecordingSync)
        {
            capture = _audioRecordingCapture;
        }

        if (capture is null)
        {
            return;
        }

        AudioRecordingButton.Content = "Finishing recording...";
        AudioRecordingButton.IsEnabled = false;
        AudioRecordingStatus.Text = "Finishing and adding the recorded WAV to the timeline...";
        try
        {
            capture.StopRecording();
        }
        catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException)
        {
            lock (_audioRecordingSync)
            {
                _audioRecordingError ??= ex;
            }

            AudioRecordingButton.Content = "Stop recording";
            AudioRecordingButton.IsEnabled = true;
            AudioRecordingStatus.Text = $"Recording could not stop cleanly: {ex.Message}";
        }
    }

    private void StopAudioRecordingForClose()
    {
        WaveInEvent? capture;
        lock (_audioRecordingSync)
        {
            _audioRecordingClosing = true;
            capture = _audioRecordingCapture;
        }

        if (capture is null)
        {
            return;
        }

        try
        {
            capture.StopRecording();
        }
        catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException)
        {
            lock (_audioRecordingSync)
            {
                _audioRecordingError ??= ex;
            }
        }
    }

    private void AudioRecording_DataAvailable(object? sender, WaveInEventArgs e)
    {
        bool stopCapture = false;
        lock (_audioRecordingSync)
        {
            if (_audioRecordingWriter is null || _audioRecordingFinalised != 0)
            {
                return;
            }

            try
            {
                _audioRecordingWriter.Write(e.Buffer, 0, e.BytesRecorded);
                _audioRecordingBytes += e.BytesRecorded;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                _audioRecordingWriteError ??= ex;
                _audioRecordingError ??= ex;
                stopCapture = true;
            }
        }

        if (stopCapture && sender is WaveInEvent capture)
        {
            try
            {
                capture.StopRecording();
            }
            catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException)
            {
                lock (_audioRecordingSync)
                {
                    _audioRecordingError ??= ex;
                }
            }
        }
    }

    private void AudioRecording_RecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (sender is not WaveInEvent capture)
        {
            return;
        }

        WaveFileWriter? writer;
        string? partialPath;
        string? finalPath;
        int startFrame;
        long bytesRecorded;
        Exception? error;
        Exception? writeError;
        lock (_audioRecordingSync)
        {
            if (Interlocked.Exchange(ref _audioRecordingFinalised, 1) != 0)
            {
                return;
            }

            writer = _audioRecordingWriter;
            _audioRecordingWriter = null;
            partialPath = _audioRecordingPartialPath;
            finalPath = _audioRecordingFinalPath;
            startFrame = _audioRecordingStartFrame;
            bytesRecorded = _audioRecordingBytes;
            writeError = _audioRecordingWriteError;
            error = _audioRecordingError ?? e.Exception;
            _audioRecordingCapture = null;
        }

        capture.DataAvailable -= AudioRecording_DataAvailable;
        capture.RecordingStopped -= AudioRecording_RecordingStopped;
        try
        {
            capture.Dispose();
        }
        catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException)
        {
            error ??= ex;
        }

        try
        {
            writer?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            writeError ??= ex;
            error ??= ex;
        }

        bool fileReady = false;
        if (writeError is null && bytesRecorded > 0 && partialPath is not null && finalPath is not null)
        {
            try
            {
                File.Move(partialPath, finalPath);
                fileReady = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error ??= ex;
            }
        }
        else if (bytesRecorded == 0 && partialPath is not null)
        {
            string? cleanupError = DeleteRecordingFile(partialPath);
            if (cleanupError is not null)
            {
                error ??= new IOException($"The empty recording could not be removed: {cleanupError}");
            }
        }

        if (finalPath is null)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            AudioRecordingButton.Content = "Record audio";
            AudioRecordingButton.IsEnabled = AudioRecordingDeviceSelector.Items.Count > 0;
            AudioRecordingDeviceSelector.IsEnabled = true;
            AudioRecordingSampleRateSelector.IsEnabled = true;
            lock (_audioRecordingSync)
            {
                _audioRecordingInProgress = false;
            }
            CompleteAudioRecording(finalPath, partialPath, startFrame, fileReady, error);
        });
    }

    private void CompleteAudioRecording(string path, string? partialPath, int startFrame, bool fileReady, Exception? error)
    {
        if (_audioRecordingClosing)
        {
            return;
        }

        if (!fileReady)
        {
            AudioRecordingStatus.Text = error is null
                ? "No audio was captured."
                : $"Recording could not be saved: {error.Message}" +
                    (partialPath is not null && File.Exists(partialPath)
                        ? $" The partial take remains at {partialPath}."
                        : string.Empty);
            return;
        }

        PausePlayback();
        var track = new AudioTrackRuntime(new AudioTrackProject
        {
            AudioPath = path,
            StartFrame = startFrame,
            Title = Path.GetFileNameWithoutExtension(path),
        });
        BeginEdit();
        _audioTracks.Add(track);
        _selectedAudioTrackIndex = _audioTracks.Count - 1;
        MarkEditChanged();
        CommitEdit();
        OpenAudioPlayer(track);
        UpdateSelectedAudioTrack();
        RefreshAudioTracks();
        UnsavedIndicator.Visibility = Visibility.Visible;
        _ = LoadAudioWaveformAsync(track);
        _ = LoadAudioTagsAsync(track);

        AudioRecordingStatus.Text = track.LoadError is null
            ? $"Saved and added {Path.GetFileName(path)} to the timeline."
            : $"Saved {Path.GetFileName(path)}, but it could not be opened: {track.LoadError}";
        StatusText.Text = track.LoadError is not null
            ? $"Audio recording was saved but could not be loaded: {track.LoadError}"
            : error is null
            ? "Audio recording added as a new timeline clip."
            : $"Audio recording was added, but capture stopped with an error: {error.Message}";
    }

    private static string? DeleteRecordingFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    private sealed record AudioRecordingInput(int DeviceNumber, string Name, int Channels)
    {
        public string DisplayName => $"{Name} ({Math.Min(Channels, 2)} channel(s))";
    }

    private sealed record AudioRecordingSampleRate(int Value, string DisplayName);
}
