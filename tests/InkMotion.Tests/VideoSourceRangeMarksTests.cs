using Xunit;

namespace InkMotion.Tests;

public sealed class VideoSourceRangeMarksTests
{
    [Fact]
    public void CreatesClipUsingMarkedSourceRangeAndTimelineStart()
    {
        Assert.True(VideoSourceRangeMarks.TryCreateClip(
            "source.mp4", 12, 25, 24, 1.25, 4.5, out TimelineClipProject? clip));
        Assert.NotNull(clip);
        Assert.Equal("source.mp4", clip.SourcePath);
        Assert.Equal(12, clip.SourceDurationSeconds);
        Assert.Equal(25, clip.StartFrame);
        Assert.Equal(1.25, clip.SourceInSeconds);
        Assert.Equal(4.5, clip.SourceOutSeconds);
    }

    [Theory]
    [InlineData("", 10, 1, 24, 1, 2)]
    [InlineData("source.mp4", 0, 1, 24, 0, 1)]
    [InlineData("source.mp4", 10, 0, 24, 0, 1)]
    [InlineData("source.mp4", 10, 1, 0, 0, 1)]
    [InlineData("source.mp4", 10, 1, 24, -1, 2)]
    [InlineData("source.mp4", 10, 1, 24, 2, 2)]
    [InlineData("source.mp4", 10, 1, 24, 2, 1)]
    [InlineData("source.mp4", 10, 1, 24, 1, 11)]
    [InlineData("source.mp4", 10, 1, 24, 1, 1.01)]
    public void RejectsInvalidOrShortMarkedRanges(
        string path, double duration, int startFrame, double frameRate, double sourceIn, double sourceOut)
    {
        Assert.False(VideoSourceRangeMarks.TryCreateClip(
            path, duration, startFrame, frameRate, sourceIn, sourceOut, out TimelineClipProject? clip));
        Assert.Null(clip);
    }

    [Fact]
    public void AllowsOneFrameRangeAndExactSourceEnd()
    {
        Assert.True(VideoSourceRangeMarks.TryCreateClip(
            "source.mp4", 10, 1, 24, 9, 10, out TimelineClipProject? clip));
        Assert.NotNull(clip);
    }
}
