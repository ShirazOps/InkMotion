using Xunit;

namespace InkMotion.Tests;

public sealed class VideoEditToolsTests
{
    [Fact]
    public void Relink_PreservesDurationWhenReplacementIsLongerAndRejectsShorterSource()
    {
        var clip = new TimelineClipProject { SourcePath = "old.mp4", StartFrame = 25, SourceDurationSeconds = 10 };
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Video, Clips = [clip] };
        Assert.False(VideoEditTools.Relink(track, clip, "short.mp4", 5));
        Assert.Equal("old.mp4", clip.SourcePath);
        Assert.True(VideoEditTools.Relink(track, clip, "long.mp4", 20));
        Assert.Equal(10, clip.SourceOutSeconds);
        Assert.Equal(20, clip.SourceDurationSeconds);
        Assert.Equal(10, VideoTimelineEditing.GetDurationSeconds(clip, track.Kind));
        Assert.Equal(25, clip.StartFrame);
        track.IsLocked = true;
        Assert.False(VideoEditTools.Relink(track, clip, "locked.mp4", 20));
        Assert.Equal("long.mp4", clip.SourcePath);
    }

    [Fact]
    public void Snap_AlignsBothEdgesWithoutOverlappingClips()
    {
        var clip = new TimelineClipProject { DurationSeconds = 2 };
        var other = new TimelineClipProject { StartFrame = 121, DurationSeconds = 2 };
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Image, Clips = [clip, other] };
        Assert.Equal(73, VideoEditTools.SnapStart(track, clip, 74, [5], 24, 0.1));
        Assert.Equal(169, VideoEditTools.SnapStart(track, clip, 168, [7], 24, 0.1));
        Assert.Equal(120, VideoEditTools.SnapStart(track, clip, 120, [5], 24, 0.1));
        Assert.Equal(30, VideoEditTools.SnapStart(track, clip, 30, [5], 24, 0.1));
    }

    [Fact]
    public void Snap_UsesNearestAnchorWithDeterministicTiesAndRespectsLocks()
    {
        var clip = new TimelineClipProject { DurationSeconds = 1 };
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Image, Clips = [clip] };
        Assert.Equal(49, VideoEditTools.SnapStart(track, clip, 50, [2, 4], 24, 0.1));
        Assert.Equal(49, VideoEditTools.SnapStart(track, clip, 50, [50d / 24, 2], 24, 0.1));
        track.IsLocked = true;
        Assert.Equal(50, VideoEditTools.SnapStart(track, clip, 50, [2], 24, 0.1));
    }

    [Fact]
    public void Slip_ChangesOnlyTheSourceRangeAndRejectsUnavailableHandles()
    {
        var clip = new TimelineClipProject { StartFrame = 25, SourceInSeconds = 2, SourceOutSeconds = 5, SourceDurationSeconds = 10 };
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Video, Clips = [clip] };
        Assert.True(VideoEditTools.Slip(track, clip, -1));
        Assert.Equal(1, clip.SourceInSeconds);
        Assert.Equal(4, clip.SourceOutSeconds);
        Assert.Equal(25, clip.StartFrame);
        Assert.Equal(3, VideoTimelineEditing.GetDurationSeconds(clip, track.Kind));
        Assert.False(VideoEditTools.Slip(track, clip, -2));
        Assert.False(VideoEditTools.Slip(track, clip, 7));
        Assert.Equal(1, clip.SourceInSeconds);
        Assert.Equal(4, clip.SourceOutSeconds);
        track.IsLocked = true;
        Assert.False(VideoEditTools.Slip(track, clip, 1));
        track.IsLocked = false;
        track.Kind = TimelineTrackKind.Image;
        Assert.False(VideoEditTools.Slip(track, clip, 1));
    }
}
