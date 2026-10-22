using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.Graphics.DirectX;

namespace InkMotion;

public sealed record VideoProxyProgress(int Frame, int FrameCount)
{
    public double Fraction => FrameCount <= 0 ? 0 : Math.Clamp((double)Frame / FrameCount, 0, 1);
}

public sealed record VideoProxyResult(string Path, long SourceLength, long SourceLastWriteUtcTicks);

public static class VideoProxyGenerator
{
    public const double FrameRate = 24;
    public const int MaximumDimension = 960;

    public static VideoProxyResult Generate(
        string sourcePath,
        double durationSeconds,
        IProgress<VideoProxyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        }

        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "InkMotion",
            "Proxies");
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, $"{Guid.NewGuid():N}.mp4");
        var sourceInfo = new FileInfo(sourcePath);
        long sourceLength = sourceInfo.Length;
        long sourceLastWriteUtcTicks = sourceInfo.LastWriteTimeUtc.Ticks;
        int frameCount = checked((int)Math.Ceiling(durationSeconds * FrameRate));
        long frameDuration = (long)Math.Round(10_000_000 / FrameRate);
        bool complete = false;
        try
        {
            using var reader = new MediaFoundationVideoReader(sourcePath);
            if (reader.Width <= 0 || reader.Height <= 0 || reader.Aperture.Width <= 0 || reader.Aperture.Height <= 0)
            {
                throw new InvalidDataException("The source video has no decodable picture area.");
            }

            (int width, int height) = GetOutputSize(reader.Aperture.Width, reader.Aperture.Height);
            if (!reader.TryGetFrame(0))
            {
                throw new InvalidDataException("The source video contains no decodable frames.");
            }

            CanvasDevice device = CanvasDevice.GetSharedDevice();
            using var sourceBitmap = CanvasBitmap.CreateFromBytes(
                device,
                reader.Pixels,
                reader.Width,
                reader.Height,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                96,
                CanvasAlphaMode.Ignore);
            using var target = new CanvasRenderTarget(device, width, height, 96);
            VideoExportProfile profile = VideoExportProfile.Create(ExportResolution.P720, FrameRate);
            using var writer = new MediaFoundationVideoWriter(
                destination,
                width,
                height,
                FrameRate,
                VideoExportCodec.H264,
                Math.Min(4_000_000, ExportPlanning.GetVideoBitRate(profile, VideoExportCodec.H264, 0.35)),
                0,
                0,
                useHardware: false);

            for (int frame = 0; frame < frameCount; frame++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double seconds = Math.Min(frame / FrameRate, Math.Max(0, durationSeconds - 1 / FrameRate));
                if (!reader.TryGetFrame(seconds))
                {
                    throw new InvalidDataException($"The source video ended before proxy frame {frame + 1} could be decoded.");
                }

                sourceBitmap.SetPixelBytes(reader.Pixels);
                (int x, int y, int apertureWidth, int apertureHeight) = reader.Aperture;
                using (CanvasDrawingSession session = target.CreateDrawingSession())
                {
                    session.Clear(Microsoft.UI.Colors.Black);
                    session.DrawImage(
                        sourceBitmap,
                        new Rect(0, 0, width, height),
                        new Rect(x, y, apertureWidth, apertureHeight));
                }

                writer.WriteVideoFrame(
                    target.GetPixelBytes(),
                    frame * frameDuration,
                    frameDuration);
                progress?.Report(new VideoProxyProgress(frame + 1, frameCount));
            }

            writer.Finish();
            sourceInfo.Refresh();
            if (!sourceInfo.Exists || sourceInfo.Length != sourceLength ||
                sourceInfo.LastWriteTimeUtc.Ticks != sourceLastWriteUtcTicks)
            {
                throw new IOException("The source video changed while its proxy was being generated. Try again.");
            }

            complete = true;
            return new VideoProxyResult(destination, sourceLength, sourceLastWriteUtcTicks);
        }
        finally
        {
            if (!complete && File.Exists(destination))
            {
                File.Delete(destination);
            }
        }
    }

    public static (int Width, int Height) GetOutputSize(int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        }

        double scale = Math.Min(1, (double)MaximumDimension / Math.Max(sourceWidth, sourceHeight));
        int width = Math.Max(2, (int)Math.Floor(sourceWidth * scale / 2) * 2);
        int height = Math.Max(2, (int)Math.Floor(sourceHeight * scale / 2) * 2);
        return (width, height);
    }
}
