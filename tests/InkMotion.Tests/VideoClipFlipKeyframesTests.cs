using Xunit;

namespace InkMotion.Tests;

public sealed class VideoClipFlipKeyframesTests
{
    [Fact]
    public void FlipKeyframes_HoldPersistAndFollowTimelineEdits()
    {
        TimelineClipProject clip = new()
        {
            SourcePath = "scene.mp4",
            SourceDurationSeconds = 6,
            FlipKeyframes =
            [
                new() { FrameOffset = 0, Horizontal = false, Vertical = false },
                new() { FrameOffset = 72, Horizontal = true, Vertical = false },
                new() { FrameOffset = 96, Horizontal = true, Vertical = true },
            ],
        };
        TimelineTrackProject track = new() { Kind = TimelineTrackKind.Video, Clips = [clip] };

        Assert.Equal(new VideoClipFlipValue(false, false), VideoClipFlipKeyframes.GetValue(clip, 71));
        Assert.Equal(new VideoClipFlipValue(true, false), VideoClipFlipKeyframes.GetValue(clip, 72));
        Assert.Equal(new VideoClipFlipValue(true, true), VideoClipFlipKeyframes.GetValue(clip, 96));
        Assert.True(VideoClipFlipKeyframes.SetValueAtTime(
            clip, track.Kind, 3.5, 24, new VideoClipFlipValue(false, true)));
        Assert.Equal(new VideoClipFlipValue(false, true), VideoClipFlipKeyframes.GetValue(clip, 84));
        Assert.Null(VideoTimelineEditing.Validate([track], 24));

        TimelineClipProject right = Assert.IsType<TimelineClipProject>(
            VideoTimelineEditing.Split(track, clip, 49, 24));
        Assert.Equal(new VideoClipFlipValue(false, false), VideoClipFlipKeyframes.GetValue(right, 0));
        Assert.True(VideoTimelineEditing.TryTrimStart(track, right, 61, 24));
        Assert.Equal(0, right.FlipKeyframes[0].FrameOffset);
        VideoTimelineEditing.RescaleFrameRate([track], 24, 60);
        Assert.Null(VideoTimelineEditing.Validate([track], 60));

        TimelineClipProject restored = Assert.Single(Assert.Single(EditorProjectSerialiser.Deserialise(
            EditorProjectSerialiser.Serialise(new EditorProject { FrameRate = 60, VideoTracks = [track] }))
            .VideoTracks).Clips, item => item.Id == clip.Id);
        Assert.Equal(clip.FlipKeyframes.Select(key => (key.FrameOffset, key.Horizontal, key.Vertical)),
            restored.FlipKeyframes.Select(key => (key.FrameOffset, key.Horizontal, key.Vertical)));
    }
}
