using Xunit;

namespace InkMotion.Tests;

public sealed class InkTimelineTests
{
    [Fact]
    public void PlayheadGeometry_MapsExposureFramesToCelWidthsAndBack()
    {
        List<EditorFrame> frames =
        [
            new() { ExposureFrames = 2 },
            new() { ExposureFrames = 100 },
            new() { ExposureFrames = 3 },
        ];
        Assert.Equal(58, InkTimeline.GetCelWidth(frames[0]));
        Assert.Equal(220, InkTimeline.GetCelWidth(frames[1]));
        Assert.Equal(29, InkTimeline.GetPlayheadX(frames, 1));
        Assert.Equal(62, InkTimeline.GetPlayheadX(frames, 2));
        Assert.Equal(172, InkTimeline.GetPlayheadX(frames, 52));
        for (int position = 0; position < InkTimeline.GetTotalExposureFrames(frames); position++)
        {
            double x = InkTimeline.GetPlayheadX(frames, position);
            Assert.Equal(position, InkTimeline.GetFramePositionAtX(frames, x));
        }
    }

    [Fact]
    public void PlayheadGeometry_ClampsOutsideAndSpacingWithoutAddingAnExtraCel()
    {
        List<EditorFrame> frames = [new() { ExposureFrames = 2 }, new() { ExposureFrames = 3 }];
        Assert.Equal(0, InkTimeline.GetFramePositionAtX(frames, -10));
        Assert.Equal(2, InkTimeline.GetFramePositionAtX(frames, 60));
        Assert.Equal(4, InkTimeline.GetFramePositionAtX(frames, 1000));
        Assert.Equal(124, InkTimeline.GetPlayheadX(frames, 5));
        Assert.Equal(124, InkTimeline.GetPlayheadX(frames, 100));
    }

    [Fact]
    public void GetStartFramePosition_UsesCumulativeCelExposures()
    {
        List<EditorFrame> frames =
        [
            new EditorFrame
            {
                Number = 1,
                ExposureFrames = 2,
                Strokes = [[new InkPoint { X = 0.1, Y = 0.5 }]],
            },
            new EditorFrame
            {
                Number = 2,
                ExposureFrames = 3,
                Strokes = [[new InkPoint { X = 0.2, Y = 0.5 }]],
            },
            Frame(3, 0.3),
        ];

        Assert.Equal(0, InkTimeline.GetStartFramePosition(frames, 0));
        Assert.Equal(2, InkTimeline.GetStartFramePosition(frames, 1));
        Assert.Equal(5, InkTimeline.GetStartFramePosition(frames, 2));
        Assert.Equal(6, InkTimeline.GetTotalExposureFrames(frames));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(6, 2)]
    public void GetFrameIndexAtPosition_MapsOutputFrameToCel(int framePosition, int expectedIndex)
    {
        List<EditorFrame> frames =
        [
            new EditorFrame { Number = 1, ExposureFrames = 2 },
            new EditorFrame { Number = 2, ExposureFrames = 3 },
            new EditorFrame { Number = 3, ExposureFrames = 1 },
        ];

        Assert.Equal(expectedIndex, InkTimeline.GetFrameIndexAtPosition(frames, framePosition));
    }

    [Fact]
    public void GetFrameIndexAtPosition_RejectsAnEmptyTimeline()
    {
        Assert.Throws<ArgumentException>(() => InkTimeline.GetFrameIndexAtPosition([], 0));
    }

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
