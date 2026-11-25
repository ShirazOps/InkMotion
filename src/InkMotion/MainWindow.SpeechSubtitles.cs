using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InkMotion;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _speechSubtitlesCancellation;
    private bool _speechSubtitlesClosed;

    private void CancelSpeechSubtitles()
    {
        _speechSubtitlesClosed = true;
        _speechSubtitlesCancellation?.Cancel();
    }

    private async void GenerateSpeechSubtitles_Click(object sender, RoutedEventArgs e)
    {
        if (_speechSubtitlesCancellation is not null)
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        IReadOnlyList<SpeechRecogniserInfo> recognisers = SpeechSubtitles.GetInstalledRecognisers();
        if (recognisers.Count == 0)
        {
            StatusText.Text = "No Windows speech recognition language is installed. Add a speech language in Windows Settings > Time & language > Speech.";
            return;
        }

        string uiCulture = System.Globalization.CultureInfo.CurrentUICulture.Name;
        var language = new ComboBox
        {
            Header = "Spoken language",
            ItemsSource = recognisers.Select(info => info.DisplayName).ToArray(),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        int preferred = recognisers.ToList().FindIndex(info =>
            string.Equals(info.CultureName, uiCulture, StringComparison.OrdinalIgnoreCase));
        language.SelectedIndex = Math.Max(0, preferred);
        var panel = new StackPanel { Spacing = 12, MinWidth = 380 };
        panel.Children.Add(language);
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "Transcribes the audible timeline mix on this device with Windows speech recognition. No audio is " +
                "uploaded. Mute or solo tracks first to exclude music and effects. Cues are added to a new subtitle " +
                "track; review the wording and timing, which are approximate.",
        });
        var options = new ContentDialog
        {
            XamlRoot = Content.XamlRoot, Title = "Generate subtitles from speech", Content = panel,
            PrimaryButtonText = "Generate", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        };
        if (await options.ShowAsync() != ContentDialogResult.Primary || language.SelectedIndex < 0 || _speechSubtitlesClosed)
        {
            return;
        }

        string cultureName = recognisers[language.SelectedIndex].CultureName;
        EditorProject snapshot = CreateProjectSnapshot();
        double frameRate = _frameRate;
        using var cancellation = new CancellationTokenSource();
        _speechSubtitlesCancellation = cancellation;
        var stage = new TextBlock { Text = "Rendering the timeline mix…" };
        var bar = new ProgressBar { Maximum = 100, MinWidth = 380 };
        var progressPanel = new StackPanel { Spacing = 12 };
        progressPanel.Children.Add(stage);
        progressPanel.Children.Add(bar);
        var progressDialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot, Title = "Generating subtitles", Content = progressPanel, CloseButtonText = "Cancel",
        };
        bool finished = false;
        progressDialog.Closing += (_, args) =>
        {
            if (!finished)
            {
                cancellation.Cancel();
            }
        };
        var renderProgress = new Progress<double>(value => bar.Value = value * 30);
        var recogniseProgress = new Progress<double>(value =>
        {
            stage.Text = "Recognising speech…";
            bar.Value = 30 + value * 70;
        });
        string wavePath = Path.Combine(Path.GetTempPath(), $"inkmotion-speech-{Guid.NewGuid():N}.wav");
        _ = progressDialog.ShowAsync();
        try
        {
            IReadOnlyList<SubtitleCueText> cues = await Task.Run(() =>
            {
                double duration = SpeechSubtitles.RenderRecognitionMix(snapshot, wavePath, cancellation.Token, renderProgress);
                IReadOnlyList<SpeechWord> words = SpeechSubtitles.Recognise(
                    wavePath, cultureName, duration, cancellation.Token, recogniseProgress);
                return SpeechSubtitles.BuildCues(words);
            });
            finished = true;
            progressDialog.Hide();
            if (_speechSubtitlesClosed || cancellation.IsCancellationRequested)
            {
                return;
            }

            if (cues.Count == 0)
            {
                StatusText.Text = "No speech was recognised in the audible timeline mix.";
                return;
            }

            IReadOnlyList<TimelineTrackProject> tracks = SubtitleSrt.CreateTracks(cues, frameRate);
            BeginEdit();
            for (int index = tracks.Count - 1; index >= 0; index--)
            {
                VideoTimelineEditing.InsertTrack(_videoTracks, tracks[index]);
            }
            MarkEditChanged();
            CommitEdit();
            _selectedTimelineTrackId = tracks[0].Id;
            _selectedTimelineClipId = tracks[0].Clips.OrderBy(clip => clip.StartFrame).First().Id;
            RefreshVideoTimeline();
            SeekToCurrentFrame();
            StatusText.Text = $"Generated {cues.Count} subtitle cue(s) from speech. Review the wording and timing before export.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_speechSubtitlesClosed)
            {
                StatusText.Text = "Subtitle generation was cancelled.";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or InvalidDataException or System.Runtime.InteropServices.COMException or
            NotSupportedException or OverflowException or FormatException)
        {
            if (!_speechSubtitlesClosed)
            {
                StatusText.Text = $"Could not generate subtitles: {ex.Message}";
            }
        }
        finally
        {
            finished = true;
            if (!_speechSubtitlesClosed)
            {
                progressDialog.Hide();
            }

            _speechSubtitlesCancellation = null;
            try
            {
                File.Delete(wavePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Temporary recognition audio is removed on a best-effort basis.
            }
        }
    }
}
