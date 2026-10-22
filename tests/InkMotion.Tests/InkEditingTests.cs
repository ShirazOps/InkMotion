using Xunit;

namespace InkMotion.Tests;

public sealed class InkEditingTests
{
    [Theory]
    [InlineData("ink-1")]
    [InlineData("ink-2")]
    public void DuplicateLayer_CopiesEveryCelIndependentlyAndPreservesAnimationAndText(string sourceId)
    {
        var project = new EditorProject
        {
            InkLayers = [new(), new() { Id = "ink-2", Name = "Reference", IsLocked = true, Opacity = 0.4, IsVisible = false }],
            Frames =
            [
                new()
                {
                    ExposureFrames = 3,
                    IsKeyframe = true,
                    TextOverlays = [new() { Text = "Caption" }],
                    Strokes = [[new() { X = 0.25, BrushWidth = 8, Pressure = 0.6, BrushColour = 0x77123456 }]],
                    LayerStrokes = new() { ["ink-2"] = [[new() { X = 0.75, BrushWidth = 8, Pressure = 0.6, BrushColour = 0x77123456 }]] },
                },
                new() { Number = 2 },
                new()
                {
                    Number = 3,
                    IsKeyframe = true,
                    Strokes = [[new() { X = 0.5 }]],
                    LayerStrokes = new() { ["ink-2"] = [[new() { X = 0.9 }]] },
                },
            ],
        };
        InkLayerProject source = project.InkLayers.Single(layer => layer.Id == sourceId);
        int index = project.InkLayers.IndexOf(source);
        InkLayerProject copy = InkEditing.DuplicateLayer(project.InkLayers, project.Frames, sourceId);
        Assert.Same(copy, project.InkLayers[index + 1]);
        Assert.NotEqual(sourceId, copy.Id);
        Assert.Equal(source.Name + " copy", copy.Name);
        Assert.Equal(source.Opacity, copy.Opacity);
        Assert.Equal(source.IsVisible, copy.IsVisible);
        Assert.False(copy.IsLocked);
        Assert.False(project.Frames[1].LayerStrokes.ContainsKey(copy.Id));
        List<InkPoint> sourceTween = Assert.Single(InkAnimation.GetDisplayedStrokes(project.Frames, 1,
            frame => ExportPlanning.GetLayerStrokes(frame, sourceId)));
        List<InkPoint> copiedTween = Assert.Single(InkAnimation.GetDisplayedStrokes(project.Frames, 1,
            frame => ExportPlanning.GetLayerStrokes(frame, copy.Id)));
        Assert.NotEmpty(sourceTween);
        Assert.Equal(sourceTween.Select(point => (point.X, point.Y, point.Pressure, point.BrushWidth, point.BrushColour)),
            copiedTween.Select(point => (point.X, point.Y, point.Pressure, point.BrushWidth, point.BrushColour)));
        foreach (EditorFrame frame in project.Frames.Where(frame => frame.Number != 2))
        {
            InkPoint original = ExportPlanning.GetLayerStrokes(frame, sourceId)[0][0];
            InkPoint cloned = frame.LayerStrokes[copy.Id][0][0];
            Assert.NotSame(original, cloned);
            Assert.Equal(original.X, cloned.X);
            Assert.Equal(original.BrushColour, cloned.BrushColour);
            Assert.Equal(original.BrushWidth, cloned.BrushWidth);
            Assert.Equal(original.Pressure, cloned.Pressure);
            cloned.X = 0.1;
            Assert.NotEqual(original.X, cloned.X);
        }

        Assert.Equal(3, project.Frames[0].ExposureFrames);
        Assert.True(project.Frames[0].IsKeyframe);
        Assert.Equal("Caption", Assert.Single(project.Frames[0].TextOverlays).Text);
        project.ActiveInkLayerId = copy.Id;
        EditorProject reopened = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        Assert.Equal(copy.Id, reopened.ActiveInkLayerId);
        Assert.Equal(0.1, reopened.Frames[2].LayerStrokes[copy.Id][0][0].X);
        Assert.Equal(source.IsLocked, sourceId == "ink-2");
    }

