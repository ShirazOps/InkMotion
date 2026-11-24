using Microsoft.Graphics.Canvas;
using Xunit;

namespace InkMotion.Tests;

public sealed class InkLayerEffectsTests
{
    private static EditorProject CreateProject(InkLayerProject layer, uint colour = 0xFF2040E0) => new()
    {
        InkLayers = [layer],
        Frames = [new()
        {
            Strokes =
            [
                [
                    new() { X = 0.2, Y = 0.5, BrushWidth = 6, Pressure = 1, BrushColour = colour },
                    new() { X = 0.8, Y = 0.5, BrushWidth = 6, Pressure = 1, BrushColour = colour },
                ],
            ],
        }],
    };

    private static uint Sample(EditorProject project, int x, int y, uint background = 0x00000000)
    {
        using var device = new CanvasDevice(true);
        using var backdrop = new CanvasRenderTarget(device, 100, 100, 96);
        using (CanvasDrawingSession session = backdrop.CreateDrawingSession())
        {
            session.Clear(TimelineCompositor.ToColour(background));
        }

        using CanvasRenderTarget composite = TimelineCompositor.CompositeInkFrame(
            device, backdrop, 100, 100, project, 0, 100, 100, 1);
        byte[] pixel = composite.GetPixelBytes(x, y, 1, 1);
        return InkColourSampling.FromPremultipliedBgra(pixel[0], pixel[1], pixel[2], pixel[3]);
    }

    [Fact]
    public void GlowAddsTintedHaloOutsideStrokeWithoutChangingStrokeCentre()
    {
        EditorProject plain = CreateProject(new() { Id = "ink-1" });
        EditorProject glowing = CreateProject(new()
        {
            Id = "ink-1", GlowRadius = 30, GlowIntensity = 3, GlowColour = 0xFFFF2000,
        });

        uint plainHalo = Sample(plain, 50, 60);
        uint halo = Sample(glowing, 50, 60);
        uint centre = Sample(glowing, 50, 50);

        Assert.Equal(0u, plainHalo >> 24);
        Assert.True((halo >> 24) > 20, $"Expected visible glow alpha, got {halo:X8}.");
        Assert.True(((halo >> 16) & 0xFF) > 200, $"Expected red glow, got {halo:X8}.");
        Assert.True((halo & 0xFF) < 40, $"Glow must use its colour rather than the stroke's blue, got {halo:X8}.");
        Assert.True((centre & 0xFF) > 200, $"Stroke stays on top of its glow, got {centre:X8}.");
    }

    [Fact]
    public void GlowRequiresCompositingAndRejectsDirectDrawing()
    {
        EditorProject project = CreateProject(new() { Id = "ink-1", GlowRadius = 10 });

        Assert.True(TimelineCompositor.RequiresLayerCompositing(project));
        project.InkLayers[0].GlowIntensity = 0;
        Assert.False(TimelineCompositor.RequiresLayerCompositing(project));
    }

    [Fact]
    public void GlowPersistsDuplicatesAndDefaultsOffForOlderProjects()
    {
        EditorProject project = CreateProject(new()
        {
            Id = "ink-1", GlowRadius = 12, GlowIntensity = 1.5, GlowColour = 0xFF00FF80,
        });

        InkLayerProject restored = Assert.Single(
            EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project)).InkLayers);
        EditorProject legacy = EditorProjectSerialiser.Deserialise(
            """{"Version":71,"InkLayers":[{"Id":"ink-1","Name":"Ink 1"}],"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""");
        InkLayerProject copy = InkEditing.DuplicateLayer(project.InkLayers, project.Frames, "ink-1");

        Assert.Equal((12d, 1.5d, 0xFF00FF80u), (restored.GlowRadius, restored.GlowIntensity, restored.GlowColour));
        Assert.Equal(EditorProject.CurrentVersion, legacy.Version);
        Assert.False(InkLayerEffects.HasEffects(Assert.Single(legacy.InkLayers)));
        Assert.Equal(12, copy.GlowRadius);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(500, 1)]
    [InlineData(10, double.NaN)]
    [InlineData(10, 9)]
    public void RejectsInvalidGlowSettings(double radius, double intensity)
    {
        EditorProject project = CreateProject(new() { Id = "ink-1", GlowRadius = radius, GlowIntensity = intensity });

        Assert.Throws<InvalidDataException>(() => EditorProjectSerialiser.Serialise(project));
    }

    [Fact]
    public void MergeDownRefusesLayersWithEffects()
    {
        var layers = new List<InkLayerProject> { new() { Id = "ink-1" }, new() { Id = "ink-2", Name = "Ink 2", GlowRadius = 8 } };

        Assert.Throws<InvalidOperationException>(() => InkEditing.MergeLayerDown(layers, [new()], "ink-2"));
    }
}
