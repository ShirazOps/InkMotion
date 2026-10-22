using Xunit;

namespace InkMotion.Tests;

public sealed class VideoTransformKeyframesTests
{
    [Fact]
    public void GetValue_InterpolatesLinearlyAndHoldsEndpointValues()
    {
        TimelineClipProject clip = CreateVideoClip();
        clip.TransformKeyframes =
        [
            new() { FrameOffset = 0, X = 0.2, Y = 0.3, Scale = 1, RotationDegrees = -20 },
            new() { FrameOffset = 48, X = 0.8, Y = 0.7, Scale = 3, RotationDegrees = 20 },
        ];

        Assert.Equal(new VideoTransformValue(0.2, 0.3, 1, -20), VideoTransformKeyframes.GetValue(clip, -1));
        Assert.Equal(new VideoTransformValue(0.5, 0.5, 2, 0), VideoTransformKeyframes.GetValue(clip, 24));
        Assert.Equal(new VideoTransformValue(0.8, 0.7, 3, 20), VideoTransformKeyframes.GetValue(clip, 100));
    }

    [Fact]
    public void SetAndRemoveAtFrame_PreservesInterpolatedValueAndKeepsKeysSorted()
    {
        TimelineClipProject clip = CreateVideoClip();
        clip.TransformKeyframes =
        [
            new() { FrameOffset = 0, X = 0.2, Y = 0.5, Scale = 1, RotationDegrees = 0 },
            new() { FrameOffset = 48, X = 0.8, Y = 0.5, Scale = 3, RotationDegrees = 0 },
        ];

        Assert.True(VideoTransformKeyframes.TrySetAtFrame(clip, TimelineTrackKind.Video, 24, 24));
        Assert.Equal(new[] { 0, 24, 48 }, clip.TransformKeyframes.Select(key => key.FrameOffset));
        Assert.Equal(0.5, clip.TransformKeyframes[1].X);
        Assert.True(VideoTransformKeyframes.SetValueAtTime(
            clip, TimelineTrackKind.Video, 1, 24, new VideoTransformValue(0.6, 0.4, 2.5, 30)));
        Assert.Equal(0.6, clip.TransformKeyframes[1].X);
        Assert.True(VideoTransformKeyframes.TryRemoveAtFrame(clip, 24));
        Assert.Equal(0, VideoTransformKeyframes.FindAdjacentFrame(clip, 24, next: false));
        Assert.Equal(48, VideoTransformKeyframes.FindAdjacentFrame(clip, 24, next: true));
    }

    [Fact]
    public void SplitAndTrim_PreserveTransformTimingWithinEachResultingClip()
    {
        TimelineClipProject clip = CreateVideoClip();
        clip.TransformKeyframes =
        [
            new() { FrameOffset = 0, X = 0.2, Y = 0.5, Scale = 1, RotationDegrees = 0 },
            new() { FrameOffset = 48, X = 0.8, Y = 0.5, Scale = 3, RotationDegrees = 0 },
        ];
        TimelineTrackProject track = new() { Kind = TimelineTrackKind.Video, Clips = [clip] };

        TimelineClipProject right = Assert.IsType<TimelineClipProject>(VideoTimelineEditing.Split(track, clip, 25, 24));
        Assert.Equal(0, right.TransformKeyframes[0].FrameOffset);
        Assert.Equal(0.5, right.TransformKeyframes[0].X);
        Assert.Equal(0, clip.TransformKeyframes[0].FrameOffset);
        Assert.Equal(23, clip.TransformKeyframes[^1].FrameOffset);
        Assert.Equal(0.4875, clip.TransformKeyframes[^1].X, 6);

        Assert.True(VideoTimelineEditing.TryTrimStart(track, right, 37, 24));
        Assert.Equal(0, right.TransformKeyframes[0].FrameOffset);
        Assert.Equal(0.65, right.TransformKeyframes[0].X, 6);
        Assert.True(VideoTimelineEditing.TryTrimEnd(track, right, 61, 24));
        Assert.All(right.TransformKeyframes, key => Assert.InRange(key.FrameOffset, 0, 23));
    }

