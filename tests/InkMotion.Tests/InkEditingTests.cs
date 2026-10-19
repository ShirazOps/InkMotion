using Xunit;

namespace InkMotion.Tests;

public sealed class InkEditingTests
{
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
