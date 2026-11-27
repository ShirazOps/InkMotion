using Microsoft.UI.Xaml;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace InkMotion;

public sealed partial class MainWindow
{
    private bool PrepareProjectTransfer()
    {
        if (_compoundContexts.Count > 0 || _switchingCompound || IsAudioRecordingInProgress ||
            _screenRecorderWindow?.IsRecording == true)
        {
            StatusText.Text = "Return to the main timeline and stop recording before transferring a project.";
            return false;
        }
        PausePlayback();
        FinishActiveEdit();
        return true;
    }

    private async void PackageProject_Click(object sender, RoutedEventArgs e)
    {
        if (!PrepareProjectTransfer()) return;
        var picker = new FileSavePicker { SuggestedFileName = "Project", DefaultFileExtension = ProjectPackage.Extension };
        picker.FileTypeChoices.Add("InkMotion project with linked media", [ProjectPackage.Extension]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        StorageFile? file = await picker.PickSaveFileAsync();
        if (file is null) return;
        try
        {
            EditorProject snapshot = CreateProjectSnapshot();
            string directory = _projectFile is null ? Environment.CurrentDirectory : Path.GetDirectoryName(_projectFile.Path)!;
            StatusText.Text = "Packaging linked media…";
            await Task.Run(() => ProjectPackage.Create(snapshot, file.Path, directory));
            StatusText.Text = $"Package saved · {file.Name}. Open it to extract into a new folder; the current project is unchanged.";
        }
        catch (Exception ex) when (IsProjectFileException(ex))
        {
            StatusText.Text = $"Could not package project: {ex.Message}";
        }
    }
}
