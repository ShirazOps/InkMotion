using Microsoft.Graphics.Canvas;
using Xunit;

namespace InkMotion.Tests;

public sealed class InkLayerBlendModeTests
{
    [Theory]
    [InlineData(InkLayerBlendMode.Normal, 128, 192, 64)]
    [InlineData(InkLayerBlendMode.Multiply, 32, 96, 48)]
    [InlineData(InkLayerBlendMode.Screen, 160, 224, 208)]
    [InlineData(InkLayerBlendMode.Add, 192, 255, 255)]
    [InlineData(InkLayerBlendMode.Overlay, 64, 192, 161)]
    public void CompositesLayerAgainstBackground(InkLayerBlendMode mode, int red, int green, int blue)
    {
        using var device = new CanvasDevice(true);
        using var background = new CanvasRenderTarget(device, 100, 100, 96);
        using (CanvasDrawingSession session = background.CreateDrawingSession())
        {
            session.Clear(TimelineCompositor.ToColour(0xFF4080C0));
        }

        var project = new EditorProject
        {
            InkLayers = [new() { Id = "ink-1", BlendMode = mode }],
            Frames = [new()
            {
                Strokes =
                [
                    [
                        new() { X = 0.1, Y = 0.5, BrushWidth = 40, Pressure = 1, BrushColour = 0xFF80C040 },
                        new() { X = 0.9, Y = 0.5, BrushWidth = 40, Pressure = 1, BrushColour = 0xFF80C040 },
                    ],
                ],
            }],
        };

        using CanvasRenderTarget composite = TimelineCompositor.CompositeInkFrame(
            device, background, 100, 100, project, 0, 100, 100, 1);
        byte[] pixel = composite.GetPixelBytes(50, 50, 1, 1);
        uint colour = InkColourSampling.FromPremultipliedBgra(pixel[0], pixel[1], pixel[2], pixel[3]);

        Assert.InRange((int)((colour >> 16) & 0xFF), red - 2, red + 2);
        Assert.InRange((int)((colour >> 8) & 0xFF), green - 2, green + 2);
        Assert.InRange((int)(colour & 0xFF), blue - 2, blue + 2);
    }

    [Fact]
    public void BlendModePersistsAndDefaultsToNormalForOlderProjects()
    {
        var project = new EditorProject
        {
            InkLayers = [new() { Id = "ink-1", BlendMode = InkLayerBlendMode.Overlay }],
        };

        EditorProject restored = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        EditorProject legacy = EditorProjectSerialiser.Deserialise(
            """{"Version":66,"InkLayers":[{"Id":"ink-1","Name":"Ink 1"}],"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""");

        Assert.Equal(InkLayerBlendMode.Overlay, Assert.Single(restored.InkLayers).BlendMode);
        Assert.Equal(InkLayerBlendMode.Normal, Assert.Single(legacy.InkLayers).BlendMode);
    }

    [Fact]
    public void NonNormalLayerRemainsVisibleOverTransparentBackground()
    {
        using var device = new CanvasDevice(true);
        using var background = new CanvasRenderTarget(device, 100, 100, 96);
        using (CanvasDrawingSession session = background.CreateDrawingSession())
        {
            session.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        }

        var project = new EditorProject
        {
            InkLayers = [new() { Id = "ink-1", BlendMode = InkLayerBlendMode.Multiply }],
            Frames = [new()
            {
                Strokes =
                [
                    [new() { X = 0.1, Y = 0.5, BrushWidth = 40, Pressure = 1, BrushColour = 0xFFE03060 },
                     new() { X = 0.9, Y = 0.5, BrushWidth = 40, Pressure = 1, BrushColour = 0xFFE03060 }],
                ],
            }],
        };

        using CanvasRenderTarget composite = TimelineCompositor.CompositeInkFrame(
            device, background, 100, 100, project, 0, 100, 100, 1);
        byte[] pixel = composite.GetPixelBytes(50, 50, 1, 1);
        uint colour = InkColourSampling.FromPremultipliedBgra(pixel[0], pixel[1], pixel[2], pixel[3]);

        Assert.True((colour >> 24) > 200);
        Assert.InRange((int)((colour >> 16) & 0xFF), 220, 230);
        Assert.InRange((int)((colour >> 8) & 0xFF), 40, 50);
        Assert.InRange((int)(colour & 0xFF), 90, 100);
    }

    [Fact]
    public void DuplicatingLayerPreservesBlendMode()
    {
        var layers = new List<InkLayerProject> { new() { BlendMode = InkLayerBlendMode.Screen } };

        InkLayerProject copy = InkEditing.DuplicateLayer(layers, [new()], "ink-1");

        Assert.Equal(InkLayerBlendMode.Screen, copy.BlendMode);
    }

    [Fact]
    public void RejectsUnknownBlendModesInSavedProjects()
    {
        var project = new EditorProject
        {
            InkLayers = [new() { BlendMode = (InkLayerBlendMode)999 }],
        };

        Assert.Throws<InvalidDataException>(() => EditorProjectSerialiser.Serialise(project));
    }
}
