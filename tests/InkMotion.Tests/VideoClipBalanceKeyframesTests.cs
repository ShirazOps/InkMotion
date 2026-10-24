using System.Text.Json.Nodes;
using NAudio.Wave;
using Xunit;

namespace InkMotion.Tests;

public sealed class VideoClipBalanceKeyframesTests
{
    private const double FrameRate = 24;

    [Fact]
    public void BalanceKeysInterpolateCloneAndSurviveSplitAndTrim()
    {
        var clip = new TimelineClipProject
        {
            SourceDurationSeconds = 4,
            SourceOutSeconds = 4,
            EmbeddedAudioBalance = 0.4,
            EmbeddedAudioBalanceKeyframes =
            [
                new() { FrameOffset = 0, Balance = -1 },
                new() { FrameOffset = 60, Balance = 1 },
            ],
        };

        Assert.Equal(0, VideoClipBalanceKeyframes.GetValue(clip, 30), 8);
        TimelineClipProject clone = VideoTimelineEditing.Clone(clip);
        Assert.NotSame(clip.EmbeddedAudioBalanceKeyframes, clone.EmbeddedAudioBalanceKeyframes);
        Assert.Equal(-1, VideoClipBalanceKeyframes.GetValue(clone, 0));

        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Video, Clips = [clip] };
        TimelineClipProject right = Assert.IsType<TimelineClipProject>(
            VideoTimelineEditing.Split(track, clip, 31, FrameRate));
        Assert.Equal(0, VideoClipBalanceKeyframes.GetValue(right, 0), 8);
        Assert.Equal(1, VideoClipBalanceKeyframes.GetValue(right, 30), 8);

        double expectedStart = VideoClipBalanceKeyframes.GetValue(right, 5);
        Assert.True(VideoTimelineEditing.TryTrimStart(track, right, 36, FrameRate));
        Assert.Equal(expectedStart, VideoClipBalanceKeyframes.GetValue(right, 0), 8);
        Assert.Equal(0, right.EmbeddedAudioBalanceKeyframes[0].FrameOffset);
    }

    [Fact]
    public void BalanceAutomationIsAppliedToEmbeddedAudioMixOverTime()
    {
        string path = Path.Combine(Path.GetTempPath(), $"inkmotion-video-balance-{Guid.NewGuid():N}.wav");
        try
        {
            using (var writer = new WaveFileWriter(path, new WaveFormat(48000, 16, 1)))
            {
                for (int sample = 0; sample < 48000; sample++)
                {
                    writer.WriteSample(0.25f);
                }
            }

            var clip = new TimelineClipProject
            {
                StartFrame = 49,
                SourcePath = path,
                SourceDurationSeconds = 1,
                SourceOutSeconds = 1,
                EmbeddedAudioBalanceKeyframes =
                [
                    new() { FrameOffset = 0, Balance = -1 },
                    new() { FrameOffset = 23, Balance = 1 },
                ],
            };
            var project = new EditorProject
            {
                FrameRate = FrameRate,
                VideoTracks = [new() { Kind = TimelineTrackKind.Video, Clips = [clip] }],
            };

            using var mixer = new ExportAudioMixer(project, 48000);
            float[] samples = new float[144000 * mixer.Channels];
            int offset = 0;
            while (offset < samples.Length)
            {
                int read = mixer.Read(samples, offset, Math.Min(1024 * mixer.Channels, samples.Length - offset));
                Assert.True(read > 0);
                offset += read;
            }

            int firstFrame = 96000 * mixer.Channels;
            Assert.True(samples[firstFrame] > samples[firstFrame + 1]);
            int finalFrame = (96000 + 47000) * mixer.Channels;
            Assert.True(samples[finalFrame + 1] > samples[finalFrame]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void VersionSixtyThreeProjectsMigrateWithNoBalanceAutomation()
    {
        EditorProject original = new()
        {
            VideoTracks =
            [
                new TimelineTrackProject
                {
                    Kind = TimelineTrackKind.Video,
                    Clips = [new TimelineClipProject
                    {
                        SourcePath = "video.mp4",
                        SourceDurationSeconds = 4,
                        EmbeddedAudioBalance = -0.5,
                    }],
                },
            ],
        };
        JsonObject json = JsonNode.Parse(EditorProjectSerialiser.Serialise(original))!.AsObject();
        json["Version"] = 63;
        JsonObject clipJson = json["VideoTracks"]![0]!["Clips"]![0]!.AsObject();
        clipJson.Remove("EmbeddedAudioBalanceKeyframes");

        EditorProject restored = EditorProjectSerialiser.Deserialise(json.ToJsonString());
        TimelineClipProject clip = Assert.Single(Assert.Single(restored.VideoTracks).Clips);
        Assert.Equal(EditorProject.CurrentVersion, restored.Version);
        Assert.Equal(-0.5, clip.EmbeddedAudioBalance);
        Assert.Empty(clip.EmbeddedAudioBalanceKeyframes);
    }
}
