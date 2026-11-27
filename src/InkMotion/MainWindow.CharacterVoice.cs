using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Speech.Synthesis;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace InkMotion;

public sealed partial class MainWindow
{
    private async void GenerateCharacterVoice_Click(object sender, RoutedEventArgs e)
    {
        PausePlayback();
        FinishActiveEdit();

        IReadOnlyList<CharacterVoiceOption> voices;
        try
        {
            voices = CharacterVoiceGenerator.GetInstalledVoices();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            StatusText.Text = $"Could not read the installed Windows voices: {ex.Message}";
            return;
        }

        if (voices.Count == 0)
        {
            StatusText.Text = "No enabled Windows speech voices are installed. Add a speech voice in Windows Settings.";
            return;
        }

        var characterName = new TextBox
        {
            Header = "Character",
            PlaceholderText = "Character name",
            Text = "Character",
            MaxLength = 100,
        };
        var dialogue = new TextBox
        {
            Header = "Dialogue",
            PlaceholderText = "Enter the line to speak",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top,
            Height = 120,
            MaxLength = 10_000,
        };
        var voice = new ComboBox
        {
            Header = "Installed Windows voice",
            ItemsSource = voices,
            DisplayMemberPath = nameof(CharacterVoiceOption.DisplayName),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var rate = new NumberBox
        {
            Header = "Speaking rate (-10 to 10)",
            Minimum = -10,
            Maximum = 10,
            Value = 0,
        };
        var volume = new NumberBox
        {
            Header = "Volume",
            Minimum = 0,
            Maximum = 100,
            Value = 100,
        };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(characterName);
        panel.Children.Add(dialogue);
        panel.Children.Add(voice);
        panel.Children.Add(rate);
        panel.Children.Add(volume);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Generate character voice",
            Content = panel,
            PrimaryButtonText = "Choose WAV location…",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (voice.SelectedItem is not CharacterVoiceOption selectedVoice ||
            !double.IsFinite(rate.Value) || !double.IsFinite(volume.Value) ||
            string.IsNullOrWhiteSpace(dialogue.Text))
        {
            StatusText.Text = "Enter dialogue and choose an installed voice, rate and volume.";
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedFileName = MakeVoiceFileName(characterName.Text),
        };
        picker.FileTypeChoices.Add("Waveform audio", [".wav"]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        StorageFile? destination = await picker.PickSaveFileAsync();
        if (destination is null)
        {
            return;
        }

        int speechRate = (int)Math.Round(rate.Value);
        int speechVolume = (int)Math.Round(volume.Value);
        string speechText = dialogue.Text;
        try
        {
            await Task.Run(() => CharacterVoiceGenerator.WriteWaveFile(
                speechText, selectedVoice.Name, speechRate, speechVolume, destination.Path));
            if (!CanApplyLanBackgroundEdit()) return;
            AddAudioTrack();
            AudioTrackRuntime track = _audioTracks[_selectedAudioTrackIndex];
            LoadAudio(track, destination);
            if (track.LoadError is string loadError)
            {
                ClearHistory();
                UpdateSelectedAudioTrack();
                RefreshAudioTracks();
                StatusText.Text = $"Could not load the generated character voice: {loadError}";
                return;
            }

            track.Project.Title = $"{characterName.Text.Trim()} voice";
            await LoadAudioWaveformAsync(track);
            ClearHistory();
            UpdateSelectedAudioTrack();
            RefreshAudioTracks();
            StatusText.Text = $"Generated a local voice for {characterName.Text.Trim()} at frame {track.Project.StartFrame}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            StatusText.Text = $"Could not generate the character voice: {ex.Message}";
        }
    }

    private static string MakeVoiceFileName(string? characterName)
    {
        string name = string.IsNullOrWhiteSpace(characterName) ? "character" : characterName.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return $"{name}-voice.wav";
    }
}
