using Xunit;

namespace InkMotion.Tests;

public sealed class InkCelRangeTests
{
    private static List<EditorFrame> Frames() => Enumerable.Range(1, 5).Select(index => new EditorFrame
    {
        Number = index,
        ExposureFrames = index,
        IsKeyframe = index != 3,
        InterpolationMode = InkInterpolationMode.EaseIn,
        Strokes = [[new() { X = index / 10d, Y = 0.5, Pressure = 0.7, BrushColour = 0x80123456 }]],
        LayerStrokes = new() { ["ink-2"] = [[new() { X = index / 10d, Y = 0.2 }]] },
        TextOverlays = [new() { Text = index.ToString() }],
    }).ToList();

    [Fact]
    public void CopyAndRepeatedPastePreserveAllCelDataAsIndependentObjects()
    {
        List<EditorFrame> frames = Frames();
        List<EditorFrame> clipboard = InkEditing.CopyCelRange(frames, 1, 3);
        Assert.Equal(3, clipboard.Count);
        Assert.False(clipboard[1].IsKeyframe);
        Assert.Equal(1, InkEditing.PasteCelRange(frames, clipboard, 1));
        Assert.Equal(8, frames.Count);
        Assert.Equal(2, frames[1].ExposureFrames);
        Assert.Equal(InkInterpolationMode.EaseIn, frames[1].InterpolationMode);
        Assert.Equal(0.7, frames[1].Strokes[0][0].Pressure);
        Assert.Equal(0x80123456u, frames[1].Strokes[0][0].BrushColour);
        Assert.Equal("2", frames[1].TextOverlays[0].Text);
        frames[1].Strokes[0][0].X = 0.9;
        frames[1].LayerStrokes["ink-2"][0][0].X = 0.9;
        frames[1].TextOverlays[0].Text = "Edited copy";
        Assert.Equal(0.2, clipboard[0].Strokes[0][0].X);
        Assert.Equal(0.2, clipboard[0].LayerStrokes["ink-2"][0][0].X);
        Assert.Equal("2", clipboard[0].TextOverlays[0].Text);
        Assert.Equal(8, InkEditing.PasteCelRange(frames, clipboard, frames.Count));
        Assert.Equal("2", frames[8].TextOverlays[0].Text);
        Assert.Equal(Enumerable.Range(1, 11), frames.Select(frame => frame.Number));
    }

