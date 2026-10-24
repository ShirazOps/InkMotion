using System.Diagnostics;
using System.Text.Json;
using NAudio.Wave;
using Xunit;

namespace InkMotion.Tests;

public sealed class FFmpegFactAttribute : FactAttribute
{
    public FFmpegFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("INKMOTION_TEST_FFMPEG")))
            Skip = "Optional integration test: set INKMOTION_TEST_FFMPEG to a local FFmpeg executable.";
    }
}

public sealed class FFmpegExportTests
{
    [Fact]
    public void Settings_RejectIncompatibleFormatsAndMissingBackend()
    {
        Assert.Throws<ArgumentException>(() => new AudioExportSettings(AudioExportCodec.Flac, 48000).Validate());
        Assert.Throws<ArgumentException>(() => new AudioExportSettings(AudioExportCodec.Opus, 44100,
            FFmpegPath: @"C:\ffmpeg.exe").Validate());
        Assert.Throws<ArgumentException>(() => new AudioExportSettings(AudioExportCodec.Flac, 96000, 32,
            FFmpegPath: @"C:\ffmpeg.exe").Validate());
        var settings = new FFmpegVideoSettings(@"C:\ffmpeg.exe", FFmpegVideoEncoder.Nvidia,
            FFmpegVideoContainer.Mp4, FFmpegVideoAudioCodec.Flac);
        Assert.Throws<ArgumentException>(() => settings.Validate(48000));
        (settings with { Container = FFmpegVideoContainer.Matroska }).Validate(384000);
        Assert.Equal("h264_nvenc", settings.EncoderName(VideoExportCodec.H264));
        Assert.Equal("hevc_qsv", (settings with { Encoder = FFmpegVideoEncoder.Intel }).EncoderName(VideoExportCodec.Hevc));
    }

