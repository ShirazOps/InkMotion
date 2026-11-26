using System.Numerics;
using Microsoft.Graphics.Canvas;
using Xunit;

namespace InkMotion.Tests;

public sealed class InkCharacterRigTests
{
    private static InkCharacterRig Rig() => new()
    {
        Bones =
        [
            new() { Id = "root", Name = "Shoulder", X = 0.2, Y = 0.5, EndX = 0.5, EndY = 0.5,
                Keys = [new(0), new(1, 90)] },
            new() { Id = "child", Name = "Elbow", ParentId = "root", X = 0.5, Y = 0.5, EndX = 0.8, EndY = 0.5 },
        ],
    };

    private static InkRigBinding Binding(InkRigBindingMode mode) => new()
    {
        Mode = mode, BoneId = "child", Influences = new() { ["root"] = 1, ["child"] = 1 },
    };

    [Fact]
    public void ChildInheritsParentRotationInPixelAspectAndKeysInterpolate()
    {
        InkCharacterRig rig = Rig();
        var point = new Vector2(160, 50);
        Vector2 moved = Vector2.Transform(point, InkCharacterRigs.Transforms(rig, 1, 200, 100)["child"]);
        Assert.InRange(moved.X, 39.99f, 40.01f);
        Assert.InRange(moved.Y, 169.99f, 170.01f);
        Assert.Equal(45, InkCharacterRigs.Evaluate(rig.Bones[0], 0.5).Rotation);
        Assert.Equal(90, InkCharacterRigs.Evaluate(rig.Bones[0], 9).Rotation);
        rig.Bones[0].Keys[0] = new(0, Interpolation: InkInterpolationMode.EaseIn);
        Assert.Equal(22.5, InkCharacterRigs.Evaluate(rig.Bones[0], 0.5).Rotation);
    }

    [Fact]
    public void WeightedVectorsBendBetweenIndependentBonesAndWeightsAreEditable()
    {
        InkCharacterRig rig = Rig();
        rig.Bones[0].Keys.Clear();
        rig.Bones[1].Keys.Add(new(0, 90));
        List<List<InkPoint>> drawing = [[new() { X = 0.2, Y = 0.5 }, new() { X = 0.8, Y = 0.5 }]];
        InkRigBinding binding = Binding(InkRigBindingMode.Weighted);
        IReadOnlyList<List<InkPoint>> bent = InkCharacterRigs.Deform(drawing, rig, binding, 0, 100, 100);
        Assert.True(bent[0].Count > 20);
        Assert.InRange(bent[0][0].X, 0.2, 0.201);
        Assert.InRange(bent[0][^1].Y, 0.79, 0.801);
        Assert.InRange(InkCharacterRigs.Weights(rig, binding, 0.65, 0.5, 100, 100).Values.Sum(), 0.99999, 1.00001);
        binding.Influences.Remove("child");
        IReadOnlyList<List<InkPoint>> fixedDrawing = InkCharacterRigs.Deform(drawing, rig, binding, 0, 100, 100);
        Assert.InRange(fixedDrawing[0][^1].Y, 0.49999, 0.50001);
        Assert.Equal(0.8, drawing[0][1].X);
        List<List<InkPoint>> triangle =
        [[new() { X = 0.2, Y = 0.4, IsFilled = true }, new() { X = 0.8, Y = 0.4, IsFilled = true },
            new() { X = 0.8, Y = 0.6, IsFilled = true }]];
        IReadOnlyList<List<InkPoint>> outline = InkCharacterRigs.Deform(triangle, rig, binding, 0, 100, 100);
        Assert.Equal(outline[0][0].X, outline[0][^1].X);
        Assert.Equal(outline[0][0].Y, outline[0][^1].Y);
    }

