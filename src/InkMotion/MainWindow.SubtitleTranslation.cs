using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InkMotion;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _subtitleTranslationCancellation;
    private bool _subtitleTranslationClosed;
    private string _translationEndpoint = "http://127.0.0.1:5273/v1";
    private string _translationModel = string.Empty;
    private string _translationSourceLanguage = string.Empty;
    private string _translationTargetLanguage = string.Empty;

    private void CancelSubtitleTranslation()
    {
        _subtitleTranslationClosed = true;
        _subtitleTranslationCancellation?.Cancel();
    }

    private async void TranslateSubtitles_Click(object sender, RoutedEventArgs e)
    {
        if (_subtitleTranslationCancellation is not null || Content.XamlRoot is null)
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        TimelineTrackProject[] sources = SelectedTimelineTrack is { Kind: TimelineTrackKind.Subtitle, Clips.Count: > 0 } selected
            ? [selected]
            : _videoTracks.Where(track => track.Kind == TimelineTrackKind.Subtitle && track.Clips.Count > 0).ToArray();
        if (sources.Length == 0)
        {
            StatusText.Text = "There are no subtitle cues to translate.";
            return;
        }

        var panel = new StackPanel { Spacing = 12, MinWidth = 420 };
        var endpoint = new TextBox { Header = "Local server address (OpenAI-compatible)", Text = _translationEndpoint };
        var model = new ComboBox
        {
            Header = "Model", IsEditable = true, Text = _translationModel, HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = "Model name the local server already has",
        };
        var findModels = new Button { Content = "Find models on this server" };
        var modelStatus = new TextBlock { TextWrapping = TextWrapping.Wrap };
        findModels.Click += async (_, _) =>
        {
            findModels.IsEnabled = false;
            modelStatus.Text = "Asking the local server…";
            try
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                IReadOnlyList<string> models = await LocalChatSubtitleTranslator.ListModelsAsync(
                    new Uri(endpoint.Text.Trim()), null, cancellation.Token);
                string current = model.Text;
                model.ItemsSource = models.ToArray();
                model.Text = current.Length > 0 ? current : models.FirstOrDefault() ?? string.Empty;
                modelStatus.Text = models.Count > 0
                    ? $"The server reports {models.Count} model(s). Some servers, such as Foundry Local, also list catalogue models that are not downloaded; choose one already installed and loaded. InkMotion never downloads models."
                    : "The server reports no models. Add one with the server's own tools first.";
            }
            catch (Exception ex) when (ex is UriFormatException or ArgumentException or InvalidDataException or
                HttpRequestException or OperationCanceledException)
            {
                modelStatus.Text = $"Could not list models: {ex.Message}";
            }
            finally
            {
                findModels.IsEnabled = true;
            }
        };
        var sourceLanguage = new TextBox
        {
            Header = "Source language (optional)", Text = _translationSourceLanguage, PlaceholderText = "Detect automatically",
        };
        var targetLanguage = new TextBox
        {
            Header = "Translate into", Text = _translationTargetLanguage, PlaceholderText = "For example French or Japanese",
        };
        panel.Children.Add(endpoint);
        panel.Children.Add(model);
        panel.Children.Add(findModels);
        panel.Children.Add(modelStatus);
        panel.Children.Add(sourceLanguage);
        panel.Children.Add(targetLanguage);
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = (sources.Length == 1 ? $"Translates {sources[0].Name}" : $"Translates all {sources.Length} subtitle tracks") +
                " into new subtitle tracks with the same timings, positions and styles; the originals are kept. " +
                "Translation uses a server that is already running on this computer, such as Foundry Local, Ollama, " +
                "LM Studio or llama.cpp, with a model you have already installed. Subtitle text is sent only to that " +
                "local address and nothing is downloaded. Word timing and styling are not carried over. Review the " +
                "translations before export.",
        });
        var options = new ContentDialog
        {
            XamlRoot = Content.XamlRoot, Title = "Translate subtitles", Content = new ScrollViewer { Content = panel },
            PrimaryButtonText = "Translate", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        };
        if (await options.ShowAsync() != ContentDialogResult.Primary || _subtitleTranslationClosed)
        {
            return;
        }

        _translationEndpoint = endpoint.Text.Trim();
        _translationModel = model.Text.Trim();
        _translationSourceLanguage = sourceLanguage.Text.Trim();
        _translationTargetLanguage = targetLanguage.Text.Trim();
        LocalChatSubtitleTranslator translator;
        try
        {
            translator = new LocalChatSubtitleTranslator(new Uri(_translationEndpoint), _translationModel,
                _translationTargetLanguage, _translationSourceLanguage);
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            StatusText.Text = $"Could not translate subtitles: {ex.Message}";
            return;
        }

        using (translator)
        using (var cancellation = new CancellationTokenSource())
        {
            _subtitleTranslationCancellation = cancellation;
            var stage = new TextBlock { Text = "Translating subtitle cues…" };
            var bar = new ProgressBar { MinWidth = 380 };
            var progressPanel = new StackPanel { Spacing = 12 };
            progressPanel.Children.Add(stage);
            progressPanel.Children.Add(bar);
            var progressDialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot, Title = "Translating subtitles", Content = progressPanel, CloseButtonText = "Cancel",
            };
            bool finished = false;
            progressDialog.Closing += (_, _) =>
            {
                if (!finished)
                {
                    cancellation.Cancel();
                }
            };
            var progress = new Progress<(int Completed, int Total)>(value =>
            {
                bar.Maximum = Math.Max(1, value.Total);
                bar.Value = value.Completed;
                stage.Text = $"Translated {value.Completed} of {value.Total} cue(s)…";
            });
            _ = progressDialog.ShowAsync();
            try
            {
                SubtitleTranslationResult result = await SubtitleTranslation.TranslateTracksAsync(sources,
                    _translationTargetLanguage, translator, progress, cancellation.Token);
                finished = true;
                progressDialog.Hide();
                if (_subtitleTranslationClosed || cancellation.IsCancellationRequested)
                {
                    return;
                }

                BeginEdit();
                for (int index = result.Tracks.Count - 1; index >= 0; index--)
                {
                    VideoTimelineEditing.InsertTrack(_videoTracks, result.Tracks[index]);
                }
                MarkEditChanged();
                CommitEdit();
                _selectedTimelineTrackId = result.Tracks[0].Id;
                _selectedTimelineClipId = result.Tracks[0].Clips.OrderBy(clip => clip.StartFrame).First().Id;
                RefreshVideoTimeline();
                SeekToCurrentFrame();
                int cueCount = result.Tracks.Sum(track => track.Clips.Count);
                StatusText.Text = $"Translated {cueCount} subtitle cue(s) into {_translationTargetLanguage} on new track(s). " +
                    "Review the translations before export.";
                await ShowSubtitleWarningsAsync("Translated subtitles with changes", result.Warnings);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                if (!_subtitleTranslationClosed)
                {
                    StatusText.Text = "Subtitle translation was cancelled. No tracks were added.";
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException or HttpRequestException or
                OperationCanceledException)
            {
                if (!_subtitleTranslationClosed)
                {
                    StatusText.Text = ex is OperationCanceledException
                        ? "The local translation server did not respond in time. No tracks were added."
                        : $"Could not translate subtitles: {ex.Message} No tracks were added.";
                }
            }
            finally
            {
                finished = true;
                if (!_subtitleTranslationClosed)
                {
                    progressDialog.Hide();
                }

                _subtitleTranslationCancellation = null;
            }
        }
    }
}
