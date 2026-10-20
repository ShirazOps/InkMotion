using Microsoft.UI.Xaml;

namespace InkMotion;

public sealed partial class MainWindow
{
    private void UpdateAudioSourceRangeInspector()
    {
        bool available = TryGetSelectedAudioClip(out AudioTrackRuntime? track) && track.Player is not null;
        AudioSourceRangeActions.IsEnabled = available;
        if (!available || track?.Player is not AudioClipPlayer player)
        {
            AudioSourceSamplesText.Text = "Load an audio clip to set sample-based source endpoints.";
            return;
        }

        AudioSourceFirstSampleBox.Value = Math.Round(track.Project.SourceInSeconds * player.SourceSampleRate);
        AudioSourceEndSampleBox.Value = track.Project.SourceOutSeconds is double end
            ? Math.Round(end * player.SourceSampleRate) : player.SourceSampleFrames;
        AudioSourceSamplesText.Text = $"{player.SourceSampleRate:N0} Hz · {player.SourceSampleFrames:N0} source sample frames. " +
            "Zero-based start, end excluded; one sample frame includes all source channels.";
    }

    private void ApplyAudioSourceSamples_Click(object sender, RoutedEventArgs e)
    {
        double first = AudioSourceFirstSampleBox.Value;
        double end = AudioSourceEndSampleBox.Value;
        if (!double.IsFinite(first) || !double.IsFinite(end) || first != Math.Truncate(first) ||
            end != Math.Truncate(end) || first < 0 || end > 9007199254740991d || end <= first)
        {
            StatusText.Text = "Use whole source sample-frame numbers: start at 0 or later and an excluded end after the start.";
            return;
        }

        ApplyAudioClipEdit(track => track.Player is AudioClipPlayer player &&
            AudioClipEditing.SetSourceSampleRange(track.Project, (long)first, (long)end,
                player.SourceSampleFrames, player.SourceSampleRate),
            "Sample-based source range applied at the existing timeline start. Other clips and fade values are unchanged.",
            "Source range unchanged. Choose available source samples and enough tempo-adjusted duration for the existing fades.");
    }
}
