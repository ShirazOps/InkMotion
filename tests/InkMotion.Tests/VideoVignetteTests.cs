using System.Text.Json.Nodes;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;
using Xunit;

namespace InkMotion.Tests;

public sealed class VideoVignetteTests
{
    [Fact]
    public void VersionTwentyEightProject_MigratesWithVignetteDisabled()
    {
        EditorProject project = EditorProjectSerialiser.Deserialise(
            """{"Version":28,"VideoTracks":[{"Id":"video","Kind":"Video","Name":"Video 1","Clips":[{"Id":"clip","SourcePath":"scene.mp4","SourceDurationSeconds":3,"BlurAmount":2}]}],"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""");

        TimelineClipProject clip = Assert.Single(Assert.Single(project.VideoTracks).Clips);
        Assert.Equal(EditorProject.CurrentVersion, project.Version);
        Assert.Equal(2, clip.BlurAmount);
        Assert.Equal(0, clip.VignetteAmount);
    }

    [Fact]
    public void VignetteAmount_RoundTripsAndClonesWithTheClip()
    {
        var clip = new TimelineClipProject
        {
            SourcePath = "scene.mp4",
            SourceDurationSeconds = 3,
            VignetteAmount = 0.65,
        };
        EditorProject project = new()
        {
            VideoTracks =
            [
                new TimelineTrackProject
                {
                    Kind = TimelineTrackKind.Video,
                    Clips = [clip],
                },
            ],
        };

        TimelineClipProject clone = VideoTimelineEditing.Clone(clip);
        TimelineClipProject restored = Assert.Single(Assert.Single(
            EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project)).VideoTracks).Clips);

        Assert.Equal(0.65, clone.VignetteAmount);
        Assert.Equal(clone.VignetteAmount, restored.VignetteAmount);
    }

    [Fact]
    public void VignetteKeyframes_InterpolateAndHoldEndpoints()
    {
        TimelineClipProject clip = new()
        {
            SourcePath = "scene.mp4",
            SourceDurationSeconds = 3,
            VignetteAmount = 0.25,
            VignetteKeyframes =
            [
                new() { FrameOffset = 12, Amount = 0.2 },
                new() { FrameOffset = 60, Amount = 0.8 },
            ],
        };

        Assert.Equal(0.2, VideoVignetteKeyframes.GetValue(clip, 0));
        Assert.Equal(0.5, VideoVignetteKeyframes.GetValue(clip, 36), 6);
        Assert.Equal(0.8, VideoVignetteKeyframes.GetValue(clip, 90));
    }

    [Fact]
    public void VignetteKeyframes_SplitTrimAndRescalePreserveTiming()
    {
        TimelineClipProject clip = new()
        {
            SourcePath = "scene.mp4",
            SourceDurationSeconds = 4,
            VignetteKeyframes =
            [
                new() { FrameOffset = 0, Amount = 0 },
                new() { FrameOffset = 48, Amount = 1 },
                new() { FrameOffset = 95, Amount = 0.5 },
            ],
        };
        TimelineTrackProject track = new() { Kind = TimelineTrackKind.Video, Clips = [clip] };

        TimelineClipProject right = Assert.IsType<TimelineClipProject>(
            VideoTimelineEditing.Split(track, clip, 49, 24));
        Assert.Equal(1, right.VignetteKeyframes[0].Amount);
        Assert.Equal(47, clip.VignetteKeyframes[^1].FrameOffset);
        Assert.True(VideoTimelineEditing.TryTrimStart(track, right, 61, 24));
        Assert.Equal(0, right.VignetteKeyframes[0].FrameOffset);
        Assert.Equal(1 - (12d / 47d) * 0.5, right.VignetteKeyframes[0].Amount, 6);
        Assert.True(VideoTimelineEditing.TryTrimEnd(track, right, 85, 24));

        VideoTimelineEditing.RescaleFrameRate([track], 24, 48);
        Assert.All(right.VignetteKeyframes, key => Assert.InRange(key.FrameOffset, 0, 47));
        Assert.Null(VideoTimelineEditing.Validate([track], 48));
    }

    [Fact]
    public void VignetteKeyframes_InspectorEditsAnimatedValueAndRemovingLastKeyKeepsItsValue()
    {
        TimelineClipProject clip = new()
        {
            SourcePath = "scene.mp4",
            SourceDurationSeconds = 3,
            VignetteAmount = 0.1,
            VignetteKeyframes =
            [
                new() { FrameOffset = 0, Amount = 0.2 },
                new() { FrameOffset = 48, Amount = 0.8 },
            ],
        };

        Assert.True(VideoVignetteKeyframes.SetValueAtTime(
            clip, TimelineTrackKind.Video, 1, 24, 0.6));
        Assert.Equal(0.6, VideoVignetteKeyframes.GetValue(clip, 24));
        Assert.True(VideoVignetteKeyframes.TryRemoveAtFrame(clip, 24));
        Assert.True(VideoVignetteKeyframes.TryRemoveAtFrame(clip, 0));
        Assert.Single(clip.VignetteKeyframes);
        Assert.True(VideoVignetteKeyframes.TryRemoveAtFrame(clip, 48));
        Assert.Empty(clip.VignetteKeyframes);
        Assert.Equal(0.8, clip.VignetteAmount);
    }

    [Fact]
    public void VignetteKeyframes_OverwriteRetainsAnimatedValuesOnBothRemainders()
    {
        TimelineClipProject original = new()
        {
            Id = "original",
            SourcePath = "old.mp4",
            SourceDurationSeconds = 6,
            VignetteKeyframes =
            [
                new() { FrameOffset = 0, Amount = 0 },
                new() { FrameOffset = 143, Amount = 1 },
            ],
        };
        TimelineTrackProject track = new() { Kind = TimelineTrackKind.Video, Clips = [original] };
        TimelineClipProject replacement = new() { Id = "replacement", SourcePath = "new.mp4", SourceDurationSeconds = 2 };

        Assert.True(VideoTimelinePlacement.TryPlace(
            track, replacement, 49, 24, VideoClipPlacementMode.Overwrite));

        TimelineClipProject[] remainders = track.Clips.Where(clip => clip.Id != replacement.Id).ToArray();
        Assert.Equal(2, remainders.Length);
        Assert.Equal(new[] { 0, 47 }, remainders[0].VignetteKeyframes.Select(key => key.FrameOffset));
        Assert.Equal(new[] { 0, 47 }, remainders[1].VignetteKeyframes.Select(key => key.FrameOffset));
        Assert.Equal(47d / 143, remainders[0].VignetteKeyframes[^1].Amount, 6);
        Assert.Equal(96d / 143, remainders[1].VignetteKeyframes[0].Amount, 6);
        Assert.Null(VideoTimelineEditing.Validate([track], 24));
    }

    [Fact]
    public void VignetteKeyframes_RoundTripCloneAndMigrateVersionFortySix()
    {
        TimelineClipProject clip = new()
        {
            SourcePath = "scene.mp4",
            SourceDurationSeconds = 3,
            VignetteAmount = 0.3,
            VignetteKeyframes =
            [
                new() { FrameOffset = 0, Amount = 0.1 },
                new() { FrameOffset = 24, Amount = 0.9 },
            ],
        };
        TimelineTrackProject track = new() { Kind = TimelineTrackKind.Video, Clips = [clip] };
        EditorProject restored = EditorProjectSerialiser.Deserialise(
            EditorProjectSerialiser.Serialise(new EditorProject { VideoTracks = [track] }));
        TimelineClipProject saved = Assert.Single(Assert.Single(restored.VideoTracks).Clips);

        Assert.Equal(new[] { 0, 24 }, VideoTimelineEditing.Clone(clip).VignetteKeyframes.Select(key => key.FrameOffset));
        Assert.Equal(new[] { 0, 24 }, saved.VignetteKeyframes.Select(key => key.FrameOffset));
        Assert.Equal(0.5, VideoVignetteKeyframes.GetValue(saved, 12), 6);
        Assert.Null(VideoTimelineEditing.Validate(restored.VideoTracks, restored.FrameRate));

        EditorProject legacyProject = new()
        {
            VideoTracks =
            [
                new TimelineTrackProject
                {
                    Kind = TimelineTrackKind.Video,
                    Clips = [new TimelineClipProject { SourcePath = "scene.mp4", SourceDurationSeconds = 3, VignetteAmount = 0.4 }],
                },
            ],
        };
        JsonObject legacy = JsonNode.Parse(EditorProjectSerialiser.Serialise(legacyProject))!.AsObject();
        legacy["Version"] = 46;
        legacy["VideoTracks"]![0]!["Clips"]![0]!.AsObject().Remove("VignetteKeyframes");
        EditorProject migrated = EditorProjectSerialiser.Deserialise(legacy.ToJsonString());
        TimelineClipProject migratedClip = Assert.Single(Assert.Single(migrated.VideoTracks).Clips);
        Assert.Equal(EditorProject.CurrentVersion, migrated.Version);
        Assert.Equal(0.4, migratedClip.VignetteAmount);
        Assert.Empty(migratedClip.VignetteKeyframes);
    }

    [Fact]
    public void VignetteKeyframe_AnimatedValueRendersThroughSharedCompositor()
    {
        using var device = new CanvasDevice(true);
        byte[] pixels = new byte[64 * 64 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
            pixels[i + 1] = 255;
            pixels[i + 2] = 255;
            pixels[i + 3] = 255;
        }

        using var source = CanvasBitmap.CreateFromBytes(
            device, pixels, 64, 64, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        using var target = new CanvasRenderTarget(device, 64, 64, 96);
        using var vignette = new VideoVignetteCanvasEffect();
        var clip = new TimelineClipProject
        {
            SourcePath = "white.png",
            DurationSeconds = 2,
            VignetteAmount = 0,
        };

        using (CanvasDrawingSession session = target.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Black);
            TimelineCompositor.DrawFitted(
                session, source, source.Bounds, 64, 64, 1, 0.5, 0.5, 1,
                colourGrade: clip, vignetteEffect: vignette, animatedVignetteAmount: 1);
        }

        byte[] rendered = target.GetPixelBytes();
        int centre = (32 * 64 + 32) * 4;
        int edge = (0 * 64 + 0) * 4;
        Assert.True(rendered[centre] > rendered[edge]);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    public void Validation_RejectsUnsupportedVignetteAmount(double amount)
    {
        var clip = new TimelineClipProject
        {
            SourcePath = "scene.mp4",
            SourceDurationSeconds = 3,
            VignetteAmount = amount,
        };

        Assert.NotNull(VideoVignette.Validate(clip, TimelineTrackKind.Video));
    }

    [Fact]
    public void Validation_RejectsInvalidVignetteKeyframes()
    {
        TimelineClipProject clip = new()
        {
            SourcePath = "scene.mp4",
            SourceDurationSeconds = 3,
            VignetteKeyframes = [new() { FrameOffset = 0, Amount = 1.1 }],
        };
        TimelineTrackProject track = new() { Kind = TimelineTrackKind.Video, Clips = [clip] };
        Assert.NotNull(VideoTimelineEditing.Validate([track], 24));
    }

    [Fact]
    public void Render_DarkensEdgesRelativeToCentre()
    {
        using var device = new CanvasDevice(true);
        byte[] pixels = new byte[64 * 64 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
            pixels[i + 1] = 255;
            pixels[i + 2] = 255;
            pixels[i + 3] = 255;
        }

        using var source = CanvasBitmap.CreateFromBytes(
            device, pixels, 64, 64, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        using var target = new CanvasRenderTarget(device, 64, 64, 96);
        using var vignette = new VideoVignetteCanvasEffect();
        var clip = new TimelineClipProject
        {
            SourcePath = "white.png",
            VignetteAmount = 1,
        };

        using (CanvasDrawingSession session = target.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Black);
            TimelineCompositor.DrawFitted(
                session, source, source.Bounds, 64, 64, 1, 0.5, 0.5, 1,
                colourGrade: clip, vignetteEffect: vignette);
        }

        byte[] rendered = target.GetPixelBytes();
        int centre = (32 * 64 + 32) * 4;
        int edge = (0 * 64 + 0) * 4;
        Assert.True(rendered[centre] > rendered[edge]);
    }
}
