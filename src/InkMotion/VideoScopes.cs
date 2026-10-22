using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.UI;

namespace InkMotion;

public sealed record VideoRgbHistogramData(int[] Red, int[] Green, int[] Blue, int SampleCount)
{
    public const int ChannelCount = 256;

    public static VideoRgbHistogramData Analyse(ReadOnlySpan<byte> bgraPixels)
    {
        if (bgraPixels.Length % 4 != 0)
        {
            throw new ArgumentException("BGRA pixel data must contain complete four-byte pixels.", nameof(bgraPixels));
        }

        int[] red = new int[ChannelCount];
        int[] green = new int[ChannelCount];
        int[] blue = new int[ChannelCount];
        int sampleCount = 0;
        for (int offset = 0; offset < bgraPixels.Length; offset += 4)
        {
            int alpha = bgraPixels[offset + 3];
            if (alpha == 0)
            {
                continue;
            }

            blue[UnpremultiplyChannel(bgraPixels[offset], alpha)]++;
            green[UnpremultiplyChannel(bgraPixels[offset + 1], alpha)]++;
            red[UnpremultiplyChannel(bgraPixels[offset + 2], alpha)]++;
            sampleCount++;
        }

        return new VideoRgbHistogramData(red, green, blue, sampleCount);
    }

    public static void Draw(CanvasDrawingSession session, Rect bounds, VideoRgbHistogramData? data)
    {
        session.FillRectangle(bounds, Color.FromArgb(255, 18, 22, 28));
        Rect plot = new(bounds.X + 1, bounds.Y + 1, Math.Max(0, bounds.Width - 2), Math.Max(0, bounds.Height - 2));
        if (plot.Width <= 0 || plot.Height <= 0)
        {
            return;
        }

        Color gridColour = Color.FromArgb(90, 130, 145, 160);
        for (int division = 1; division < 4; division++)
        {
            float y = (float)(plot.Y + (plot.Height * division / 4));
            session.DrawLine((float)plot.X, y, (float)(plot.X + plot.Width), y, gridColour, 1);
        }

        if (data is null || data.SampleCount == 0)
        {
            return;
        }

        int maximum = Math.Max(data.Red.Max(), Math.Max(data.Green.Max(), data.Blue.Max()));
        if (maximum == 0)
        {
            return;
        }

        DrawChannel(session, plot, data.Red, maximum, Color.FromArgb(190, 255, 90, 90));
        DrawChannel(session, plot, data.Green, maximum, Color.FromArgb(190, 100, 235, 135));
        DrawChannel(session, plot, data.Blue, maximum, Color.FromArgb(190, 110, 165, 255));
    }

    private static void DrawChannel(CanvasDrawingSession session, Rect plot, IReadOnlyList<int> counts, int maximum, Color colour)
    {
        float bottom = (float)(plot.Y + plot.Height);
        for (int value = 0; value < ChannelCount; value++)
        {
            if (counts[value] == 0)
            {
                continue;
            }

            float x = (float)(plot.X + (value / (double)(ChannelCount - 1) * plot.Width));
            float height = (float)(plot.Height * Math.Sqrt(counts[value] / (double)maximum));
            session.DrawLine(x, bottom, x, bottom - height, colour, 1.5f);
        }
    }

    internal static int UnpremultiplyChannel(byte channel, int alpha) =>
        Math.Min(byte.MaxValue, (channel * byte.MaxValue + (alpha / 2)) / alpha);
}

public sealed record VideoLumaWaveformData(byte[] BgraPixels, int SampleCount)
{
    public const int Width = 128;
    public const int Height = 128;

    public static VideoLumaWaveformData Analyse(ReadOnlySpan<byte> bgraPixels, int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 ||
            bgraPixels.Length != checked(sourceWidth * sourceHeight * 4))
        {
            throw new ArgumentException("BGRA pixel data must match the supplied source dimensions.", nameof(bgraPixels));
        }

        int[] density = new int[Width * Height];
        int sampleCount = 0;
        for (int y = 0; y < sourceHeight; y++)
        {
            for (int x = 0; x < sourceWidth; x++)
            {
                int offset = ((y * sourceWidth) + x) * 4;
                int alpha = bgraPixels[offset + 3];
                if (alpha == 0)
                {
                    continue;
                }

                int blue = VideoRgbHistogramData.UnpremultiplyChannel(bgraPixels[offset], alpha);
                int green = VideoRgbHistogramData.UnpremultiplyChannel(bgraPixels[offset + 1], alpha);
                int red = VideoRgbHistogramData.UnpremultiplyChannel(bgraPixels[offset + 2], alpha);
                int luma = Math.Clamp((int)Math.Round((0.2126 * red) + (0.7152 * green) + (0.0722 * blue)), 0, 255);
                int column = Math.Min(Width - 1, x * Width / sourceWidth);
                int row = Height - 1 - (luma * (Height - 1) / byte.MaxValue);
                density[(row * Width) + column]++;
                sampleCount++;
            }
        }

        int maximum = density.Max();
        byte[] waveform = new byte[Width * Height * 4];
        for (int index = 0; index < density.Length; index++)
        {
            if (density[index] == 0)
            {
                continue;
            }

            byte intensity = (byte)Math.Clamp(
                Math.Round(70 + (185 * Math.Sqrt(density[index] / (double)maximum))),
                0,
                byte.MaxValue);
            int offset = index * 4;
            waveform[offset] = (byte)(intensity / 3);
            waveform[offset + 1] = intensity;
            waveform[offset + 2] = (byte)(intensity / 2);
            waveform[offset + 3] = intensity;
        }