    [Fact]
    public void RigsBindingsAndPoseKeysRoundTripAndCloneIndependently()
    {
        var project = new EditorProject { CharacterRig = Rig() };
        project.InkLayers[0].RigBinding = Binding(InkRigBindingMode.Weighted);
        EditorProject restored = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        EditorProject clone = CompoundTimelines.Clone(restored);
        clone.CharacterRig!.Bones[0].Keys.Clear();
        clone.InkLayers[0].RigBinding!.Influences["root"] = 8;
        Assert.Equal(2, restored.CharacterRig!.Bones[0].Keys.Count);
        Assert.Equal(1, restored.InkLayers[0].RigBinding!.Influences["root"]);
        InkLayerProject duplicate = InkEditing.DuplicateLayer(restored.InkLayers, restored.Frames, "ink-1");
        duplicate.RigBinding!.Influences["root"] = 4;
        Assert.Equal(1, restored.InkLayers[0].RigBinding!.Influences["root"]);
        Assert.Throws<InvalidOperationException>(() => InkEditing.MergeLayerDown(restored.InkLayers, restored.Frames, duplicate.Id));
        Assert.Null(EditorProjectSerialiser.Deserialise("""{"Version":87,"Frames":[{"Number":1}]}""").CharacterRig);
    }

    [Theory]
    [InlineData("cycle")]
    [InlineData("missing-parent")]
    [InlineData("duplicate")]
    [InlineData("bad-key")]
    [InlineData("zero-weight")]
    [InlineData("missing-binding")]
    public void InvalidRigsFailBeforePersistence(string error)
    {
        var project = new EditorProject { CharacterRig = Rig() };
        project.InkLayers[0].RigBinding = Binding(InkRigBindingMode.Weighted);
        switch (error)
        {
            case "cycle": project.CharacterRig.Bones[0].ParentId = "child"; break;
            case "missing-parent": project.CharacterRig.Bones[0].ParentId = "absent"; break;
            case "duplicate": project.CharacterRig.Bones[1].Id = "root"; break;
            case "bad-key": project.CharacterRig.Bones[0].Keys[1] = new(double.NaN); break;
            case "zero-weight": project.InkLayers[0].RigBinding!.Influences["root"] = 0; break;
            case "missing-binding": project.InkLayers[0].RigBinding!.BoneId = "absent"; break;
        }
        Assert.Throws<InvalidDataException>(() => EditorProjectSerialiser.Serialise(project));
    }

    [Theory]
    [InlineData(InkRigBindingMode.CutOut)]
    [InlineData(InkRigBindingMode.Weighted)]
    public void SharedRendererUsesRigAtTimelineTimeAndGraphMatchesStack(InkRigBindingMode mode)
    {
        var project = new EditorProject { CharacterRig = Rig() };
        project.InkLayers[0].RigBinding = Binding(mode);
        project.Frames[0].ExposureFrames = 48;
        project.Frames[0].Strokes = [[new() { X = 0.5, Y = 0.5, BrushWidth = 8, Pressure = 1, BrushColour = 0xFFFF0000 }]];
        using var device = new CanvasDevice(true);
        using var background = new CanvasRenderTarget(device, 100, 100, 96);
        using (var session = background.CreateDrawingSession()) session.Clear(Microsoft.UI.Colors.Transparent);
        using CanvasRenderTarget stack = TimelineCompositor.CompositeInkFrame(device, background, 100, 100, project, 0, 100, 100, 1, 1);
        Assert.True(stack.GetPixelBytes(20, 80, 1, 1)[3] > 200);
        Assert.Equal(0, stack.GetPixelBytes(50, 50, 1, 1)[3]);
        project.CompositeGraph = InkCompositeGraphs.FromLayerStack(project.InkLayers);
        using CanvasRenderTarget graph = TimelineCompositor.CompositeInkFrame(device, background, 100, 100, project, 0, 100, 100, 1, 1);
        Assert.Equal(stack.GetPixelBytes(), graph.GetPixelBytes());
        project.CharacterRig.Enabled = false;
        using CanvasRenderTarget rest = TimelineCompositor.CompositeInkFrame(device, background, 100, 100, project, 0, 100, 100, 1, 1);
        Assert.True(rest.GetPixelBytes(50, 50, 1, 1)[3] > 200);
    }
}
