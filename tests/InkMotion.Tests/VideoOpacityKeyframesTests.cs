using Xunit;

namespace InkMotion.Tests;

public sealed class VideoOpacityKeyframesTests
{
    private const double FrameRate = 24;

    [Fact]
    public void GetValue_InterpolatesAndHoldsOutsideKeyedRange()
    {
        TimelineClipProject clip = CreateVideoClip();
        clip.OpacityKeyframes =
        [
            new() { FrameOffset = 12, Opacity = 0.2 },
            new() { FrameOffset = 60, Opacity = 0.8 },
        ];

        Assert.Equal(0.2, VideoOpacityKeyframes.GetValue(clip, 0));
        Assert.Equal(0.5, VideoOpacityKeyframes.GetValue(clip, 36));
        Assert.Equal(0.8, VideoOpacityKeyframes.GetValue(clip, 100));
    }

    [Fact]
    public void SliderEditsUpdateKeysAndOpacityStillComposesWithFades()
    {
        TimelineClipProject clip = new()
        {
            Text = "Title",
            DurationSeconds = 4,
            FadeInSeconds = 1,
            FadeOutSeconds = 2,
            Opacity = 0.8,
        };
        Assert.True(VideoOpacityKeyframes.TrySetAtFrame(clip, TimelineTrackKind.Text, 0, FrameRate));
        Assert.True(VideoOpacityKeyframes.SetValueAtTime(
            clip, TimelineTrackKind.Text, 2, FrameRate, 0.6));

        Assert.Equal(new[] { 0, 48 }, clip.OpacityKeyframes.Select(key => key.FrameOffset));
        Assert.Equal(0.375, VideoTimelineEditing.GetOpacity(clip, TimelineTrackKind.Text, 0.5, FrameRate), 6);
        Assert.Equal(0.15, VideoTimelineEditing.GetOpacity(clip, TimelineTrackKind.Text, 3.5, FrameRate), 6);

        Assert.True(VideoOpacityKeyframes.TryRemoveAtFrame(clip, 0));
        Assert.Equal(0.6, VideoTimelineEditing.GetOpacity(clip, TimelineTrackKind.Text, 2, FrameRate), 6);
    }

    [Fact]
    public void SplitAndTrim_PreserveOpacityTimingAndValues()
    {
        TimelineClipProject clip = CreateVideoClip();
        clip.OpacityKeyframes =
        [
            new() { FrameOffset = 0, Opacity = 0.2 },
            new() { FrameOffset = 48, Opacity = 0.8 },
            new() { FrameOffset = 95, Opacity = 0.1 },
        ];
        TimelineTrackProject track = new() { Kind = TimelineTrackKind.Video, Clips = [clip] };

        TimelineClipProject right = Assert.IsType<TimelineClipProject>(
            VideoTimelineEditing.Split(track, clip, 49, FrameRate));
        Assert.Equal(0.2 + ((0.8 - 0.2) * 47 / 48), VideoOpacityKeyframes.GetValue(clip, 47), 6);
        Assert.Equal(0.8, VideoOpacityKeyframes.GetValue(right, 0), 6);
        Assert.Equal(0.1, VideoOpacityKeyframes.GetValue(right, 47), 6);

        Assert.True(VideoTimelineEditing.TryTrimStart(track, right, 61, FrameRate));
        Assert.Equal(0.8 + ((0.1 - 0.8) * 12 / 47), VideoOpacityKeyframes.GetValue(right, 0), 6);
        Assert.True(VideoTimelineEditing.TryTrimEnd(track, right, 85, FrameRate));
        Assert.Equal(0, right.OpacityKeyframes[0].FrameOffset);
        Assert.Equal(23, right.OpacityKeyframes[^1].FrameOffset);
        Assert.Null(VideoTimelineEditing.Validate([track], FrameRate));
    }

    [Fact]
    public void PlaybackSpeedAndFrameRateChangesRetainValidOpacityKeys()
    {
        TimelineClipProject clip = CreateVideoClip();
        clip.OpacityKeyframes =
        [
            new() { FrameOffset = 0, Opacity = 0.2 },
            new() { FrameOffset = 24, Opacity = 0.6 },
            new() { FrameOffset = 95, Opacity = 1 },
        ];
        TimelineTrackProject track = new() { Kind = TimelineTrackKind.Video, Clips = [clip] };

        Assert.True(VideoTimelineEditing.TrySetPlaybackSpeed(track, clip, 2, FrameRate));
        Assert.Equal(new[] { 0, 24, 47 }, clip.OpacityKeyframes.Select(key => key.FrameOffset));
        VideoTimelineEditing.RescaleFrameRate([track], FrameRate, 48);

        Assert.Equal(new[] { 0, 48, 94 }, clip.OpacityKeyframes.Select(key => key.FrameOffset));
        Assert.Null(VideoTimelineEditing.Validate([track], 48));
    }

