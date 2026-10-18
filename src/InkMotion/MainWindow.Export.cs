using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace InkMotion;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _exportCancellation;

    private VideoExportCodec GetSelectedExportCodec() =>
        ExportCodecSelector?.SelectedItem is ComboBoxItem { Tag: string tag } &&
        Enum.TryParse(tag, out VideoExportCodec codec)
            ? codec
            : VideoExportCodec.H264;

    private async void ExportVideo_Click(object sender, RoutedEventArgs e)
    {
        if (_exportCancellation is not null ||
            ExportResolutionSelector.SelectedItem is not ComboBoxItem { Tag: string resolutionName } ||
            !Enum.TryParse(resolutionName, out ExportResolution resolution))
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        var picker = new FileSavePicker
        {
            SuggestedFileName = _projectFile is null ? "Untitled" : Path.GetFileNameWithoutExtension(_projectFile.Name),
            DefaultFileExtension = ".mp4",
        };
        picker.FileTypeChoices.Add("MPEG-4 video", [".mp4"]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        StorageFile? file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        var request = new VideoExportRequest(
            CreateProjectSnapshot(),
            VideoExportProfile.Create(resolution, _frameRate),
            GetSelectedExportCodec(),
            file.Path,
            InkSurface.ActualHeight);
        using var cancellation = new CancellationTokenSource();
        _exportCancellation = cancellation;
        SetExportRunning(true);
        var progress = new Progress<VideoExportProgress>(update =>
        {
            ExportProgressBar.IsIndeterminate = update.FrameCount == 0;
            ExportProgressBar.Value = update.Fraction;
            ExportStatusText.Text = update.FrameCount == 0
                ? $"{update.Stage}…"
                : $"{update.Stage} · frame {update.Frame:N0} of {update.FrameCount:N0} ({update.Fraction:P0})";
        });

        try
        {
            VideoExportResult result = await Task.Run(
                () => VideoExporter.Export(request, progress, cancellation.Token),
                cancellation.Token);
            string encoder = result.UsedHardwareEncoder ? "GPU encoder" : "software encoder";
            string summary = $"Exported {Path.GetFileName(result.OutputPath)} · {result.DurationSeconds:0.0} s · {encoder}";
            ExportStatusText.Text = result.Warnings.Count == 0
                ? summary
                : $"{summary}{Environment.NewLine}{string.Join(Environment.NewLine, result.Warnings)}";
            StatusText.Text = summary;
        }
        catch (OperationCanceledException)
        {
            ExportStatusText.Text = "Export cancelled; the partial file was removed.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or AudioEncoderUnavailableException or System.Runtime.InteropServices.COMException)
        {
            ExportStatusText.Text = $"Export failed: {ex.Message}";
            StatusText.Text = "Export failed";
        }
        finally
        {
            _exportCancellation = null;
            SetExportRunning(false);
        }
    }

    private void CancelExport_Click(object sender, RoutedEventArgs e)
    {
        _exportCancellation?.Cancel();
        CancelExportButton.IsEnabled = false;
        ExportStatusText.Text = "Cancelling…";
    }

    private void SetExportRunning(bool running)
    {
        ExportVideoButton.IsEnabled = !running;
        ExportResolutionSelector.IsEnabled = !running;
        ExportCodecSelector.IsEnabled = !running;
        CancelExportButton.IsEnabled = running;
        CancelExportButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        ExportProgressBar.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        ExportStatusText.Visibility = Visibility.Visible;
        if (running)
        {
            ExportProgressBar.Value = 0;
            ExportProgressBar.IsIndeterminate = true;
            ExportStatusText.Text = "Starting export…";
        }
    }
}
