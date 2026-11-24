using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;

namespace InkMotion;

public sealed partial class MainWindow
{
    private void SoundEffectsFlyout_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout flyout || flyout.Items.Count > 0)
        {
            return;
        }

        foreach (SoundEffectDefinition effect in SoundEffectLibrary.All)
        {
            var item = new MenuFlyoutItem
            {
                Text = $"{effect.Name}  ·  {effect.DurationSeconds:0.##} s",
                Tag = effect.Id,
            };
            ToolTipService.SetToolTip(item, effect.Description);
            item.Click += AddSoundEffect_Click;
            flyout.Items.Add(item);
        }
    }

    private async void AddSoundEffect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string id })
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        SoundEffectDefinition effect = SoundEffectLibrary.Get(id);
        try
        {
            string path = await Task.Run(() => SoundEffectLibrary.EnsureFile(id));
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            AddAudioTrack();
            AudioTrackRuntime track = _audioTracks[_selectedAudioTrackIndex];
            LoadAudio(track, file);
            if (track.LoadError is string loadError)
            {
                ClearHistory();
                UpdateSelectedAudioTrack();
                RefreshAudioTracks();
                StatusText.Text = $"Could not load the {effect.Name} sound effect: {loadError}";
                return;
            }

            await LoadAudioWaveformAsync(track);
            ClearHistory();
            UpdateSelectedAudioTrack();
            RefreshAudioTracks();
            StatusText.Text = $"Added the {effect.Name} sound effect at frame {track.Project.StartFrame} on a new audio track.";
        }
        catch (Exception ex) when (IsFileAccessException(ex))
        {
            StatusText.Text = $"Could not create the {effect.Name} sound effect: {ex.Message}";
        }
    }
}
