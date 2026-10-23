using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
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
        double rotationDegrees = 0,
        TimelineClipProject? colourGrade = null,
        VideoColourGradingEffect? colourGradeEffect = null,
        VideoChromaKeyEffect? chromaKeyEffect = null,
        VideoBlurCanvasEffect? blurEffect = null,
        VideoVignetteCanvasEffect? vignetteEffect = null,
        int clipFrameOffset = 0,
        VideoSharpenCanvasEffect? sharpenEffect = null,
        VideoColourGradingValue? animatedColourGrade = null)
    {
        Rect destination = GetFitRect(new Size(sourceRect.Width, sourceRect.Height), width, height, scale, x, y);
        if (!destination.IsEmpty && opacity > 0)
        {
            Matrix3x2 previous = session.Transform;
            VideoColourGradingEffect? temporaryColourGradeEffect = null;
            try
            {
                session.Transform = Matrix3x2.CreateRotation((float)(rotationDegrees * Math.PI / 180),
                    new Vector2((float)(x * width), (float)(y * height))) * previous;
                VideoColourGradingValue gradingValue = animatedColourGrade ??
                    (colourGrade is null
                        ? new VideoColourGradingValue(0, 0, 0, 0, 1)
                        : VideoColourGradingKeyframes.GetStaticValue(colourGrade));
                if (colourGrade is null || VideoColourGrading.IsNeutral(colourGrade, gradingValue))
                {
                    ICanvasImage output = bitmap;
                    if (colourGrade is not null && !string.IsNullOrWhiteSpace(colourGrade.ColourLutPath))
                    {
                        VideoColourGradingEffect effect = colourGradeEffect ??
                            (temporaryColourGradeEffect = new VideoColourGradingEffect());
                        output = effect.GetOutput(bitmap, colourGrade, gradingValue);
                    }

                    if (colourGrade?.ChromaKeyEnabled == true)
                    {
                        output = (chromaKeyEffect ??
                            throw new InvalidOperationException("The active chroma-key effect was not provided."))
                            .GetOutput(output, colourGrade);
                    }

                    if (colourGrade is not null && colourGrade.BlurAmount > 0)
                    {
                        output = (blurEffect ??
                            throw new InvalidOperationException("The active blur effect was not provided."))
                            .GetOutput(output, colourGrade);
                    }

                    if (colourGrade is not null && colourGrade.VignetteAmount > 0)
                    {
                        output = (vignetteEffect ??
                            throw new InvalidOperationException("The active vignette effect was not provided."))
                            .GetOutput(output, colourGrade);
                    }

                    if (colourGrade is not null && colourGrade.SharpenAmount > 0)
                    {
                        output = (sharpenEffect ??
                            throw new InvalidOperationException("The active sharpen effect was not provided."))
                            .GetOutput(output, colourGrade);
                    }

                    DrawWithMask(session, output, destination, sourceRect, opacity, colourGrade, clipFrameOffset, width, height);
                }
                else
                {
                    VideoColourGradingEffect effect = colourGradeEffect ??
                        (temporaryColourGradeEffect = new VideoColourGradingEffect());
                    ICanvasImage output = effect.GetOutput(bitmap, colourGrade, gradingValue);
                    if (colourGrade.ChromaKeyEnabled)
                    {
                        output = (chromaKeyEffect ??
                            throw new InvalidOperationException("The active chroma-key effect was not provided."))
                            .GetOutput(output, colourGrade);
                    }

                    if (colourGrade.BlurAmount > 0)
                    {
                        output = (blurEffect ??
                            throw new InvalidOperationException("The active blur effect was not provided."))
                            .GetOutput(output, colourGrade);
                    }

                    if (colourGrade.VignetteAmount > 0)
                    {
                        output = (vignetteEffect ??
                            throw new InvalidOperationException("The active vignette effect was not provided."))
                            .GetOutput(output, colourGrade);
                    }

                    if (colourGrade.SharpenAmount > 0)
                    {
                        output = (sharpenEffect ??
                            throw new InvalidOperationException("The active sharpen effect was not provided."))
                            .GetOutput(output, colourGrade);
                    }

                    DrawWithMask(session, output, destination, sourceRect, opacity, colourGrade, clipFrameOffset, width, height);
                }
            }

            finally
            {
                temporaryColourGradeEffect?.Dispose();
                session.Transform = previous;
            }
        }
    }

    private static void DrawWithMask(
        CanvasDrawingSession session,
        ICanvasImage output,
        Rect destination,
        Rect sourceRect,
        float opacity,
        TimelineClipProject? clip,
        int clipFrameOffset,
        float width,
        float height)
    {
        if (clip is null || clip.MaskShape == VideoMaskShape.None)
        {
            session.DrawImage(output, destination, sourceRect, opacity);
            return;
        }

        VideoClipMask.DrawMaskedImage(
            session, output, destination, sourceRect, opacity, clip, clipFrameOffset, width, height);
    }

    public static void DrawTitle(
        CanvasDrawingSession session,
        TimelineClipProject clip,
        float width,
        float height,
        float opacity,
        VideoTransformValue transform)
    {
        float fontSize = (float)(clip.FontSize * transform.Scale * height / TitleReferenceHeight);
        if (fontSize < 1 || string.IsNullOrWhiteSpace(clip.Text))
        {
            return;
        }

        Matrix3x2 previous = session.Transform;
        try
        {
            session.Transform = Matrix3x2.CreateRotation(
                (float)(transform.RotationDegrees * Math.PI / 180),
                new Vector2((float)(transform.X * width), (float)(transform.Y * height))) * previous;
            DrawTitleText(session, clip, width, height, opacity, fontSize, transform);
        }
        finally
        {
            session.Transform = previous;
        }
    }

    private static void DrawTitleText(
        CanvasDrawingSession session,
        TimelineClipProject clip,
        float width,
        float height,
        float opacity,
        float fontSize,
        VideoTransformValue transform)
    {
        using var format = new CanvasTextFormat
        {
            FontFamily = clip.FontFamily,
            FontSize = fontSize,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.WholeWord,
        };
        float boxWidth = width * 0.9f * (float)transform.Scale;
        float boxHeight = height * (float)transform.Scale;
        using var layout = new CanvasTextLayout(session, clip.Text, format, boxWidth, boxHeight);
        var origin = new Vector2(
            (float)(transform.X * width) - (boxWidth / 2),
            (float)(transform.Y * height) - (boxHeight / 2));
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
        if (stroke.Count >= 3 && stroke[0].IsFilled)
        {
            using var path = new CanvasPathBuilder(session);
            path.SetFilledRegionDetermination(CanvasFilledRegionDetermination.Alternate);
            path.BeginFigure((float)(stroke[0].X * width), (float)(stroke[0].Y * height));
            foreach (InkPoint point in stroke.Skip(1))
            {
                path.AddLine((float)(point.X * width), (float)(point.Y * height));
            }
            path.EndFigure(CanvasFigureLoop.Closed);
            using var geometry = CanvasGeometry.CreatePath(path);
            session.FillGeometry(geometry, colour);
            return;
        }

        if (stroke.Count > 0 && stroke[0].BrushTexture == InkBrushTexture.Grain)
        {
            InkGrainBrush.Draw(session, stroke, colour, width, height, widthScale);
            return;
        }

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

        using CanvasTextLayout layout = CreateInkTextLayout(session, overlay, fontSize, opacity);
        session.DrawTextLayout(layout,
            new Vector2((float)(overlay.X * width), (float)(overlay.Y * height)),
            WithOpacity(ToColour(overlay.Colour), opacity));
    }

    public static Rect GetInkTextBounds(ICanvasResourceCreator device, InkTextOverlay overlay,
        float width, float height, float sizeScale = 1)
    {
        if (overlay.FontSize * sizeScale < 1 || string.IsNullOrWhiteSpace(overlay.Text))
        {
            return Rect.Empty;
        }

        using CanvasTextLayout layout = CreateInkTextLayout(device, overlay, (float)(overlay.FontSize * sizeScale));
        Rect bounds = layout.DrawBounds;
        return new Rect(bounds.X + overlay.X * width, bounds.Y + overlay.Y * height, bounds.Width, bounds.Height);
    }

    public static CanvasTextLayout CreateInkTextLayout(ICanvasResourceCreator device, InkTextOverlay overlay,
        float fontSize, double opacity = 1)
    {
        using var format = new CanvasTextFormat
        {
            FontFamily = overlay.FontFamily,
            FontSize = fontSize,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };
        var layout = new CanvasTextLayout(device, overlay.Text, format, 0, 0);
        try
        {
            foreach (InkTextStyleRange range in overlay.StyleRanges)
            {
                layout.SetFontFamily(range.Start, range.Length, range.FontFamily);
                layout.SetFontSize(range.Start, range.Length, (float)(range.FontSize * fontSize / overlay.FontSize));
                layout.SetFontWeight(range.Start, range.Length,
                    new Windows.UI.Text.FontWeight { Weight = (ushort)(range.Bold ? 700 : 400) });
                layout.SetFontStyle(range.Start, range.Length,
                    range.Italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal);
                layout.SetUnderline(range.Start, range.Length, range.Underline);
                layout.SetColor(range.Start, range.Length, WithOpacity(ToColour(range.Colour), opacity));
            }
            return layout;
        }
        catch
        {
            layout.Dispose();
            throw;
        }
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
