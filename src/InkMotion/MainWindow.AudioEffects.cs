using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InkMotion;

public sealed partial class MainWindow
{
    private bool _capturingAudioNoiseProfile;

    private void UpdateAudioEffectsInspector(AudioTrackProject? track)
    {
        AudioEffectsControls.IsEnabled = track is not null;
        AudioEffectSettings settings = track?.Effects ?? new();
        AudioNoiseProfile? profile = settings.NoiseProfile;
        AudioClipPlayer? player = TryGetSelectedAudioClip(out AudioTrackRuntime? selectedTrack) &&
            ReferenceEquals(selectedTrack.Project, track) ? selectedTrack.Player : null;
        bool profileMatchesSource = profile is not null && player is not null &&
            track?.AudioPath is string sourcePath &&
            profile.MatchesSource(sourcePath, player.SourceSampleRate, player.ChannelCount);
        CaptureAudioNoiseProfileButton.IsEnabled = track?.AudioPath is not null && player is not null &&
            !_capturingAudioNoiseProfile;
        AudioNoiseReductionEnabled.IsEnabled = profileMatchesSource;
        AudioNoiseReductionStrengthBox.IsEnabled = profileMatchesSource;
        if (player is not null)
        {
            AudioNoiseProfileStartBox.Value = 0;
            AudioNoiseProfileEndBox.Value = player.SourceSampleFrames;
        }

        AudioNoiseProfileText.Text = profile switch
        {
            null => player is null
                ? "Load an audio source, then capture a noise-only range of at least 512 sample frames."
                : $"Source: {player.SourceSampleRate:N0} Hz · {player.ChannelCount} channel(s) · " +
                  $"{player.SourceSampleFrames:N0} frames. Select a noise-only range below.",
            _ when profileMatchesSource =>
                $"Profile ready · {profile.SampleRate:N0} Hz · {profile.Channels} channel(s) · " +
                $"{profile.Magnitudes.Length:N0} spectral bins. Saved in the project.",
            _ => "Saved profile does not match this source. Capture a new noise-only sample before enabling reduction.",
        };
        AudioEffectsEnabled.IsChecked = settings.Enabled;
        AudioHighPassBox.Value = settings.HighPassHz;
        AudioLowPassBox.Value = settings.LowPassHz;
        AudioBassBox.Value = settings.BassDecibels;
        AudioMidBox.Value = settings.MidDecibels;
        AudioTrebleBox.Value = settings.TrebleDecibels;
        AudioCompressorThresholdBox.Value = settings.CompressorThresholdDecibels;
        AudioCompressorRatioBox.Value = settings.CompressorRatio;
        AudioCompressorAttackBox.Value = settings.AttackMilliseconds;
        AudioCompressorReleaseBox.Value = settings.ReleaseMilliseconds;
        AudioGateEnabled.IsChecked = settings.GateEnabled;
        AudioGateThresholdBox.Value = settings.GateThresholdDecibels;
        AudioGateAttackBox.Value = settings.GateAttackMilliseconds;
        AudioGateReleaseBox.Value = settings.GateReleaseMilliseconds;
        AudioGateHoldBox.Value = settings.GateHoldMilliseconds;
        AudioNoiseReductionEnabled.IsChecked = settings.NoiseReductionEnabled;
        AudioNoiseReductionStrengthBox.Value = settings.NoiseReductionStrengthDecibels;
        AudioLimiterEnabled.IsChecked = settings.LimiterEnabled;
        AudioLimiterCeilingBox.Value = settings.LimiterCeilingDecibels;
        AudioLimiterReleaseBox.Value = settings.LimiterReleaseMilliseconds;
        AudioDelayEnabled.IsChecked = settings.DelayEnabled;
        AudioDelayTimeBox.Value = settings.DelayMilliseconds;
        AudioDelayFeedbackBox.Value = settings.DelayFeedback;
        AudioDelayWetBox.Value = settings.DelayWetGain;
        AudioReverbEnabled.IsChecked = settings.ReverbEnabled;
        AudioReverbRoomBox.Value = settings.ReverbRoomScale;
        AudioReverbDecayBox.Value = settings.ReverbDecaySeconds;
        AudioReverbDampingBox.Value = settings.ReverbDamping;
        AudioReverbWetBox.Value = settings.ReverbWetGain;
    }

    private void AudioEffectsChanged(object sender, RoutedEventArgs e) => ApplyAudioEffects();

    private void AudioEffectNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => ApplyAudioEffects();

