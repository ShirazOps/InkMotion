using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;

namespace InkMotion;

/// <summary>
/// Drawing shared by the live preview and the exporter, so titles, images and ink render identically at any
/// output size. Positions are normalised to the frame; sizes are scaled from their authored reference.
/// </summary>
public static class TimelineCompositor
{
    /// <summary>Title font sizes are authored against a 1080-pixel-high frame.</summary>
    public const double TitleReferenceHeight = 1080;

    public static Color ToColour(uint argb) => Color.FromArgb(
        (byte)(argb >> 24),
        (byte)(argb >> 16),
        (byte)(argb >> 8),
        (byte)argb);

    public static Color WithOpacity(Color colour, double opacity) =>
        Color.FromArgb((byte)(colour.A * Math.Clamp(opacity, 0, 1)), colour.R, colour.G, colour.B);

    public static double GetInkWidth(InkPoint point) =>
        point.BrushWidth * (0.25 + (1.5 * point.Pressure));

    /// <summary>Fits a source into the frame preserving aspect ratio, then scales and centres it on (x, y).</summary>
    public static Rect GetFitRect(Size source, double width, double height, double scale, double x, double y)
    {
        if (source.Width <= 0 || source.Height <= 0 || width <= 0 || height <= 0)
        {
            return Rect.Empty;
        }

        double fit = Math.Min(width / source.Width, height / source.Height) * scale;
        double drawWidth = source.Width * fit;
        double drawHeight = source.Height * fit;
        return new Rect((x * width) - (drawWidth / 2), (y * height) - (drawHeight / 2), drawWidth, drawHeight);
    }

    public static void DrawFitted(
        CanvasDrawingSession session,
        CanvasBitmap bitmap,
        Rect sourceRect,
        float width,
        float height,
        double scale,
        double x,
        double y,
        float opacity,
        double rotationDegrees = 0)
    {
        Rect destination = GetFitRect(new Size(sourceRect.Width, sourceRect.Height), width, height, scale, x, y);
        if (!destination.IsEmpty && opacity > 0)
        {
            Matrix3x2 previous = session.Transform;
            try
            {
                session.Transform = Matrix3x2.CreateRotation((float)(rotationDegrees * Math.PI / 180),
                    new Vector2((float)(x * width), (float)(y * height))) * previous;
                session.DrawImage(bitmap, destination, sourceRect, opacity);
            }
            finally
            {
                session.Transform = previous;
            }
        }
    }

    public static void DrawTitle(
        CanvasDrawingSession session,
        TimelineClipProject clip,
        float width,
        float height,
        float opacity)
    {
        float fontSize = (float)(clip.FontSize * height / TitleReferenceHeight);
        if (fontSize < 1 || string.IsNullOrWhiteSpace(clip.Text))
        {
            return;
        }

        using var format = new CanvasTextFormat
        {
            FontFamily = clip.FontFamily,
            FontSize = fontSize,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.WholeWord,
        };
        float boxWidth = width * 0.9f;
        using var layout = new CanvasTextLayout(session, clip.Text, format, boxWidth, height);
        var origin = new Vector2((float)(clip.X * width) - (boxWidth / 2), (float)(clip.Y * height) - (height / 2));
        Color colour = ToColour(clip.Colour);
        byte alpha = (byte)(colour.A * Math.Clamp(opacity, 0, 1));
        float shadowOffset = Math.Max(1, fontSize * 0.04f);
        session.DrawTextLayout(layout, origin + new Vector2(shadowOffset), Color.FromArgb((byte)(alpha * 0.6), 0, 0, 0));
        session.DrawTextLayout(layout, origin, Color.FromArgb(alpha, colour.R, colour.G, colour.B));
    }

    /// <summary>Draws a pressure-sensitive stroke; <paramref name="widthScale"/> maps authored brush sizes to the target.</summary>
    public static void DrawInkStroke(
        CanvasDrawingSession session,
        IReadOnlyList<InkPoint> stroke,
        Color colour,
        float width,
        float height,
        float widthScale)
    {
        for (int i = 0; i < stroke.Count; i++)
        {
            InkPoint point = stroke[i];
            var position = new Vector2((float)(point.X * width), (float)(point.Y * height));
            double brush = GetInkWidth(point) * widthScale;
            session.FillCircle(position, (float)(brush / 2), colour);
            if (i == 0)
            {
                continue;
            }

            InkPoint previous = stroke[i - 1];
            session.DrawLine(
                new Vector2((float)(previous.X * width), (float)(previous.Y * height)),
                position,
                colour,
                (float)(((GetInkWidth(previous) * widthScale) + brush) / 2));
        }
    }

    public static void DrawInkText(
        CanvasDrawingSession session,
        InkTextOverlay overlay,
        double opacity,
        float width,
        float height,
        float sizeScale)
    {
        float fontSize = (float)(overlay.FontSize * sizeScale);
        if (fontSize < 1 || string.IsNullOrWhiteSpace(overlay.Text))
        {
            return;
        }

        using var format = new CanvasTextFormat
        {
            FontFamily = overlay.FontFamily,
            FontSize = fontSize,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
        };
        session.DrawText(
            overlay.Text,
            new Vector2((float)(overlay.X * width), (float)(overlay.Y * height)),
            WithOpacity(ToColour(overlay.Colour), opacity),
            format);
    }

    /// <summary>Draws every visible ink layer and the frame's text overlays for one animation drawing.</summary>
    public static void DrawInkFrame(
        CanvasDrawingSession session,
        EditorProject project,
        int frameIndex,
        float width,
        float height,
        float sizeScale)
    {
        foreach (InkLayerProject layer in project.InkLayers.Where(layer => layer.IsVisible))
        {
            IReadOnlyList<List<InkPoint>> strokes = InkAnimation.GetDisplayedStrokes(
                project.Frames,
                frameIndex,
                frame => ExportPlanning.GetLayerStrokes(frame, layer.Id));
            foreach (List<InkPoint> stroke in strokes)
            {
                if (stroke.Count > 0)
                {
                    Color colour = WithOpacity(ToColour(stroke[0].BrushColour), layer.Opacity);
                    DrawInkStroke(session, stroke, colour, width, height, sizeScale);
                }
            }
        }

        foreach (InkTextOverlay overlay in project.Frames[frameIndex].TextOverlays)
        {
            DrawInkText(session, overlay, 1, width, height, sizeScale);
        }
    }
}