        return new VideoLumaWaveformData(waveform, sampleCount);
    }

    public static void Draw(CanvasDrawingSession session, Rect bounds, CanvasBitmap? bitmap)
    {
        session.FillRectangle(bounds, Color.FromArgb(255, 18, 22, 28));
        Rect plot = new(bounds.X + 1, bounds.Y + 1, Math.Max(0, bounds.Width - 2), Math.Max(0, bounds.Height - 2));
        if (plot.Width <= 0 || plot.Height <= 0)
        {
            return;
        }

        if (bitmap is not null)
        {
            session.DrawImage(bitmap, plot, new Rect(0, 0, Width, Height), 1);
        }

        Color gridColour = Color.FromArgb(90, 130, 145, 160);
        for (int division = 1; division < 4; division++)
        {
            float y = (float)(plot.Y + (plot.Height * division / 4));
            session.DrawLine((float)plot.X, y, (float)(plot.X + plot.Width), y, gridColour, 1);
        }
    }
}

public sealed record VideoVectorscopeData(int[] Density, byte[] BgraPixels, int SampleCount)
{
    public const int Width = 128;
    public const int Height = 128;
    private const double ChromaRange = 0.6;

    public static VideoVectorscopeData Analyse(ReadOnlySpan<byte> bgraPixels)
    {
        if (bgraPixels.Length % 4 != 0)
        {
            throw new ArgumentException("BGRA pixel data must contain complete four-byte pixels.", nameof(bgraPixels));
        }

        int[] density = new int[Width * Height];
        int[] blueSums = new int[density.Length];
        int[] greenSums = new int[density.Length];
        int[] redSums = new int[density.Length];
        int sampleCount = 0;
        for (int offset = 0; offset < bgraPixels.Length; offset += 4)
        {
            int alpha = bgraPixels[offset + 3];
            if (alpha == 0)
            {
                continue;
            }

            int blue = VideoRgbHistogramData.UnpremultiplyChannel(bgraPixels[offset], alpha);
            int green = VideoRgbHistogramData.UnpremultiplyChannel(bgraPixels[offset + 1], alpha);
            int red = VideoRgbHistogramData.UnpremultiplyChannel(bgraPixels[offset + 2], alpha);
            double luma = (0.2126 * red) + (0.7152 * green) + (0.0722 * blue);
            double cb = (blue - luma) / (byte.MaxValue * 2 * (1 - 0.0722));
            double cr = (red - luma) / (byte.MaxValue * 2 * (1 - 0.2126));
            int x = Math.Clamp((int)Math.Round(((cb / (2 * ChromaRange)) + 0.5) * (Width - 1)), 0, Width - 1);
            int y = Math.Clamp((int)Math.Round((0.5 - (cr / (2 * ChromaRange))) * (Height - 1)), 0, Height - 1);
            int index = (y * Width) + x;
            density[index]++;
            blueSums[index] += blue;
            greenSums[index] += green;
            redSums[index] += red;
            sampleCount++;
        }

        int maximum = density.Max();
        byte[] pixels = new byte[density.Length * 4];
        for (int index = 0; index < density.Length; index++)
        {
            int count = density[index];
            if (count == 0)
            {
                continue;
            }

            int alpha = (int)Math.Round(70 + (185 * Math.Sqrt(count / (double)maximum)));
            int offset = index * 4;
            pixels[offset] = Premultiply(blueSums[index] / count, alpha);
            pixels[offset + 1] = Premultiply(greenSums[index] / count, alpha);
            pixels[offset + 2] = Premultiply(redSums[index] / count, alpha);
            pixels[offset + 3] = (byte)alpha;
        }

        return new VideoVectorscopeData(density, pixels, sampleCount);
    }

    public static void Draw(CanvasDrawingSession session, Rect bounds, CanvasBitmap? bitmap)
    {
        session.FillRectangle(bounds, Color.FromArgb(255, 18, 22, 28));
        double diameter = Math.Max(0, Math.Min(bounds.Width, bounds.Height) - 8);
        if (diameter <= 0)
        {
            return;
        }

        float centreX = (float)(bounds.X + (bounds.Width / 2));
        float centreY = (float)(bounds.Y + (bounds.Height / 2));
        float radius = (float)(diameter / 2);
        Rect plot = new(centreX - radius, centreY - radius, diameter, diameter);
        if (bitmap is not null)
        {
            session.DrawImage(bitmap, plot, new Rect(0, 0, Width, Height), 1);
        }

        Color gridColour = Color.FromArgb(120, 130, 145, 160);
        session.DrawEllipse(centreX, centreY, radius, radius, gridColour, 1);
        session.DrawEllipse(centreX, centreY, radius * 0.5f, radius * 0.5f, Color.FromArgb(65, 130, 145, 160), 1);
        session.DrawLine(centreX - radius, centreY, centreX + radius, centreY, gridColour, 1);
        session.DrawLine(centreX, centreY - radius, centreX, centreY + radius, gridColour, 1);
    }

    private static byte Premultiply(int channel, int alpha) =>
        (byte)((channel * alpha + (byte.MaxValue / 2)) / byte.MaxValue);
}
