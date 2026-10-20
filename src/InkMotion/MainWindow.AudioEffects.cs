using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InkMotion;

public sealed partial class MainWindow
{
    private void UpdateAudioEffectsInspector(AudioTrackProject? track)
    {
        AudioEffectsControls.IsEnabled = track is not null;
        AudioEffectSettings settings = track?.Effects ?? new();
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
            StatusText.Text = "Use valid effect values: filters off at 0 Hz or 20-20000 Hz, high-pass below low-pass; reverb scale 0.5-2, decay 0.1-10 s, damping 0-0.95; delay 1-2000 ms, feedback 0-0.95; wet gains 0-1.";
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
