using Xunit;

namespace InkMotion.Tests;

public sealed class VideoInteractiveTrimTests
{
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
