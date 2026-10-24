using System.Text.Json.Nodes;
using Xunit;

namespace InkMotion.Tests;

public sealed class VideoClipAudioBalanceTests
{
    [Fact]
    public void EmbeddedAudioBalance_ClonesPersistsAndIsBypassedByOriginalPreview()
    {
        TimelineClipProject clip = new()
        {
            SourcePath = "video.mp4",
            SourceDurationSeconds = 4,
            EmbeddedAudioBalance = -0.75,
        };
        TimelineClipProject clone = VideoTimelineEditing.Clone(clip);
        TimelineTrackProject track = new() { Kind = TimelineTrackKind.Video, Clips = [clip] };
        TimelineClipLocation location = new(track, clip);

        Assert.Equal(-0.75, clone.EmbeddedAudioBalance);
        Assert.Equal(-0.75, VideoTimelineEditing.CreatePreviewAudioProject(location, false, false).Balance);
        Assert.Equal(0, VideoTimelineEditing.CreatePreviewAudioProject(location, true, false).Balance);

        EditorProject restored = EditorProjectSerialiser.Deserialise(
            EditorProjectSerialiser.Serialise(new EditorProject { VideoTracks = [track] }));
        Assert.Equal(-0.75, Assert.Single(Assert.Single(restored.VideoTracks).Clips).EmbeddedAudioBalance);
    }

    [Fact]
    public void VersionSixtyTwoProjectsMigrateWithCentredEmbeddedAudio()
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
        json["Version"] = 62;
        JsonObject clipJson = json["VideoTracks"]![0]!["Clips"]![0]!.AsObject();
        clipJson.Remove("EmbeddedAudioBalance");

        EditorProject restored = EditorProjectSerialiser.Deserialise(json.ToJsonString());
        TimelineClipProject clip = Assert.Single(Assert.Single(restored.VideoTracks).Clips);
        Assert.Equal(EditorProject.CurrentVersion, restored.Version);
        Assert.Equal(0, clip.EmbeddedAudioBalance);
    }
}
