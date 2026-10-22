using System.Numerics;
using Microsoft.Graphics.Canvas;

namespace InkMotion;

public static class InkColourSampling
{
    public static uint Sample(ICanvasResourceCreator device, EditorProject project, int frameIndex,
        double width, double height, double x, double y) =>
        SampleComposite(device, project, frameIndex, width, height, x, y, 0, (_, _, _) => { });

    public static uint SampleComposite(ICanvasResourceCreator device, EditorProject project, int frameIndex,
        double width, double height, double x, double y, uint background,
        Action<CanvasDrawingSession, float, float> drawMedia)
    {
        ArgumentNullException.ThrowIfNull(drawMedia);
        (int pixelX, int pixelY) = GetPixel(x, y, width, height);
        using var pixel = new CanvasRenderTarget(device, 1, 1, 96,
            Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
        using (CanvasDrawingSession session = pixel.CreateDrawingSession())
        {
            session.Clear(TimelineCompositor.ToColour(background));
            session.Transform = Matrix3x2.CreateTranslation(-pixelX, -pixelY);
            drawMedia(session, (float)width, (float)height);
            TimelineCompositor.DrawInkFrame(session, project, frameIndex, (float)width, (float)height, 1);
        }

        byte[] bytes = pixel.GetPixelBytes();
        return FromPremultipliedBgra(bytes[0], bytes[1], bytes[2], bytes[3]);
    }

    public static (int X, int Y) GetPixel(double x, double y, double width, double height)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) ||
            !double.IsFinite(height) || width < 1 || height < 1 ||
            x < 0 || y < 0 || x > width || y > height)
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Choose a point inside the drawing canvas.");
        }

        return ((int)Math.Min(Math.Floor(x), Math.Ceiling(width) - 1),
            (int)Math.Min(Math.Floor(y), Math.Ceiling(height) - 1));
    }

    public static uint FromPremultipliedBgra(byte blue, byte green, byte red, byte alpha)
    {
        if (alpha == 0)
        {
            return 0;
        }

        uint r = (uint)Math.Clamp((int)Math.Round(red * 255d / alpha), 0, 255);
        uint g = (uint)Math.Clamp((int)Math.Round(green * 255d / alpha), 0, 255);
        uint b = (uint)Math.Clamp((int)Math.Round(blue * 255d / alpha), 0, 255);
        return ((uint)alpha << 24) | (r << 16) | (g << 8) | b;
    }
}
