using Microsoft.Graphics.Canvas;
using Xunit;

namespace InkMotion.Tests;

public sealed class InkAlphaLockTests
{
    private static List<InkPoint> Stroke(uint colour, double width, bool locked = false) =>
    [
        new() { X = 0.2, Y = 0.5, BrushWidth = width, Pressure = 1, BrushColour = colour, AlphaLocked = locked },
        new() { X = 0.8, Y = 0.5, BrushWidth = width, Pressure = 1, BrushColour = colour, AlphaLocked = locked },
    ];

    private static byte[] Render(EditorProject project)
    {
        using var device = new CanvasDevice(true);
        using var background = new CanvasRenderTarget(device, 100, 100, 96);
        using (var session = background.CreateDrawingSession()) session.Clear(Microsoft.UI.Colors.Transparent);
        using CanvasRenderTarget result = TimelineCompositor.CompositeInkFrame(device, background, 100, 100, project, 0, 100, 100, 1);
        return result.GetPixelBytes();
    }

    private static byte[] Alpha(byte[] pixels) => pixels.Where((_, index) => index % 4 == 3).ToArray();

    [Fact]
    public void SourceAtopPaintKeepsExactAlphaIncludingSoftEdgesAndTransparentPixels()
    {
        byte[] destination = [40, 20, 10, 80, 0, 0, 0, 0, 50, 100, 150, 200];
        byte[] paint = [0, 0, 255, 255, 255, 255, 255, 255, 20, 30, 40, 80];
        InkAlphaLock.Paint(destination, paint);
        Assert.Equal(new byte[] { 0, 0, 80, 80 }, destination[..4]);
        Assert.Equal(new byte[4], destination[4..8]);
        Assert.Equal(new byte[] { 80, 0, 200 }, Alpha(destination));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0.5)]
    public void PaintingRecoloursWithoutIncreasingLayerAlphaAndOpacityIsAppliedOnce(double opacity)
    {
        var project = new EditorProject();
        project.InkLayers[0].Opacity = opacity;
        project.Frames[0].Strokes = [Stroke(0x800000FF, 8)];
        byte[] original = Render(project);
        project.Frames[0].Strokes.Add(Stroke(0xFFFF0000, 40, true));
        byte[] painted = Render(project);
        Assert.Equal(Alpha(original), Alpha(painted));
        int centre = (50 * 100 + 50) * 4;
        Assert.Equal(painted[centre + 3], painted[centre + 2]);
        Assert.Equal(0, painted[centre]);
        project.CompositeGraph = InkCompositeGraphs.FromLayerStack(project.InkLayers);
        Assert.Equal(painted, Render(project));
    }

    [Theory]
    [InlineData(InkBrushTexture.Blur)]
    [InlineData(InkBrushTexture.Smudge)]
    public void AlphaLockedModifiersCannotSpreadOrRemoveExistingAlpha(InkBrushTexture texture)
    {
        var project = new EditorProject();
        project.Frames[0].Strokes = [Stroke(0xFFFFFFFF, 8)];
        byte[] original = Render(project);
        List<InkPoint> modifier = Stroke(0xFFFF0000, 30, true);
        modifier[0].Y = 0.5; modifier[1].Y = 0.65;
        foreach (InkPoint point in modifier) point.BrushTexture = texture;
        project.Frames[0].Strokes.Add(modifier);
        Assert.Equal(Alpha(original), Alpha(Render(project)));
    }

    [Fact]
    public void AlphaLockedBucketFillRecoloursOnlyConnectedArtworkAndHandlesCanvasEdges()
    {
        var project = new EditorProject();
        List<InkPoint> left = InkStrokeEditing.CreateShape(InkShape.Rectangle, 0, 0.2, 0.35, 0.8, 2, 0xFF0000FF, true);
        List<InkPoint> right = InkStrokeEditing.CreateShape(InkShape.Rectangle, 0.65, 0.2, 1, 0.8, 2, 0xFF0000FF, true);
        project.Frames[0].Strokes = [left, right];
        byte[] original = Render(project);
        using var device = new CanvasDevice(true);
        List<InkPoint> fill = InkBucketFill.Create(device, project.Frames[0].Strokes, 100, 100, 20, 50, 0xFFFF0000, alphaLocked: true);
        Assert.All(fill, point => Assert.True(point.AlphaLocked));
        project.Frames[0].Strokes.Add(fill);
        byte[] painted = Render(project);
        Assert.Equal(Alpha(original), Alpha(painted));
        Assert.Equal(255, painted[(50 * 100 + 20) * 4 + 2]);
        Assert.Equal(255, painted[(50 * 100 + 80) * 4]);
        Assert.Throws<InvalidOperationException>(() => InkBucketFill.Create(device, project.Frames[0].Strokes, 100, 100, 50, 50, 0xFFFF0000, true));
    }

    [Fact]
    public void SavedRestrictionsSurviveCopiesErasureAndSelectionTransforms()
    {
        var project = new EditorProject();
        project.InkLayers[0].AlphaLock = true;
        project.Frames[0].Strokes = [Stroke(0xFF0000FF, 8), Stroke(0xFFFF0000, 40, true)];
        EditorProject restored = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        EditorProject copy = CompoundTimelines.Clone(restored);
        Assert.True(copy.InkLayers[0].AlphaLock);
        Assert.True(copy.Frames[0].Strokes[1][0].AlphaLocked);
        Assert.True(InkStrokeEditing.ReflectAcrossVerticalAxis(copy.Frames[0].Strokes[1][0]).AlphaLocked);
        Assert.True(InkStrokeEditing.Stabilise(copy.Frames[0].Strokes[1][0], null, 0.2).AlphaLocked);
        InkStrokeEditing.Erase(copy.Frames[0].Strokes, 50, 50, 100, 100, 4, wholeStroke: true);
        Assert.Empty(copy.Frames[0].Strokes);
        Assert.Equal(2, restored.Frames[0].Strokes.Count);
        copy = CompoundTimelines.Clone(restored);
        copy.Frames[0].Strokes.RemoveAt(0);
        Assert.All(Alpha(Render(copy)), alpha => Assert.Equal(0, alpha));
        restored.Frames[0].Strokes[1][1].AlphaLocked = false;
        Assert.Throws<InvalidDataException>(() => EditorProjectSerialiser.Serialise(restored));
        Assert.False(EditorProjectSerialiser.Deserialise("""{"Version":89,"Frames":[{"Number":1}]}""").InkLayers[0].AlphaLock);
    }
}
