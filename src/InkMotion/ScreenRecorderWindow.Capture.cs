using System.Diagnostics;
using Microsoft.UI.Windowing;

namespace InkMotion;

public sealed partial class ScreenRecorderWindow
{
    private ScreenRecordingInputs? _inputs;
    private string? _captureError;
    private Task<ScreenRecordingInputsResult>? _stoppingInputs;
    private string? _completedProjectPath;
    private TimeSpan _captureLimit;
    private bool _timedStop;

    private sealed record DeviceChoice(string Label, object? Device = null)
    {
        public override string ToString() => Label;
    }

    private async Task LoadDevicesAsync()
    {
        if (_busy) throw new InvalidOperationException("Stop recording before refreshing devices.");
        _connectedDisplays = DesktopRecordingFrames.Displays();
        _displays.ItemsSource = _connectedDisplays;
        _displays.SelectedIndex = _connectedDisplays.Count > 0 ? 0 : -1;
        _system.ItemsSource = new[] { new DeviceChoice("None (off)") };
        _microphone.ItemsSource = new[] { new DeviceChoice("None (off)") };
        _camera.ItemsSource = new[] { new DeviceChoice("None (off)") };
        var errors = new List<string>();
        try
        {
            IReadOnlyList<ScreenRecordingAudioDevice> audio = ScreenRecordingInputs.ListAudioDevices();
            _system.ItemsSource = new[] { new DeviceChoice("None (off)") }.Concat(audio
                .Where(device => device.Flow == ScreenRecordingAudioFlow.RenderLoopback).Select(device => new DeviceChoice(device.Name, device))).ToArray();
            _microphone.ItemsSource = new[] { new DeviceChoice("None (off)") }.Concat(audio
                .Where(device => device.Flow == ScreenRecordingAudioFlow.Microphone).Select(device => new DeviceChoice(device.Name, device))).ToArray();
        }
        catch (Exception ex) when (IsCaptureException(ex)) { errors.Add($"Audio devices unavailable: {ex.Message}"); }
        try
        {
            _camera.ItemsSource = new[] { new DeviceChoice("None (off)") }.Concat((await ScreenRecordingInputs.ListCamerasAsync())
                .Select(device => new DeviceChoice(device.Name, device))).ToArray();
        }
        catch (Exception ex) when (IsCaptureException(ex)) { errors.Add($"Cameras unavailable: {ex.Message}"); }
        _system.SelectedIndex = _microphone.SelectedIndex = _camera.SelectedIndex = 0;
        _start.IsEnabled = _connectedDisplays.Count > 0;
        _status.Text = errors.Count > 0 ? string.Join("\n", errors) :
            "Ready. Choose capture areas and optional sources. Nothing is being recorded.";
    }

    private async Task StartAsync()
    {
        if (_busy) return;
        bool openWhenSaved = _openWhenSaved.IsChecked == true;
        _completedProjectPath = null;
        try
        {
            if (_countdown.SelectedItem is not int countdown ||
                !double.IsFinite(_stopAfter.Value) || _stopAfter.Value < 0 || _stopAfter.Value > 1440 ||
                (_stopAfter.Value > 0 && _stopAfter.Value < 1d / 60))
                throw new ArgumentException("Choose a countdown and a recording limit from one second to 1,440 minutes, or 0 for manual stopping.");
            bool minimize = _minimize.IsChecked == true;
            if (minimize && AppWindow.Presenter is not OverlappedPresenter)
                throw new InvalidOperationException("This recorder window cannot be minimised. Turn off automatic minimising and try again.");
            _captureLimit = TimeSpan.FromMinutes(_stopAfter.Value);
            _timedStop = false;
            ScreenRegion[] connected = DesktopRecordingFrames.Displays().Select(display => display.Bounds).ToArray();
            ScreenCapturePlan plan = ScreenCapturePlan.Create(_regions.ToArray(), connected,
                (int)_resolution.SelectedItem, (int)_fps.SelectedItem);
            Directory.CreateDirectory(ScreenRecordingStore.Root);
            var drive = new DriveInfo(Path.GetPathRoot(ScreenRecordingStore.Root)!);
            if (drive.AvailableFreeSpace < 1024L * 1024 * 1024)
                throw new IOException("At least 1 GiB of free recording storage is required.");
            if (_shortcutRegistered)
                throw new InvalidOperationException("The previous recording shortcut could not be released. Close and reopen the recorder before starting another take.");
            if (_stopShortcut.IsChecked == true) RegisterStopShortcut();
            CloseAreaOutlines();
            _busy = true;
            _captureError = null;
            _stoppingInputs = null;
            _start.IsEnabled = false;
            _configurationHost.IsEnabled = false;
            _stop.IsEnabled = true;
            _stop.Content = "Cancel countdown";
            _stopCapture = new CancellationTokenSource();
            _directory = ScreenRecordingStore.CreateDirectory();
            _manifest = new() { Plan = plan, WebcamPictureInPicture = _pip.IsChecked == true };
            ScreenRecordingStore.Save(_directory, _manifest);
            _run = RunCaptureAsync(plan, _cursor.IsChecked == true,
                (_system.SelectedItem as DeviceChoice)?.Device as ScreenRecordingAudioDevice,
                (_microphone.SelectedItem as DeviceChoice)?.Device as ScreenRecordingAudioDevice,
                (_camera.SelectedItem as DeviceChoice)?.Device as ScreenRecordingCameraDevice,
                countdown, minimize, _stopCapture.Token);
            await _run;
        }
        catch (Exception ex) when (IsCaptureException(ex))
        {
            _status.Text = $"Could not start recording: {ex.Message}";
        }
        finally
        {
            UnregisterStopShortcut();
            _busy = false;
            _start.IsEnabled = _connectedDisplays.Count > 0;
            _configurationHost.IsEnabled = true;
            _stop.IsEnabled = false;
            _stop.Content = "Stop and save";
            _stopCapture?.Dispose();
            _stopCapture = null;
        }
        if (_completedProjectPath is not null && !_closingRequested && openWhenSaved)
        {
            try { await _open(_completedProjectPath); }
            catch (Exception ex) when (IsCaptureException(ex)) { _status.Text += $"\nProject saved but could not open: {ex.Message}"; }
        }
    }

