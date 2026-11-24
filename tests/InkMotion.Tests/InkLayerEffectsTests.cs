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

    [Fact]
    public void PixelateAveragesPremultipliedCellsAnchoredToOrigin()
    {
        // 3x2 image, cell 2: left cell has one opaque white pixel of four, right edge cell is 1x2.
        byte[] bgra =
        [
            255, 255, 255, 255, 0, 0, 0, 0, 10, 20, 30, 40,
            0, 0, 0, 0, 0, 0, 0, 0, 30, 40, 50, 60,
        ];

        InkLayerEffects.Pixelate(bgra, 3, 2, 2);

        Assert.Equal(new byte[] { 64, 64, 64, 64 }, bgra[0..4]);
        Assert.Equal(new byte[] { 64, 64, 64, 64 }, bgra[16..20]);
        Assert.Equal(new byte[] { 20, 30, 40, 50 }, bgra[8..12]);
        Assert.Equal(new byte[] { 20, 30, 40, 50 }, bgra[20..24]);
    }

    [Fact]
    public void PixelLayerRendersHardEdgedBlocksInComposite()
    {
        var layer = new InkLayerProject { Id = "ink-1", PixelSize = 20 };
        EditorProject project = CreateProject(layer, 0xFFFFFFFF);

        uint first = Sample(project, 41, 41);
        uint second = Sample(project, 58, 58);
        uint outside = Sample(project, 50, 61);

        Assert.True(TimelineCompositor.RequiresLayerCompositing(project));
        Assert.Equal(first, second);
        Assert.InRange((int)(first >> 24), 40, 220);
        Assert.Equal(0u, outside >> 24);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(65)]
    [InlineData(double.PositiveInfinity)]
    public void RejectsInvalidPixelSizes(double size)
    {
        EditorProject project = CreateProject(new() { Id = "ink-1", PixelSize = size });

        Assert.Throws<InvalidDataException>(() => EditorProjectSerialiser.Serialise(project));
    }

    [Fact]
    public void PixelSizePersistsAndDefaultsOffForOlderProjects()
    {
        EditorProject project = CreateProject(new() { Id = "ink-1", PixelSize = 16 });

        InkLayerProject restored = Assert.Single(
            EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project)).InkLayers);
        EditorProject legacy = EditorProjectSerialiser.Deserialise(
            """{"Version":72,"InkLayers":[{"Id":"ink-1","Name":"Ink 1","GlowRadius":4}],"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""");

        Assert.Equal(16, restored.PixelSize);
        Assert.Equal(0, Assert.Single(legacy.InkLayers).PixelSize);
        Assert.Equal(4, legacy.InkLayers[0].GlowRadius);
    }

    private static EditorProject CreateClippingProject(InkLayerProject baseLayer, InkLayerProject clipped)
    {
        EditorProject project = CreateProject(baseLayer);
        project.InkLayers.Add(clipped);
        project.Frames[0].LayerStrokes[clipped.Id] =
        [
            [
                new() { X = 0.5, Y = 0.1, BrushWidth = 20, Pressure = 1, BrushColour = 0xFFFF0000 },
                new() { X = 0.5, Y = 0.9, BrushWidth = 20, Pressure = 1, BrushColour = 0xFFFF0000 },
            ],
        ];
        return project;
    }

    [Fact]
    public void ClippedLayerShowsOnlyInsideTheBaseLayersPixels()
    {
        EditorProject unclipped = CreateClippingProject(new() { Id = "ink-1" }, new() { Id = "ink-2" });
        EditorProject clipped = CreateClippingProject(new() { Id = "ink-1" }, new() { Id = "ink-2", ClipToLayerBelow = true });

        Assert.Equal(0xFFFF0000u, Sample(unclipped, 50, 20));
        Assert.True(TimelineCompositor.RequiresLayerCompositing(clipped));
        Assert.Equal(0xFFFF0000u, Sample(clipped, 50, 50));
        Assert.Equal(0u, Sample(clipped, 50, 20) >> 24);
        Assert.Equal(0xFF2040E0u, Sample(clipped, 25, 50));
    }

    [Fact]
    public void ClippedLayerIsHiddenWithoutAVisibleBase()
    {
        EditorProject hiddenBase = CreateClippingProject(
            new() { Id = "ink-1", IsVisible = false }, new() { Id = "ink-2", ClipToLayerBelow = true });
        var bottom = new InkLayerProject { Id = "ink-1", ClipToLayerBelow = true };
        EditorProject bottomClipped = CreateProject(bottom);

        Assert.Equal(0u, Sample(hiddenBase, 50, 50) >> 24);
        Assert.Equal(0u, Sample(bottomClipped, 50, 50) >> 24);
    }

    [Fact]
    public void ClippingMaskUsesBaseEffectsAndStacksOverOneBase()
    {
        EditorProject project = CreateClippingProject(
            new() { Id = "ink-1", GlowRadius = 30, GlowIntensity = 3 }, new() { Id = "ink-2", ClipToLayerBelow = true });
        project.InkLayers.Add(new() { Id = "ink-3", ClipToLayerBelow = true });
        project.Frames[0].LayerStrokes["ink-3"] =
        [
            [
                new() { X = 0.7, Y = 0.5, BrushWidth = 6, Pressure = 1, BrushColour = 0xFF00FF00 },
                new() { X = 0.7, Y = 0.9, BrushWidth = 6, Pressure = 1, BrushColour = 0xFF00FF00 },
            ],
        ];

        uint glowArea = Sample(project, 50, 60);
        uint secondClip = Sample(project, 70, 50);
        uint secondOutside = Sample(project, 70, 85);

        Assert.True(((glowArea >> 16) & 0xFF) > ((glowArea >> 8) & 0xFF), $"Clip should reveal red within the glow, got {glowArea:X8}.");
        Assert.Equal(0xFF00FF00u, secondClip);
        Assert.True((secondOutside >> 24) < 128, $"Glow fringe only partially reveals the clipped stroke, got {secondOutside:X8}.");
    }

    [Fact]
    public void ClippingPersistsDefaultsOffAndBlocksMerging()
    {
        EditorProject project = CreateClippingProject(new() { Id = "ink-1" }, new() { Id = "ink-2", ClipToLayerBelow = true });

        EditorProject restored = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        EditorProject legacy = EditorProjectSerialiser.Deserialise(
            """{"Version":73,"InkLayers":[{"Id":"ink-1","Name":"Ink 1","PixelSize":4}],"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""");

        Assert.True(restored.InkLayers[1].ClipToLayerBelow);
        Assert.False(Assert.Single(legacy.InkLayers).ClipToLayerBelow);
        Assert.Throws<InvalidOperationException>(() => InkEditing.MergeLayerDown(project.InkLayers, project.Frames, "ink-2"));
    }

    private static EditorProject CreateHoldoutProject(double matteOpacity = 1)
    {
        EditorProject project = CreateClippingProject(
            new() { Id = "ink-1" }, new() { Id = "ink-2", IsHoldout = true, Opacity = matteOpacity });
        project.InkLayers.Add(new() { Id = "ink-3" });
        project.Frames[0].LayerStrokes["ink-3"] =
        [
            [
                new() { X = 0.4, Y = 0.3, BrushWidth = 6, Pressure = 1, BrushColour = 0xFFFFFFFF },
                new() { X = 0.6, Y = 0.3, BrushWidth = 6, Pressure = 1, BrushColour = 0xFFFFFFFF },
            ],
        ];
        return project;
    }

    [Fact]
    public void HoldoutRevealsBackgroundOverLowerInkOnly()
    {
        EditorProject project = CreateHoldoutProject();

        Assert.True(TimelineCompositor.RequiresLayerCompositing(project));
        Assert.Equal(0xFF00FF00u, Sample(project, 50, 50, 0xFF00FF00));
        Assert.Equal(0xFF2040E0u, Sample(project, 25, 50, 0xFF00FF00));
        Assert.Equal(0xFF00FF00u, Sample(project, 50, 20, 0xFF00FF00));
        Assert.Equal(0xFFFFFFFFu, Sample(project, 50, 30, 0xFF00FF00));
        Assert.Equal(0u, Sample(project, 50, 50) >> 24);
    }

    [Fact]
    public void HoldoutOpacityGivesPartialOcclusion()
    {
        uint mixed = Sample(CreateHoldoutProject(0.5), 50, 50, 0xFF00FF00);

        Assert.InRange((int)((mixed >> 8) & 0xFF), 90, 170);
        Assert.InRange((int)(mixed & 0xFF), 90, 140);
    }

    [Fact]
    public void HoldoutIsNeverAClippingBaseOrClippedLayer()
    {
        EditorProject project = CreateClippingProject(
            new() { Id = "ink-1", IsHoldout = true }, new() { Id = "ink-2", ClipToLayerBelow = true });

        Assert.Equal(0u, Sample(project, 50, 50) >> 24);
        Assert.Equal(0u, Sample(project, 50, 20) >> 24);
    }

    [Fact]
    public void HoldoutPersistsDuplicatesDefaultsOffAndBlocksMerging()
    {
        EditorProject project = CreateHoldoutProject();

        EditorProject restored = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        EditorProject legacy = EditorProjectSerialiser.Deserialise(
            """{"Version":75,"InkLayers":[{"Id":"ink-1","Name":"Ink 1"}],"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""");
        InkLayerProject copy = InkEditing.DuplicateLayer(project.InkLayers, project.Frames, "ink-2");

        Assert.True(restored.InkLayers[1].IsHoldout);
        Assert.Equal(EditorProject.CurrentVersion, legacy.Version);
        Assert.False(Assert.Single(legacy.InkLayers).IsHoldout);
        Assert.True(copy.IsHoldout);
        Assert.Throws<InvalidOperationException>(() => InkEditing.MergeLayerDown(project.InkLayers, project.Frames, "ink-2"));
        Assert.Throws<InvalidOperationException>(() => InkEditing.MergeLayerDown(project.InkLayers, project.Frames, "ink-3"));
    }
}