    [FFmpegFact]
    public void AudioFormats_RoundTripSelectedDepthRateChannelsAndDuration()
    {
        string ffmpeg = Environment.GetEnvironmentVariable("INKMOTION_TEST_FFMPEG")!;
        string directory = Path.Combine(Path.GetTempPath(), $"inkmotion-ffmpeg-audio-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string input = Path.Combine(directory, "tone.wav");
        try
        {
            WriteTone(input, 0.2);
            var project = new EditorProject { AudioTracks = [new() { AudioPath = input }] };
            foreach (int rate in AudioExportSettings.SampleRates)
            foreach (int depth in new[] { 16, 24 })
            {
                string output = Path.Combine(directory, $"audio-{rate}-{depth}.flac");
                AudioExportResult result = AudioExporter.Export(project,
                    new(AudioExportCodec.Flac, rate, depth, FFmpegPath: ffmpeg), output, null, CancellationToken.None);
                using JsonDocument probe = Probe(ffmpeg, output);
                JsonElement audio = probe.RootElement.GetProperty("streams")[0];
                Assert.Equal("flac", audio.GetProperty("codec_name").GetString());
                Assert.Equal(rate.ToString(), audio.GetProperty("sample_rate").GetString());
                Assert.Equal(depth.ToString(), audio.GetProperty("bits_per_raw_sample").GetString());
                Assert.Equal(2, audio.GetProperty("channels").GetInt32());
                Assert.Equal(2, result.Channels);
                string decoded = Path.Combine(directory, "decoded.f32");
                Run(ffmpeg, "-y", "-v", "error", "-i", output, "-f", "f32le", decoded);
                byte[] bytes = File.ReadAllBytes(decoded);
                Assert.InRange(bytes.Length / (double)(sizeof(float) * rate * 2), 0.199, 0.201);
                float[] samples = new float[bytes.Length / sizeof(float)];
                Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
                Assert.InRange(samples.Max(Math.Abs), 0.1f, 0.4f);
                File.Delete(decoded);
            }
            string opus = Path.Combine(directory, "audio.opus");
            AudioExporter.Export(project, new(AudioExportCodec.Opus, 48000, FFmpegPath: ffmpeg),
                opus, null, CancellationToken.None);
            using JsonDocument opusProbe = Probe(ffmpeg, opus);
            Assert.Equal("opus", opusProbe.RootElement.GetProperty("streams")[0].GetProperty("codec_name").GetString());
        }
        finally
        {
            foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    [FFmpegFact]
    public void HardwareChoices_UseRequestedEncoderOrFailWithoutFallback()
    {
        string ffmpeg = Environment.GetEnvironmentVariable("INKMOTION_TEST_FFMPEG")!;
        foreach (FFmpegVideoEncoder encoder in new[] { FFmpegVideoEncoder.Nvidia, FFmpegVideoEncoder.Intel, FFmpegVideoEncoder.Amd })
        {
            var settings = new FFmpegVideoSettings(ffmpeg, encoder, FFmpegVideoContainer.Mp4, FFmpegVideoAudioCodec.Aac);
            var request = new VideoExportRequest(new(), VideoExportProfile.Create(ExportResolution.Uhd4K, 120),
                VideoExportCodec.H264, "", 720, FFmpeg: settings);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                Assert.Equal(settings.EncoderName(request.Codec), FFmpegVideoExporter.Probe(request, timeout.Token));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                Assert.Contains(settings.EncoderName(request.Codec), ex.Message);
            }
        }
    }

    [FFmpegFact]
    public void VideoContainers_RoundTripTheirAdvertisedAudioCodecs()
    {
        string ffmpeg = Environment.GetEnvironmentVariable("INKMOTION_TEST_FFMPEG")!;
        string directory = Path.Combine(Path.GetTempPath(), $"inkmotion-ffmpeg-containers-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string input = Path.Combine(directory, "tone.wav");
        try
        {
            WriteTone(input, 0.2);
            var project = new EditorProject { AudioTracks = [new() { AudioPath = input }] };
            foreach (FFmpegVideoContainer container in Enum.GetValues<FFmpegVideoContainer>())
            foreach (FFmpegVideoAudioCodec codec in Enum.GetValues<FFmpegVideoAudioCodec>())
            {
                if (container == FFmpegVideoContainer.Mp4 && codec != FFmpegVideoAudioCodec.Aac) continue;
                var settings = new FFmpegVideoSettings(ffmpeg, FFmpegVideoEncoder.Software, container, codec);
                string output = Path.Combine(directory, $"{container}-{codec}{settings.Extension}");
                int rate = codec == FFmpegVideoAudioCodec.Pcm ? 384000 : 48000;
                VideoExportResult result = VideoExporter.Export(new(project,
                    VideoExportProfile.Create(ExportResolution.P720, 120), VideoExportCodec.H264, output, 720,
                    AudioSampleRate: rate, FFmpeg: settings), null, CancellationToken.None);
                using JsonDocument probe = Probe(ffmpeg, output);
                JsonElement audio = probe.RootElement.GetProperty("streams").EnumerateArray()
                    .Single(stream => stream.GetProperty("codec_type").GetString() == "audio");
                Assert.Equal(codec switch { FFmpegVideoAudioCodec.Pcm => "pcm_s24le",
                    FFmpegVideoAudioCodec.Flac => "flac", FFmpegVideoAudioCodec.Opus => "opus", _ => "aac" },
                    audio.GetProperty("codec_name").GetString());
                Assert.Equal(rate.ToString(), audio.GetProperty("sample_rate").GetString());
                Assert.Equal(24, result.FrameCount);
            }
        }
        finally
        {
            foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    [FFmpegFact]
    public void Video_RetainsSegmentsOnCancellationResumesAndMuxesLosslessAudio()
    {
        string ffmpeg = Environment.GetEnvironmentVariable("INKMOTION_TEST_FFMPEG")!;
        string directory = Path.Combine(Path.GetTempPath(), $"inkmotion-ffmpeg-video-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string checkpoints = Path.Combine(directory, "segments");
        string input = Path.Combine(directory, "tone.wav");
        string output = Path.Combine(directory, "movie.mkv");
        try
        {
            WriteTone(input, 5.3);
            var project = new EditorProject { AudioTracks = [new() { AudioPath = input }] };
            var request = new VideoExportRequest(project, VideoExportProfile.Create(ExportResolution.P720, 29.97),
                VideoExportCodec.H264, output, 720, FFmpeg: new(ffmpeg, FFmpegVideoEncoder.Software,
                    FFmpegVideoContainer.Matroska, FFmpegVideoAudioCodec.Flac), CheckpointDirectory: checkpoints);
            using var cancellation = new CancellationTokenSource();
            var progress = new ActionProgress(update =>
            {
                if (update.Frame > 150) cancellation.Cancel();
            });
            File.WriteAllText(output, "Existing destination must survive cancellation.");
            Assert.Throws<OperationCanceledException>(() => VideoExporter.Export(request, progress, cancellation.Token));
            Assert.Equal("Existing destination must survive cancellation.", File.ReadAllText(output));
            string segment = Directory.GetFiles(checkpoints, "segment-*.mp4").Single();
            Assert.True(new FileInfo(segment).Length > 0);
            bool reused = false;
            VideoExportResult result = VideoExporter.Export(request,
                new ActionProgress(update => reused |= update.Stage.Contains("Reusing")), CancellationToken.None);
            Assert.True(reused);
            Assert.Equal("libx264", result.EncoderName);
            using JsonDocument probe = Probe(ffmpeg, output);
            JsonElement[] streams = probe.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            Assert.Contains(streams, stream => stream.GetProperty("codec_name").GetString() == "flac");
            Assert.Contains(streams, stream => stream.GetProperty("codec_name").GetString() == "h264");
            Assert.InRange(double.Parse(probe.RootElement.GetProperty("format").GetProperty("duration").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture), 5.29, 5.36);
            Assert.Empty(Directory.GetFiles(checkpoints, "segment-*.mp4"));
        }
        finally
        {
            if (Directory.Exists(checkpoints))
            {
                foreach (string file in Directory.GetFiles(checkpoints)) File.Delete(file);
                Directory.Delete(checkpoints);
            }
            foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    private sealed class ActionProgress(Action<VideoExportProgress> action) : IProgress<VideoExportProgress>
    {
        public void Report(VideoExportProgress value) => action(value);
    }

    private static void WriteTone(string path, double duration)
    {
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(48000, 1));
        for (int i = 0; i < 48000 * duration; i++) writer.WriteSample((float)(0.25 * Math.Sin(i * 2 * Math.PI * 440 / 48000)));
    }

    private static JsonDocument Probe(string ffmpeg, string path) => JsonDocument.Parse(Run(
        Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe"), "-v", "error", "-show_streams", "-show_format", "-of", "json", path));

    private static string Run(string executable, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using Process process = Process.Start(info)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(60000), "FFmpeg validation exceeded 60 seconds.");
        Assert.True(process.ExitCode == 0, error.GetAwaiter().GetResult());
        return output.GetAwaiter().GetResult();
    }
}