    [Fact]
    public void RemoveLayer_DeletesDrawingsAcrossCelsWithoutChangingOtherLayersOrSnapshot()
    {
        var project = new EditorProject
        {
            InkLayers = [new(), new() { Id = "ink-2", Name = "Detail" }],
            Frames =
            [
                new()
                {
                    ExposureFrames = 5,
                    TextOverlays = [new() { Text = "Global caption" }],
                    Strokes = [[new() { X = 0.3 }]],
                    LayerStrokes = new() { ["ink-2"] = [[new() { X = 0.8 }]] },
                },
                new() { Number = 2, LayerStrokes = new() { ["ink-2"] = [[new() { X = 0.9 }]] } },
            ],
        };
        EditorFrame snapshot = InkEditing.Clone(project.Frames[0]);
        InkEditing.RemoveLayer(project.InkLayers, project.Frames, "ink-2");
        Assert.Equal("ink-1", Assert.Single(project.InkLayers).Id);
        Assert.All(project.Frames, frame => Assert.Empty(frame.LayerStrokes));
        Assert.Equal(0.8, snapshot.LayerStrokes["ink-2"][0][0].X);
        Assert.Equal(0.3, project.Frames[0].Strokes[0][0].X);
        Assert.Equal("Global caption", Assert.Single(project.Frames[0].TextOverlays).Text);
        Assert.Equal(5, project.Frames[0].ExposureFrames);
        EditorProject reopened = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        Assert.Single(reopened.InkLayers);
        Assert.All(reopened.Frames, frame => Assert.Empty(frame.LayerStrokes));
    }

    [Theory]
    [InlineData("ink-1", false, false)]
    [InlineData("ink-2", true, false)]
    [InlineData("ink-2", false, true)]
    [InlineData("missing", false, false)]
    public void RemoveLayer_RejectsBaseLockedLastVisibleAndUnknownWithoutMutation(string id, bool locked, bool baseHidden)
    {
        var project = new EditorProject
        {
            InkLayers = [new() { IsVisible = !baseHidden }, new() { Id = "ink-2", IsLocked = locked }],
            Frames = [new() { LayerStrokes = new() { ["ink-2"] = [[new() { X = 0.2 }]] } }],
        };
        string before = EditorProjectSerialiser.Serialise(project);
        Assert.Throws<InvalidOperationException>(() => InkEditing.RemoveLayer(project.InkLayers, project.Frames, id));
        Assert.Equal(before, EditorProjectSerialiser.Serialise(project));
    }

    [Fact]
    public void DuplicateLayer_RejectsUnknownWithoutMutationAndAllocatesUniqueCopies()
    {
        var project = new EditorProject();
        Assert.Throws<InvalidOperationException>(() => InkEditing.DuplicateLayer(project.InkLayers, project.Frames, "missing"));
        Assert.Single(project.InkLayers);
        InkLayerProject first = InkEditing.DuplicateLayer(project.InkLayers, project.Frames, "ink-1");
        InkLayerProject second = InkEditing.DuplicateLayer(project.InkLayers, project.Frames, "ink-1");
        Assert.NotEqual(first.Id, second.Id);
        EditorProjectSerialiser.Serialise(project);
    }

    [Fact]
    public void BrushPresets_StayWithinToolsRangesAndPreserveHueWhileReplacingAlpha()
    {
        Assert.Equal(6, InkBrushPresets.All.Count);
        Assert.Equal(6, InkBrushPresets.All.Select(preset => preset.Name).Distinct().Count());
        foreach (InkBrushPreset preset in InkBrushPresets.All)
        {
            Assert.InRange(preset.Width, 1, 12);
            Assert.InRange(preset.Opacity, 0, 1);
            Assert.InRange(preset.Stabilisation, 0, 0.9);
            uint colour = preset.ApplyColour(0x00112233);
            Assert.Equal(0x00112233u, colour & 0x00FFFFFF);
            Assert.Equal((uint)Math.Round(preset.Opacity * 255), colour >> 24);
            var project = new EditorProject
            {
                Frames = [new() { Strokes = [[new() { BrushWidth = preset.Width, BrushColour = colour, BrushTexture = preset.Texture }]] }],
            };
            EditorProject reopened = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
            Assert.Equal(colour, reopened.Frames[0].Strokes[0][0].BrushColour);
            Assert.Equal(preset.Width, reopened.Frames[0].Strokes[0][0].BrushWidth);
            Assert.Equal(preset.Texture, reopened.Frames[0].Strokes[0][0].BrushTexture);
        }
    }

