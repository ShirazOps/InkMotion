using Xunit;
using Microsoft.Graphics.Canvas;

namespace InkMotion.Tests;

public sealed class InkColourSamplingTests
{
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