    private async void CaptureAudioNoiseProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_capturingAudioNoiseProfile || !TryGetSelectedAudioClip(out AudioTrackRuntime? track) ||
            track.Project.AudioPath is not string sourcePath || track.Player is not AudioClipPlayer player)
        {
            StatusText.Text = "Select an available audio clip before capturing its noise profile.";
            return;
        }

        double firstValue = AudioNoiseProfileStartBox.Value;
        double endValue = AudioNoiseProfileEndBox.Value;
        if (!double.IsFinite(firstValue) || !double.IsFinite(endValue) ||
            firstValue != Math.Truncate(firstValue) || endValue != Math.Truncate(endValue) ||
            firstValue < 0 || endValue <= firstValue || endValue > player.SourceSampleFrames ||
            endValue - firstValue < AudioNoiseProfile.HopSize)
        {
            StatusText.Text = $"Choose a valid noise-only range of at least {AudioNoiseProfile.HopSize} whole source sample frames.";
            return;
        }

        _capturingAudioNoiseProfile = true;
        CaptureAudioNoiseProfileButton.IsEnabled = false;
        AudioNoiseProfileText.Text = "Analysing the selected source range for a noise profile...";
        try
        {
            AudioNoiseProfile profile = await Task.Run(() => AudioNoiseProfile.Capture(
                sourcePath, (long)firstValue, (long)endValue));
            if (!TryGetSelectedAudioClip(out AudioTrackRuntime? currentTrack) ||
                !ReferenceEquals(currentTrack, track))
            {
                return;
            }

            ApplyAudioClipEdit(selected =>
            {
                selected.Project.Effects = selected.Project.Effects with { NoiseProfile = profile };
                return true;
            }, "Noise profile captured and saved with the project. Enable reduction to apply it.",
                "Could not save the noise profile.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or NotSupportedException or FormatException or OverflowException or
            System.Runtime.InteropServices.COMException)
        {
            AudioNoiseProfileText.Text = $"Noise profile capture failed: {ex.Message}";
            StatusText.Text = "Could not capture the selected noise profile.";
        }
        finally
        {
            _capturingAudioNoiseProfile = false;
            UpdateSelectedAudioTrack();
        }
    }

    private void ApplyAudioEffects()
    {
        if (_isInitialising || _isRestoringProject || !TryGetSelectedAudioClip(out AudioTrackRuntime? track))
        {
            return;
        }

        var settings = new AudioEffectSettings
        {
            Enabled = AudioEffectsEnabled.IsChecked == true,
            HighPassHz = AudioHighPassBox.Value,
            LowPassHz = AudioLowPassBox.Value,
            BassDecibels = AudioBassBox.Value,
            MidDecibels = AudioMidBox.Value,
            TrebleDecibels = AudioTrebleBox.Value,
            CompressorThresholdDecibels = AudioCompressorThresholdBox.Value,
            CompressorRatio = AudioCompressorRatioBox.Value,
            AttackMilliseconds = AudioCompressorAttackBox.Value,
            ReleaseMilliseconds = AudioCompressorReleaseBox.Value,
            GateEnabled = AudioGateEnabled.IsChecked == true,
            GateThresholdDecibels = AudioGateThresholdBox.Value,
            GateAttackMilliseconds = AudioGateAttackBox.Value,
            GateReleaseMilliseconds = AudioGateReleaseBox.Value,
            GateHoldMilliseconds = AudioGateHoldBox.Value,
            NoiseReductionEnabled = AudioNoiseReductionEnabled.IsChecked == true,
            NoiseReductionStrengthDecibels = AudioNoiseReductionStrengthBox.Value,
            NoiseProfile = track.Project.Effects.NoiseProfile,
            LimiterEnabled = AudioLimiterEnabled.IsChecked == true,
            LimiterCeilingDecibels = AudioLimiterCeilingBox.Value,
            LimiterReleaseMilliseconds = AudioLimiterReleaseBox.Value,
            DelayEnabled = AudioDelayEnabled.IsChecked == true,
            DelayMilliseconds = AudioDelayTimeBox.Value,
            DelayFeedback = AudioDelayFeedbackBox.Value,
            DelayWetGain = AudioDelayWetBox.Value,
            ReverbEnabled = AudioReverbEnabled.IsChecked == true,
            ReverbRoomScale = AudioReverbRoomBox.Value,
            ReverbDecaySeconds = AudioReverbDecayBox.Value,
            ReverbDamping = AudioReverbDampingBox.Value,
            ReverbWetGain = AudioReverbWetBox.Value,
        };
        if (!settings.IsValid)
        {
            StatusText.Text = "Use valid effect values and capture a matching noise-only profile before enabling spectral noise reduction.";
            UpdateSelectedAudioTrack();
            return;
        }

        if (settings.NoiseReductionEnabled &&
            (track.Player is not AudioClipPlayer player || track.Project.AudioPath is not string path ||
             settings.NoiseProfile is not AudioNoiseProfile noiseProfile ||
             !noiseProfile.MatchesSource(path, player.SourceSampleRate, player.ChannelCount)))
        {
            StatusText.Text = "Capture a noise-only profile from the selected source before enabling noise reduction.";
            UpdateSelectedAudioTrack();
            return;
        }

        if (settings == track.Project.Effects)
        {
            return;
        }

        ApplyAudioClipEdit(selected =>
        {
            selected.Project.Effects = settings;
            return true;
        }, settings.Enabled ? "Audio processing chain enabled for preview and export." : "Audio effects bypassed; settings retained.",
            "Could not update audio effects.");
    }

    private void ResetAudioEffects_Click(object sender, RoutedEventArgs e) =>
        ApplyAudioClipEdit(selected =>
        {
            if (selected.Project.Effects == new AudioEffectSettings())
            {
                return false;
            }

            selected.Project.Effects = new();
            return true;
        }, "Audio effects reset and bypassed.", "Audio effects are already at their defaults.");
}
