using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

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
        SpeechModelStore modelStore = SpeechModelStore.Default;
        bool whisperInstalled = modelStore.Whisper is not null;
        if (recognisers.Count == 0 && !whisperInstalled)
        {
            StatusText.Text = "No Windows speech recognition language is installed. Add a speech language in Windows Settings > Time & language > Speech.";
            return;
        }

        IReadOnlyList<SpeechRecogniserInfo> languages = recognisers.Count > 0
            ? recognisers
            : [new SpeechRecogniserInfo("en-US", "English (Whisper model default)")];
        string uiCulture = System.Globalization.CultureInfo.CurrentUICulture.Name;
        var language = new ComboBox
        {
            Header = "Spoken language",
            ItemsSource = languages.Select(info => info.DisplayName).ToArray(),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        int preferred = languages.ToList().FindIndex(info =>
            string.Equals(info.CultureName, uiCulture, StringComparison.OrdinalIgnoreCase));
        language.SelectedIndex = Math.Max(0, preferred);
        var panel = new StackPanel { Spacing = 12, MinWidth = 380 };
        panel.Children.Add(language);
        var whisperLanguage = new TextBox
        {
            Header = "Whisper language code (ISO 639-1)",
            Text = new System.Globalization.CultureInfo(uiCulture).TwoLetterISOLanguageName,
            IsEnabled = whisperInstalled,
        };
        panel.Children.Add(whisperLanguage);
        var whisperToggle = new CheckBox
        {
            Content = "Use imported multilingual Whisper tiny model",
            IsChecked = whisperInstalled,
            IsEnabled = whisperInstalled,
        };
        var diarisationToggle = new CheckBox
        {
            Content = "Label speakers (requires imported local models)",
            IsChecked = false,
            IsEnabled = modelStore.Diarisation is not null,
        };
        var whisperImport = new Button { Content = "Import Whisper model files…" };
        whisperImport.Click += ImportWhisperSpeechModel_Click;
        var diarisationImport = new Button { Content = "Import speaker-label models…" };
        diarisationImport.Click += ImportSpeechDiarisationModels_Click;
        var modelStatus = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"Whisper: {(whisperInstalled ? "installed" : "not installed")}; speaker labels: " +
                $"{(modelStore.Diarisation is null ? "not installed" : "installed")}. Import extracted ONNX files " +
                "from a model you have obtained; models run on this device and are never downloaded by InkMotion.",
        };
        panel.Children.Add(whisperToggle);
        panel.Children.Add(whisperImport);
        panel.Children.Add(diarisationToggle);
        panel.Children.Add(diarisationImport);
        panel.Children.Add(modelStatus);
        var confidence = new Slider { Minimum = 0, Maximum = 1, StepFrequency = 0.05, Value = 0.25 };
        var confidenceLabel = new TextBlock { Text = "Ignore Windows-recogniser output below 25% confidence" };
        confidence.ValueChanged += (_, args) =>
            confidenceLabel.Text = $"Ignore Windows-recogniser output below {args.NewValue:P0} confidence";
        whisperToggle.Checked += (_, _) =>
        {
            confidence.IsEnabled = false;
            whisperLanguage.IsEnabled = true;
        };
        whisperToggle.Unchecked += (_, _) =>
        {
            confidence.IsEnabled = true;
            whisperLanguage.IsEnabled = false;
        };
        confidence.IsEnabled = !whisperInstalled;
        panel.Children.Add(confidenceLabel);
        panel.Children.Add(confidence);
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "Transcribes the audible timeline mix on this device. Whisper uses the imported model; the " +
                "fallback is Windows speech recognition. No audio is uploaded. Mute or solo tracks first to exclude " +
                "music and effects. Windows-recogniser confidence filtering and pause-aware punctuation are " +
                "available. Local speech models support up to one hour per run. Generated cues remain editable and " +
                "should be reviewed.",
        });
        whisperImport.Tag = (WhisperToggle: whisperToggle, DiarisationToggle: diarisationToggle,
            Status: modelStatus, WhisperLanguage: whisperLanguage);
        diarisationImport.Tag = whisperImport.Tag;
        var options = new ContentDialog
        {
            XamlRoot = Content.XamlRoot, Title = "Generate subtitles from speech", Content = panel,
            PrimaryButtonText = "Generate", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        };
        if (await options.ShowAsync() != ContentDialogResult.Primary || language.SelectedIndex < 0 || _speechSubtitlesClosed)
        {
            return;
        }

        string cultureName = languages[language.SelectedIndex].CultureName;
        bool useWhisper = whisperToggle.IsChecked == true;
        bool useDiarisation = diarisationToggle.IsChecked == true;
        string whisperLanguageCode = whisperLanguage.Text.Trim().ToLowerInvariant();
        if (useWhisper && (whisperLanguageCode.Length is < 2 or > 3 ||
            whisperLanguageCode.Any(character => !char.IsAsciiLetter(character))))
        {
            StatusText.Text = "Enter a valid Whisper language code, such as en or fr.";
            return;
        }

        if (!useWhisper && recognisers.Count == 0)
        {
            StatusText.Text = "Choose Whisper or install a Windows speech recognition language.";
            return;
        }

        double minimumConfidence = confidence.Value;
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
                IReadOnlyList<SpeechWord> words = useWhisper
                    ? SpeechSubtitles.RecogniseWithWhisper(wavePath, whisperLanguageCode,
                        cancellation.Token, recogniseProgress)
                    : SpeechSubtitles.Recognise(wavePath, cultureName, duration, cancellation.Token, recogniseProgress);
                if (useDiarisation)
                {
                    IReadOnlyList<SpeechSpeakerSegment> speakers =
                        SpeechSubtitles.Diarise(wavePath, cancellation.Token, recogniseProgress);
                    words = SpeechSubtitles.AssignSpeakers(words, speakers);
                }

                return SpeechSubtitles.BuildCues(words, new SpeechCueOptions { MinimumConfidence = minimumConfidence });
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

    private async void ImportWhisperSpeechModel_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".onnx");
        picker.FileTypeFilter.Add(".txt");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0) return;
        try
        {
            IReadOnlyList<string> hashes = await SpeechModelStore.Default.ImportWhisperFilesAsync(
                files.Select(file => file.Path).ToArray());
            StatusText.Text = "Imported Whisper model files. SHA-256: " + string.Join("; ", hashes);
            RefreshSpeechModelOptions(sender);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText.Text = $"Could not import the Whisper model: {ex.Message}";
        }
    }

    private async void ImportSpeechDiarisationModels_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".onnx");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0) return;
        try
        {
            IReadOnlyList<string> hashes = await SpeechModelStore.Default.ImportDiarisationFilesAsync(
                files.Select(file => file.Path).ToArray());
            StatusText.Text = "Imported speaker-label models. SHA-256: " + string.Join("; ", hashes);
            RefreshSpeechModelOptions(sender);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText.Text = $"Could not import the speaker-label models: {ex.Message}";
        }
    }

    private static void RefreshSpeechModelOptions(object sender)
    {
        if (sender is not Button { Tag: ValueTuple<CheckBox, CheckBox, TextBlock, TextBox> options }) return;
        (CheckBox whisper, CheckBox diarisation, TextBlock status, TextBox language) = options;
        whisper.IsEnabled = SpeechModelStore.Default.Whisper is not null;
        if (whisper.IsEnabled) whisper.IsChecked = true;
        language.IsEnabled = whisper.IsEnabled;
        diarisation.IsEnabled = SpeechModelStore.Default.Diarisation is not null;
        status.Text = $"Whisper: {(whisper.IsEnabled ? "installed" : "not installed")}; speaker labels: " +
            $"{(diarisation.IsEnabled ? "installed" : "not installed")}. Imported models run on this device; " +
            "InkMotion does not download them.";
    }
}
