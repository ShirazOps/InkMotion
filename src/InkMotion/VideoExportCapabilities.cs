using System.Runtime.InteropServices;
using System.Diagnostics;

namespace InkMotion;

public sealed record VideoExportCapability(
    VideoExportProfile Profile,
    VideoExportCodec Codec,
    bool SoftwareSupported,
    string? SoftwareFailure);

public sealed record VideoExportPerformance(
    VideoExportProfile Profile,
    VideoExportCodec Codec,
    int FrameCount,
    double ElapsedSeconds,
    long OutputBytes,
    bool HardwareTransformsPreferred)
{
    public double FramesPerSecond => ElapsedSeconds <= 0 ? 0 : FrameCount / ElapsedSeconds;
}

public static class VideoExportCapabilities
{
    public const double PerformanceSampleDurationSeconds = 2;
    public static IReadOnlyList<double> CertificationFrameRates { get; } = [24, 30, 60, 120];

    public static int GetPerformanceFrameCount(VideoExportProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return checked((int)Math.Ceiling(profile.FrameRate * PerformanceSampleDurationSeconds));
    }

    public static VideoExportPerformance MeasureProfilePerformance(
        VideoExportProfile profile,
        VideoExportCodec codec,
        bool preferHardwareTransforms = false)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!Enum.IsDefined(codec))
        {
            throw new ArgumentOutOfRangeException(nameof(codec));
        }

        string path = Path.Combine(Path.GetTempPath(), $"inkmotion-performance-{Guid.NewGuid():N}.mp4");
        int frameCount = GetPerformanceFrameCount(profile);
        long frameDuration = ExportPlanning.GetFrameTime(1, profile.FrameRate);
        byte[] frame = CreateTestPattern(profile.Width, profile.Height);
        int previousMarkerX = -1;
        int previousMarkerY = -1;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using (var writer = new MediaFoundationVideoWriter(
                       path,
                       profile.Width,
                       profile.Height,
                       profile.FrameRate,
                       codec,
                       ExportPlanning.GetVideoBitRate(profile, codec),
                       audioChannels: 0,
                       audioChannelMask: 0,
                       useHardware: preferHardwareTransforms))
            {
                for (int index = 0; index < frameCount; index++)
                {
                    int markerX = (index * 17) % Math.Max(1, profile.Width - 64);
                    int markerY = (index * 11) % Math.Max(1, profile.Height - 64);
                    if (previousMarkerX >= 0)
                    {
                        RestorePatternRegion(frame, profile.Width, profile.Height, previousMarkerX, previousMarkerY);
                    }

                    DrawMovingMarker(frame, profile.Width, markerX, markerY, index);
                    writer.WriteVideoFrame(frame, index * frameDuration, frameDuration);
                    previousMarkerX = markerX;
                    previousMarkerY = markerY;
                }

                writer.Finish();
            }

            stopwatch.Stop();
            return new VideoExportPerformance(
                profile, codec, frameCount, stopwatch.Elapsed.TotalSeconds, new FileInfo(path).Length,
                preferHardwareTransforms);
        }
        finally
        {
            TryDelete(path);
        }
    }

    public static IReadOnlyList<VideoExportCapability> DiscoverSoftwareCapabilities()
    {
        var results = new List<VideoExportCapability>();
        string probeDirectory = Path.Combine(Path.GetTempPath(), "InkMotion-VideoEncoderProbes");
        Directory.CreateDirectory(probeDirectory);

        foreach (var dimensions in VideoExportProfile.Resolutions)
        {
            foreach (double frameRate in CertificationFrameRates)
            {
                VideoExportProfile profile = VideoExportProfile.Create(dimensions.Resolution, frameRate);
                foreach (VideoExportCodec codec in Enum.GetValues<VideoExportCodec>())
                {
                    string path = Path.Combine(probeDirectory, $"{Guid.NewGuid():N}.mp4");
                    string? failure = null;
                    try
                    {
                        int bitRate = ExportPlanning.GetVideoBitRate(profile, codec);
                        using var writer = new MediaFoundationVideoWriter(
                            path, profile.Width, profile.Height, profile.FrameRate, codec, bitRate,
                            audioChannels: 0, audioChannelMask: 0, useHardware: false);
                        byte[] frame = new byte[checked(profile.Width * profile.Height * 4)];
                        writer.WriteVideoFrame(frame, 0, ExportPlanning.GetFrameTime(1, profile.FrameRate));
                        writer.Finish();
                    }
                    catch (Exception ex) when (ex is COMException or InvalidOperationException or
                        NotSupportedException or IOException or ArgumentException or OverflowException)
                    {
                        failure = ex.Message;
                    }
                    finally
                    {
                        TryDelete(path);
                    }

                    results.Add(new VideoExportCapability(profile, codec, failure is null, failure));
                }
            }
        }

        return results;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static byte[] CreateTestPattern(int width, int height)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int block = ((x / 32) * 37 + (y / 32) * 59) % 220 + 24;
                int index = ((y * width) + x) * 4;
                pixels[index] = (byte)block;
                pixels[index + 1] = (byte)((block * 3) % 256);
                pixels[index + 2] = (byte)((block * 7) % 256);
                pixels[index + 3] = 255;
            }
        }

        return pixels;
    }

    private static void DrawMovingMarker(byte[] frame, int width, int x, int y, int frameIndex)
    {
        int right = Math.Min(width, x + 64);
        int bottom = Math.Min(frame.Length / (width * 4), y + 64);
        byte value = (byte)(64 + (frameIndex * 37 % 192));
        for (int row = y; row < bottom; row++)
        {
            for (int column = x; column < right; column++)
            {
                int index = ((row * width) + column) * 4;
                frame[index] = value;
                frame[index + 1] = (byte)(255 - value);
                frame[index + 2] = value;
            }
        }
    }

    private static void RestorePatternRegion(byte[] frame, int width, int height, int x, int y)
    {
        for (int row = y; row < Math.Min(height, y + 64); row++)
        {
            for (int column = x; column < Math.Min(width, x + 64); column++)
            {
                int block = ((column / 32) * 37 + (row / 32) * 59) % 220 + 24;
                int index = ((row * width) + column) * 4;
                frame[index] = (byte)block;
                frame[index + 1] = (byte)((block * 3) % 256);
                frame[index + 2] = (byte)((block * 7) % 256);
            }
        }
    }
}
