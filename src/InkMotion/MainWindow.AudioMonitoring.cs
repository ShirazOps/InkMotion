using Microsoft.UI.Xaml;
using NAudio.Wave;
using Windows.Devices.Enumeration;

namespace InkMotion;

public sealed partial class MainWindow
{
    private AudioInputMonitor? _audioInputMonitor;
    private DeviceWatcher? _monitorCaptureWatcher;
    private DeviceWatcher? _monitorRenderWatcher;
    private bool _audioMonitoringClosed;

    private void InitialiseAudioMonitoring()
    {
        try
        {
            _monitorCaptureWatcher = CreateAudioMonitorWatcher(DeviceClass.AudioCapture);
            _monitorRenderWatcher = CreateAudioMonitorWatcher(DeviceClass.AudioRender);
            RefreshAudioMonitorOutputs();
        }
        catch (Exception ex) when (IsAudioMonitorError(ex))
        {
            AudioMonitoringButton.IsEnabled = false;
            AudioMonitoringStatus.Text = $"Audio device watching could not start: {ex.Message}";
        }
    }

    private DeviceWatcher CreateAudioMonitorWatcher(DeviceClass deviceClass)
    {
        DeviceWatcher watcher = DeviceInformation.CreateWatcher(deviceClass);
        bool enumerated = false;
        watcher.EnumerationCompleted += (_, _) => Volatile.Write(ref enumerated, true);
        watcher.Added += (_, _) =>
        {
            if (Volatile.Read(ref enumerated)) AudioMonitorDevicesChanged();
        };
        watcher.Removed += (_, _) => AudioMonitorDevicesChanged();
        watcher.Stopped += (_, _) =>
        {
            if (!_audioMonitoringClosed) AudioMonitorDevicesChanged();
        };
        watcher.Start();
        return watcher;
    }

    private void AudioMonitorDevicesChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_audioMonitoringClosed) return;
        StopAudioMonitoring("Audio devices changed. Refresh devices and explicitly restart monitoring.");
        AudioMonitoringButton.IsEnabled = false;
    });

    private void RefreshAudioMonitorOutputs()
    {
        var outputs = Enumerable.Range(0, WaveOut.DeviceCount)
            .Select(index => new AudioMonitoringOutput(index, WaveOut.GetCapabilities(index).ProductName))
            .ToArray();
        AudioMonitoringOutputSelector.ItemsSource = outputs;
        AudioMonitoringOutputSelector.DisplayMemberPath = nameof(AudioMonitoringOutput.Name);
        AudioMonitoringOutputSelector.SelectedIndex = outputs.Length > 0 ? 0 : -1;
        AudioMonitoringButton.IsEnabled = outputs.Length > 0 &&
            AudioRecordingDeviceSelector.Items.Count > 0 &&
            _monitorCaptureWatcher?.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted &&
            _monitorRenderWatcher?.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted;
        AudioMonitoringStatus.Text = outputs.Length > 0
            ? "Monitoring is off. Use headphones to prevent feedback. No audio is saved or transmitted."
            : "No audio output devices were found.";
    }

    private void RefreshAudioDevices_Click(object sender, RoutedEventArgs e)
    {
        if (IsAudioRecordingInProgress)
        {
            AudioMonitoringStatus.Text = "Stop the audio take before refreshing devices.";
            return;
        }
        StopAudioMonitoring("Monitoring stopped for device refresh.");
        InitialiseAudioRecordingControls();
        try
        {
            RefreshAudioMonitorOutputs();
        }
        catch (Exception ex) when (IsAudioMonitorError(ex))
        {
            AudioMonitoringButton.IsEnabled = false;
            AudioMonitoringStatus.Text = $"Audio devices could not be listed: {ex.Message}";
        }
    }

    private void AudioMonitoring_Click(object sender, RoutedEventArgs e)
    {
        if (_audioInputMonitor is not null)
        {
            StopAudioMonitoring("Monitoring stopped. No audio was saved.");
            return;
        }
        if (IsAudioRecordingInProgress || _screenRecorderWindow is not null)
        {
            AudioMonitoringStatus.Text = "Stop audio recording and close Screen recorder before monitoring an input.";
            return;
        }
        if (AudioRecordingDeviceSelector.SelectedItem is not AudioRecordingInput input ||
            AudioRecordingSampleRateSelector.SelectedItem is not AudioRecordingSampleRate rate ||
            AudioMonitoringOutputSelector.SelectedItem is not AudioMonitoringOutput output ||
            !double.IsFinite(AudioMonitoringGainBox.Value))
        {
            AudioMonitoringStatus.Text = "Choose an input, output, sample rate and monitoring level.";
            return;
        }
        try
        {
            var monitor = new AudioInputMonitor(input.DeviceNumber, output.DeviceNumber, rate.Value,
                input.Channels, AudioMonitoringGainBox.Value);
            _audioInputMonitor = monitor;
            monitor.Failed += AudioMonitorFailed;
            monitor.Start();
            AudioMonitoringButton.Content = "Stop input monitoring";
            SetAudioMonitoringControls(false);
            AudioMonitoringStatus.Text = $"Monitoring {input.Name} to {output.Name} at {rate.DisplayName}. No recording; use headphones.";
        }
        catch (Exception ex) when (IsAudioMonitorError(ex))
        {
            StopAudioMonitoring($"Monitoring could not start: {ex.Message}");
        }
    }

    private void AudioMonitorFailed(AudioInputMonitor monitor, string message) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!_audioMonitoringClosed && ReferenceEquals(monitor, _audioInputMonitor))
            StopAudioMonitoring($"Monitoring stopped: {message} Refresh devices before restarting.");
    });

    private void SetAudioMonitoringControls(bool enabled)
    {
        AudioRecordingDeviceSelector.IsEnabled = enabled;
        AudioRecordingSampleRateSelector.IsEnabled = enabled;
        AudioMonitoringOutputSelector.IsEnabled = enabled;
        AudioMonitoringGainBox.IsEnabled = enabled;
    }

    private void StopAudioMonitoring(string message)
    {
        AudioInputMonitor? monitor = _audioInputMonitor;
        _audioInputMonitor = null;
        if (monitor is not null)
        {
            monitor.Failed -= AudioMonitorFailed;
            try
            {
                monitor.Dispose();
            }
            catch (Exception ex) when (IsAudioMonitorError(ex))
            {
                message += $" Device cleanup failed: {ex.Message}";
            }
        }
        AudioMonitoringButton.Content = "Monitor input (headphones)";
        SetAudioMonitoringControls(true);
        AudioMonitoringStatus.Text = message;
    }

    private void CloseAudioMonitoring()
    {
        _audioMonitoringClosed = true;
        StopAudioMonitoring("Monitoring stopped.");
        foreach (DeviceWatcher? watcher in new[] { _monitorCaptureWatcher, _monitorRenderWatcher })
        {
            if (watcher?.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                watcher.Stop();
        }
    }

    private static bool IsAudioMonitorError(Exception ex) =>
        ex is NAudio.MmException or InvalidOperationException or ArgumentException or
            UnauthorizedAccessException or System.Runtime.InteropServices.COMException;

    private sealed record AudioMonitoringOutput(int DeviceNumber, string Name);
}
