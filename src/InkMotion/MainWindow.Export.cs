using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace InkMotion;

public sealed partial class MainWindow
{
    private bool _submittingExport;
    private bool _hasActiveExports;
    private bool _checkingAudioEncoders;

    private async void CheckAudioEncoders_Click(object sender, RoutedEventArgs e)
    {
        if (_checkingAudioEncoders || _submittingExport || _hasActiveExports)
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
            CheckAudioEncodersButton.IsEnabled = !_submittingExport && !_hasActiveExports;
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
        if (_submittingExport || _hasActiveExports)
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
        await SubmitExportJobAsync(snapshot, file.Path, settings, null);
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
        if (_submittingExport || _hasActiveExports ||
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

        var settings = new ExportVideoSettings(
            VideoExportProfile.Create(resolution, frameRate),
            GetSelectedExportCodec(),
            InkSurface.ActualHeight,
            ExportHardwareToggle.IsOn,
            ExportQualitySlider.Value / 100,
            int.Parse(audioRate),
            int.Parse(audioBitRate));
        await SubmitExportJobAsync(CreateProjectSnapshot(), file.Path, null, settings);
    }

    private void CancelExport_Click(object sender, RoutedEventArgs e)
    {
        if (ExportJobsSelector.SelectedItem is not ExportJobStatus { IsActive: true } job)
        {
            return;
        }

        try
        {
            _exportJobStore.Cancel(job.Id);
            CancelExportButton.IsEnabled = false;
            ExportStatusText.Text = "Cancellation requested. The worker will stop and remove its partial output.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            ExportStatusText.Text = $"Could not request cancellation: {ex.Message}";
        }
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