    [Fact]
    public void DrawingLock_PersistsClonesAndPreventsReorderingAcrossLockedLayers()
    {
        var project = new EditorProject
        {
            InkLayers = [new(), new() { Id = "locked", Name = "Reference", IsLocked = true }, new() { Id = "top" }],
        };
        Assert.False(InkEditing.MoveLayer(project.InkLayers, "locked", -1));
        Assert.False(InkEditing.MoveLayer(project.InkLayers, "top", -1));
        Assert.False(InkEditing.MoveLayer(project.InkLayers, "ink-1", 1));
        Assert.Equal(new[] { "ink-1", "locked", "top" }, project.InkLayers.Select(layer => layer.Id));
        InkLayerProject clone = InkEditing.CloneLayer(project.InkLayers[1]);
        Assert.True(clone.IsLocked);
        clone.IsLocked = false;
        Assert.True(project.InkLayers[1].IsLocked);
        EditorProject reopened = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        Assert.True(reopened.InkLayers[1].IsLocked);
        Assert.False(reopened.InkLayers[0].IsLocked);
        reopened.InkLayers[1].IsLocked = false;
        Assert.True(InkEditing.MoveLayer(reopened.InkLayers, "top", -1));
    }

    [Fact]
    public void VersionEleven_MigratesToUnlockedLayersWithoutChangingDrawings()
    {
        EditorProject project = EditorProjectSerialiser.Deserialise(
            """{"Version":11,"Frames":[{"Number":1,"Strokes":[[{"X":0.25,"Y":0.5}]]}]}""");
        Assert.Equal(EditorProject.CurrentVersion, project.Version);
        Assert.False(project.InkLayers[0].IsLocked);
        Assert.Equal(0.25, project.Frames[0].Strokes[0][0].X);
    }

    [Fact]
    public void InsertCopy_PreservesTextLayersExposureAndIndependentPoints()
    {
        var source = new EditorFrame
        {
            ExposureFrames = 12,
            IsKeyframe = true,
            TextOverlays = [new() { Text = "Caption", FontSize = 60 }],
            Strokes = [[new() { X = 0.25, Y = 0.5, BrushColour = 0xFF00FF00 }]],
            LayerStrokes = new() { ["ink-2"] = [[new() { X = 0.75, Y = 0.5 }]] },
        };
        List<EditorFrame> frames = [source, new() { Number = 2 }];
        int inserted = InkEditing.InsertCopy(frames, 0, source);
        Assert.Equal(1, inserted);
        Assert.Equal(new[] { 1, 2, 3 }, frames.Select(frame => frame.Number));
        EditorFrame copy = frames[inserted];
        Assert.Equal(12, copy.ExposureFrames);
        Assert.True(copy.IsKeyframe);
        Assert.Equal("Caption", copy.TextOverlays.Single().Text);
        copy.TextOverlays[0].Text = "Changed";
        copy.Strokes[0][0].X = 0.1;
        copy.LayerStrokes["ink-2"][0][0].X = 0.9;
        Assert.Equal("Caption", source.TextOverlays[0].Text);
        Assert.Equal(0.25, source.Strokes[0][0].X);
        Assert.Equal(0.75, source.LayerStrokes["ink-2"][0][0].X);
    }

    [Fact]
    public void MoveLayer_PreservesIdsAndBaseLayerAcrossSaveLoad()
    {
        var project = new EditorProject
        {
            InkLayers = [new(), new() { Id = "ink-2", Name = "Ink 2" }],
        };
        Assert.True(InkEditing.MoveLayer(project.InkLayers, "ink-1", 1));
        Assert.Equal(new[] { "ink-2", "ink-1" }, project.InkLayers.Select(layer => layer.Id));
        Assert.False(InkEditing.MoveLayer(project.InkLayers, "ink-1", 1));
        EditorProject reopened = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        Assert.Equal(new[] { "ink-2", "ink-1" }, reopened.InkLayers.Select(layer => layer.Id));
    }
}
