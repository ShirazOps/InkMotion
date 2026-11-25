using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Windows.UI;

namespace InkMotion;

public static partial class TimelineCompositor
{
    private static readonly Vector3 LumaWeights = new(0.2126f, 0.7152f, 0.0722f);

    /// <summary>Evaluates the enabled node graph once per node and returns a new image owned by the caller.</summary>
    private static CanvasRenderTarget CompositeGraphFrame(
        ICanvasResourceCreator device,
        ICanvasImage background,
        float targetWidth,
        float targetHeight,
        EditorProject project,
        InkCompositeGraph graph,
        int frameIndex,
        float width,
        float height,
        float sizeScale,
        double seconds,
        Matrix3x2 drawingTransform)
    {
        var nodes = graph.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var results = new Dictionary<string, CanvasRenderTarget?>(StringComparer.Ordinal);
        var evaluating = new HashSet<string>(StringComparer.Ordinal);

        CanvasRenderTarget? Input(InkCompositeNode node, int slot) =>
            slot < node.Inputs.Count && node.Inputs[slot] is string id && nodes.ContainsKey(id) ? Evaluate(id) : null;

        CanvasRenderTarget? Evaluate(string id)
        {
            if (results.TryGetValue(id, out CanvasRenderTarget? cached)) return cached;
            if (!evaluating.Add(id)) throw new InvalidOperationException("A compositing graph cannot contain loops.");
            InkCompositeNode node = nodes[id];
            CanvasRenderTarget? result = node.Kind switch
            {
                InkCompositeNodeKind.Picture => DrawGraphPicture(device, background, targetWidth, targetHeight),
                InkCompositeNodeKind.InkLayer => DrawGraphLayer(device, project, node, frameIndex, targetWidth, targetHeight,
                    width, height, sizeScale, seconds, drawingTransform),
                InkCompositeNodeKind.Text => DrawGraphText(device, project, frameIndex, targetWidth, targetHeight,
                    width, height, sizeScale, seconds, drawingTransform),
                InkCompositeNodeKind.Merge => MergeGraphImages(device, Input(node, 0), Input(node, 1), node, targetWidth, targetHeight),
                InkCompositeNodeKind.Blur => BlurGraphImage(device, Input(node, 0), node, sizeScale, targetWidth, targetHeight),
                InkCompositeNodeKind.Glow => Input(node, 0) is CanvasRenderTarget glowSource
                    ? InkLayerEffects.ApplyGlow(device, glowSource, new InkLayerProject
                    {
                        GlowRadius = node.Radius, GlowIntensity = node.Intensity, GlowColour = node.Colour,
                    }, sizeScale)
                    : null,
                InkCompositeNodeKind.Pixelate => PixelateGraphImage(device, Input(node, 0), node, sizeScale, targetWidth, targetHeight),
                InkCompositeNodeKind.ColourAdjust => AdjustGraphColour(device, Input(node, 0), node, targetWidth, targetHeight),
                InkCompositeNodeKind.Mask => MaskGraphImage(device, Input(node, 0), Input(node, 1), node.Invert, targetWidth, targetHeight),
                InkCompositeNodeKind.Output => null,
                _ => null,
            };
            evaluating.Remove(id);
            results[id] = result;
            return result;
        }

        try
        {
            InkCompositeNode? output = InkCompositeGraphs.GetOutput(graph);
            CanvasRenderTarget? final = output is null ? null : Input(output, 0);
            CanvasRenderTarget composite = CreateTarget(device, targetWidth, targetHeight);
            using CanvasDrawingSession session = composite.CreateDrawingSession();
            session.Clear(Color.FromArgb(0, 0, 0, 0));
            if (final is not null) session.DrawImage(final);
            return composite;
        }
        finally
        {
            foreach (CanvasRenderTarget? result in results.Values) result?.Dispose();
        }
    }

    private static CanvasRenderTarget DrawGraphPicture(ICanvasResourceCreator device, ICanvasImage background,
        float targetWidth, float targetHeight) =>
        DrawGraphTarget(device, targetWidth, targetHeight, session => session.DrawImage(background));