    private async Task RunCaptureAsync(ScreenCapturePlan plan, bool cursor, ScreenRecordingAudioDevice? system,
        ScreenRecordingAudioDevice? microphone, ScreenRecordingCameraDevice? camera,
        int countdown, bool minimize, CancellationToken stop)
    {
        string directory = _directory!;
        ScreenRecordingManifest manifest = _manifest!;
        string? projectPath = null;
        _clock.Reset();
        _inputs = new(directory, _clock);
        try
        {
            if (camera is not null) await _inputs.InitializeCameraAsync(camera.Id, stop);
            for (int second = countdown; second > 0; second--)
            {
                _status.Text = $"Recording {plan.Width} × {plan.Height} at {plan.FrameRate} fps starts in {second}…";
                await Task.Delay(1000, stop);
            }
            stop.ThrowIfCancellationRequested();
            if (minimize) ((OverlappedPresenter)AppWindow.Presenter).Minimize();
            _stop.Content = "Stop and save";
            _clock.Start();
            if (_captureLimit > TimeSpan.Zero) _stopCapture!.CancelAfter(_captureLimit);
            if (system is not null) { await _inputs.StartAudioAsync(system, stop); Checkpoint(); }
            if (microphone is not null) { await _inputs.StartAudioAsync(microphone, stop); Checkpoint(); }
            if (camera is not null) { await _inputs.StartCameraAsync(stop); Checkpoint(); }
            manifest.Status = "Recording";
            Checkpoint();
            _timer.Start();
            UpdateRecordingStatus();
            await Task.Run(() => ScreenRecordingEncoder.Record(directory, manifest, _clock,
                () => new DesktopRecordingFrames(plan, cursor), Checkpoint, stop));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            _timedStop = _captureLimit > TimeSpan.Zero && _clock.Elapsed >= _captureLimit;
        }
        catch (Exception ex) when (IsCaptureException(ex) || ex is ScreenRecordingInputException)
        {
            _captureError = DescribeError(ex);
            RequestStop();
        }
        finally
        {
            UnregisterStopShortcut();
            _timedStop |= stop.IsCancellationRequested && _clock.IsRunning &&
                _captureLimit > TimeSpan.Zero && _clock.Elapsed >= _captureLimit;
            _clock.Stop();
            _timer.Stop();
            _stop.IsEnabled = false;
            _status.Text = "Finalising recording sources and recovery project…";
            try
            {
                _stoppingInputs ??= _inputs.StopAsync();
                ScreenRecordingInputsResult result = await _stoppingInputs;
                ApplyInputResult(result);
            }
            catch (Exception ex) when (IsCaptureException(ex) || ex is ScreenRecordingInputException)
            {
                _captureError = JoinError(_captureError, DescribeError(ex));
                ApplyInputResult(_inputs.GetFinalisedTracks());
            }
            try { await _inputs.DisposeAsync(); }
            catch (Exception ex) when (IsCaptureException(ex) || ex is ScreenRecordingInputException)
            {
                _captureError = JoinError(_captureError, DescribeError(ex));
            }
            _clock.Stop();
            lock (manifest)
            {
                manifest.Error = _captureError;
                manifest.Status = _captureError is not null ? "Interrupted" :
                    manifest.Media.Any(media => media.Kind == "Screen" && media.Finalised) ? "Completed" : "No screen footage";
                try
                {
                    ScreenRecordingStore.Save(directory, manifest);
                    if (manifest.Media.Any(media => media.Kind == "Screen" && media.Finalised))
                        projectPath = ScreenRecordingStore.SaveProject(directory, manifest);
                }
                catch (Exception ex) when (IsCaptureException(ex))
                {
                    _captureError = JoinError(_captureError, $"Could not save recovery project: {ex.Message}");
                }
            }
            _inputs = null;
            _status.Text = (_captureError is not null ? $"Recording interrupted: {_captureError}" :
                projectPath is not null ? (_timedStop ? "Recording limit reached. Recording saved." : "Recording saved.") :
                !_clock.IsRunning && _clock.Elapsed == TimeSpan.Zero ? "Countdown cancelled. Nothing was recorded." :
                "No completed screen video was captured.") +
                $"\nSources retained at {directory}. Repeated slow frames: {manifest.DroppedFrames}.";
            try { RefreshLibrary(); }
            catch (Exception ex) when (IsCaptureException(ex)) { _status.Text += $"\nCould not refresh storage: {ex.Message}"; }
        }
        _completedProjectPath = projectPath;
    }

