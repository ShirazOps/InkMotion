using System.Text.Json.Nodes;
using Xunit;

namespace InkMotion.Tests;

public sealed class VideoClipVolumeKeyframesTests
{
    private const double FrameRate = 24;

    [Fact]
    public void VolumeKeyframes_InterpolateAndComposeWithClipGainAndFades()
    {
        TimelineClipProject clip = new()
        {
            SourcePath = "video.mp4",
            SourceDurationSeconds = 4,
            Volume = 0.8,
            FadeInSeconds = 1,
            VolumeKeyframes =
            [
                new() { FrameOffset = 0, Volume = 1 },
                new() { FrameOffset = 48, Volume = 0.5 },
                new() { FrameOffset = 72, Volume = 0 },
            ],
        };

        Assert.Equal(0.6, ExportPlanning.GetVideoClipAudioGain(clip, 1, default, FrameRate), 6);
        Assert.Equal(0.4, ExportPlanning.GetVideoClipAudioGain(clip, 2, default, FrameRate), 6);
        Assert.Equal(0, ExportPlanning.GetVideoClipAudioGain(clip, 3, default, FrameRate), 6);
        Assert.Equal(48, VideoClipVolumeKeyframes.FindAdjacentFrame(clip, 24, next: true));
        Assert.Equal(0, VideoClipVolumeKeyframes.FindAdjacentFrame(clip, 24, next: false));
    }

    [Fact]
    public void SplitTrimAndRoundTrip_PreserveVolumeKeyframeTiming()
    {
        TimelineClipProject clip = new()
        {
            SourcePath = "video.mp4",
            SourceDurationSeconds = 4,
            Volume = 0.8,
            VolumeKeyframes =
            [
                new() { FrameOffset = 0, Volume = 1 },
                new() { FrameOffset = 48, Volume = 0.5 },
                new() { FrameOffset = 95, Volume = 0 },
            ],
        };
        TimelineTrackProject track = new() { Kind = TimelineTrackKind.Video, Clips = [clip] };
        TimelineClipProject right = Assert.IsType<TimelineClipProject>(
            VideoTimelineEditing.Split(track, clip, 49, FrameRate));

        Assert.Equal(0.5, VideoClipVolumeKeyframes.GetValue(right, 0), 6);
        Assert.Equal(0, VideoClipVolumeKeyframes.GetValue(right, 47), 6);
        Assert.True(VideoTimelineEditing.TryTrimStart(track, right, 61, FrameRate));
        Assert.Equal(0.5 + ((0 - 0.5) * 12 / 47), VideoClipVolumeKeyframes.GetValue(right, 0), 6);

        EditorProject restored = EditorProjectSerialiser.Deserialise(
            EditorProjectSerialiser.Serialise(new EditorProject { VideoTracks = [track] }));
        TimelineClipProject restoredRight = Assert.Single(
            Assert.Single(restored.VideoTracks).Clips,
            candidate => candidate.Id == right.Id);
        Assert.Equal(right.VolumeKeyframes.Select(key => (key.FrameOffset, key.Volume)),
            restoredRight.VolumeKeyframes.Select(key => (key.FrameOffset, key.Volume)));
        Assert.Null(VideoTimelineEditing.Validate([track], FrameRate));
    }

    [Fact]
    public void VersionSixtyOneProjectsMigrateWithoutVolumeKeyframes()
    {
        EditorProject original = new()
        {
            VideoTracks =
            [
                new TimelineTrackProject
                {
                    Kind = TimelineTrackKind.Video,
                    Clips = [new TimelineClipProject { SourcePath = "video.mp4", SourceDurationSeconds = 4 }],
                },
            ],
        };
        JsonObject json = JsonNode.Parse(EditorProjectSerialiser.Serialise(original))!.AsObject();
        json["Version"] = 61;
        JsonObject clipJson = json["VideoTracks"]![0]!["Clips"]![0]!.AsObject();
        clipJson.Remove("VolumeKeyframes");

        EditorProject restored = EditorProjectSerialiser.Deserialise(json.ToJsonString());
        TimelineClipProject clip = Assert.Single(Assert.Single(restored.VideoTracks).Clips);
        Assert.Equal(EditorProject.CurrentVersion, restored.Version);
        Assert.Empty(clip.VolumeKeyframes);
        Assert.Equal(1, clip.Volume);
    }
}