    private static CanvasRenderTarget? DrawGraphLayer(ICanvasResourceCreator device, EditorProject project,
        InkCompositeNode node, int frameIndex, float targetWidth, float targetHeight, float width, float height,
        float sizeScale, double seconds, Matrix3x2 drawingTransform)
    {
        InkLayerProject? layer = project.InkLayers.FirstOrDefault(candidate => candidate.Id == node.LayerId);
        bool paintedVisible = layer is not null && InkAutoMattes.IsPaintedVisible(layer, project.VideoTracks, seconds);
        if (layer is null || !layer.IsVisible ||
            (!paintedVisible && !InkAutoMattes.IsActive(layer, project.VideoTracks, seconds)))
        {
            return null;
        }

        CanvasRenderTarget result = DrawGraphTarget(device, targetWidth, targetHeight, session =>
        {
            session.Transform = drawingTransform;
            if (paintedVisible) DrawInkLayer(session, project, layer, frameIndex, width, height, sizeScale, seconds);
            if (layer.IsHoldout)
                InkAutoMattes.Draw(session, layer.AutoMatte, project.VideoTracks, seconds, width, height,
                    Color.FromArgb((byte)Math.Round(255 * Math.Clamp(layer.Opacity, 0, 1)), 255, 255, 255));
        });
        try
        {
            if (InkLayerEffects.HasPixelation(layer)) InkLayerEffects.ApplyPixelation(result, layer, sizeScale);
            if (!InkLayerEffects.HasGlow(layer)) return result;
            CanvasRenderTarget glowing = InkLayerEffects.ApplyGlow(device, result, layer, sizeScale);
            result.Dispose();
            return glowing;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private static CanvasRenderTarget DrawGraphText(ICanvasResourceCreator device, EditorProject project, int frameIndex,
        float targetWidth, float targetHeight, float width, float height, float sizeScale, double seconds,
        Matrix3x2 drawingTransform) =>
        DrawGraphTarget(device, targetWidth, targetHeight, session =>
        {
            session.Transform = drawingTransform;
            foreach (InkTextOverlay overlay in project.Frames[frameIndex].TextOverlays)
            {
                if (!InkObjectMotion.IsVisible(overlay.ObjectTracking, seconds) ||
                    !InkObjectMotion.IsSourceCurrent(overlay.ObjectTracking, project.VideoTracks)) continue;
                InkObjectMotion.Draw(session, overlay.ObjectTracking, seconds, width, height, drawing =>
                    DrawInkText(drawing, overlay, 1, width, height, sizeScale));
            }
        });

    private static CanvasRenderTarget? MergeGraphImages(ICanvasResourceCreator device, CanvasRenderTarget? background,
        CanvasRenderTarget? foreground, InkCompositeNode node, float targetWidth, float targetHeight)
    {
        if (background is null && (foreground is null || node.Opacity <= 0)) return null;
        return DrawGraphTarget(device, targetWidth, targetHeight, session =>
        {
            if (foreground is null || node.Opacity <= 0)
            {
                session.DrawImage(background);
                return;
            }

            if (node.BlendMode == InkLayerBlendMode.Normal || background is null)
            {
                if (background is not null) session.DrawImage(background);
                session.DrawImage(foreground, 0, 0, foreground.Bounds, (float)node.Opacity);
                return;
            }

            using var faded = new OpacityEffect { Source = foreground, Opacity = (float)node.Opacity };
            using var blend = new BlendEffect
            {
                Background = background,
                Foreground = node.Opacity < 1 ? faded : foreground,
                Mode = GetBlendEffectMode(node.BlendMode),
            };
            session.DrawImage(blend);
        });
    }

    private static CanvasRenderTarget? BlurGraphImage(ICanvasResourceCreator device, CanvasRenderTarget? source,
        InkCompositeNode node, float sizeScale, float targetWidth, float targetHeight)
    {
        if (source is null) return null;
        return DrawGraphTarget(device, targetWidth, targetHeight, session =>
        {
            using var blur = new GaussianBlurEffect
            {
                Source = source,
                BlurAmount = (float)Math.Min(250, node.Radius * sizeScale / 2),
                BorderMode = EffectBorderMode.Hard,
                Optimization = EffectOptimization.Quality,
            };
            session.DrawImage(blur);
        });
    }

    private static CanvasRenderTarget? PixelateGraphImage(ICanvasResourceCreator device, CanvasRenderTarget? source,
        InkCompositeNode node, float sizeScale, float targetWidth, float targetHeight)
    {
        if (source is null) return null;
        CanvasRenderTarget result = DrawGraphTarget(device, targetWidth, targetHeight, session => session.DrawImage(source));
        try
        {
            InkLayerEffects.ApplyPixelation(result, new InkLayerProject { PixelSize = node.Radius }, sizeScale);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private static CanvasRenderTarget? AdjustGraphColour(ICanvasResourceCreator device, CanvasRenderTarget? source,
        InkCompositeNode node, float targetWidth, float targetHeight)
    {
        if (source is null) return null;
        return DrawGraphTarget(device, targetWidth, targetHeight, session =>
        {
            using var hue = new HueRotationEffect { Source = source, Angle = (float)(node.Hue * Math.PI / 180) };
            using var adjust = new ColorMatrixEffect
            {
                Source = hue,
                AlphaMode = CanvasAlphaMode.Straight,
                ClampOutput = true,
                ColorMatrix = CreateColourAdjustMatrix(node.Brightness, node.Contrast, node.Saturation),
            };
            session.DrawImage(adjust);
        });
    }

    /// <summary>Luma-weighted saturation, then contrast about mid-grey, then a brightness offset, in straight alpha.</summary>
    public static Matrix5x4 CreateColourAdjustMatrix(double brightness, double contrast, double saturation)
    {
        float s = (float)saturation, c = (float)contrast;
        float Cell(float weight, bool diagonal) => c * (((1 - s) * weight) + (diagonal ? s : 0));
        float offset = (float)(0.5 - (0.5 * contrast) + brightness);
        return new Matrix5x4
        {
            M11 = Cell(LumaWeights.X, true), M12 = Cell(LumaWeights.X, false), M13 = Cell(LumaWeights.X, false),
            M21 = Cell(LumaWeights.Y, false), M22 = Cell(LumaWeights.Y, true), M23 = Cell(LumaWeights.Y, false),
            M31 = Cell(LumaWeights.Z, false), M32 = Cell(LumaWeights.Z, false), M33 = Cell(LumaWeights.Z, true),
            M44 = 1,
            M51 = offset, M52 = offset, M53 = offset,
        };
    }

    private static CanvasRenderTarget? MaskGraphImage(ICanvasResourceCreator device, CanvasRenderTarget? image,
        CanvasRenderTarget? matte, bool invert, float targetWidth, float targetHeight)
    {
        if (image is null) return null;
        return DrawGraphTarget(device, targetWidth, targetHeight, session =>
        {
            if (matte is null)
            {
                if (invert) session.DrawImage(image);
                return;
            }

            if (invert)
            {
                using var outside = new CompositeEffect { Mode = CanvasComposite.DestinationOut };
                outside.Sources.Add(image);
                outside.Sources.Add(matte);
                session.DrawImage(outside);
                return;
            }

            using var inside = new AlphaMaskEffect { Source = image, AlphaMask = matte };
            session.DrawImage(inside);
        });
    }

    private static CanvasRenderTarget DrawGraphTarget(ICanvasResourceCreator device, float targetWidth, float targetHeight,
        Action<CanvasDrawingSession> draw)
    {
        CanvasRenderTarget target = CreateTarget(device, targetWidth, targetHeight);
        try
        {
            using CanvasDrawingSession session = target.CreateDrawingSession();
            session.Clear(Color.FromArgb(0, 0, 0, 0));
            draw(session);
            return target;
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }
}
