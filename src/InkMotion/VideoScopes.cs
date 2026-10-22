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

            blue[Unpremultiply(bgraPixels[offset], alpha)]++;
            green[Unpremultiply(bgraPixels[offset + 1], alpha)]++;
            red[Unpremultiply(bgraPixels[offset + 2], alpha)]++;
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

    private static int Unpremultiply(byte channel, int alpha) =>
        Math.Min(byte.MaxValue, (channel * byte.MaxValue + (alpha / 2)) / alpha);
}
