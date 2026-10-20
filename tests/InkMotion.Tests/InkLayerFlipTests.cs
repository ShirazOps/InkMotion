using Xunit;

namespace InkMotion.Tests;

public sealed class InkLayerFlipTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void FlipReflectsAllDisplayedCelsRetainsTweeningAndRoundTrips(bool horizontal, bool baseLayer)
    {
        string id = baseLayer ? "ink-1" : "detail";
        var project = new EditorProject
        {
            InkLayers = [new(), new() { Id = "detail", Opacity = 0.6, IsVisible = false }],
            Frames =
            [
                new() { IsKeyframe = true, ExposureFrames = 3, Strokes = Stroke(0.1), LayerStrokes = new() { ["detail"] = Stroke(0.2) } },
                new() { Number = 2, ExposureFrames = 2, TextOverlays = [new() { Text = "Caption" }] },
                new() { Number = 3, IsKeyframe = true, Strokes = Stroke(0.7), LayerStrokes = new() { ["detail"] = Stroke(0.6) } },
                new() { Number = 4 },
            ],
        };
        var before = Enumerable.Range(0, 4).Select(index => InkEditing.CloneStrokes(
            InkAnimation.GetDisplayedStrokes(project.Frames, index, frame => ExportPlanning.GetLayerStrokes(frame, id)).ToList())).ToList();
        string otherId = baseLayer ? "detail" : "ink-1";
        var other = project.Frames.Select(frame => ExportPlanning.GetLayerStrokes(frame, otherId)).ToList();
        InkLayerProject layer = project.InkLayers.Single(candidate => candidate.Id == id);
        InkPoint snapshotPoint = ExportPlanning.GetLayerStrokes(project.Frames[0], id)[0][0];

        Assert.True(InkEditing.FlipLayer(project.InkLayers, project.Frames, id, horizontal));
        Assert.Same(layer, project.InkLayers.Single(candidate => candidate.Id == id));
        Assert.NotSame(snapshotPoint, ExportPlanning.GetLayerStrokes(project.Frames[0], id)[0][0]);
        Assert.Equal(baseLayer ? 0.1 : 0.2, snapshotPoint.X);
        Assert.Empty(ExportPlanning.GetLayerStrokes(project.Frames[1], id));
        Assert.Empty(ExportPlanning.GetLayerStrokes(project.Frames[3], id));
        Assert.Equal(3, project.Frames[0].ExposureFrames);
        Assert.Equal(2, project.Frames[1].ExposureFrames);
        Assert.False(project.Frames[1].IsKeyframe);
        Assert.Equal("Caption", Assert.Single(project.Frames[1].TextOverlays).Text);
        Assert.False(project.InkLayers[1].IsVisible);
        Assert.Equal(0.6, project.InkLayers[1].Opacity);
        for (int index = 0; index < 4; index++)
        {
            if (other[index].Count > 0)
            {
                Assert.Same(other[index], ExportPlanning.GetLayerStrokes(project.Frames[index], otherId));
            }
            else
            {
                Assert.Empty(ExportPlanning.GetLayerStrokes(project.Frames[index], otherId));
            }
        }

        EditorProject reopened = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        foreach (EditorProject candidate in new[] { project, reopened })
        {
            for (int index = 0; index < 4; index++)
            {
                var actual = InkAnimation.GetDisplayedStrokes(candidate.Frames, index, frame => ExportPlanning.GetLayerStrokes(frame, id));
                Assert.Equal(before[index].Count, actual.Count);
                for (int stroke = 0; stroke < actual.Count; stroke++)
                {
                    Assert.Equal(before[index][stroke].Count, actual[stroke].Count);
                    for (int point = 0; point < actual[stroke].Count; point++)
                    {
                        InkPoint original = before[index][stroke][point];
                        InkPoint flipped = actual[stroke][point];
                        Assert.Equal(horizontal ? 1 - original.X : original.X, flipped.X, 12);
                        Assert.Equal(horizontal ? original.Y : 1 - original.Y, flipped.Y, 12);
                        Assert.Equal(original.Pressure, flipped.Pressure, 12);
                        Assert.Equal(original.BrushWidth, flipped.BrushWidth, 12);
                        Assert.Equal(original.BrushColour, flipped.BrushColour);
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DoubleFlipRestoresGeometryAndEmptyOrCentredDrawingsAreNoOps(bool horizontal)
    {
        var layers = new List<InkLayerProject> { new() };
        var frames = new List<EditorFrame> { new() };
        Assert.False(InkEditing.FlipLayer(layers, frames, "ink-1", horizontal));
        frames[0].Strokes = [[new() { X = 0.5, Y = 0.5 }]];
        Assert.False(InkEditing.FlipLayer(layers, frames, "ink-1", horizontal));
        frames[0].Strokes = Stroke(0.2);
        var before = InkEditing.Clone(frames[0]);
        Assert.True(InkEditing.FlipLayer(layers, frames, "ink-1", horizontal));
        Assert.True(InkEditing.FlipLayer(layers, frames, "ink-1", horizontal));
        Assert.Equal(before.Strokes[0][0].X, frames[0].Strokes[0][0].X, 12);
        Assert.Equal(before.Strokes[0][0].Y, frames[0].Strokes[0][0].Y, 12);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("locked")]
    [InlineData("coordinates")]
    public void InvalidFlipDoesNotPartiallyMutateDrawings(string reason)
    {
        var layers = new List<InkLayerProject> { new() { IsLocked = reason == "locked" } };
        var frames = new List<EditorFrame> { new() { Strokes = Stroke(0.2) }, new() { Strokes = Stroke(0.8) } };
        if (reason == "coordinates")
        {
            frames[1].Strokes[0][0].X = double.NaN;
        }

        List<List<InkPoint>> first = frames[0].Strokes;
        Assert.Throws<InvalidOperationException>(() => InkEditing.FlipLayer(layers, frames,
            reason == "missing" ? "missing" : "ink-1", true));
        Assert.Same(first, frames[0].Strokes);
        Assert.Equal(0.2, frames[0].Strokes[0][0].X);
    }

    private static List<List<InkPoint>> Stroke(double x) =>
        [[new() { X = x, Y = 0.2, Pressure = 0.6, BrushWidth = 7, BrushColour = 0x77123456 },
          new() { X = x + 0.05, Y = 0.3, Pressure = 0.8, BrushWidth = 8, BrushColour = 0x77123456 }]];
}
