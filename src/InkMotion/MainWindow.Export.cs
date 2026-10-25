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
    private bool _checkingFFmpeg;
    private FFmpegAudioCapabilities? _ffmpegCapabilities;
    private readonly string _ffmpegPreferencePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InkMotion", "ffmpeg-path.txt");

    private void InitialiseFFmpegPreference()
    {
        try
        {
            if (File.Exists(_ffmpegPreferencePath))
                FFmpegPathBox.Text = File.ReadAllText(_ffmpegPreferencePath).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FFmpegCapabilitiesText.Text = $"Could not restore optional encoder preference: {ex.Message}";
        }
    }

    private void FFmpegVideoOptions_Changed(object sender, SelectionChangedEventArgs e) => UpdateFFmpegVideoOptions();

    private void UpdateFFmpegVideoOptions()
    {
        if (VideoAudioRateSelector is null || VideoAudioBitRateSelector is null ||
            VideoAudioDepthSelector is null || ExportHardwareToggle is null ||
            MatroskaExportOption is null || VideoPcmOption is null || VideoFlacOption is null || VideoOpusOption is null ||
            WmvExportOption is null || Av1ExportOption is null || AsfExportOption is null || VideoWmaOption is null)
            return;
        bool ffmpeg = VideoBackendSelector.SelectedIndex > 0;
        WmvExportOption.IsEnabled = VideoBackendSelector.SelectedIndex == 1;
        Av1ExportOption.IsEnabled = ffmpeg;
        if ((!ffmpeg && GetSelectedExportCodec() is VideoExportCodec.Wmv or VideoExportCodec.Av1) ||
            (!WmvExportOption.IsEnabled && GetSelectedExportCodec() == VideoExportCodec.Wmv))
            ExportCodecSelector.SelectedIndex = 0;
        bool wmv = ffmpeg && GetSelectedExportCodec() == VideoExportCodec.Wmv;
        AsfExportOption.IsEnabled = wmv;
        VideoWmaOption.IsEnabled = wmv;
        if (wmv)
        {
            VideoContainerSelector.SelectedIndex = 2;
            VideoAudioCodecSelector.SelectedIndex = 4;
        }
        else if (VideoContainerSelector.SelectedIndex == 2)
            VideoContainerSelector.SelectedIndex = 0;
        MatroskaExportOption.IsEnabled = ffmpeg;
        ExportHardwareToggle.IsEnabled = !ffmpeg;
        if (!ffmpeg) VideoContainerSelector.SelectedIndex = 0;
        bool matroska = ffmpeg && VideoContainerSelector.SelectedIndex == 1;
        VideoPcmOption.IsEnabled = matroska;
        VideoFlacOption.IsEnabled = matroska && _ffmpegCapabilities?.Flac == true;
        VideoOpusOption.IsEnabled = matroska && _ffmpegCapabilities?.Opus == true;
        if (!matroska && !wmv) VideoAudioCodecSelector.SelectedIndex = 0;
        if (matroska && VideoAudioCodecSelector.SelectedIndex == 4) VideoAudioCodecSelector.SelectedIndex = 0;
        int codec = VideoAudioCodecSelector.SelectedIndex;
        bool lossless = codec is 1 or 2;
        VideoAudioDepthSelector.IsEnabled = lossless;
        VideoAudioBitRateSelector.IsEnabled = !lossless;
        for (int i = 0; i < VideoAudioRateSelector.Items.Count; i++)
            ((ComboBoxItem)VideoAudioRateSelector.Items[i]).IsEnabled = lossless || i == 1 || (i == 0 && codec != 3);
        if (VideoAudioRateSelector.SelectedItem is not ComboBoxItem { IsEnabled: true })
            VideoAudioRateSelector.SelectedIndex = 1;
    }

    private FFmpegVideoSettings? GetFFmpegVideoSettings()
    {
        if (VideoBackendSelector.SelectedIndex <= 0) return null;
        string path = FFmpegPathBox.Text.Trim();
        if (_ffmpegCapabilities is null ||
            !string.Equals(path, _ffmpegCapabilities.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Select a local FFmpeg executable and check its capabilities first.");
        return new(path, (FFmpegVideoEncoder)(VideoBackendSelector.SelectedIndex - 1),
            (FFmpegVideoContainer)VideoContainerSelector.SelectedIndex,
            (FFmpegVideoAudioCodec)VideoAudioCodecSelector.SelectedIndex,
            VideoAudioDepthSelector.SelectedIndex == 0 ? 16 : 24);
    }

    private async void ChooseFFmpeg_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".exe");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            FFmpegPathBox.Text = file.Path;
            CheckFFmpeg_Click(sender, e);
        }
    }

    private async void CheckFFmpeg_Click(object sender, RoutedEventArgs e)
    {
        if (_checkingFFmpeg) return;
        _checkingFFmpeg = true;
        CheckFFmpegButton.IsEnabled = false;
        ChooseFFmpegButton.IsEnabled = false;
        FlacExportOption.IsEnabled = false;
        OpusExportOption.IsEnabled = false;
        WmaExportOption.IsEnabled = false;
        _ffmpegCapabilities = null;
        UpdateFFmpegVideoOptions();
        FFmpegCapabilitiesText.Text = "Checking the selected local FFmpeg executable...";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            string path = FFmpegPathBox.Text.Trim();
            _ffmpegCapabilities = await Task.Run(() => FFmpegAudioEncoder.Discover(path, timeout.Token));
            FlacExportOption.IsEnabled = _ffmpegCapabilities.Flac;
            OpusExportOption.IsEnabled = _ffmpegCapabilities.Opus;
            WmaExportOption.IsEnabled = _ffmpegCapabilities.Wma;
            UpdateFFmpegVideoOptions();
            FFmpegCapabilitiesText.Text = $"{_ffmpegCapabilities.Version}\n" +
                $"FLAC: {(_ffmpegCapabilities.Flac ? "available" : "unavailable")} | " +
                $"Opus (libopus): {(_ffmpegCapabilities.Opus ? "available" : "unavailable")}\n" +
                $"WMA (wmav2): {(_ffmpegCapabilities.Wma ? "available" : "unavailable")}\n" +
                "Build availability is not an encode guarantee. Each export checks the encoder and fails explicitly if the selected format is unsupported. Licensing depends on your FFmpeg build; InkMotion does not redistribute it.";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_ffmpegPreferencePath)!);
                File.WriteAllText(_ffmpegPreferencePath, _ffmpegCapabilities.ExecutablePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                FFmpegCapabilitiesText.Text += $"\nCould not save the optional encoder path: {ex.Message}";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
            System.ComponentModel.Win32Exception or OperationCanceledException or ArgumentException)
        {
            if (AudioExportCodecSelector.SelectedIndex >= 4) AudioExportCodecSelector.SelectedIndex = 0;
            FFmpegCapabilitiesText.Text = $"Could not check FFmpeg: {ex.Message}";
        }
        finally
        {
            _checkingFFmpeg = false;
            CheckFFmpegButton.IsEnabled = true;
            ChooseFFmpegButton.IsEnabled = true;
        }
    }

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
            if (VideoBackendSelector.SelectedIndex > 0)
            {
                FFmpegVideoSettings settings = GetFFmpegVideoSettings()!;
                if (ExportResolutionSelector.SelectedItem is not ComboBoxItem { Tag: string resolutionName } ||
                    !Enum.TryParse(resolutionName, out ExportResolution resolution))
                    throw new InvalidOperationException("Choose a video resolution.");
                var request = new VideoExportRequest(new(), VideoExportProfile.Create(resolution, GetExportFrameRate()),
                    GetSelectedExportCodec(), "", 720, FFmpeg: settings);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                string encoder = await Task.Run(() => FFmpegVideoExporter.Probe(request, timeout.Token));
                VideoEncoderCapabilitiesText.Text = $"Selected encoder {encoder} successfully encoded and finalised a frame at " +
                    $"{request.Profile.Width}×{request.Profile.Height}, {request.Profile.FrameRate:0.###} fps. " +
                    "No fallback was permitted. This checks this PC and selected profile, not sustained performance or other devices.";
                return;
            }
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
            ArgumentException or System.ComponentModel.Win32Exception or OperationCanceledException)
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
            FFmpegVideoSettings? ffmpeg = GetFFmpegVideoSettings();
            double quality = ExportQualitySlider.Value / 100;
            VideoExportPerformance result = ffmpeg is null ? await Task.Run(() =>
                VideoExportCapabilities.MeasureProfilePerformance(profile, codec, preferHardware)) :
                await Task.Run(() => FFmpegVideoExporter.Measure(new(new(), profile, codec, "", 720,
                    Quality: quality, FFmpeg: ffmpeg)));
            VideoEncoderCapabilitiesText.Text =
                $"Synthetic sample: {result.FrameCount} frames at {profile.Width}×{profile.Height}, " +
                $"{profile.FrameRate:0.###} fps, {ExportPlanning.CodecName(codec)}. " +
                $"Encoded in {result.ElapsedSeconds:0.00}s ({result.FramesPerSecond:0.0} fps); " +
                $"{result.OutputBytes / 1_000_000d:0.0} MB. " +
                (ffmpeg is null ? $"Hardware-transform preference {(result.HardwareTransformsPreferred ? "enabled" : "disabled")}. " +
                    "Media Foundation does not identify the actual encoder hardware." :
                    $"Encoder: {ffmpeg.EncoderName(codec)} (no fallback).") +
                " Performance is this run's synthetic result, not broad device certification.";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or
            NotSupportedException or IOException or ArgumentException or OverflowException or
            System.ComponentModel.Win32Exception or OperationCanceledException)
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
        bool flac = AudioExportCodecSelector.SelectedIndex == (int)AudioExportCodec.Flac;
        bool opus = AudioExportCodecSelector.SelectedIndex == (int)AudioExportCodec.Opus;
        bool floatingPoint = AudioExportCodecSelector.SelectedIndex == 1;
        AudioExportRateSelector.ItemsSource = wave || flac ? AudioExportSettings.SampleRates :
            opus ? new[] { 48000 } : new[] { 44100, 48000 };
        AudioExportRateSelector.SelectedItem = 48000;
        AudioExportDepthSelector.IsEnabled = (wave && !floatingPoint) || flac;
        ((ComboBoxItem)AudioExportDepthSelector.Items[2]).IsEnabled = !flac;
        AudioExportDepthSelector.SelectedIndex = floatingPoint ? 2 : 1;
        AudioExportBitRateSelector.IsEnabled = !wave && !flac;
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
            int.Parse(depth), int.Parse(bitRate), FFmpegPathBox.Text.Trim());
        if (settings.UsesFFmpeg && (_ffmpegCapabilities is null ||
            !string.Equals(settings.FFmpegPath, _ffmpegCapabilities.ExecutablePath, StringComparison.OrdinalIgnoreCase)))
        {
            ExportStatusText.Visibility = Visibility.Visible;
            ExportStatusText.Text = "Check the selected FFmpeg executable's capabilities before exporting FLAC, Opus or WMA.";
            return;
        }
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
            ExportStatusText.Text = "Choose the embedded audio sample rate and bit rate.";
            return;
        }

        FFmpegVideoSettings? ffmpeg;
        try
        {
            ffmpeg = GetFFmpegVideoSettings();
            ffmpeg?.Validate(int.Parse(audioRate));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            ExportStatusText.Visibility = Visibility.Visible;
            ExportStatusText.Text = ex.Message;
            return;
        }
        string extension = ffmpeg?.Extension ?? ".mp4";
        var picker = new FileSavePicker
        {
            SuggestedFileName = _projectFile is null ? "Untitled" : Path.GetFileNameWithoutExtension(_projectFile.Name),
            DefaultFileExtension = extension,
        };
        picker.FileTypeChoices.Add(extension == ".mp4" ? "MPEG-4 video" : "Matroska video", [extension]);
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
            int.Parse(audioBitRate), ffmpeg);
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
        VideoBackendSelector.IsEnabled = canSubmit;
        VideoContainerSelector.IsEnabled = canSubmit;
        VideoAudioCodecSelector.IsEnabled = canSubmit;
        ExportQualitySlider.IsEnabled = canSubmit;
        VideoAudioRateSelector.IsEnabled = canSubmit;
        VideoAudioBitRateSelector.IsEnabled = canSubmit;
        if (canSubmit) UpdateFFmpegVideoOptions();
        CheckAudioEncodersButton.IsEnabled = canSubmit && !_hasActiveExports &&
            !_checkingAudioEncoders && !_checkingVideoEncoders;
        CheckVideoEncodersButton.IsEnabled = canSubmit && !_hasActiveExports &&
            !_checkingVideoEncoders && !_checkingAudioEncoders;
        MeasureVideoPerformanceButton.IsEnabled = canSubmit && !_hasActiveExports &&
            !_measuringVideoPerformance && !_checkingAudioEncoders && !_checkingVideoEncoders;
    }
}
