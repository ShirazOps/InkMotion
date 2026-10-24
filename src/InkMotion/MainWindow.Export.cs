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
    private bool _checkingVideoEncoders;
    private bool _measuringVideoPerformance;

    private async void CheckVideoEncoders_Click(object sender, RoutedEventArgs e)
    {
        if (_checkingVideoEncoders || _checkingAudioEncoders || _measuringVideoPerformance ||
            _submittingExport || _hasActiveExports)
        {
            return;
        }

        _checkingVideoEncoders = true;
        CheckVideoEncodersButton.IsEnabled = false;
        MeasureVideoPerformanceButton.IsEnabled = false;
        VideoEncoderCapabilitiesText.Text = "Encoding one-frame software probes for H.264/HEVC at 720p–4K and common 24–120 fps rates. No user files are changed.";
        try
        {
            IReadOnlyList<VideoExportCapability> capabilities =
                await Task.Run(VideoExportCapabilities.DiscoverSoftwareCapabilities);
            VideoEncoderCapabilitiesText.Text = string.Join(
                Environment.NewLine,
                capabilities.Select(capability =>
                    $"{(capability.SoftwareSupported ? "Available" : "Unavailable")} · " +
                    $"{ExportPlanning.CodecName(capability.Codec)} · " +
                    $"{capability.Profile.Width}×{capability.Profile.Height} · " +
                    $"{capability.Profile.FrameRate:0.###} fps" +
                    (capability.SoftwareFailure is null ? string.Empty : $" · {capability.SoftwareFailure}"))) +
                Environment.NewLine +
                "This checks real software encode/finalise support for these profiles on this PC. It does not certify GPU encoder use or sustained-speed/resolution performance.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            VideoEncoderCapabilitiesText.Text = $"Could not run video encoder capability probes: {ex.Message}";
        }
        finally
        {
            _checkingVideoEncoders = false;
            CheckVideoEncodersButton.IsEnabled = !_submittingExport && !_hasActiveExports;
            MeasureVideoPerformanceButton.IsEnabled = !_submittingExport && !_hasActiveExports;
        }
    }

    private async void CheckAudioEncoders_Click(object sender, RoutedEventArgs e)
    {
        if (_checkingAudioEncoders || _checkingVideoEncoders || _measuringVideoPerformance ||
            _submittingExport || _hasActiveExports)
        {
            return;
        }

        _checkingAudioEncoders = true;
        CheckAudioEncodersButton.IsEnabled = false;
        MeasureVideoPerformanceButton.IsEnabled = false;
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
            MeasureVideoPerformanceButton.IsEnabled = !_submittingExport && !_hasActiveExports;
        }
    }

    private async void MeasureVideoPerformance_Click(object sender, RoutedEventArgs e)
    {
        if (_measuringVideoPerformance || _checkingVideoEncoders || _checkingAudioEncoders ||
            _submittingExport || _hasActiveExports ||
            ExportResolutionSelector.SelectedItem is not ComboBoxItem { Tag: string resolutionName } ||
            !Enum.TryParse(resolutionName, out ExportResolution resolution) ||
            !double.IsFinite(GetExportFrameRate()) || GetExportFrameRate() < 24 || GetExportFrameRate() > 120)
        {
            return;
        }

        _measuringVideoPerformance = true;
        MeasureVideoPerformanceButton.IsEnabled = false;
        CheckVideoEncodersButton.IsEnabled = false;
        CheckAudioEncodersButton.IsEnabled = false;
        ExportVideoButton.IsEnabled = false;
        ExportAudioButton.IsEnabled = false;
        ToolbarExportButton.IsEnabled = false;
        ToolbarExportAudioButton.IsEnabled = false;
        VideoEncoderCapabilitiesText.Text =
            "Measuring a two-second synthetic sample for the selected profile. This writes only a temporary file.";
        try
        {
            VideoExportProfile profile = VideoExportProfile.Create(resolution, GetExportFrameRate());
            VideoExportCodec codec = GetSelectedExportCodec();
            bool preferHardware = ExportHardwareToggle.IsOn;
            VideoExportPerformance result = await Task.Run(() =>
                VideoExportCapabilities.MeasureProfilePerformance(profile, codec, preferHardware));
            VideoEncoderCapabilitiesText.Text =
                $"Synthetic sample: {result.FrameCount} frames at {profile.Width}×{profile.Height}, " +
                $"{profile.FrameRate:0.###} fps, {ExportPlanning.CodecName(codec)}. " +
                $"Encoded in {result.ElapsedSeconds:0.00}s ({result.FramesPerSecond:0.0} fps); " +
                $"{result.OutputBytes / 1_000_000d:0.0} MB. " +
                $"Hardware-transform preference {(result.HardwareTransformsPreferred ? "enabled" : "disabled")}. " +
                "Performance is this run's synthetic result; Media Foundation does not identify or certify the actual encoder hardware.";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or
            NotSupportedException or IOException or ArgumentException or OverflowException)
        {
            VideoEncoderCapabilitiesText.Text = $"Could not measure the selected video profile: {ex.Message}";
        }
        finally
        {
            _measuringVideoPerformance = false;
            MeasureVideoPerformanceButton.IsEnabled = !_submittingExport && !_hasActiveExports;
            CheckVideoEncodersButton.IsEnabled = !_submittingExport && !_hasActiveExports && !_checkingVideoEncoders;
            CheckAudioEncodersButton.IsEnabled = !_submittingExport && !_hasActiveExports && !_checkingAudioEncoders;
            ExportVideoButton.IsEnabled = !_submittingExport;
            ExportAudioButton.IsEnabled = !_submittingExport;
            ToolbarExportButton.IsEnabled = !_submittingExport;
            ToolbarExportAudioButton.IsEnabled = !_submittingExport;
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
        if (_submittingExport || _measuringVideoPerformance ||
            _checkingVideoEncoders || _checkingAudioEncoders)
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
        if (_submittingExport || _measuringVideoPerformance ||
            _checkingVideoEncoders || _checkingAudioEncoders ||
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
            ExportStatusText.Text = "Cancellation requested. Queued jobs stop before rendering; active jobs clean up partial output.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            ExportStatusText.Text = $"Could not request cancellation: {ex.Message}";
        }
    }

    private void SetExportSubmissionBusy(bool busy)
    {
        bool canSubmit = !busy && !_measuringVideoPerformance;
        ExportVideoButton.IsEnabled = canSubmit;
        ToolbarExportButton.IsEnabled = canSubmit;
        ToolbarExportAudioButton.IsEnabled = canSubmit;
        ExportAudioButton.IsEnabled = canSubmit;
        AudioExportOptions.IsEnabled = canSubmit;
        ExportResolutionSelector.IsEnabled = canSubmit;
        ExportCodecSelector.IsEnabled = canSubmit;
        ExportFrameRateBox.IsEnabled = canSubmit;
        ExportHardwareToggle.IsEnabled = canSubmit;
        ExportQualitySlider.IsEnabled = canSubmit;
        VideoAudioRateSelector.IsEnabled = canSubmit;
        VideoAudioBitRateSelector.IsEnabled = canSubmit;
        CheckAudioEncodersButton.IsEnabled = canSubmit && !_hasActiveExports &&
            !_checkingAudioEncoders && !_checkingVideoEncoders;
        CheckVideoEncodersButton.IsEnabled = canSubmit && !_hasActiveExports &&
            !_checkingVideoEncoders && !_checkingAudioEncoders;
        MeasureVideoPerformanceButton.IsEnabled = canSubmit && !_hasActiveExports &&
            !_measuringVideoPerformance && !_checkingAudioEncoders && !_checkingVideoEncoders;
    }
}
