using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Graphics.Canvas;

namespace InkMotion;

public enum FFmpegVideoEncoder { Software, Nvidia, Intel, Amd }
public enum FFmpegVideoContainer { Mp4, Matroska }
public enum FFmpegVideoAudioCodec { Aac, Pcm, Flac, Opus }

public sealed record FFmpegVideoSettings(string ExecutablePath, FFmpegVideoEncoder Encoder,
    FFmpegVideoContainer Container, FFmpegVideoAudioCodec AudioCodec, int AudioBits = 24)
{
    public string Extension => Container == FFmpegVideoContainer.Mp4 ? ".mp4" : ".mkv";

    public string EncoderName(VideoExportCodec codec) => (codec, Encoder) switch
    {
        (VideoExportCodec.H264, FFmpegVideoEncoder.Software) => "libx264",
        (VideoExportCodec.Hevc, FFmpegVideoEncoder.Software) => "libx265",
        (VideoExportCodec.H264, FFmpegVideoEncoder.Nvidia) => "h264_nvenc",
        (VideoExportCodec.Hevc, FFmpegVideoEncoder.Nvidia) => "hevc_nvenc",
        (VideoExportCodec.H264, FFmpegVideoEncoder.Intel) => "h264_qsv",
        (VideoExportCodec.Hevc, FFmpegVideoEncoder.Intel) => "hevc_qsv",
        (VideoExportCodec.H264, FFmpegVideoEncoder.Amd) => "h264_amf",
        (VideoExportCodec.Hevc, FFmpegVideoEncoder.Amd) => "hevc_amf",
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };

    public void Validate(int sampleRate)
    {
        if (!Enum.IsDefined(Encoder) || !Enum.IsDefined(Container) || !Enum.IsDefined(AudioCodec) ||
            string.IsNullOrWhiteSpace(ExecutablePath) || !Path.IsPathFullyQualified(ExecutablePath) ||
            !AudioExportSettings.SampleRates.Contains(sampleRate) ||
            AudioBits is not (16 or 24) ||
            (Container == FFmpegVideoContainer.Mp4 && AudioCodec != FFmpegVideoAudioCodec.Aac) ||
            (AudioCodec == FFmpegVideoAudioCodec.Aac && sampleRate is not (44100 or 48000)) ||
            (AudioCodec == FFmpegVideoAudioCodec.Opus && sampleRate != 48000))
        {
            throw new ArgumentException("Unsupported FFmpeg video settings. MP4 uses AAC at 44.1/48 kHz; Matroska also supports 16/24-bit PCM/FLAC and 48 kHz Opus.");
        }
    }
}

