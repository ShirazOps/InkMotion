using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;

namespace InkMotion;

/// <summary>Non-destructive drawing-layer effects rendered after the layer's strokes and before blending.</summary>
public static class InkLayerEffects
{
    public const double MaxGlowRadius = 120;
    public const double MaxGlowIntensity = 3;

    public static bool IsValid(InkLayerProject layer) =>
        double.IsFinite(layer.GlowRadius) && layer.GlowRadius is >= 0 and <= MaxGlowRadius &&
        double.IsFinite(layer.GlowIntensity) && layer.GlowIntensity is >= 0 and <= MaxGlowIntensity;

    public static bool HasGlow(InkLayerProject layer) => layer.GlowRadius > 0 && layer.GlowIntensity > 0;

    public static bool HasEffects(InkLayerProject layer) => HasGlow(layer);

    /// <summary>Returns a new image containing a tinted, blurred halo beneath the layer artwork.</summary>
    public static CanvasRenderTarget ApplyGlow(ICanvasResourceCreator device, CanvasRenderTarget layer,
        InkLayerProject settings, float sizeScale)
    {
        float width = (float)layer.Size.Width;
        float height = (float)layer.Size.Height;
        var result = new CanvasRenderTarget(device, width, height, 96,
            Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
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
