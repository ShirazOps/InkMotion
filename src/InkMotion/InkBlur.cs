using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;

namespace InkMotion;

internal static class InkBlur
{
    public static void Apply(CanvasRenderTarget layer, IReadOnlyList<InkPoint> stroke,
        float width, float height, float sizeScale)
    {
        using var blurred = new CanvasRenderTarget(layer, (float)layer.Size.Width, (float)layer.Size.Height, 96);
        using var mask = new CanvasRenderTarget(layer, (float)layer.Size.Width, (float)layer.Size.Height, 96);
        using var effect = new GaussianBlurEffect
        {
            Source = layer,
            BlurAmount = Math.Min(250, (float)(stroke[0].BrushWidth * 0.15 * sizeScale)),
            BorderMode = EffectBorderMode.Soft,
        };
        using (CanvasDrawingSession drawing = blurred.CreateDrawingSession())
        {
            drawing.Clear(Microsoft.UI.Colors.Transparent);
            drawing.DrawImage(effect);
        }

        List<InkPoint> footprint = InkEditing.CloneStrokes([stroke.ToList()])[0];
        foreach (InkPoint point in footprint) point.BrushTexture = InkBrushTexture.Round;
        using (CanvasDrawingSession drawing = mask.CreateDrawingSession())
        {
            drawing.Clear(Microsoft.UI.Colors.Transparent);
            TimelineCompositor.DrawInkStroke(drawing, footprint, Microsoft.UI.Colors.White, width, height, sizeScale);
        }
        byte[] pixels = layer.GetPixelBytes();
        byte[] softened = blurred.GetPixelBytes();
        byte[] coverage = mask.GetPixelBytes();
        for (int pixel = 0; pixel < pixels.Length; pixel += 4)
        {
            double strength = coverage[pixel + 3] / 255d * 0.7;
            if (strength == 0) continue;
            for (int channel = 0; channel < 4; channel++)
                pixels[pixel + channel] = (byte)Math.Round(
                    pixels[pixel + channel] * (1 - strength) + softened[pixel + channel] * strength);
        }
        layer.SetPixelBytes(pixels);
    }
}