    [Fact]
    public void StillDurationAndOverwritePreserveOpacityKeyframes()
    {
        TimelineClipProject image = new()
        {
            SourcePath = "image.png",
            DurationSeconds = 4,
            OpacityKeyframes =
            [
                new() { FrameOffset = 0, Opacity = 0.2 },
                new() { FrameOffset = 72, Opacity = 0.8 },
            ],
        };
        TimelineTrackProject imageTrack = new() { Kind = TimelineTrackKind.Image, Clips = [image] };
        Assert.True(VideoTimelineEditing.TrySetStillDuration(imageTrack, image, 2, FrameRate));
        Assert.Equal(47, image.OpacityKeyframes[^1].FrameOffset);
        Assert.Equal(0.2 + ((0.8 - 0.2) * 47 / 72), image.OpacityKeyframes[^1].Opacity, 6);
        Assert.Null(VideoTimelineEditing.Validate([imageTrack], FrameRate));

        TimelineClipProject original = CreateVideoClip();
        original.SourceDurationSeconds = 6;
        original.OpacityKeyframes =
        [
            new() { FrameOffset = 0, Opacity = 0.2 },
            new() { FrameOffset = 143, Opacity = 0.8 },
        ];
        TimelineTrackProject videoTrack = new() { Kind = TimelineTrackKind.Video, Clips = [original] };
        TimelineClipProject replacement = new() { SourcePath = "replacement.mp4", SourceDurationSeconds = 2 };

        Assert.True(VideoTimelinePlacement.TryPlace(
            videoTrack, replacement, 49, FrameRate, VideoClipPlacementMode.Overwrite));

        TimelineClipProject[] unaffected = videoTrack.Clips.Where(clip => clip.Id != replacement.Id).ToArray();
        Assert.Equal(2, unaffected.Length);
        Assert.Equal((0, 47), (unaffected[0].OpacityKeyframes[0].FrameOffset,
            unaffected[0].OpacityKeyframes[^1].FrameOffset));
        Assert.Equal(0, unaffected[1].OpacityKeyframes[0].FrameOffset);
        Assert.Equal(47, unaffected[1].OpacityKeyframes[^1].FrameOffset);
        Assert.Null(VideoTimelineEditing.Validate([videoTrack], FrameRate));
    }

    [Fact]
    public void RoundTripAndClone_PreserveOpacityKeyframes()
    {
        TimelineClipProject clip = CreateVideoClip();
        clip.Opacity = 0.8;
        clip.OpacityKeyframes =
        [
            new() { FrameOffset = 12, Opacity = 0.25 },
            new() { FrameOffset = 48, Opacity = 0.75 },
        ];
        TimelineTrackProject track = new() { Kind = TimelineTrackKind.Video, Clips = [clip] };

        TimelineClipProject cloned = VideoTimelineEditing.Clone(clip);
        EditorProject restored = EditorProjectSerialiser.Deserialise(
            EditorProjectSerialiser.Serialise(new EditorProject { VideoTracks = [track] }));
        TimelineClipProject restoredClip = Assert.Single(Assert.Single(restored.VideoTracks).Clips);

        Assert.Equal(new[] { 12, 48 }, cloned.OpacityKeyframes.Select(key => key.FrameOffset));
        Assert.Equal(new[] { 12, 48 }, restoredClip.OpacityKeyframes.Select(key => key.FrameOffset));
        Assert.Equal(0.5, VideoOpacityKeyframes.GetValue(restoredClip, 30), 6);
        Assert.Equal(0.4, VideoOpacityKeyframes.GetOpacityAtTime(restoredClip, 30 / FrameRate, FrameRate), 6);
    }

    [Fact]
    public void Validate_RejectsDuplicateOutOfRangeAndUnsupportedOpacityKeys()
    {
        TimelineClipProject clip = CreateVideoClip();
        TimelineTrackProject track = new() { Kind = TimelineTrackKind.Video, Clips = [clip] };
        clip.OpacityKeyframes =
        [
            new() { FrameOffset = 0, Opacity = 0.5 },
            new() { FrameOffset = 0, Opacity = 0.75 },
        ];
        Assert.NotNull(VideoTimelineEditing.Validate([track], FrameRate));

        clip.OpacityKeyframes = [new() { FrameOffset = 96, Opacity = 0.5 }];
        Assert.NotNull(VideoTimelineEditing.Validate([track], FrameRate));

        clip.OpacityKeyframes = [new() { FrameOffset = 0, Opacity = 1.1 }];
        Assert.NotNull(VideoTimelineEditing.Validate([track], FrameRate));
    }

    private static TimelineClipProject CreateVideoClip() => new()
    {
        SourcePath = "video.mp4",
        SourceDurationSeconds = 4,
    };
}
