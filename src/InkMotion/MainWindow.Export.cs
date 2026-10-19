using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace InkMotion;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _exportCancellation;
    private bool _checkingAudioEncoders;

    private async void CheckAudioEncoders_Click(object sender, RoutedEventArgs e)
    {
        if (_checkingAudioEncoders || _exportCancellation is not null)
        {
            return;
        }

        _checkingAudioEncoders = true;
        CheckAudioEncodersButton.IsEnabled = false;
        AudioEncoderCapabilitiesText.Text = "Checking installed Windows audio encoder formats...";
        try
        {
            IReadOnlyList<AudioEncoderFormat> formats = await Task.Run(AudioEncoderCapabilities.Discover);
            string[] lines = formats.GroupBy(format => (format.Codec, format.SampleRate, format.Channels))
                .Select(group => $"{group.Key.Codec} | {group.Key.SampleRate:N0} Hz | {group.Key.Channels} ch | " +
                    string.Join(", ", group.Select(format => format.BitRate / 1000)) + " kbps").ToArray();
            AudioEncoderCapabilitiesText.Text = "PCM/float WAV: built in; all listed WAV rates/depths.\n" +
                (lines.Length == 0 ? "No matching Windows MP3/AAC encoder formats found." : string.Join(Environment.NewLine, lines)) +
                "\nFor separate audio files only; MP4 encoder compatibility is checked when exporting video.";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or NotSupportedException)
        {
            AudioEncoderCapabilitiesText.Text = $"Could not query Windows audio encoders: {ex.Message}";
        }
        finally
        {
            _checkingAudioEncoders = false;
            CheckAudioEncodersButton.IsEnabled = _exportCancellation is null;
        }
    }

    private double GetExportFrameRate() => ExportFrameRateBox?.Value ?? 24;

    private void AudioExportCodec_Changed(object sender, SelectionChangedEventArgs e) => UpdateAudioExportOptions();

    private void UpdateAudioExportOptions()
    {
        if (AudioExportRateSelector is null || AudioExportDepthSelector is null || AudioExportBitRateSelector is null)
        {
            return;
        }

        bool wave = AudioExportCodecSelector.SelectedIndex < 2;
        bool floatingPoint = AudioExportCodecSelector.SelectedIndex == 1;
        AudioExportRateSelector.ItemsSource = wave ? AudioExportSettings.SampleRates : new[] { 44100, 48000 };
        AudioExportRateSelector.SelectedItem = 48000;
        AudioExportDepthSelector.IsEnabled = wave && !floatingPoint;
        AudioExportDepthSelector.SelectedIndex = floatingPoint ? 2 : 1;
        AudioExportBitRateSelector.IsEnabled = !wave;
    }

    private async void ExportAudio_Click(object sender, RoutedEventArgs e)
    {
        if (_exportCancellation is not null)
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        if (AudioExportRateSelector.SelectedItem is not int rate ||
            AudioExportDepthSelector.SelectedItem is not ComboBoxItem { Tag: string depth } ||
            AudioExportBitRateSelector.SelectedItem is not ComboBoxItem { Tag: string bitRate })
        {
            ExportStatusText.Visibility = Visibility.Visible;
            ExportStatusText.Text = "Choose the audio output rate, bit depth and bit rate.";
            return;
        }

        var settings = new AudioExportSettings((AudioExportCodec)AudioExportCodecSelector.SelectedIndex, rate,
            int.Parse(depth), int.Parse(bitRate));
        var picker = new FileSavePicker
        {
            SuggestedFileName = _projectFile is null ? "Untitled mix" : Path.GetFileNameWithoutExtension(_projectFile.Name),
            DefaultFileExtension = settings.Extension,
        };
        picker.FileTypeChoices.Add("Audio mix", [settings.Extension]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        StorageFile? file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        EditorProject snapshot = CreateProjectSnapshot();
        using var cancellation = new CancellationTokenSource();
        _exportCancellation = cancellation;
        SetExportRunning(true);
        ExportStatusText.Text = "Rendering audio mix…";
        var progress = new Progress<double>(fraction =>
        {
            ExportProgressBar.IsIndeterminate = false;
            ExportProgressBar.Value = fraction;
            ExportStatusText.Text = $"Rendering audio mix · {fraction:P0}";
        });
        try
        {
            AudioExportResult result = await Task.Run(() => AudioExporter.Export(snapshot, settings, file.Path, progress, cancellation.Token));
            ExportStatusText.Text = $"Exported {Path.GetFileName(file.Path)} · {result.SampleRate:N0} Hz · {result.Channels} channels" +
                (result.Warnings.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, result.Warnings));
            StatusText.Text = "Audio mix exported.";
        }
        catch (OperationCanceledException)
        {
            ExportStatusText.Text = "Audio export cancelled; the partial file was removed.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            ExportStatusText.Text = $"Audio export failed: {ex.Message}";
            StatusText.Text = "Audio export failed.";
        }
        finally
        {
            _exportCancellation = null;
            SetExportRunning(false);
        }
    }

    private void ExportSettings_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        UpdateExportProfileReadout();

    private void ExportFrameRate_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args) =>
        UpdateExportProfileReadout();

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
        double frameRate = GetExportFrameRate();
        if (!double.IsFinite(frameRate) || frameRate < 24 || frameRate > 120)
        {
            ExportStatusText.Visibility = Visibility.Visible;
            ExportStatusText.Text = "Choose an output frame rate from 24 to 120 fps.";
            return;
        }

        if (VideoAudioRateSelector.SelectedItem is not ComboBoxItem { Tag: string audioRate } ||
            VideoAudioBitRateSelector.SelectedItem is not ComboBoxItem { Tag: string audioBitRate })
        {
            ExportStatusText.Visibility = Visibility.Visible;
            ExportStatusText.Text = "Choose the MP4 AAC sample rate and bit rate.";
            return;
        }

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
            VideoExportProfile.Create(resolution, frameRate),
            GetSelectedExportCodec(),
            file.Path,
            InkSurface.ActualHeight,
            ExportHardwareToggle.IsOn,
            ExportQualitySlider.Value / 100,
            int.Parse(audioRate),
            int.Parse(audioBitRate));
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
        ToolbarExportButton.IsEnabled = !running;
        ExportAudioButton.IsEnabled = !running;
        AudioExportOptions.IsEnabled = !running;
        CheckAudioEncodersButton.IsEnabled = !running && !_checkingAudioEncoders;
        ExportResolutionSelector.IsEnabled = !running;
        ExportCodecSelector.IsEnabled = !running;
        ExportFrameRateBox.IsEnabled = !running;
        ExportHardwareToggle.IsEnabled = !running;
        ExportQualitySlider.IsEnabled = !running;
        VideoAudioRateSelector.IsEnabled = !running;
        VideoAudioBitRateSelector.IsEnabled = !running;
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
