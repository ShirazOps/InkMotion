using Xunit;

namespace InkMotion.Tests;

public sealed class InkLayerMergeTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MergePreservesDisplayedDrawingsAcrossAllCelsAndRoundTrip(bool intoBase)
    {
        var project = new EditorProject
        {
            InkLayers =
            [
                new(),
                new() { Id = "detail", Name = "Detail" },
                new() { Id = "top", Name = "Top" },
            ],
            Frames =
            [
                new()
                {
                    IsKeyframe = true,
                    ExposureFrames = 3,
                    Strokes = Stroke(0.1),
                    LayerStrokes = new() { ["detail"] = Stroke(0.3), ["top"] = Stroke(0.7) },
                    TextOverlays = [new() { Text = "Caption" }],
                },
                new() { Number = 2, ExposureFrames = 2 },
                new()
                {
                    Number = 3,
                    IsKeyframe = true,
                    Strokes = Stroke(0.2),
                    LayerStrokes = new() { ["detail"] = Stroke(0.4) },
                },
                new() { Number = 4 },
            ],
        };
        string source = intoBase ? "detail" : "top";
        string target = intoBase ? "ink-1" : "detail";
        project.InkLayers.Single(layer => layer.Id == source).Opacity = 0.6;
        project.InkLayers.Single(layer => layer.Id == target).Opacity = 0.6;
        var expected = Enumerable.Range(0, project.Frames.Count).Select(index =>
            InkEditing.CloneStrokes(InkAnimation.GetDisplayedStrokes(project.Frames, index,
                frame => ExportPlanning.GetLayerStrokes(frame, target)).Concat(
                InkAnimation.GetDisplayedStrokes(project.Frames, index,
                    frame => ExportPlanning.GetLayerStrokes(frame, source))).ToList())).ToList();
        EditorFrame snapshot = InkEditing.Clone(project.Frames[0]);
        InkPoint original = ExportPlanning.GetLayerStrokes(project.Frames[0], source)[0][0];
        InkLayerProject destination = project.InkLayers.Single(layer => layer.Id == target);
        Assert.Same(destination, InkEditing.MergeLayerDown(project.InkLayers, project.Frames, source));
        Assert.Equal(2, project.InkLayers.Count);
        Assert.Equal(0.6, destination.Opacity);
        Assert.All(project.Frames, frame => Assert.False(frame.LayerStrokes.ContainsKey(source)));
        Assert.Equal(3, project.Frames[0].ExposureFrames);
        Assert.Equal(2, project.Frames[1].ExposureFrames);
        Assert.True(project.Frames[0].IsKeyframe);
        Assert.False(project.Frames[1].IsKeyframe);
        Assert.Equal("Caption", Assert.Single(project.Frames[0].TextOverlays).Text);
        Assert.NotSame(original, ExportPlanning.GetLayerStrokes(project.Frames[0], target)[1][0]);
        Assert.True(snapshot.LayerStrokes.ContainsKey(source));
        project.ActiveInkLayerId = target;
        EditorProject reopened = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        for (int index = 0; index < project.Frames.Count; index++)
        {
            AssertStrokes(expected[index], InkAnimation.GetDisplayedStrokes(project.Frames, index,
                frame => ExportPlanning.GetLayerStrokes(frame, target)));
            AssertStrokes(expected[index], InkAnimation.GetDisplayedStrokes(reopened.Frames, index,
                frame => ExportPlanning.GetLayerStrokes(frame, target)));
        }
    }

    [Theory]
    [InlineData("base")]
    [InlineData("missing")]
    [InlineData("source-locked")]
    [InlineData("target-locked")]
    [InlineData("source-hidden")]
    [InlineData("target-hidden")]
    [InlineData("opacity")]
    public void InvalidMergeDoesNotMutateProject(string reason)
    {
        var project = new EditorProject
        {
            InkLayers = [new(), new() { Id = "detail" }],
            Frames = [new() { Strokes = Stroke(0.1), LayerStrokes = new() { ["detail"] = Stroke(0.8) } }],
        };
        switch (reason)
        {
            case "source-locked": project.InkLayers[1].IsLocked = true; break;
            case "target-locked": project.InkLayers[0].IsLocked = true; break;
            case "source-hidden": project.InkLayers[1].IsVisible = false; break;
            case "target-hidden": project.InkLayers[0].IsVisible = false; break;
            case "opacity": project.InkLayers[1].Opacity = 0.5; break;
        }

        string before = EditorProjectSerialiser.Serialise(project);
        string id = reason == "base" ? "ink-1" : reason == "missing" ? "missing" : "detail";
        Assert.Throws<InvalidOperationException>(() => InkEditing.MergeLayerDown(project.InkLayers, project.Frames, id));
        Assert.Equal(before, EditorProjectSerialiser.Serialise(project));
    }

    [Fact]
    public void EmptyLayersMergeWithoutIntroducingDrawings()
    {
        var project = new EditorProject { InkLayers = [new(), new() { Id = "empty" }] };
        InkEditing.MergeLayerDown(project.InkLayers, project.Frames, "empty");
        Assert.Single(project.InkLayers);
        Assert.All(project.Frames, frame => Assert.Empty(frame.Strokes));
    }

    private static List<List<InkPoint>> Stroke(double x) =>
        [[new() { X = x, Y = 0.2, Pressure = 0.6, BrushWidth = 7, BrushColour = 0x77123456 },
          new() { X = x + 0.05, Y = 0.3, Pressure = 0.8, BrushWidth = 8, BrushColour = 0x77123456 }]];

    private static void AssertStrokes(IReadOnlyList<List<InkPoint>> expected, IReadOnlyList<List<InkPoint>> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].Select(point => (point.X, point.Y, point.Pressure, point.BrushWidth, point.BrushColour)),
                actual[index].Select(point => (point.X, point.Y, point.Pressure, point.BrushWidth, point.BrushColour)));
        }
    }
}
