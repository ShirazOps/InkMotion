using Microsoft.Graphics.Canvas;
using System.Text.Json.Nodes;
using Windows.Graphics.DirectX;
using Xunit;

namespace InkMotion.Tests;

public sealed class VideoBlurTests
{
    [Fact]
    public void VersionTwentySevenProject_MigratesWithBlurDisabled()
    {
        EditorProject project = EditorProjectSerialiser.Deserialise(
            """{"Version":27,"VideoTracks":[{"Id":"video","Kind":"Video","Name":"Video 1","Clips":[{"Id":"clip","SourcePath":"scene.mp4","SourceDurationSeconds":3,"ChromaKeyEnabled":true}]}],"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""");

        TimelineClipProject clip = Assert.Single(Assert.Single(project.VideoTracks).Clips);
        Assert.Equal(EditorProject.CurrentVersion, project.Version);
        Assert.True(clip.ChromaKeyEnabled);
        Assert.Equal(0, clip.BlurAmount);
    }

    [Fact]
    public void BlurAmount_RoundTripsAndClonesWithTheClip()
    {
        EditorProject project = new()
        {
            VideoTracks =
            [
                new TimelineTrackProject
                {
                    Id = "video",
                    Kind = TimelineTrackKind.Video,
                    Name = "Video 1",
                    Clips =
                    [
                        new TimelineClipProject
                        {
                            Id = "clip",
                            SourcePath = "scene.mp4",
                            SourceDurationSeconds = 3,
                            BlurAmount = 8.5,
                        },
                    ],
                },
            ],
        };

        TimelineClipProject original = Assert.Single(Assert.Single(project.VideoTracks).Clips);
        TimelineClipProject clone = VideoTimelineEditing.Clone(original);
        TimelineClipProject restored = Assert.Single(Assert.Single(
            EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project)).VideoTracks).Clips);

        Assert.Equal(8.5, clone.BlurAmount);
        Assert.Equal(clone.BlurAmount, restored.BlurAmount);
    }

    [Fact]
    public void BlurKeyframes_InterpolateAndPreserveTimingThroughTimelineEdits()
    {
        TimelineClipProject clip = new()
        {
            SourcePath = "scene.mp4",
            SourceDurationSeconds = 4,
            BlurKeyframes =
            [
                new() { FrameOffset = 0, Amount = 0 },
                new() { FrameOffset = 48, Amount = 12 },
                new() { FrameOffset = 95, Amount = 3 },
            ],
        };
        TimelineTrackProject track = new() { Kind = TimelineTrackKind.Video, Clips = [clip] };

        Assert.Equal(6, VideoBlurKeyframes.GetValue(clip, 24));
        Assert.True(VideoBlurKeyframes.SetValueAtTime(clip, track.Kind, 1, 24, 8));
        Assert.Equal(8, VideoBlurKeyframes.GetValue(clip, 24));
        TimelineClipProject right = Assert.IsType<TimelineClipProject>(
            VideoTimelineEditing.Split(track, clip, 49, 24));
        Assert.Equal(12, right.BlurKeyframes[0].Amount);
        Assert.Equal(47, clip.BlurKeyframes[^1].FrameOffset);
        Assert.True(VideoTimelineEditing.TryTrimStart(track, right, 61, 24));
        Assert.Equal(0, right.BlurKeyframes[0].FrameOffset);
        Assert.True(VideoTimelineEditing.TryTrimEnd(track, right, 85, 24));

        VideoTimelineEditing.RescaleFrameRate([track], 24, 48);
        Assert.All(right.BlurKeyframes, key => Assert.InRange(key.FrameOffset, 0, 47));
        Assert.Null(VideoTimelineEditing.Validate([track], 48));
    }

    [Fact]
    public void BlurKeyframes_RoundTripCloneAndMigrateVersionFortySeven()
    {
        TimelineClipProject clip = new()
        {
            SourcePath = "scene.mp4",
            SourceDurationSeconds = 3,
            BlurAmount = 1,
            BlurKeyframes =
            [
                new() { FrameOffset = 0, Amount = 2 },
                new() { FrameOffset = 24, Amount = 10 },
            ],
        };
        TimelineTrackProject track = new() { Kind = TimelineTrackKind.Video, Clips = [clip] };
        EditorProject restored = EditorProjectSerialiser.Deserialise(
            EditorProjectSerialiser.Serialise(new EditorProject { VideoTracks = [track] }));
        TimelineClipProject saved = Assert.Single(Assert.Single(restored.VideoTracks).Clips);

        Assert.Equal(new[] { 0, 24 }, VideoTimelineEditing.Clone(clip).BlurKeyframes.Select(key => key.FrameOffset));
        Assert.Equal(new[] { 0, 24 }, saved.BlurKeyframes.Select(key => key.FrameOffset));
        Assert.Equal(6, VideoBlurKeyframes.GetValue(saved, 12));
        Assert.Null(VideoTimelineEditing.Validate(restored.VideoTracks, restored.FrameRate));

        EditorProject legacyProject = new()
        {
            VideoTracks =
            [
                new TimelineTrackProject
                {
                    Kind = TimelineTrackKind.Video,
                    Clips = [new TimelineClipProject { SourcePath = "scene.mp4", SourceDurationSeconds = 3, BlurAmount = 4 }],
                },
            ],
        };
        JsonObject legacy = JsonNode.Parse(EditorProjectSerialiser.Serialise(legacyProject))!.AsObject();
        legacy["Version"] = 47;
        legacy["VideoTracks"]![0]!["Clips"]![0]!.AsObject().Remove("BlurKeyframes");
        EditorProject migrated = EditorProjectSerialiser.Deserialise(legacy.ToJsonString());
        TimelineClipProject migratedClip = Assert.Single(Assert.Single(migrated.VideoTracks).Clips);
        Assert.Equal(EditorProject.CurrentVersion, migrated.Version);
        Assert.Equal(4, migratedClip.BlurAmount);
        Assert.Empty(migratedClip.BlurKeyframes);
    }

    [Fact]
    public void BlurKeyframes_StillDurationAndOverwritePreserveEvaluatedBoundaries()
    {
        TimelineClipProject image = new()
        {
            SourcePath = "image.png",
            DurationSeconds = 4,
            BlurKeyframes =
            [
                new() { FrameOffset = 0, Amount = 0 },
                new() { FrameOffset = 72, Amount = 12 },
            ],
        };
        TimelineTrackProject imageTrack = new() { Kind = TimelineTrackKind.Image, Clips = [image] };
        Assert.True(VideoTimelineEditing.TrySetStillDuration(imageTrack, image, 2, 24));
        Assert.Equal(47, image.BlurKeyframes[^1].FrameOffset);
        Assert.Equal(12 * 47d / 72, image.BlurKeyframes[^1].Amount, 6);
        Assert.Null(VideoTimelineEditing.Validate([imageTrack], 24));

        TimelineClipProject original = new()
        {
            Id = "original",
            SourcePath = "old.mp4",
            SourceDurationSeconds = 6,
            BlurKeyframes =
            [
                new() { FrameOffset = 0, Amount = 0 },
                new() { FrameOffset = 143, Amount = 20 },
            ],
        };
        TimelineTrackProject videoTrack = new() { Kind = TimelineTrackKind.Video, Clips = [original] };
        TimelineClipProject replacement = new() { Id = "replacement", SourcePath = "new.mp4", SourceDurationSeconds = 2 };
        Assert.True(VideoTimelinePlacement.TryPlace(
            videoTrack, replacement, 49, 24, VideoClipPlacementMode.Overwrite));

        TimelineClipProject[] remainders = videoTrack.Clips.Where(clip => clip.Id != replacement.Id).ToArray();
        Assert.Equal(2, remainders.Length);
        Assert.Equal(new[] { 0, 47 }, remainders[0].BlurKeyframes.Select(key => key.FrameOffset));
        Assert.Equal(new[] { 0, 47 }, remainders[1].BlurKeyframes.Select(key => key.FrameOffset));
        Assert.Equal(20 * 47d / 143, remainders[0].BlurKeyframes[^1].Amount, 6);
        Assert.Equal(20 * 96d / 143, remainders[1].BlurKeyframes[0].Amount, 6);
        Assert.Null(VideoTimelineEditing.Validate([videoTrack], 24));
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(20.01)]
    [InlineData(double.NaN)]
    public void Validation_RejectsUnsupportedBlurAmount(double blurAmount)
    {
        var clip = new TimelineClipProject
        {
            SourcePath = "scene.mp4",
            SourceDurationSeconds = 3,
            BlurAmount = blurAmount,
        };

        Assert.NotNull(VideoBlurEffect.Validate(clip, TimelineTrackKind.Video));
    }

    [Fact]
    public void Validation_RejectsBlurOnTitles()
    {
        var title = new TimelineClipProject { Text = "Title", BlurAmount = 1 };

        Assert.NotNull(VideoBlurEffect.Validate(title, TimelineTrackKind.Text));
    }

    [Fact]
    public void Render_BlurSoftensSourceEdges()
    {
        using var device = new CanvasDevice(true);
        byte[] sourcePixels = new byte[32 * 32 * 4];
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                int offset = (y * 32 + x) * 4;
                byte value = x < 16 ? (byte)0 : (byte)255;
                sourcePixels[offset] = value;
                sourcePixels[offset + 1] = value;
                sourcePixels[offset + 2] = value;
                sourcePixels[offset + 3] = 255;
            }
        }

        using var source = CanvasBitmap.CreateFromBytes(
            device, sourcePixels, 32, 32, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        using var target = new CanvasRenderTarget(device, 32, 32, 96);
        using var blur = new VideoBlurCanvasEffect();
        var clip = new TimelineClipProject
        {
            SourcePath = "edge-test.png",
            BlurAmount = 5,
        };

        using (CanvasDrawingSession session = target.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Black);
            TimelineCompositor.DrawFitted(
                session, source, source.Bounds, 32, 32, 1, 0.5, 0.5, 1,
                colourGrade: clip, blurEffect: blur);
        }

        byte[] rendered = target.GetPixelBytes();
        int edgePixel = (16 * 32 + 15) * 4;
        Assert.InRange(rendered[edgePixel], 20, 235);
    }

    [Fact]
    public void Render_AnimatedBlurAmountUsesTheSharedCompositor()
    {
        using var device = new CanvasDevice(true);
        byte[] sourcePixels = new byte[32 * 32 * 4];
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                int offset = (y * 32 + x) * 4;
                byte value = x < 16 ? (byte)0 : (byte)255;
                sourcePixels[offset] = value;
                sourcePixels[offset + 1] = value;
                sourcePixels[offset + 2] = value;
                sourcePixels[offset + 3] = 255;
            }
        }

        using var source = CanvasBitmap.CreateFromBytes(
            device, sourcePixels, 32, 32, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        using var target = new CanvasRenderTarget(device, 32, 32, 96);
        using var blur = new VideoBlurCanvasEffect();
        var clip = new TimelineClipProject { SourcePath = "edge-test.png", BlurAmount = 0 };

        using (CanvasDrawingSession session = target.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Black);
            TimelineCompositor.DrawFitted(
                session, source, source.Bounds, 32, 32, 1, 0.5, 0.5, 1,
                colourGrade: clip, blurEffect: blur, animatedBlurAmount: 5);
        }

        byte[] rendered = target.GetPixelBytes();
        int edgePixel = (16 * 32 + 15) * 4;
        Assert.InRange(rendered[edgePixel], 20, 235);
    }

    [Fact]
    public void Validation_RejectsInvalidBlurKeyframes()
    {
        TimelineClipProject clip = new()
        {
            SourcePath = "scene.mp4",
            SourceDurationSeconds = 3,
            BlurKeyframes = [new() { FrameOffset = 0, Amount = 20.1 }],
        };
        TimelineTrackProject track = new() { Kind = TimelineTrackKind.Video, Clips = [clip] };
        Assert.NotNull(VideoTimelineEditing.Validate([track], 24));
    }
}
