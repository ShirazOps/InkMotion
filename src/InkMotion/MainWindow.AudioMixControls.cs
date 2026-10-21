using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InkMotion;

public sealed partial class MainWindow
{
    private long _lastAudioMeterTick;
    private bool HasSoloAudio => _audioTracks.Any(track => track.Project.Solo);

    private void UpdateAudioMeter()
    {
        if (_workspace != "Audio")
        {
            foreach (AudioTrackRuntime track in _audioTracks)
            {
                track.Player?.TakeMeterReading();
            }
            return;
        }

        AudioMeterReading? selectedReading = null;
        for (int index = 0; index < _audioTracks.Count; index++)
        {
            AudioMeterReading? reading = _audioTracks[index].Player?.TakeMeterReading();
            if (index == _selectedAudioTrackIndex)
            {
                selectedReading = reading;
            }
        }

        if (selectedReading is null)
        {
            AudioMeterProgress.Value = 0;
            AudioMeterText.Text = "Selected track monitor: no audio";
            return;
        }

        AudioMeterProgress.Value = Math.Clamp((selectedReading.PeakDecibels + 60) / 60 * 100, 0, 100);
        AudioMeterText.Text = $"Track monitor peak {MeterDecibels(selectedReading.PeakDecibels)} / RMS {MeterDecibels(selectedReading.RmsDecibels)} dBFS" +
            (selectedReading.Clipped ? " - CLIP (latched)" : string.Empty);
    }

    private static string MeterDecibels(double value) => double.IsNegativeInfinity(value) ? "-inf" : value.ToString("0.0");

    private void ResetAudioClipIndicator_Click(object sender, RoutedEventArgs e)
    {
        foreach (AudioTrackRuntime track in _audioTracks)
        {
            track.Player?.TakeMeterReading(resetClip: true);
        }
        UpdateAudioMeter();
    }

    private void AudioSolo_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitialising || _isRestoringProject || !TryGetSelectedAudioClip(out AudioTrackRuntime? track))
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        track.Project.Solo = AudioSoloButton.IsChecked == true;
        MarkEditChanged();
        CommitEdit();
        SeekToCurrentFrame();
        UpdateSelectedAudioTrack();
        RefreshAudioTracks();
        StatusText.Text = HasSoloAudio
            ? "Solo enabled: only solo audio tracks are heard and exported; muted tracks stay silent."
            : "Solo disabled: the complete mix is restored.";
    }

    private void AudioBalance_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isInitialising || _isRestoringProject || !TryGetSelectedAudioClip(out AudioTrackRuntime? track))
        {
            return;
        }

        if (!double.IsFinite(args.NewValue))
        {
            StatusText.Text = "Balance must be a number between -100 and 100.";
            UpdateSelectedAudioTrack();
            return;
        }

        double balance = Math.Clamp(args.NewValue / 100, -1, 1);
        if (Math.Abs(track.Project.Balance - balance) < 1e-8)
        {
            return;
        }

        ApplyAudioClipEdit(selected =>
        {
            selected.Project.Balance = balance;
            return true;
        }, $"Audio balance set to {balance * 100:+0;-0;0}.", "Could not change this clip's balance.");
    }

    private void AudioSpatialPan_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitialising || _isRestoringProject || !TryGetSelectedAudioClip(out AudioTrackRuntime? track) ||
            track.Player is not AudioClipPlayer { ChannelCount: 1 })
        {
            return;
        }

        bool enabled = AudioSpatialPanToggle.IsOn;
        if (track.Project.SpatialPanEnabled == enabled)
        {
            return;
        }

        ApplyAudioClipEdit(selected =>
        {
            selected.Project.SpatialPanEnabled = enabled;
            return true;
        }, enabled ? "Spatial azimuth pan enabled for this mono clip." : "Spatial azimuth pan disabled for this clip.",
            "Could not change spatial panning.");
    }

    private void AudioSpatialPanAzimuth_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isInitialising || _isRestoringProject || !TryGetSelectedAudioClip(out AudioTrackRuntime? track) ||
            track.Player is not AudioClipPlayer { ChannelCount: 1 } || !AudioSpatialPanToggle.IsOn)
        {
            return;
        }

        if (!double.IsFinite(args.NewValue) || args.NewValue is < -180 or > 180)
        {
            StatusText.Text = "Spatial azimuth must be between -180 and 180 degrees.";
            UpdateSelectedAudioTrack();
            return;
        }

        double azimuth = args.NewValue;
        if (Math.Abs(track.Project.SpatialPanAzimuthDegrees - azimuth) < 1e-8)
        {
            return;
        }

        ApplyAudioClipEdit(selected =>
        {
            selected.Project.SpatialPanAzimuthDegrees = azimuth;
            return true;
        }, $"Spatial pan set to {azimuth:0}°.", "Could not change spatial pan.");
    }
}
