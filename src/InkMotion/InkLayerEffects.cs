using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;

namespace InkMotion;

/// <summary>Non-destructive drawing-layer effects rendered after the layer's strokes and before blending.</summary>
public static class InkLayerEffects
{
    public const double MaxGlowRadius = 120;
    public const double MaxGlowIntensity = 3;
    public const double MinPixelSize = 2;
    public const double MaxPixelSize = 64;

    public static bool IsValid(InkLayerProject layer) =>
        double.IsFinite(layer.GlowRadius) && layer.GlowRadius is >= 0 and <= MaxGlowRadius &&
        double.IsFinite(layer.GlowIntensity) && layer.GlowIntensity is >= 0 and <= MaxGlowIntensity &&
        double.IsFinite(layer.PixelSize) && (layer.PixelSize == 0 || layer.PixelSize is >= MinPixelSize and <= MaxPixelSize);

    public static bool HasGlow(InkLayerProject layer) => layer.GlowRadius > 0 && layer.GlowIntensity > 0;

    public static bool HasPixelation(InkLayerProject layer) => layer.PixelSize >= MinPixelSize;

    public static bool HasEffects(InkLayerProject layer) => HasGlow(layer) || HasPixelation(layer);

    /// <summary>Averages premultiplied pixels in square cells anchored to the output origin, in place.</summary>
    public static void ApplyPixelation(CanvasRenderTarget layer, InkLayerProject settings, float sizeScale)
    {
        int cell = Math.Max(2, (int)Math.Round(settings.PixelSize * sizeScale));
        int width = (int)layer.SizeInPixels.Width;
        int height = (int)layer.SizeInPixels.Height;
        byte[] pixels = layer.GetPixelBytes();
        if (layer.Format == Windows.Graphics.DirectX.DirectXPixelFormat.R16G16B16A16Float)
            PixelateHalf(pixels, width, height, cell);
        else
            Pixelate(pixels, width, height, cell);
        layer.SetPixelBytes(pixels);
    }

    /// <summary>Half-float RGBA variant used by HDR composites, preserving values above 1.</summary>
    public static void PixelateHalf(byte[] rgbaHalf, int width, int height, int cell)
    {
        Span<Half> pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(rgbaHalf.AsSpan());
        Span<double> sum = stackalloc double[4];
        for (int top = 0; top < height; top += cell)
        {
            int bottom = Math.Min(height, top + cell);
            for (int left = 0; left < width; left += cell)
            {
                int right = Math.Min(width, left + cell);
                int count = (bottom - top) * (right - left);
                sum.Clear();
                for (int y = top; y < bottom; y++)
                    for (int index = (y * width + left) * 4, end = (y * width + right) * 4; index < end; index++)
                        sum[index & 3] += (double)pixels[index];
                if (sum[3] <= 0) continue;
                for (int y = top; y < bottom; y++)
                    for (int index = (y * width + left) * 4, end = (y * width + right) * 4; index < end; index++)
                        pixels[index] = (Half)(sum[index & 3] / count);
            }
        }
    }

    public static void Pixelate(byte[] bgra, int width, int height, int cell)
    {
        for (int top = 0; top < height; top += cell)
        {
            int bottom = Math.Min(height, top + cell);
            for (int left = 0; left < width; left += cell)
            {
                int right = Math.Min(width, left + cell);
                int count = (bottom - top) * (right - left);
                int b = 0, g = 0, r = 0, a = 0;
                for (int y = top; y < bottom; y++)
                {
                    for (int x = left, index = (y * width + left) * 4; x < right; x++, index += 4)
                    {
                        b += bgra[index];
                        g += bgra[index + 1];
                        r += bgra[index + 2];
                        a += bgra[index + 3];
                    }
                }

                if (a == 0) continue;
                byte mb = (byte)((b + count / 2) / count), mg = (byte)((g + count / 2) / count);
                byte mr = (byte)((r + count / 2) / count), ma = (byte)((a + count / 2) / count);
                for (int y = top; y < bottom; y++)
                {
                    for (int x = left, index = (y * width + left) * 4; x < right; x++, index += 4)
                    {
                        bgra[index] = mb;
                        bgra[index + 1] = mg;
                        bgra[index + 2] = mr;
                        bgra[index + 3] = ma;
                    }
                }
            }
        }
    }

    /// <summary>Returns a new image containing a tinted, blurred halo beneath the layer artwork.</summary>
    public static CanvasRenderTarget ApplyGlow(ICanvasResourceCreator device, CanvasRenderTarget layer,
        InkLayerProject settings, float sizeScale)
    {
        float width = (float)layer.Size.Width;
        float height = (float)layer.Size.Height;
        var result = new CanvasRenderTarget(device, width, height, 96, layer.Format, CanvasAlphaMode.Premultiplied);
        try
        {
            using var blur = new GaussianBlurEffect
            {
                Source = layer,
                BlurAmount = (float)Math.Min(250, settings.GlowRadius * sizeScale / 2),
                BorderMode = EffectBorderMode.Soft,
                Optimization = EffectOptimization.Quality,
            };
            float colourAlpha = ((settings.GlowColour >> 24) & 0xFF) / 255f;
            using var tint = new ColorMatrixEffect
            {
                Source = blur,
                AlphaMode = CanvasAlphaMode.Straight,
                ClampOutput = true,
                ColorMatrix = new Matrix5x4
                {
                    M44 = (float)settings.GlowIntensity * colourAlpha,
                    M51 = ((settings.GlowColour >> 16) & 0xFF) / 255f,
                    M52 = ((settings.GlowColour >> 8) & 0xFF) / 255f,
                    M53 = (settings.GlowColour & 0xFF) / 255f,
                },
            };
            using CanvasDrawingSession session = result.CreateDrawingSession();
            session.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            session.DrawImage(tint);
            session.DrawImage(layer);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }
}
