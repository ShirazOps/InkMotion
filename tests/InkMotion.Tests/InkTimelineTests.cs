using Xunit;

namespace InkMotion.Tests;

public sealed class InkTimelineTests
{
    [Fact]
    public void DeleteFrame_RemovesSelectedFrameAndRenumbersFollowingFrames()
    {
        List<EditorFrame> frames =
        [
            Frame(1, 0.1),
            Frame(2, 0.2),
            Frame(3, 0.3),
        ];

        int selectedFrameIndex = InkTimeline.DeleteFrame(frames, 1);

        Assert.Equal(1, selectedFrameIndex);
        Assert.Collection(
            frames,
            frame => Assert.Equal((1, 0.1), (frame.Number, frame.Strokes[0][0].X)),
            frame => Assert.Equal((2, 0.3), (frame.Number, frame.Strokes[0][0].X)));
    }

    [Fact]
    public void DeleteFrame_WhenRemovingLastFrame_SelectsPreviousFrame()
    {
        List<EditorFrame> frames = [Frame(1, 0.1), Frame(2, 0.2)];

        int selectedFrameIndex = InkTimeline.DeleteFrame(frames, 1);

        Assert.Equal(0, selectedFrameIndex);
        Assert.Equal(1, Assert.Single(frames).Number);
        Assert.Equal(0.1, frames[0].Strokes[0][0].X);
    }

    [Fact]
    public void DeleteFrame_RejectsDeletingTheOnlyFrame()
    {
        List<EditorFrame> frames = [Frame(1, 0.1)];

        Assert.Throws<InvalidOperationException>(() => InkTimeline.DeleteFrame(frames, 0));
        Assert.Single(frames);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void DeleteFrame_RejectsAnInvalidIndex(int frameIndex)
    {
        List<EditorFrame> frames = [Frame(1, 0.1), Frame(2, 0.2)];

        Assert.Throws<ArgumentOutOfRangeException>(() => InkTimeline.DeleteFrame(frames, frameIndex));
        Assert.Equal(2, frames.Count);
    }

    private static EditorFrame Frame(int number, double x) =>
        new()
        {
            Number = number,
            Strokes = [[new InkPoint { X = x, Y = 0.5 }]],
        };
}
