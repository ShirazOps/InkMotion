using Xunit;

namespace InkMotion.Tests;

public sealed class VideoSourceRangeTests
{
    [Fact]
    public void ChangesBothEndpointsWithoutMovingTimelineOrChangingProperties()
    {
        var clip = new TimelineClipProject
        {
            Id = "selected", SourcePath = "source.mp4", StartFrame = 25,
            SourceDurationSeconds = 10, SourceInSeconds = 1, SourceOutSeconds = 3,
            FadeInSeconds = 0.2, FadeOutSeconds = 0.3, Volume = 0.4, Opacity = 0.6,
        };
        var next = new TimelineClipProject { SourcePath = "next.mp4", StartFrame = 145, SourceDurationSeconds = 3 };
        var track = new TimelineTrackProject { Clips = [clip, next] };
        Assert.True(VideoEditTools.SetSourceRange(track, clip, 4, 8, 24));
        Assert.Equal(25, clip.StartFrame);
        Assert.Equal(4, clip.SourceInSeconds);
        Assert.Equal(8, clip.SourceOutSeconds);
        Assert.Equal(10, clip.SourceDurationSeconds);
        Assert.Equal(4, VideoTimelineEditing.GetDurationSeconds(clip, track.Kind));
        Assert.Equal(0.2, clip.FadeInSeconds);
        Assert.Equal(0.3, clip.FadeOutSeconds);
        Assert.Equal(0.4, clip.Volume);
        Assert.Equal(0.6, clip.Opacity);
        Assert.Equal(145, next.StartFrame);
        var project = new EditorProject { VideoTracks = [track] };
        EditorProject reopened = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        Assert.Equal(4, reopened.VideoTracks[0].Clips[0].SourceInSeconds);
        Assert.Equal(8, reopened.VideoTracks[0].Clips[0].SourceOutSeconds);
    }

    [Theory]
    [InlineData(-1, 3, 24)]
    [InlineData(2, 11, 24)]
    [InlineData(4, 3, 24)]
    [InlineData(2, 2.01, 24)]
    [InlineData(1, 3, 24)]
    [InlineData(1, 7, 24)]
    [InlineData(1, 4, 0)]
    [InlineData(double.NaN, 3, 24)]
    [InlineData(1, double.PositiveInfinity, 24)]
    public void InvalidOrUnchangedRangeRejectsWithoutMutation(double sourceIn, double sourceOut, double rate)
    {
        var clip = new TimelineClipProject
        {
            SourcePath = "source.mp4", SourceDurationSeconds = 10, SourceInSeconds = 1, SourceOutSeconds = 3,
        };
        var next = new TimelineClipProject { SourcePath = "next.mp4", SourceDurationSeconds = 2, StartFrame = 121 };
        var track = new TimelineTrackProject { Clips = [clip, next] };
        var project = new EditorProject { VideoTracks = [track] };
        string before = EditorProjectSerialiser.Serialise(project);
        Assert.False(VideoEditTools.SetSourceRange(track, clip, sourceIn, sourceOut, rate));
        Assert.Equal(before, EditorProjectSerialiser.Serialise(project));
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("image")]
    [InlineData("foreign")]
    [InlineData("fades")]
    public void RejectsProtectedOrIncompatibleClips(string reason)
    {
        var clip = new TimelineClipProject
        {
            SourcePath = "source.mp4", SourceDurationSeconds = 10, SourceOutSeconds = 5,
            FadeInSeconds = 1, FadeOutSeconds = 1,
        };
        var track = new TimelineTrackProject { Clips = [clip] };
        if (reason == "locked") track.IsLocked = true;
        if (reason == "image") track.Kind = TimelineTrackKind.Image;
        TimelineClipProject selected = reason == "foreign" ? VideoTimelineEditing.Clone(clip) : clip;
        Assert.False(VideoEditTools.SetSourceRange(track, selected, 2, reason == "fades" ? 3 : 6, 24));
        Assert.Equal(0, clip.SourceInSeconds);
        Assert.Equal(5, clip.SourceOutSeconds);
    }

    [Fact]
    public void AllowsExactNeighbourBoundaryAndOneFrameClip()
    {
        var clip = new TimelineClipProject { SourcePath = "source.mp4", SourceDurationSeconds = 10, SourceOutSeconds = 1 };
        var next = new TimelineClipProject { SourcePath = "next.mp4", SourceDurationSeconds = 1, StartFrame = 49 };
        var track = new TimelineTrackProject { Clips = [clip, next] };
        Assert.True(VideoEditTools.SetSourceRange(track, clip, 3, 5, 24));
        Assert.True(VideoEditTools.SetSourceRange(track, clip, 3, 3 + 1d / 24, 24));
    }
}
