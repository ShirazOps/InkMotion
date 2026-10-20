using Xunit;

namespace InkMotion.Tests;

public sealed class InkCelCycleTests
{
    private static List<List<InkPoint>> Stroke(double x) => [[new() { X = x, Y = 0.2, BrushColour = 0x77123456, BrushWidth = 7, Pressure = 0.6 }]];

    private static EditorProject Project() => new()
    {
        InkLayers = [new(), new() { Id = "detail", IsLocked = true }],
        Frames =
        [
            new() { IsKeyframe = true, ExposureFrames = 2, Strokes = Stroke(0.1), LayerStrokes = new() { ["detail"] = Stroke(0.2) },
                TextOverlays = [new() { Text = "Cycle" }], InterpolationMode = InkInterpolationMode.EaseIn },
            new() { Number = 2, ExposureFrames = 3 },
            new() { Number = 3, IsKeyframe = true, ExposureFrames = 4, Strokes = Stroke(0.5), LayerStrokes = new() { ["detail"] = Stroke(0.6) } },
            new() { Number = 4 },
            new() { Number = 5, IsKeyframe = true, Strokes = Stroke(0.9) },
        ],
    };

    [Fact]
    public void RepeatsExposuresDrawingsTextAndTweenSettingsWithoutAliasing()
    {
        EditorProject project = Project();
        EditorFrame snapshot = InkEditing.Clone(project.Frames[0]);
        var originals = project.Frames.ToArray();
        Assert.Equal(3, InkEditing.RepeatCelRange(project.Frames, 0, 2, 2));
        Assert.Equal(11, project.Frames.Count);
        Assert.Equal(29, InkTimeline.GetTotalExposureFrames(project.Frames));
        Assert.Equal(Enumerable.Range(1, 11), project.Frames.Select(frame => frame.Number));
        Assert.Same(originals[3], project.Frames[9]);
        Assert.Same(originals[4], project.Frames[10]);
        for (int index = 0; index < 9; index++)
        {
            EditorFrame expected = originals[index % 3];
            EditorFrame actual = project.Frames[index];
            Assert.Equal(expected.ExposureFrames, actual.ExposureFrames);
            Assert.Equal(expected.IsKeyframe, actual.IsKeyframe);
            Assert.Equal(expected.InterpolationMode, actual.InterpolationMode);
            foreach (string layer in new[] { "ink-1", "detail" })
            {
                var displayed = InkAnimation.GetDisplayedStrokes(project.Frames, index,
                    frame => ExportPlanning.GetLayerStrokes(frame, layer));
                var reference = InkAnimation.GetDisplayedStrokes(originals, index % 3,
                    frame => ExportPlanning.GetLayerStrokes(frame, layer));
                Assert.Equal(reference.SelectMany(stroke => stroke).Select(point => (point.X, point.Pressure, point.BrushWidth, point.BrushColour)),
                    displayed.SelectMany(stroke => stroke).Select(point => (point.X, point.Pressure, point.BrushWidth, point.BrushColour)));
            }
        }

        Assert.Equal(InkAnimation.GetDisplayedStrokes(originals, 3)[0][0].X,
            InkAnimation.GetDisplayedStrokes(project.Frames, 9)[0][0].X);
        project.Frames[3].Strokes[0][0].X = 0.8;
        project.Frames[3].LayerStrokes["detail"][0][0].Pressure = 0.1;
        project.Frames[3].TextOverlays[0].Text = "Changed";
        Assert.Equal(0.1, project.Frames[0].Strokes[0][0].X);
        Assert.Equal(0.1, project.Frames[6].Strokes[0][0].X);
        Assert.Equal(0.6, project.Frames[0].LayerStrokes["detail"][0][0].Pressure);
        Assert.Equal("Cycle", project.Frames[6].TextOverlays[0].Text);
        Assert.Equal("Cycle", snapshot.TextOverlays[0].Text);
        EditorProject reopened = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        Assert.Equal(11, reopened.Frames.Count);
        Assert.Equal("Changed", reopened.Frames[3].TextOverlays[0].Text);
        Assert.True(reopened.InkLayers[1].IsLocked);
    }

    [Theory]
    [InlineData(-1, 2, 1)]
    [InlineData(2, 1, 1)]
    [InlineData(0, 5, 1)]
    [InlineData(0, 2, 0)]
    [InlineData(0, 2, 101)]
    [InlineData(1, 2, 1)]
    [InlineData(0, 1, 1)]
    public void InvalidRangesOrOpenTweenBoundariesDoNotMutate(int first, int last, int copies)
    {
        EditorProject project = Project();
        string before = EditorProjectSerialiser.Serialise(project);
        Assert.Throws<InvalidOperationException>(() => InkEditing.RepeatCelRange(project.Frames, first, last, copies));
        Assert.Equal(before, EditorProjectSerialiser.Serialise(project));
    }

    [Fact]
    public void SingleKeyframeCycleRepeatsWithoutChangingOtherCels()
    {
        EditorProject project = Project();
        Assert.Equal(1, InkEditing.RepeatCelRange(project.Frames, 0, 0, 3));
        Assert.Equal(8, project.Frames.Count);
        Assert.All(project.Frames.Take(4), frame => Assert.Equal(2, frame.ExposureFrames));
        Assert.All(project.Frames.Take(4), frame => Assert.True(frame.IsKeyframe));
    }

    [Fact]
    public void OperationAndTimelineLimitsRejectBeforeAllocationOrMutation()
    {
        var frames = Enumerable.Range(1, 101).Select(index => new EditorFrame { Number = index, IsKeyframe = true }).ToList();
        Assert.Throws<InvalidOperationException>(() => InkEditing.RepeatCelRange(frames, 0, 100, 100));
        Assert.Equal(101, frames.Count);
        frames = Enumerable.Range(1, 22000).Select(index => new EditorFrame { Number = index, IsKeyframe = true, ExposureFrames = 999 }).ToList();
        Assert.Throws<InvalidOperationException>(() => InkEditing.RepeatCelRange(frames, 0, 21999, 100));
        Assert.Equal(22000, frames.Count);
    }
}
