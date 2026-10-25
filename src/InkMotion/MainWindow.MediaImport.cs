using Windows.Storage;

namespace InkMotion;

public sealed partial class MainWindow
{
    private async Task<StorageFile?> PrepareMediaImportAsync(StorageFile file)
    {
        if (FFmpegImportToggle.IsChecked != true || !FFmpegMediaImport.CanPrepare(file.Path)) return file;
        try
        {
            string executable = FFmpegPathBox.Text.Trim();
            if (_ffmpegCapabilities is null ||
                !string.Equals(executable, _ffmpegCapabilities.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("On Export, select a local FFmpeg executable and check its capabilities before using compatibility imports.");
            StatusText.Text = $"Preparing compatibility import for {file.Name}. Editing copies are retained in LocalAppData\\InkMotion\\ImportedMedia.";
            string before = EditorProjectSerialiser.Serialise(CreateProjectSnapshot());
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "InkMotion", "ImportedMedia");
            using var timeout = new CancellationTokenSource(TimeSpan.FromHours(2));
            string path = await Task.Run(() => FFmpegMediaImport.Prepare(file.Path, executable, directory, timeout.Token));
            if (before != EditorProjectSerialiser.Serialise(CreateProjectSnapshot()))
                throw new InvalidOperationException("The project changed while preparing media. Import again using the current project.");
            return path == file.Path ? file : await StorageFile.GetFileFromPathAsync(path);
        }
        catch (Exception ex) when (IsFileAccessException(ex) || ex is InvalidOperationException or
            System.ComponentModel.Win32Exception or OperationCanceledException or System.Text.Json.JsonException)
        {
            StatusText.Text = $"Could not import {file.Name}: {ex.Message}";
            return null;
        }
    }
}