    [Fact]
    public void FasterPlayback_TrimsTransformKeysToTheNewClipEnd()
    {
        TimelineClipProject clip = CreateVideoClip();
        clip.SourceOutSeconds = 4;
        clip.TransformKeyframes =
        [
            new() { FrameOffset = 0, X = 0.2, Y = 0.5, Scale = 1 },
            new() { FrameOffset = 95, X = 0.8, Y = 0.5, Scale = 2 },
        ];
        TimelineTrackProject track = new() { Kind = TimelineTrackKind.Video, Clips = [clip] };

        Assert.True(VideoTimelineEditing.TrySetPlaybackSpeed(track, clip, 2, 24));

        Assert.Equal(new[] { 0, 47 }, clip.TransformKeyframes.Select(key => key.FrameOffset));
        Assert.Null(VideoTimelineEditing.Validate([track], 24));
    }

    [Fact]
    public void Validate_RejectsDuplicateOutOfRangeAndInvalidTransformKeys()
    {
        TimelineTrackProject track = new()
        {
            Kind = TimelineTrackKind.Image,
            Clips = [new TimelineClipProject { SourcePath = "image.png", DurationSeconds = 2 }],
        };
        TimelineClipProject clip = track.Clips[0];
        clip.TransformKeyframes =
        [
            new() { FrameOffset = 0, X = 0.5, Y = 0.5, Scale = 1 },
            new() { FrameOffset = 0, X = 0.5, Y = 0.5, Scale = 1 },
        ];
        Assert.NotNull(VideoTimelineEditing.Validate([track], 24));

        clip.TransformKeyframes =
        [
            new() { FrameOffset = 48, X = 0.5, Y = 0.5, Scale = 1 },
        ];
        Assert.NotNull(VideoTimelineEditing.Validate([track], 24));

        clip.TransformKeyframes =
        [
            new() { FrameOffset = 1, X = 0.5, Y = 0.5, Scale = 10 },
        ];
        Assert.NotNull(VideoTimelineEditing.Validate([track], 24));
    }

    [Fact]
    public void Keyframes_RoundTripAndMoveWithTheClip()
    {
        TimelineClipProject clip = CreateVideoClip();
        clip.StartFrame = 121;
        clip.TransformKeyframes =
        [
            new() { FrameOffset = 12, X = 0.25, Y = 0.6, Scale = 1.5, RotationDegrees = 15 },
        ];
        EditorProject restored = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(
            new EditorProject
            {
                VideoTracks = [new TimelineTrackProject { Kind = TimelineTrackKind.Video, Clips = [clip] }],
            }));

        TimelineClipProject restoredClip = Assert.Single(Assert.Single(restored.VideoTracks).Clips);
        Assert.Equal(EditorProject.CurrentVersion, restored.Version);
        Assert.Equal(121, restoredClip.StartFrame);
        VideoTransformKeyframe keyframe = Assert.Single(restoredClip.TransformKeyframes);
        Assert.Equal(12, keyframe.FrameOffset);
        Assert.Equal(0.25, keyframe.X);
        Assert.Equal(15, keyframe.RotationDegrees);
        Assert.Equal(0.25, VideoTransformKeyframes.GetValueAtTime(restoredClip, 5.5, 24).X);
    }

    [Fact]
    public void RescaleFrameRate_PreservesKeyframeTimesWithinTheClip()
    {
        TimelineTrackProject track = new()
        {
            Kind = TimelineTrackKind.Video,
            Clips = [CreateVideoClip()],
        };
        TimelineClipProject clip = track.Clips[0];
        clip.TransformKeyframes =
        [
            new() { FrameOffset = 12, X = 0.2, Y = 0.5, Scale = 1 },
            new() { FrameOffset = 24, X = 0.8, Y = 0.5, Scale = 2 },
        ];

        VideoTimelineEditing.RescaleFrameRate([track], 24, 48);

        Assert.Equal(new[] { 24, 48 }, clip.TransformKeyframes.Select(key => key.FrameOffset));
        Assert.Null(VideoTimelineEditing.Validate([track], 48));
    }

    private static TimelineClipProject CreateVideoClip() => new()
    {
        SourcePath = "video.mp4",
        SourceDurationSeconds = 4,
    };
}
