using Xunit;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;

namespace InkMotion.Tests;

public sealed class InkColourSamplingTests
{
    [Fact]
    public void CompositeSamplesMediaThenInkOverThePreviewBackgroundWithoutChangingProject()
    {
        using var device = new CanvasDevice(true);
        using var image = CanvasBitmap.CreateFromColors(device, [Microsoft.UI.Colors.Blue], 1, 1);
        var project = new EditorProject
        {
            InkLayers = [new() { Id = "ink-1", Opacity = 0.5, IsLocked = true }],
            Frames = [new()
            {
                Strokes = [[new() { X = 0.5, Y = 0.5, BrushWidth = 30, Pressure = 1, BrushColour = 0xFFFF0000 }]],
            }],
        };
        string before = EditorProjectSerialiser.Serialise(project);
        void DrawMedia(CanvasDrawingSession session, float width, float height) =>
            session.DrawImage(image, new Rect(25, 25, 50, 50), image.Bounds);

        uint mixed = InkColourSampling.SampleComposite(device, project, 0, 100, 100, 50, 50,
            0xFF07080A, DrawMedia);

        Assert.Equal(0xFF7F0080u, mixed);
        Assert.Equal(0xFF0000FFu, InkColourSampling.SampleComposite(device, project, 0,
            100, 100, 30, 30, 0xFF07080A, DrawMedia));
        Assert.Equal(0xFF07080Au, InkColourSampling.SampleComposite(device, project, 0,
            100, 100, 1, 1, 0xFF07080A, DrawMedia));
        Assert.Equal(before, EditorProjectSerialiser.Serialise(project));
    }

    [Theory]
    [InlineData(10, 10)]
    [InlineData(50, 50)]
    [InlineData(70, 30)]
    [InlineData(99, 99)]
    public void CompositePixelMatchesFullFrameWithTransformedMediaAndText(int x, int y)
    {
        using var device = new CanvasDevice(true);
        using var image = CanvasBitmap.CreateFromColors(device, [Microsoft.UI.Colors.Green], 1, 1);
        var project = new EditorProject();
        var clip = new TimelineClipProject { Text = "Preview", Colour = 0xFF00FFFF, FontSize = 300 };
        void DrawMedia(CanvasDrawingSession session, float width, float height)
        {
            TimelineCompositor.DrawFitted(session, image, image.Bounds, width, height,
                0.5, 0.6, 0.4, 0.5f, rotationDegrees: 25);
            TimelineCompositor.DrawTitle(session, clip, width, height, 1);
        }

        using var target = new CanvasRenderTarget(device, 100, 100, 96);
        using (CanvasDrawingSession session = target.CreateDrawingSession())
        {
            session.Clear(TimelineCompositor.ToColour(0xFF07080A));
            DrawMedia(session, 100, 100);
            TimelineCompositor.DrawInkFrame(session, project, 0, 100, 100, 1);
        }

        byte[] expected = target.GetPixelBytes(x, y, 1, 1);
        uint actual = InkColourSampling.SampleComposite(device, project, 0, 100, 100,
            x, y, 0xFF07080A, DrawMedia);

        Assert.Equal(InkColourSampling.FromPremultipliedBgra(expected[0], expected[1], expected[2], expected[3]), actual);
    }

    [Fact]
    public void CompositeDoesNotReturnAColourWhenMediaRenderingFails()
    {
        using var device = new CanvasDevice(true);
        Assert.Throws<InvalidOperationException>(() => InkColourSampling.SampleComposite(
            device, new EditorProject(), 0, 100, 100, 50, 50, 0xFF07080A,
            (_, _, _) => throw new InvalidOperationException("Media unavailable.")));
    }

    [Fact]
    public void SamplesRenderedInkWithOpacityAndExcludesHiddenLayersWithoutChangingTheProject()
    {
        using var device = new CanvasDevice(true);
        var project = new EditorProject
        {
            InkLayers = [
                new() { Id = "ink-1", Opacity = 0.5, IsLocked = true },
                new() { Id = "hidden", IsVisible = false }],
            Frames = [new()
            {
                Strokes = [[new() { X = 0.5, Y = 0.5, BrushWidth = 30, Pressure = 1, BrushColour = 0xFFFF0000 }]],
                LayerStrokes = new()
                {
                    ["hidden"] = [[new() { X = 0.5, Y = 0.5, BrushWidth = 30, Pressure = 1, BrushColour = 0xFF00FF00 }]],
                },
            }],
        };
        string before = EditorProjectSerialiser.Serialise(project);
        uint sampled = InkColourSampling.Sample(device, project, 0, 100, 100, 50, 50);
        Assert.Equal(0x7FFF0000u, sampled);
        Assert.Equal(0u, InkColourSampling.Sample(device, project, 0, 100, 100, 1, 1));
        Assert.Equal(before, EditorProjectSerialiser.Serialise(project));
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(12.75, 6.5, 12, 6)]
    [InlineData(100, 50, 99, 49)]
    public void MapsCanvasCoordinatesToPixelsIncludingTheFarEdge(double x, double y, int expectedX, int expectedY)
    {
        Assert.Equal((expectedX, expectedY), InkColourSampling.GetPixel(x, y, 100, 50));
    }

    [Fact]
    public void HandlesFractionalCanvasBounds()
    {
        Assert.Equal((100, 50), InkColourSampling.GetPixel(100.25, 50.5, 100.25, 50.5));
    }

    [Theory]
    [InlineData(double.NaN, 0, 100, 50)]
    [InlineData(0, double.PositiveInfinity, 100, 50)]
    [InlineData(-1, 0, 100, 50)]
    [InlineData(0, 51, 100, 50)]
    [InlineData(0, 0, 0, 50)]
    public void RejectsInvalidAndOutsideCoordinates(double x, double y, double width, double height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => InkColourSampling.GetPixel(x, y, width, height));
    }

    [Theory]
    [InlineData(0, 0, 255, 255, 0xFFFF0000u)]
    [InlineData(128, 64, 0, 128, 0x800080FFu)]
    [InlineData(12, 34, 56, 255, 0xFF38220Cu)]
    [InlineData(0, 0, 0, 0, 0u)]
    [InlineData(255, 255, 255, 0, 0u)]
    public void ConvertsPremultipliedPixelBytesToStraightArgb(byte blue, byte green, byte red, byte alpha, uint expected)
    {
        Assert.Equal(expected, InkColourSampling.FromPremultipliedBgra(blue, green, red, alpha));
    }
}
