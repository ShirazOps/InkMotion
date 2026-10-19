using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InkMotion;

public sealed partial class MainWindow
{
    private bool HasSoloAudio => _audioTracks.Any(track => track.Project.Solo);

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
}
