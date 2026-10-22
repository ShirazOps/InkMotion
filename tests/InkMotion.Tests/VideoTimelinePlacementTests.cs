using Xunit;

namespace InkMotion.Tests;

public sealed class VideoTimelinePlacementTests
{
    [Fact]
    public void InsertSplitsCrossingClipAndShiftsLaterClipsOnSameTrack()
    {
        var crossing = new TimelineClipProject
        {
            Id = "crossing", SourcePath = "old.mp4", SourceDurationSeconds = 6,
            FadeInSeconds = 0.5, FadeOutSeconds = 0.5,
        };
        var later = new TimelineClipProject { Id = "later", SourcePath = "later.mp4", StartFrame = 193, SourceDurationSeconds = 2 };
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Video, Clips = [crossing, later] };
        var inserted = new TimelineClipProject { Id = "inserted", SourcePath = "new.mp4", SourceDurationSeconds = 2 };

        Assert.True(VideoTimelinePlacement.TryPlace(track, inserted, 49, 24, VideoClipPlacementMode.Insert));

        Assert.Equal(4, track.Clips.Count);
        Assert.Same(crossing, track.Clips.Single(clip => clip.Id == "crossing"));
        Assert.Equal(1, crossing.StartFrame);
        Assert.Equal(0, crossing.SourceInSeconds);
        Assert.Equal(2, crossing.SourceOutSeconds!.Value, 6);
        Assert.Equal(0, crossing.FadeOutSeconds);
        TimelineClipProject right = Assert.Single(track.Clips,
            clip => clip.Id != "crossing" && clip.Id != "later" && clip.Id != "inserted");
        Assert.Equal(97, right.StartFrame);
        Assert.Equal(2, right.SourceInSeconds, 6);
        Assert.Equal(0, right.FadeInSeconds);
        Assert.Equal(49, track.Clips.Single(clip => clip.Id == "inserted").StartFrame);
        Assert.Equal(241, later.StartFrame);
        Assert.Null(VideoTimelineEditing.Validate([track], 24));
    }

    [Fact]
    public void OverwritePreservesUnaffectedSourcePortionsAndDownstreamPositions()
    {
        var original = new TimelineClipProject
        {
            Id = "original", SourcePath = "old.mp4", SourceDurationSeconds = 8,
            FadeInSeconds = 0.5, FadeOutSeconds = 0.5,
        };
        var later = new TimelineClipProject { Id = "later", SourcePath = "later.mp4", StartFrame = 241, SourceDurationSeconds = 2 };
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Video, Clips = [original, later] };
        var inserted = new TimelineClipProject { Id = "inserted", SourcePath = "new.mp4", SourceDurationSeconds = 3 };

        Assert.True(VideoTimelinePlacement.TryPlace(track, inserted, 49, 24, VideoClipPlacementMode.Overwrite));

        Assert.Equal(4, track.Clips.Count);
        Assert.Same(original, track.Clips.Single(clip => clip.Id == "original"));
        Assert.Equal(1, original.StartFrame);
        Assert.Equal(2, original.SourceOutSeconds!.Value, 6);
        Assert.Equal(0, original.FadeOutSeconds);
        TimelineClipProject right = Assert.Single(track.Clips,
            clip => clip.Id != "original" && clip.Id != "later" && clip.Id != "inserted");
        Assert.Equal(121, right.StartFrame);
        Assert.Equal(5, right.SourceInSeconds, 6);
        Assert.Equal(0, right.FadeInSeconds);
        Assert.Equal(241, later.StartFrame);
        Assert.Null(VideoTimelineEditing.Validate([track], 24));
    }

    [Fact]
    public void OverwriteCanReplaceOneClipAndPreserveTrailingRemainder()
    {
        var original = new TimelineClipProject
        {
            Id = "original", SourcePath = "old.mp4", StartFrame = 49, SourceDurationSeconds = 6,
        };
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Video, Clips = [original] };
        var inserted = new TimelineClipProject { Id = "inserted", SourcePath = "new.mp4", SourceDurationSeconds = 3 };

        Assert.True(VideoTimelinePlacement.TryPlace(track, inserted, 25, 24, VideoClipPlacementMode.Overwrite));

        Assert.Equal(2, track.Clips.Count);
        TimelineClipProject remainder = Assert.Single(track.Clips, clip => clip.Id != "inserted");
        Assert.Equal(97, remainder.StartFrame);
        Assert.Equal(2, remainder.SourceInSeconds, 6);
        Assert.Equal(0, remainder.FadeInSeconds);
    }

    [Fact]
    public void ReverseOverwriteSplitsTheSourceRangeInPlaybackOrder()
    {
        var original = new TimelineClipProject
        {
            Id = "original",
            SourcePath = "old.mp4",
            SourceOutSeconds = 10,
            SourceDurationSeconds = 10,
            PlaybackSpeed = 2,
            IsReversed = true,
        };
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Video, Clips = [original] };
        var inserted = new TimelineClipProject
        {
            Id = "inserted",
            SourcePath = "new.mp4",
            SourceDurationSeconds = 2,
            PlaybackSpeed = 2,
        };

        Assert.True(VideoTimelinePlacement.TryPlace(track, inserted, 49, 24, VideoClipPlacementMode.Overwrite));

        TimelineClipProject left = track.Clips.Single(clip => clip.Id == "original");
        TimelineClipProject right = track.Clips.Single(clip => clip.Id != "original" && clip.Id != "inserted");
        Assert.Equal((6d, 10d), (left.SourceInSeconds, left.SourceOutSeconds));
        Assert.Equal((0d, 4d), (right.SourceInSeconds, right.SourceOutSeconds));
        Assert.Equal(73, right.StartFrame);
        Assert.Null(VideoTimelineEditing.Validate([track], 24));
    }

    [Fact]
    public void OverwriteCanRemoveFullyCoveredClip()
    {
        var original = new TimelineClipProject
        {
            Id = "covered", SourcePath = "old.mp4", StartFrame = 49, SourceDurationSeconds = 2,
        };
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Video, Clips = [original] };
        var inserted = new TimelineClipProject { Id = "inserted", SourcePath = "new.mp4", SourceDurationSeconds = 3 };

        Assert.True(VideoTimelinePlacement.TryPlace(track, inserted, 49, 24, VideoClipPlacementMode.Overwrite));

        Assert.Equal("inserted", Assert.Single(track.Clips).Id);
    }

    [Theory]
    [InlineData(TimelineTrackKind.Image)]
    [InlineData(TimelineTrackKind.Text)]
    public void RejectsWrongTrackKindWithoutMutation(TimelineTrackKind kind)
    {
        var original = kind == TimelineTrackKind.Text
            ? new TimelineClipProject { Text = "old", DurationSeconds = 4 }
            : new TimelineClipProject { SourcePath = "old.png", DurationSeconds = 4 };
        var track = new TimelineTrackProject { Kind = kind, Clips = [original] };
        var inserted = new TimelineClipProject { SourcePath = "new.mp4", SourceDurationSeconds = 2 };

        Assert.False(VideoTimelinePlacement.TryPlace(track, inserted, 1, 24, VideoClipPlacementMode.Insert));
        Assert.Same(original, Assert.Single(track.Clips));
    }

    [Fact]
    public void LockedOrInvalidPlacementRejectsWithoutMutation()
    {
        var original = new TimelineClipProject { SourcePath = "old.mp4", SourceDurationSeconds = 4 };
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Video, Clips = [original] };
        var inserted = new TimelineClipProject { SourcePath = "new.mp4", SourceDurationSeconds = 2 };
        string before = EditorProjectSerialiser.Serialise(new EditorProject { VideoTracks = [track] });
        track.IsLocked = true;
        Assert.False(VideoTimelinePlacement.TryPlace(track, inserted, 1, 24, VideoClipPlacementMode.Insert));
        track.IsLocked = false;
        Assert.False(VideoTimelinePlacement.TryPlace(track, inserted, 1, 0, VideoClipPlacementMode.Overwrite));
        Assert.Equal(before, EditorProjectSerialiser.Serialise(new EditorProject { VideoTracks = [track] }));
    }

    [Fact]
    public void RejectsDuplicateSourceClipIdWithoutMutation()
    {
        var existing = new TimelineClipProject { Id = "same", SourcePath = "old.mp4", SourceDurationSeconds = 4 };
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Video, Clips = [existing] };
        var inserted = new TimelineClipProject { Id = "same", SourcePath = "new.mp4", SourceDurationSeconds = 2 };

        Assert.False(VideoTimelinePlacement.TryPlace(track, inserted, 1, 24, VideoClipPlacementMode.Insert));

        Assert.Same(existing, Assert.Single(track.Clips));
        Assert.Equal("old.mp4", existing.SourcePath);
    }
}
