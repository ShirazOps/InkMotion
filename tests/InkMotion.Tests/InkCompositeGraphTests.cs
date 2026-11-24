using Microsoft.Graphics.Canvas;
using Xunit;

namespace InkMotion.Tests;

public sealed class InkCompositeGraphTests
{
    // Layer "ink-1": horizontal blue line through y = 50. Layer "ink-2": vertical red line through x = 50.
    private static EditorProject CreateProject(params InkLayerProject[] layers)
    {
        var project = new EditorProject
        {
            InkLayers = [.. layers],
            Frames = [new()
            {
                Strokes =
                [
                    [
                        new() { X = 0.2, Y = 0.5, BrushWidth = 6, Pressure = 1, BrushColour = 0xFF2040E0 },
                        new() { X = 0.8, Y = 0.5, BrushWidth = 6, Pressure = 1, BrushColour = 0xFF2040E0 },
                    ],
                ],
            }],
        };
        if (layers.Length > 1)
        {
            project.Frames[0].LayerStrokes[layers[1].Id] =
            [
                [
                    new() { X = 0.5, Y = 0.1, BrushWidth = 20, Pressure = 1, BrushColour = 0xFFFF0000 },
                    new() { X = 0.5, Y = 0.9, BrushWidth = 20, Pressure = 1, BrushColour = 0xFFFF0000 },
                ],
            ];
        }

        return project;
    }

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

    private static InkCompositeGraph LinearGraph(params InkCompositeNode[] nodes)
    {
        var graph = new InkCompositeGraph { Enabled = true, Nodes = [.. nodes] };
        InkCompositeNode output = InkCompositeGraphs.CreateNode(InkCompositeNodeKind.Output, "out", 0, 0);
        output.Inputs[0] = nodes[^1].Id;
        graph.Nodes.Add(output);
        return graph;
    }

    private static InkCompositeNode Node(InkCompositeNodeKind kind, string id, params string?[] inputs)
    {
        InkCompositeNode node = InkCompositeGraphs.CreateNode(kind, id, 0, 0);
        for (int index = 0; index < inputs.Length; index++) node.Inputs[index] = inputs[index];
        return node;
    }

    private static void AssertClose(uint expected, uint actual, int tolerance = 3)
    {
        for (int shift = 0; shift < 32; shift += 8)
        {
            int difference = Math.Abs((int)((expected >> shift) & 0xFF) - (int)((actual >> shift) & 0xFF));
            Assert.True(difference <= tolerance, $"Expected {expected:X8} but got {actual:X8}.");
        }
    }

    [Fact]
    public void GraphBuiltFromLayerStackMatchesStackRendering()
    {
        EditorProject stack = CreateProject(
            new() { Id = "ink-1", GlowRadius = 12, GlowIntensity = 2, GlowColour = 0xFF00FF00 },
            new() { Id = "ink-2", ClipToLayerBelow = true, BlendMode = InkLayerBlendMode.Screen });
        EditorProject graphed = CreateProject(
            new() { Id = "ink-1", GlowRadius = 12, GlowIntensity = 2, GlowColour = 0xFF00FF00 },
            new() { Id = "ink-2", ClipToLayerBelow = true, BlendMode = InkLayerBlendMode.Screen });
        graphed.CompositeGraph = InkCompositeGraphs.FromLayerStack(graphed.InkLayers);

        Assert.Null(InkCompositeGraphs.Validate(graphed.CompositeGraph));
        foreach ((int x, int y) in new[] { (50, 50), (30, 50), (50, 20), (50, 56), (10, 10) })
            AssertClose(Sample(stack, x, y, 0xFF404040), Sample(graphed, x, y, 0xFF404040));
    }

    [Fact]
    public void EnabledGraphReplacesLayerStackAndDisabledGraphIsIgnored()
    {
        EditorProject project = CreateProject(new() { Id = "ink-1" }, new() { Id = "ink-2" });
        InkCompositeNode layer = Node(InkCompositeNodeKind.InkLayer, "a");
        layer.LayerId = "ink-1";
        project.CompositeGraph = LinearGraph(layer);

        Assert.True(TimelineCompositor.RequiresLayerCompositing(project));
        Assert.Equal(0u, Sample(project, 50, 20) >> 24);
        AssertClose(0xFF2040E0, Sample(project, 30, 50));

        project.CompositeGraph.Enabled = false;
        Assert.Equal(0xFFFF0000u, Sample(project, 50, 20));
    }

    [Fact]
    public void MergeOpacityInvertedMaskAndColourAdjustRender()
    {
        EditorProject project = CreateProject(new() { Id = "ink-1" }, new() { Id = "ink-2" });
        InkCompositeNode blue = Node(InkCompositeNodeKind.InkLayer, "blue");
        blue.LayerId = "ink-1";
        InkCompositeNode red = Node(InkCompositeNodeKind.InkLayer, "red");
        red.LayerId = "ink-2";
        InkCompositeNode cut = Node(InkCompositeNodeKind.Mask, "cut", "red", "blue");
        cut.Invert = true;
        InkCompositeNode grey = Node(InkCompositeNodeKind.ColourAdjust, "grey", "cut");
        grey.Saturation = 0;
        InkCompositeNode merge = Node(InkCompositeNodeKind.Merge, "merge", "picture", "grey");
        merge.Opacity = 0.5;
        project.CompositeGraph = LinearGraph(Node(InkCompositeNodeKind.Picture, "picture"), blue, red, cut, grey, merge);

        uint background = Sample(project, 10, 10, 0xFF000000);
        uint holeInRed = Sample(project, 50, 50, 0xFF000000);
        uint halfGrey = Sample(project, 50, 20, 0xFF000000);

        Assert.Equal(0xFF000000u, background);
        Assert.Equal(0xFF000000u, holeInRed);
        Assert.Equal(0xFFu, halfGrey >> 24);
        int r = (int)((halfGrey >> 16) & 0xFF), g = (int)((halfGrey >> 8) & 0xFF), b = (int)(halfGrey & 0xFF);
        Assert.True(Math.Abs(r - g) <= 2 && Math.Abs(g - b) <= 2, $"Expected grey, got {halfGrey:X8}.");
        Assert.InRange(r, 20, 40);
    }