    [Theory]
    [InlineData(1, 3, 0, 0, "2,3,4,1,5")]
    [InlineData(1, 3, 5, 2, "1,5,2,3,4")]
    [InlineData(3, 4, 1, 1, "1,4,5,2,3")]
    public void MoveUsesOriginalInsertionIndexAndPreservesCelIdentityAndTotalExposure(
        int first, int last, int before, int expectedIndex, string expectedOrder)
    {
        List<EditorFrame> frames = Frames();
        EditorFrame original = frames[first];
        int exposure = frames.Sum(frame => frame.ExposureFrames);
        Assert.Equal(expectedIndex, InkEditing.MoveCelRange(frames, first, last, before));
        Assert.Same(original, frames[expectedIndex]);
        Assert.Equal(expectedOrder, string.Join(",", frames.Select(frame => frame.TextOverlays[0].Text)));
        Assert.Equal(exposure, frames.Sum(frame => frame.ExposureFrames));
        Assert.Equal(Enumerable.Range(1, 5), frames.Select(frame => frame.Number));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void MovingWithinOrImmediatelyAfterRangeIsANoOp(int before)
    {
        List<EditorFrame> frames = Frames();
        EditorFrame[] original = frames.ToArray();
        Assert.Equal(1, InkEditing.MoveCelRange(frames, 1, 3, before));
        Assert.Equal(original, frames);
    }

    [Fact]
    public void InvalidRangesAndOverflowLeaveTimelineUntouched()
    {
        List<EditorFrame> frames = Frames();
        EditorFrame[] original = frames.ToArray();
        Assert.Throws<InvalidOperationException>(() => InkEditing.CopyCelRange(frames, 3, 1));
        Assert.Equal(2, InkEditing.CopyCelRange(frames, 2, 3).Count);
        Assert.Throws<InvalidOperationException>(() => InkEditing.CopyCelRange(frames, 1, 5));
        Assert.Throws<InvalidOperationException>(() => InkEditing.MoveCelRange(frames, 1, 3, 6));
        Assert.Throws<InvalidOperationException>(() => InkEditing.PasteCelRange(frames, [], 1));
        Assert.Throws<InvalidOperationException>(() =>
            InkEditing.PasteCelRange(frames, [new() { IsKeyframe = true, ExposureFrames = int.MaxValue }], 1));
        Assert.Equal(original, frames);
    }

    [Theory]
    [InlineData(0, 1, 0, "3,4,5")]
    [InlineData(1, 3, 1, "1,5")]
    [InlineData(3, 4, 2, "1,2,3")]
    public void DeleteKeepsUnaffectedCelDataAndSelectsNearestRemainingCel(
        int first, int last, int selected, string expected)
    {
        List<EditorFrame> frames = Frames();
        Assert.Equal(selected, InkEditing.DeleteCelRange(frames, first, last));
        Assert.Equal(expected, string.Join(",", frames.Select(frame => frame.TextOverlays[0].Text)));
        Assert.Equal(Enumerable.Range(1, frames.Count), frames.Select(frame => frame.Number));
    }

    [Fact]
    public void DeleteCannotRemoveEveryCel()
    {
        List<EditorFrame> frames = Frames();
        EditorFrame[] original = frames.ToArray();
        Assert.Throws<InvalidOperationException>(() => InkEditing.DeleteCelRange(frames, 0, 4));
        Assert.Equal(original, frames);
    }

    [Fact]
    public void CopiedRangeRetainsItsInternalTweenWhenPastedElsewhere()
    {
        List<EditorFrame> frames = Frames();
        frames[2].Strokes.Clear();
        frames[2].LayerStrokes.Clear();
        List<InkPoint> before = InkAnimation.GetDisplayedStrokes(frames, 2)[0];
        List<EditorFrame> clipboard = InkEditing.CopyCelRange(frames, 1, 3);
        InkEditing.PasteCelRange(frames, clipboard, 0);
        List<InkPoint> after = InkAnimation.GetDisplayedStrokes(frames, 1)[0];
        Assert.Equal(before.Select(point => (point.X, point.Y, point.Pressure)),
            after.Select(point => (point.X, point.Y, point.Pressure)));
    }

    [Fact]
    public void CopyMaterializesOpenTweenEdgesWithoutChangingTheSourceAnimation()
    {
        List<EditorFrame> frames = Frames();
        foreach (int index in new[] { 1, 2, 3 })
        {
            frames[index].IsKeyframe = false;
            frames[index].Strokes.Clear();
            frames[index].LayerStrokes.Clear();
        }
        frames[0].Strokes[0][0].X = 0;
        frames[4].Strokes[0][0].X = 1;
        frames[0].LayerStrokes["ink-2"][0][0].X = 0;
        frames[4].LayerStrokes["ink-2"][0][0].X = 1;

        var expected = Enumerable.Range(1, 3).Select(index => (
            InkAnimation.GetDisplayedStrokes(frames, index)[0][0].X,
            InkAnimation.GetDisplayedStrokes(frames, index,
                frame => ExportPlanning.GetLayerStrokes(frame, "ink-2"))[0][0].X)).ToArray();

        List<EditorFrame> clipboard = InkEditing.CopyCelRange(frames, 1, 3);

        Assert.All(clipboard, frame => Assert.True(frame.IsKeyframe));
        Assert.Equal(expected, clipboard.Select(frame => (
            InkAnimation.GetDisplayedStrokes(clipboard, clipboard.IndexOf(frame))[0][0].X,
            InkAnimation.GetDisplayedStrokes(clipboard, clipboard.IndexOf(frame),
                cel => ExportPlanning.GetLayerStrokes(cel, "ink-2"))[0][0].X)));
        Assert.All(frames.Skip(1).Take(3), frame =>
        {
            Assert.False(frame.IsKeyframe);
            Assert.Empty(frame.Strokes);
            Assert.Empty(frame.LayerStrokes);
        });
    }

    [Fact]
    public void RangeEditsRoundTripAndOriginalSnapshotRestoresPreviousOrder()
    {
        var project = new EditorProject
        {
            InkLayers = [new(), new() { Id = "ink-2", IsLocked = true }],
            Frames = Frames(),
        };
        string before = EditorProjectSerialiser.Serialise(project);
        InkEditing.MoveCelRange(project.Frames, 1, 3, 0);
        project.SelectedFrameIndex = 0;
        EditorProject restored = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        Assert.Equal("2", restored.Frames[0].TextOverlays[0].Text);
        Assert.True(restored.InkLayers[1].IsLocked);
        Assert.Equal(0.2, restored.Frames[0].LayerStrokes["ink-2"][0][0].X);
        Assert.Equal("1", EditorProjectSerialiser.Deserialise(before).Frames[0].TextOverlays[0].Text);
    }
}