    private void Checkpoint()
    {
        if (_manifest is null || _directory is null) return;
        lock (_manifest)
        {
            if (_inputs is not null)
            {
                ScreenRecordingInputsSnapshot snapshot = _inputs.GetSnapshot();
                foreach (ScreenRecordingAudioTrackSnapshot audio in snapshot.AudioTracks)
                    UpdateMedia(audio.Path, audio.Flow == ScreenRecordingAudioFlow.RenderLoopback ? "System" : "Microphone",
                        audio.StartOffsetSeconds, audio.ElapsedDurationSeconds, false);
                if (snapshot.CameraTrack is { } camera)
                    UpdateMedia(camera.Path, "Webcam", camera.StartOffsetSeconds, camera.ElapsedDurationSeconds, false);
            }
            ScreenRecordingStore.Save(_directory, _manifest);
        }
    }

    private void ApplyInputResult(ScreenRecordingInputsResult result)
    {
        lock (_manifest!)
        {
            foreach (ScreenRecordingAudioTrackResult audio in result.AudioTracks)
                UpdateMedia(audio.Path, audio.Flow == ScreenRecordingAudioFlow.RenderLoopback ? "System" : "Microphone",
                    audio.StartOffsetSeconds, audio.DurationSeconds, true);
            if (result.CameraTrack is { } camera)
                UpdateMedia(camera.Path, "Webcam", camera.StartOffsetSeconds, camera.DurationSeconds, true);
            foreach (string warning in result.AudioTracks.SelectMany(audio => audio.Warnings)
                .Concat(result.CameraTrack?.Warnings ?? []))
                if (!_manifest.Warnings.Contains(warning)) _manifest.Warnings.Add(warning);
        }
    }

    private void UpdateMedia(string path, string kind, double offset, double duration, bool finalised)
    {
        string name = Path.GetFileName(path);
        ScreenRecordingMedia? media = _manifest!.Media.FirstOrDefault(media => media.FileName == name);
        if (media is null) { media = new() { FileName = name, Kind = kind }; _manifest.Media.Add(media); }
        media.OffsetSeconds = offset;
        media.DurationSeconds = duration;
        media.Finalised |= finalised;
    }

    private void UpdateRecordingStatus()
    {
        if (_directory is null || _manifest is null) return;
        try
        {
            _inputs?.ThrowIfFailed();
            if (new DriveInfo(Path.GetPathRoot(_directory)!).AvailableFreeSpace < 512L * 1024 * 1024)
                throw new IOException("Recording stopped because less than 512 MiB of storage remains.");
            Checkpoint();
            _status.Text = $"RECORDING · {_clock.Elapsed:hh\\:mm\\:ss} · {ScreenRecordingStore.StorageBytes(_directory) / 1048576d:0.0} MiB" +
                $" · {_manifest.DroppedFrames} slow frames repeated" +
                (_captureLimit > TimeSpan.Zero ? $" · stops in {TimeSpan.FromTicks(Math.Max(0, (_captureLimit - _clock.Elapsed).Ticks)):hh\\:mm\\:ss}" : "") +
                $"\n{_directory}";
        }
        catch (Exception ex) when (IsCaptureException(ex) || ex is ScreenRecordingInputException)
        {
            _captureError = DescribeError(ex);
            RequestStop();
            _timer.Stop();
        }
    }

    private static string DescribeError(Exception ex) => ex is ScreenRecordingInputException
        ? $"{ex.Message} {ex.InnerException?.Message}" : ex.Message;
    private static string JoinError(string? first, string next) => first is null ? next : $"{first} {next}";
}
