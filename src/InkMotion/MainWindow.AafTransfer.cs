using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace InkMotion;

public sealed partial class MainWindow
{
    private string _aafPython = "";

    private async Task<string?> ChooseAafPythonAsync(bool importing)
    {
        var path = new TextBox { Header = "Trusted Python 3 python.exe (environment with pyaaf2 installed)", Text = _aafPython };
        var browse = new Button { Content = "Choose python.exe…" };
        browse.Click += async (_, _) =>
        {
            StorageFile? file = await PickTimelineFileAsync([".exe"]);
            if (file is not null) path.Text = file.Path;
        };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = (importing ? "This replaces the current project; save edits first. " : "") +
                "AAF supports linked mono PCM WAV audio only, at 24/30/60 fps, with frame-aligned cuts, source trims and gaps. No embedded essence, stereo/multichannel, picture, fades, effects, automation, buses, retiming or sample-based compositions. Names, tags and editor metadata are not transferred. Each clip becomes an independent audio track. Missing WAVs remain offline for relinking. Python and the MIT-licensed pyaaf2 library are optional, user-installed tools; nothing is bundled or downloaded. Only select a trusted interpreter. Conversion has a two-minute limit.",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 540,
        });
        panel.Children.Add(path);
        panel.Children.Add(browse);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot, Title = importing ? "Import linked WAV AAF?" : "Export linked WAV AAF?",
            Content = panel, PrimaryButtonText = importing ? "Replace project" : "Choose export file",
            CloseButtonText = "Cancel",
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        _aafPython = path.Text.Trim();
        return _aafPython;
    }

    private async void ExportAaf_Click(object sender, RoutedEventArgs e)
    {
        if (!PrepareProjectTransfer()) return;
        try
        {
            EditorProject snapshot = CreateProjectSnapshot();
            AafAudioTimeline timeline = await Task.Run(() => AafInterchange.FromProject(snapshot));
            string? python = await ChooseAafPythonAsync(importing: false);
            if (python is null) return;
            var picker = new FileSavePicker { SuggestedFileName = "Audio edit", DefaultFileExtension = ".aaf" };
            picker.FileTypeChoices.Add("AAF linked mono PCM WAV audio", [".aaf"]);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            StorageFile? file = await picker.PickSaveFileAsync();
            if (file is null) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            StatusText.Text = "Writing linked WAV AAF…";
            await Task.Run(() => AafInterchange.Export(timeline, file.Path, python, timeout.Token));
            StatusText.Text = $"AAF audio edit exported · {file.Name}. Keep the linked WAV files; no media is embedded.";
        }
        catch (OperationCanceledException) { StatusText.Text = "AAF export exceeded its two-minute limit."; }
        catch (Exception ex) when (IsProjectFileException(ex) || ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = $"Could not export AAF: {ex.Message}";
        }
    }

    private async void ImportAaf_Click(object sender, RoutedEventArgs e)
    {
        if (IsLanSessionActive)
        {
            StatusText.Text = "Leave the collaboration session before importing another project.";
            return;
        }
        if (!PrepareProjectTransfer()) return;
        StorageFile? file = await PickTimelineFileAsync([".aaf"]);
        if (file is null) return;
        _lanProjectFileOperations++;
        try
        {
            string? python = await ChooseAafPythonAsync(importing: true);
            if (python is null) return;
            string before = EditorProjectSerialiser.Serialise(CreateProjectSnapshot());
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            StatusText.Text = "Reading linked WAV AAF…";
            EditorProject project = await Task.Run(() => AafInterchange.Import(file.Path, python, timeout.Token));
            if (before != EditorProjectSerialiser.Serialise(CreateProjectSnapshot()))
                throw new InvalidDataException("The project changed during AAF import; import again.");
            if (IsLanSessionActive)
                throw new InvalidOperationException("Leave the collaboration session before importing another project.");
            ClearEditingClipboard();
            ClearHistory();
            RestoreProjectState(project);
            string? audioWarning = await LoadAudioTracksAsync(project.AudioTracks);
            await LoadTimelineSourcesAsync();
            _projectFile = null;
            UnsavedIndicator.Visibility = Visibility.Visible;
            StatusText.Text = $"Linked WAV AAF imported · {file.Name}. Save as an InkMotion project." +
                (audioWarning is null ? "" : $" {audioWarning}");
        }
        catch (OperationCanceledException) { StatusText.Text = "AAF import exceeded its two-minute limit."; }
        catch (Exception ex) when (IsProjectFileException(ex) || ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = $"Could not import AAF: {ex.Message}";
        }
        finally { _lanProjectFileOperations--; }
    }
}