    [Fact]
    public void MissingLayerAndUnconnectedOutputRenderTransparent()
    {
        EditorProject project = CreateProject(new InkLayerProject { Id = "ink-1" });
        InkCompositeNode missing = Node(InkCompositeNodeKind.InkLayer, "missing");
        missing.LayerId = "deleted-layer";
        project.CompositeGraph = LinearGraph(missing);

        Assert.Equal(0u, Sample(project, 30, 50) >> 24);
        project.CompositeGraph.Nodes[^1].Inputs[0] = null;
        Assert.Equal(0u, Sample(project, 30, 50) >> 24);
    }

    [Fact]
    public void ColourAdjustMatrixKeepsIdentityAndMakesGrey()
    {
        var identity = TimelineCompositor.CreateColourAdjustMatrix(0, 1, 1);
        var grey = TimelineCompositor.CreateColourAdjustMatrix(0, 1, 0);

        Assert.Equal(1f, identity.M11, 3);
        Assert.Equal(0f, identity.M12, 3);
        Assert.Equal(grey.M11, grey.M12, 3);
        Assert.Equal(grey.M12, grey.M13, 3);
    }

    [Fact]
    public void GraphPersistsAndOlderProjectsHaveNone()
    {
        EditorProject project = CreateProject(new InkLayerProject { Id = "ink-1" });
        project.CompositeGraph = InkCompositeGraphs.FromLayerStack(project.InkLayers);
        project.CompositeGraph.Nodes[1].X = 321;

        EditorProject restored = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        EditorProject legacy = EditorProjectSerialiser.Deserialise(
            """{"Version":77,"InkLayers":[{"Id":"ink-1","Name":"Ink 1"}],"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""");

        Assert.NotNull(restored.CompositeGraph);
        Assert.True(restored.CompositeGraph.Enabled);
        Assert.Equal(project.CompositeGraph.Nodes.Count, restored.CompositeGraph.Nodes.Count);
        Assert.Equal(321, restored.CompositeGraph.Nodes[1].X);
        Assert.Equal("ink-1", restored.CompositeGraph.Nodes[1].LayerId);
        Assert.Null(legacy.CompositeGraph);
        Assert.Equal(EditorProject.CurrentVersion, legacy.Version);
    }

    [Fact]
    public void ValidationRejectsLoopsDuplicateOutputsMissingInputsAndBadSettings()
    {
        InkCompositeGraph Valid() => LinearGraph(Node(InkCompositeNodeKind.Picture, "p"), Node(InkCompositeNodeKind.Blur, "b", "p"));

        InkCompositeGraph loop = Valid();
        loop.Nodes.Add(Node(InkCompositeNodeKind.Merge, "m1", "m2", null));
        loop.Nodes.Add(Node(InkCompositeNodeKind.Merge, "m2", "m1", null));
        InkCompositeGraph twoOutputs = Valid();
        twoOutputs.Nodes.Add(Node(InkCompositeNodeKind.Output, "out2", "b"));
        InkCompositeGraph missing = Valid();
        missing.Nodes[1].Inputs[0] = "ghost";
        InkCompositeGraph badBlur = Valid();
        badBlur.Nodes[1].Radius = 1000;
        InkCompositeGraph wrongInputs = Valid();
        wrongInputs.Nodes[1].Inputs.Add(null);
        InkCompositeGraph fromOutput = Valid();
        fromOutput.Nodes.Add(Node(InkCompositeNodeKind.Blur, "after", "out"));

        Assert.Null(InkCompositeGraphs.Validate(Valid()));
        Assert.NotNull(InkCompositeGraphs.Validate(loop));
        Assert.NotNull(InkCompositeGraphs.Validate(twoOutputs));
        Assert.NotNull(InkCompositeGraphs.Validate(missing));
        Assert.NotNull(InkCompositeGraphs.Validate(badBlur));
        Assert.NotNull(InkCompositeGraphs.Validate(wrongInputs));
        Assert.NotNull(InkCompositeGraphs.Validate(fromOutput));
        Assert.True(InkCompositeGraphs.WouldCreateCycle(Valid(), "b", "p"));
        Assert.False(InkCompositeGraphs.WouldCreateCycle(Valid(), "p", "b"));

        EditorProject project = CreateProject(new InkLayerProject { Id = "ink-1" });
        project.CompositeGraph = loop;
        Assert.Throws<InvalidDataException>(() => EditorProjectSerialiser.Serialise(project));
    }
}
