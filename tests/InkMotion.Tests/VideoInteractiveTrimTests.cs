using Xunit;

namespace InkMotion.Tests;

public sealed class VideoInteractiveTrimTests
{
    [Theory]
    [InlineData(TimelineTrackKind.Text)]
    [InlineData(TimelineTrackKind.Image)]
    public void StillClipEdgesCanShortenExtendAndRestoreDuration(TimelineTrackKind kind)
    {
        var clip = new TimelineClipProject
        {
            StartFrame = 49, DurationSeconds = 4, Text = "Title", SourcePath = "image.png",
            TextBackgroundColour = 0x80FF0000,
            TransformKeyframes = [new() { FrameOffset = 24, X = 0.4, Y = 0.5 }],
        };
        var track = new TimelineTrackProject { Kind = kind, Clips = [clip] };

        Assert.True(VideoEditTools.TryTrimEdge(track, clip, TimelineTrimEdge.Start, 25, 24));
        Assert.Equal(5, clip.DurationSeconds);
        Assert.Equal(48, Assert.Single(clip.TransformKeyframes).FrameOffset);
        Assert.True(VideoEditTools.TryTrimEdge(track, clip, TimelineTrimEdge.End, 169, 24));
        Assert.Equal(6, clip.DurationSeconds);
        Assert.True(VideoEditTools.TryTrimEdge(track, clip, TimelineTrimEdge.End, 145, 24));
        Assert.Equal(5, clip.DurationSeconds);
        Assert.True(VideoEditTools.TryTrimEdge(track, clip, TimelineTrimEdge.Start, 49, 24));
        Assert.Equal(4, clip.DurationSeconds);
        Assert.Equal(0x80FF0000u, clip.TextBackgroundColour);
        Assert.Equal(0.4, VideoTransformKeyframes.GetValue(clip, 24).X);
        Assert.Null(VideoTimelineEditing.Validate([track], 24));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VideoExtensionHonoursSpeedDirectionAndAvailableSource(bool reversed)
    {
        var clip = new TimelineClipProject
        {
            SourcePath = "source.mp4", StartFrame = 49, SourceDurationSeconds = 10,
            SourceInSeconds = 2, SourceOutSeconds = 8, PlaybackSpeed = 2, IsReversed = reversed,
        };
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Video, Clips = [clip] };
        Assert.True(VideoEditTools.TryTrimEdge(track, clip, TimelineTrimEdge.Start, 25, 24));
        Assert.Equal(reversed ? 2 : 0, clip.SourceInSeconds);
        Assert.Equal(reversed ? 10 : 8, clip.SourceOutSeconds);
        Assert.True(VideoEditTools.TryTrimEdge(track, clip, TimelineTrimEdge.End, 145, 24));
        Assert.Equal(0, clip.SourceInSeconds);
        Assert.Equal(10, clip.SourceOutSeconds);
        string before = EditorProjectSerialiser.Serialise(new EditorProject { VideoTracks = [track] });
        Assert.False(VideoEditTools.TryTrimEdge(track, clip, TimelineTrimEdge.End, 169, 24));
        Assert.Equal(before, EditorProjectSerialiser.Serialise(new EditorProject { VideoTracks = [track] }));
    }

    [Fact]
    public void InteractiveTrimRetainsRetimedAnimationInsteadOfDroppingIt()
    {
        var clip = new TimelineClipProject
        {
            SourcePath = "source.mp4", SourceDurationSeconds = 10,
            TransformKeyframes = [new() { FrameOffset = 48, X = 0.3, Y = 0.6 }],
            ChromaKeyEnabledKeyframes = [new() { FrameOffset = 48, Enabled = true }],
        };
        var track = new TimelineTrackProject { Clips = [clip] };
        Assert.True(VideoEditTools.TryTrimEdge(track, clip, TimelineTrimEdge.Start, 25, 24));
        Assert.Contains(clip.TransformKeyframes, key => key.FrameOffset == 24 && key.X == 0.3);
        Assert.Contains(clip.ChromaKeyEnabledKeyframes, key => key.FrameOffset == 24);
        Assert.Null(VideoTimelineEditing.Validate([track], 24));
    }

    [Fact]
    public void StartEdgeTrimAdvancesSourceAndPreservesClipIdentityAndContent()
    {
        var clip = new TimelineClipProject
        {
            Id = "clip", SourcePath = "source.mp4", StartFrame = 25, SourceDurationSeconds = 10,
            SourceInSeconds = 1, FadeInSeconds = 1, FadeOutSeconds = 0.5, Volume = 0.4, Opacity = 0.7,
        };
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Video, Clips = [clip] };

        Assert.True(VideoEditTools.TryTrimEdge(track, clip, TimelineTrimEdge.Start, 49, 24));

        Assert.Same(clip, Assert.Single(track.Clips));
        Assert.Equal(49, clip.StartFrame);
        Assert.Equal(2, clip.SourceInSeconds, 6);
        Assert.Null(clip.SourceOutSeconds);
        Assert.Equal(8, VideoTimelineEditing.GetDurationSeconds(clip, track.Kind), 6);
        Assert.Equal(1, clip.FadeInSeconds, 6);
        Assert.Equal(0.5, clip.FadeOutSeconds, 6);
        Assert.Equal(0.4, clip.Volume);
        Assert.Equal(0.7, clip.Opacity);
    }

    [Fact]
    public void EndEdgeTrimSetsSourceOutAndKeepsTimelineStart()
    {
        var clip = new TimelineClipProject
        {
            SourcePath = "source.mp4", StartFrame = 25, SourceDurationSeconds = 10,
            FadeInSeconds = 1, FadeOutSeconds = 2,
        };
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Video, Clips = [clip] };

        Assert.True(VideoEditTools.TryTrimEdge(track, clip, TimelineTrimEdge.End, 121, 24));

        Assert.Equal(25, clip.StartFrame);
        Assert.Equal(4, clip.SourceOutSeconds!.Value, 6);
        Assert.Equal(4, VideoTimelineEditing.GetDurationSeconds(clip, track.Kind), 6);
        Assert.Equal(1, clip.FadeInSeconds, 6);
        Assert.Equal(2, clip.FadeOutSeconds, 6);
    }

    [Fact]
    public void RejectsEdgeTrimThatOverlapsNeighbourWithoutMutating()
    {
        var clip = new TimelineClipProject { SourcePath = "source.mp4", StartFrame = 25, SourceDurationSeconds = 5 };
        var next = new TimelineClipProject { SourcePath = "next.mp4", StartFrame = 145, SourceDurationSeconds = 5 };
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Video, Clips = [clip, next] };
        string before = EditorProjectSerialiser.Serialise(new EditorProject { VideoTracks = [track] });

        Assert.False(VideoEditTools.TryTrimEdge(track, clip, TimelineTrimEdge.End, 169, 24));
        Assert.Equal(before, EditorProjectSerialiser.Serialise(new EditorProject { VideoTracks = [track] }));
    }

    [Fact]
    public void RejectsLockedTrackAndCutsOutsideClip()
    {
        var clip = new TimelineClipProject { SourcePath = "source.mp4", StartFrame = 25, SourceDurationSeconds = 5 };
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Video, Clips = [clip] };
        Assert.False(VideoEditTools.TryTrimEdge(track, clip, TimelineTrimEdge.Start, 25, 24));
        track.IsLocked = true;
        Assert.False(VideoEditTools.TryTrimEdge(track, clip, TimelineTrimEdge.End, 49, 24));
        Assert.Equal(25, clip.StartFrame);
        Assert.Null(clip.SourceOutSeconds);
    }
}