public static class FFmpegVideoExporter
{
    public static VideoExportPerformance Measure(VideoExportRequest request)
    {
        FFmpegVideoSettings settings = request.FFmpeg ?? throw new ArgumentException("Choose an FFmpeg encoder.");
        settings.Validate(request.AudioSampleRate);
        string path = Path.Combine(Path.GetTempPath(), $"inkmotion-video-measure-{Guid.NewGuid():N}.mp4");
        int count = VideoExportCapabilities.GetPerformanceFrameCount(request.Profile);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        byte[] frame = VideoExportCapabilities.CreateTestPattern(request.Profile.Width, request.Profile.Height);
        var clock = Stopwatch.StartNew();
        try
        {
            EncodeSegment(request, path, Enumerable.Repeat(frame, count), timeout.Token);
            clock.Stop();
            return new(request.Profile, request.Codec, count, clock.Elapsed.TotalSeconds, new FileInfo(path).Length,
                settings.Encoder != FFmpegVideoEncoder.Software);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    public static string Probe(VideoExportRequest request, CancellationToken cancellationToken)
    {
        FFmpegVideoSettings settings = request.FFmpeg ?? throw new ArgumentException("Choose an FFmpeg encoder.");
        settings.Validate(request.AudioSampleRate);
        FFmpegAudioEncoder.Discover(settings.ExecutablePath, cancellationToken);
        string temporary = Path.Combine(Path.GetTempPath(), $"inkmotion-video-probe-{Guid.NewGuid():N}.mp4");
        try
        {
            EncodeSegment(request, temporary,
                [new byte[checked(request.Profile.Width * request.Profile.Height * 4)]], cancellationToken);
            return settings.EncoderName(request.Codec);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static VideoExportResult Export(VideoExportRequest request, IProgress<VideoExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        FFmpegVideoSettings settings = request.FFmpeg ?? throw new ArgumentException("Choose an FFmpeg encoder.");
        settings.Validate(request.AudioSampleRate);
        ExportPlanning.ValidateOutputPath(request.Project, request.OutputPath);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        progress?.Report(new(0, 0, "Checking selected FFmpeg encoder (no fallback)"));
        string encoder = Probe(request, timeout.Token);
        var warnings = new List<string>();
        double audioDuration;
        bool hasAudio;
        using (var mix = new ExportAudioMixer(request.Project, request.AudioSampleRate))
        {
            audioDuration = mix.DurationSeconds;
            hasAudio = mix.SourceCount > 0;
            warnings.AddRange(mix.Warnings);
        }
        double duration = VideoExporter.GetDurationSeconds(request.Project, audioDuration);
        int count = ExportPlanning.GetFrameCount(duration, request.Profile.FrameRate);
        if (count == 0) throw new InvalidOperationException("Nothing to export: add video, images, titles, audio or ink first.");
        string directory = request.CheckpointDirectory ??
            Path.Combine(Path.GetTempPath(), $"inkmotion-segments-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string manifest = Path.Combine(directory, "identity.txt");
        string identity = CheckpointIdentity(request);
        if (File.Exists(manifest) && File.ReadAllText(manifest) != identity)
        {
            throw new InvalidOperationException("Export settings or linked source files changed since the saved checkpoint. Submit a new export instead of resuming this job.");
        }
        if (!File.Exists(manifest))
        {
            string pendingManifest = manifest + ".partial";
            File.WriteAllText(pendingManifest, identity);
            File.Move(pendingManifest, manifest, overwrite: true);
        }
        string temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(request.OutputPath))!,
            $".inkmotion-{Guid.NewGuid():N}{settings.Extension}");
        string audioPath = Path.Combine(directory, "audio.wav");
        int audioChannels = 0;
        var segments = new List<string>();
        bool completed = false;
        string? stemPath = null;
        try
        {
            int segmentFrames = Math.Max(1, (int)Math.Round(request.Profile.FrameRate * 5));
            using (var device = new CanvasDevice())
            using (var renderer = new VideoExporter.FrameRenderer(device, request, warnings))
            {
                for (int start = 0; start < count; start += segmentFrames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int end = Math.Min(count, start + segmentFrames);
                    string segment = Path.Combine(directory, $"segment-{start:D10}.mp4");
                    segments.Add(segment);
                    if (File.Exists(segment) && new FileInfo(segment).Length > 0)
                    {
                        progress?.Report(new(end, count, "Reusing completed video segment"));
                        continue;
                    }
                    string partial = segment + ".partial";
                    try
                    {
                        IEnumerable<byte[]> Frames()
                        {
                            for (int frame = start; frame < end; frame++)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                yield return renderer.Render(frame);
                                if (frame % 5 == 0) progress?.Report(new(frame + 1, count, $"Encoding with {encoder}"));
                            }
                        }
                        EncodeSegment(request, partial, Frames(), cancellationToken);
                        File.Move(partial, segment, overwrite: true);
                    }
                    finally
                    {
                        if (File.Exists(partial)) File.Delete(partial);
                    }
                }
            }
            progress?.Report(new(count, count, "Preparing audio and final container"));
            if (hasAudio)
            {
                AudioExportCodec audioCodec = settings.AudioCodec switch
                {
                    FFmpegVideoAudioCodec.Flac => AudioExportCodec.Flac,
                    FFmpegVideoAudioCodec.Opus => AudioExportCodec.Opus,
                    _ => AudioExportCodec.PcmWave,
                };
                var audio = new AudioExportSettings(audioCodec, request.AudioSampleRate, settings.AudioBits,
                    request.AudioBitRate, settings.ExecutablePath);
                audioPath = Path.Combine(directory, "audio" + audio.Extension);
                AudioExportResult result = AudioExporter.Export(request.Project, audio, audioPath, null, cancellationToken);
                warnings.AddRange(result.Warnings);
                audioChannels = result.Channels;
            }
            string concat = Path.Combine(directory, "segments.txt");
            // Only generated basenames are written; source/output paths never enter the concat syntax.
            File.WriteAllLines(concat, segments.Select(path => $"file '{Path.GetFileName(path)}'"), new UTF8Encoding(false));
            var arguments = new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-f", "concat",
                "-safe", "1", "-i", concat,
            };
            if (hasAudio) arguments.AddRange(["-i", audioPath]);
            arguments.AddRange(["-map", "0:v:0", "-c:v", "copy"]);
            if (hasAudio)
            {
                arguments.AddRange(["-map", "1:a:0", "-c:a",
                    settings.AudioCodec == FFmpegVideoAudioCodec.Aac ? "aac" : "copy"]);
                if (settings.AudioCodec == FFmpegVideoAudioCodec.Aac)
                {
                    if (audioChannels > 6)
                    {
                        arguments.AddRange(["-ac", "2"]);
                        stemPath = ExportPlanning.GetSurroundStemPath(request.OutputPath);
                        ExportPlanning.ValidateOutputPath(request.Project, stemPath);
                        warnings.Add($"AAC output is a stereo downmix because the source layout exceeds 5.1. The full layout is in {Path.GetFileName(stemPath)}.");
                        audioChannels = 2;
                    }
                    arguments.AddRange(["-b:a", request.AudioBitRate.ToString(CultureInfo.InvariantCulture)]);
                }
            }
            if (settings.Container == FFmpegVideoContainer.Mp4) arguments.AddRange(["-movflags", "+faststart"]);
            arguments.AddRange(["-f", settings.Container == FFmpegVideoContainer.Mp4 ? "mp4" : "matroska", temporary]);
            FFmpegAudioEncoder.Run(settings.ExecutablePath, arguments, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0)
                throw new IOException("FFmpeg did not produce a final video file.");
            if (stemPath is not null)
                File.Copy(audioPath, stemPath, overwrite: true);
            File.Move(temporary, request.OutputPath, overwrite: true);
            completed = true;
            return new(request.OutputPath, stemPath, count, duration, settings.Encoder != FFmpegVideoEncoder.Software,
                warnings.Distinct().ToArray(), audioChannels, encoder);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(audioPath)) File.Delete(audioPath);
            if (completed || request.CheckpointDirectory is null)
            {
                foreach (string path in segments.Append(manifest).Append(Path.Combine(directory, "segments.txt")))
                {
                    try { if (File.Exists(path)) File.Delete(path); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Trace.TraceWarning($"Could not remove export checkpoint {path}: {ex.Message}");
                    }
                }
                if (request.CheckpointDirectory is null && !Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            }
        }
    }

    private static string CheckpointIdentity(VideoExportRequest request)
    {
        var content = new StringBuilder(EditorProjectSerialiser.Serialise(request.Project));
        content.Append(JsonSerializer.Serialize(new
        {
            request.Profile, request.Codec, request.InkReferenceHeight, request.Quality,
            request.AudioSampleRate, request.AudioBitRate, request.FFmpeg, CheckpointVersion = 1,
        }));
        IEnumerable<string?> paths = request.Project.AudioTracks.Select(track => track.AudioPath)
            .Concat(request.Project.VideoTracks.SelectMany(track => track.Clips)
                .SelectMany(clip => new[] { clip.SourcePath, clip.ProxyPath, clip.ColourLutPath }))
            .Append(request.FFmpeg!.ExecutablePath);
        foreach (string? path in paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct())
        {
            var file = new FileInfo(path!);
            content.Append('\n').Append(file.FullName).Append('|')
                .Append(file.Exists ? file.Length : -1).Append('|')
                .Append(file.Exists ? file.LastWriteTimeUtc.Ticks : -1);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content.ToString())));
    }

    private static void EncodeSegment(VideoExportRequest request, string path, IEnumerable<byte[]> frames,
        CancellationToken cancellationToken)
    {
        FFmpegVideoSettings settings = request.FFmpeg!;
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-f", "rawvideo",
            "-pixel_format", "bgra", "-video_size", $"{request.Profile.Width}x{request.Profile.Height}",
            "-framerate", request.Profile.FrameRate.ToString("R", CultureInfo.InvariantCulture),
            "-i", "pipe:0", "-an", "-c:v", settings.EncoderName(request.Codec), "-pix_fmt", "yuv420p",
            "-b:v", ExportPlanning.GetVideoBitRate(request.Profile, request.Codec, request.Quality).ToString(CultureInfo.InvariantCulture),
            "-f", "mp4", path,
        };
        using Process process = FFmpegAudioEncoder.Start(settings.ExecutablePath, arguments, redirectInput: true);
        using CancellationTokenRegistration registration = cancellationToken.Register(() => FFmpegAudioEncoder.Terminate(process));
        Task<string> errors = FFmpegAudioEncoder.ReadTailAsync(process.StandardError);
        try
        {
            foreach (byte[] frame in frames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                process.StandardInput.BaseStream.Write(frame);
            }
            process.StandardInput.Close();
            process.WaitForExit();
            string diagnostic = errors.GetAwaiter().GetResult();
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"FFmpeg encoder {settings.EncoderName(request.Codec)} failed without fallback: {diagnostic}");
        }
        catch (IOException ex)
        {
            FFmpegAudioEncoder.Terminate(process);
            process.WaitForExit();
            string diagnostic = errors.GetAwaiter().GetResult();
            cancellationToken.ThrowIfCancellationRequested();
            throw new IOException($"FFmpeg encoder {settings.EncoderName(request.Codec)} stopped: {diagnostic}", ex);
        }
        finally
        {
            FFmpegAudioEncoder.Terminate(process);
        }
    }
}
