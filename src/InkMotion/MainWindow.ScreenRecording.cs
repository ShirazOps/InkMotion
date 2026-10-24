using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;

namespace InkMotion;

public sealed partial class MainWindow
{
    private ScreenRecorderWindow? _screenRecorderWindow;
    private bool _closingAfterScreenRecording;

    private void ScreenRecorder_Click(object sender, RoutedEventArgs e)
    {
        if (IsAudioRecordingInProgress)
        {
            StatusText.Text = "Stop the current audio take before opening screen recording.";
            return;
        }
        PausePlayback();
        if (_screenRecorderWindow is null)
        {
            _screenRecorderWindow = new ScreenRecorderWindow(OpenScreenRecordingAsync, IsRecordingSourceInUse);
            _screenRecorderWindow.Closed += (_, _) => _screenRecorderWindow = null;
        }
        _screenRecorderWindow.Activate();
    }

    private bool IsRecordingSourceInUse(string directory)
    {
        string prefix = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        return _videoTracks.SelectMany(track => track.Clips).Select(clip => clip.SourcePath)
            .Concat(_audioTracks.Select(track => track.Project.AudioPath))
            .Any(path => path is not null && Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private async Task OpenScreenRecordingAsync(string path)
    {
        if (IsAudioRecordingInProgress)
            throw new InvalidOperationException("Stop the audio take before opening a recording project.");
        if (UnsavedIndicator.Visibility == Visibility.Visible)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot, Title = "Open the recorded project?",
                Content = "Your current project has unsaved edits. Save it first, or explicitly replace it. The recording is already saved and can be opened later from Screen recorder.",
                PrimaryButtonText = "Replace current project", CloseButtonText = "Keep editing",
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }
        PausePlayback();
        FinishActiveEdit();
        StorageFile file = await StorageFile.GetFileFromPathAsync(path);
        EditorProject project = EditorProjectSerialiser.Deserialise(await FileIO.ReadTextAsync(file));
        ClearEditingClipboard();
        ClearHistory();
        RestoreProjectState(project);
        string? audioWarning = await LoadAudioTracksAsync(project.AudioTracks);
        _projectFile = file;
        UnsavedIndicator.Visibility = Visibility.Collapsed;
        string? videoWarning = await LoadTimelineSourcesAsync();
        ShowWorkspace("Video");
        StatusText.Text = $"Recording opened as an editable project. {audioWarning} {videoWarning}".Trim();
        Activate();
    }

    private async void MainWindow_ScreenRecordingClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closingAfterScreenRecording || _screenRecorderWindow is null) return;
        args.Cancel = true;
        await _screenRecorderWindow.StopAndCloseAsync();
        _closingAfterScreenRecording = true;
        Close();
    }
}
