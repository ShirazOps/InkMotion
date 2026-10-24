using System.Text.Json.Nodes;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;
using Xunit;

namespace InkMotion.Tests;

public sealed class VideoChromaKeyTests
{
    [Fact]
    public void VersionTwentySixProject_MigratesWithChromaKeyDisabled()
    {
        EditorProject project = EditorProjectSerialiser.Deserialise(
            """{"Version":26,"VideoTracks":[{"Id":"video","Kind":"Video","Name":"Video 1","Clips":[{"Id":"clip","SourcePath":"scene.mp4","SourceDurationSeconds":3}]}],"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""");

        TimelineClipProject clip = Assert.Single(Assert.Single(project.VideoTracks).Clips);
        Assert.Equal(EditorProject.CurrentVersion, project.Version);
        Assert.False(clip.ChromaKeyEnabled);
        Assert.Equal(0xFF00FF00u, clip.ChromaKeyColour);
        Assert.Equal(0.1, clip.ChromaKeyTolerance);
        Assert.True(clip.ChromaKeyFeather);
    }

    [Fact]
    public void Settings_RoundTripAndCloneWithTheClip()
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
                            ChromaKeyEnabled = true,
                            ChromaKeyColour = 0xFF0000FF,
                            ChromaKeyTolerance = 0.35,
                            ChromaKeyFeather = false,
                        },
                    ],
                },
            ],
        };

        TimelineClipProject original = Assert.Single(Assert.Single(project.VideoTracks).Clips);
        TimelineClipProject copy = VideoTimelineEditing.Clone(original);
        TimelineClipProject restored = Assert.Single(Assert.Single(
            EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project)).VideoTracks).Clips);

        Assert.Equal((true, 0xFF0000FFu, 0.35, false),
            (copy.ChromaKeyEnabled, copy.ChromaKeyColour, copy.ChromaKeyTolerance, copy.ChromaKeyFeather));
        Assert.Equal((copy.ChromaKeyEnabled, copy.ChromaKeyColour, copy.ChromaKeyTolerance, copy.ChromaKeyFeather),
            (restored.ChromaKeyEnabled, restored.ChromaKeyColour, restored.ChromaKeyTolerance, restored.ChromaKeyFeather));
    }

    [Fact]
    public void ToleranceKeyframes_InterpolateAndPreserveTimingThroughEdits()
    {
        TimelineClipProject clip = new()
        {
            SourcePath = "scene.mp4",
            SourceDurationSeconds = 4,
            ChromaKeyKeyframes =
            [
                new() { FrameOffset = 0, Tolerance = 0 },
                new() { FrameOffset = 48, Tolerance = 0.8 },
                new() { FrameOffset = 95, Tolerance = 0.2 },
            ],
        };
        TimelineTrackProject track = new() { Kind = TimelineTrackKind.Video, Clips = [clip] };

        Assert.Equal(0.4, VideoChromaKeyKeyframes.GetValue(clip, 24), 6);
        Assert.True(VideoChromaKeyKeyframes.SetValueAtTime(clip, track.Kind, 1, 24, 0.6));
        Assert.Equal(0.6, VideoChromaKeyKeyframes.GetValue(clip, 24));
        TimelineClipProject right = Assert.IsType<TimelineClipProject>(
            VideoTimelineEditing.Split(track, clip, 49, 24));
        Assert.Equal(0.8, right.ChromaKeyKeyframes[0].Tolerance);
        Assert.Equal(47, clip.ChromaKeyKeyframes[^1].FrameOffset);
        Assert.True(VideoTimelineEditing.TryTrimStart(track, right, 61, 24));
        Assert.Equal(0, right.ChromaKeyKeyframes[0].FrameOffset);
        Assert.True(VideoTimelineEditing.TryTrimEnd(track, right, 85, 24));

        VideoTimelineEditing.RescaleFrameRate([track], 24, 48);
        Assert.All(right.ChromaKeyKeyframes, key => Assert.InRange(key.FrameOffset, 0, 47));
        Assert.Null(VideoTimelineEditing.Validate([track], 48));
    }

    [Fact]
    public void ToleranceKeyframes_StillDurationOverwriteAndLastKeyRemovalPreserveValues()
    {
        TimelineClipProject image = new()
        {
            SourcePath = "image.png",
            DurationSeconds = 4,
            ChromaKeyKeyframes =
            [
                new() { FrameOffset = 0, Tolerance = 0 },
                new() { FrameOffset = 72, Tolerance = 0.9 },
            ],
        };
        TimelineTrackProject imageTrack = new() { Kind = TimelineTrackKind.Image, Clips = [image] };
        Assert.True(VideoTimelineEditing.TrySetStillDuration(imageTrack, image, 2, 24));
        Assert.Equal(47, image.ChromaKeyKeyframes[^1].FrameOffset);
        Assert.Equal(0.9 * 47 / 72, image.ChromaKeyKeyframes[^1].Tolerance, 6);
        Assert.Null(VideoTimelineEditing.Validate([imageTrack], 24));

        TimelineClipProject original = new()
        {
            Id = "original",
            SourcePath = "old.mp4",
            SourceDurationSeconds = 6,
            ChromaKeyKeyframes =
            [
                new() { FrameOffset = 0, Tolerance = 0 },
                new() { FrameOffset = 143, Tolerance = 0.8 },
            ],
        };
        TimelineTrackProject videoTrack = new() { Kind = TimelineTrackKind.Video, Clips = [original] };
        TimelineClipProject replacement = new() { Id = "replacement", SourcePath = "new.mp4", SourceDurationSeconds = 2 };
        Assert.True(VideoTimelinePlacement.TryPlace(
            videoTrack, replacement, 49, 24, VideoClipPlacementMode.Overwrite));

        TimelineClipProject[] remainders = videoTrack.Clips.Where(clip => clip.Id != replacement.Id).ToArray();
        Assert.Equal(2, remainders.Length);
        Assert.Equal(new[] { 0, 47 }, remainders[0].ChromaKeyKeyframes.Select(key => key.FrameOffset));
        Assert.Equal(new[] { 0, 47 }, remainders[1].ChromaKeyKeyframes.Select(key => key.FrameOffset));
        Assert.Equal(0.8 * 47 / 143, remainders[0].ChromaKeyKeyframes[^1].Tolerance, 6);
        Assert.Equal(0.8 * 96 / 143, remainders[1].ChromaKeyKeyframes[0].Tolerance, 6);
        Assert.Null(VideoTimelineEditing.Validate([videoTrack], 24));

        TimelineClipProject single = new()
        {
            SourcePath = "image.png",
            DurationSeconds = 1,
            ChromaKeyTolerance = 0.1,
            ChromaKeyKeyframes = [new() { FrameOffset = 0, Tolerance = 0.75 }],
        };
        Assert.True(VideoChromaKeyKeyframes.TryRemoveAtFrame(single, 0));
        Assert.Empty(single.ChromaKeyKeyframes);
        Assert.Equal(0.75, single.ChromaKeyTolerance);
    }

    [Fact]
    public void ToleranceKeyframes_RoundTripCloneAndMigrateVersionFortyNine()
    {
        TimelineClipProject clip = new()
        {
            SourcePath = "scene.mp4",
            SourceDurationSeconds = 3,
            ChromaKeyTolerance = 0.1,
            ChromaKeyKeyframes =
            [
                new() { FrameOffset = 0, Tolerance = 0.2 },
                new() { FrameOffset = 24, Tolerance = 0.8 },
            ],
        };
        TimelineTrackProject track = new() { Kind = TimelineTrackKind.Video, Clips = [clip] };
        EditorProject restored = EditorProjectSerialiser.Deserialise(
            EditorProjectSerialiser.Serialise(new EditorProject { VideoTracks = [track] }));
        TimelineClipProject saved = Assert.Single(Assert.Single(restored.VideoTracks).Clips);

        Assert.Equal(new[] { 0, 24 }, VideoTimelineEditing.Clone(clip).ChromaKeyKeyframes.Select(key => key.FrameOffset));
        Assert.Equal(new[] { 0, 24 }, saved.ChromaKeyKeyframes.Select(key => key.FrameOffset));
        Assert.Equal(0.5, VideoChromaKeyKeyframes.GetValue(saved, 12), 6);
        Assert.Null(VideoTimelineEditing.Validate(restored.VideoTracks, restored.FrameRate));

        EditorProject legacyProject = new()
        {
            VideoTracks =
            [
                new TimelineTrackProject
                {
                    Kind = TimelineTrackKind.Video,
                    Clips = [new TimelineClipProject { SourcePath = "scene.mp4", SourceDurationSeconds = 3, ChromaKeyTolerance = 0.35 }],
                },
            ],
        };
        JsonObject legacy = JsonNode.Parse(EditorProjectSerialiser.Serialise(legacyProject))!.AsObject();
        legacy["Version"] = 49;
        legacy["VideoTracks"]![0]!["Clips"]![0]!.AsObject().Remove("ChromaKeyKeyframes");
        EditorProject migrated = EditorProjectSerialiser.Deserialise(legacy.ToJsonString());
        TimelineClipProject migratedClip = Assert.Single(Assert.Single(migrated.VideoTracks).Clips);
        Assert.Equal(EditorProject.CurrentVersion, migrated.Version);
        Assert.Equal(0.35, migratedClip.ChromaKeyTolerance);
        Assert.Empty(migratedClip.ChromaKeyKeyframes);
    }

    [Fact]
    public void FeatherKeyframes_HoldUntilNextKeyAndFollowTimelineEdits()
    {
        TimelineClipProject clip = new()
        {
            SourcePath = "scene.mp4",
            SourceDurationSeconds = 6,
            ChromaKeyFeather = true,
            ChromaKeyFeatherKeyframes =
            [
                new() { FrameOffset = 0, Feather = false },
                new() { FrameOffset = 72, Feather = true },
                new() { FrameOffset = 96, Feather = false },
            ],
        };
        TimelineTrackProject track = new() { Kind = TimelineTrackKind.Video, Clips = [clip] };

        Assert.False(VideoChromaKeyFeatherKeyframes.GetValue(clip, 71));
        Assert.True(VideoChromaKeyFeatherKeyframes.GetValue(clip, 72));
        Assert.True(VideoChromaKeyFeatherKeyframes.GetValue(clip, 95));
        Assert.False(VideoChromaKeyFeatherKeyframes.GetValue(clip, 96));
        Assert.True(VideoChromaKeyFeatherKeyframes.SetValueAtTime(clip, track.Kind, 3.5, 24, true));
        Assert.Equal(84, clip.ChromaKeyFeatherKeyframes[2].FrameOffset);
        Assert.True(VideoChromaKeyFeatherKeyframes.GetValue(clip, 84));

        TimelineClipProject restored = Assert.Single(Assert.Single(
            EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(
                new EditorProject { VideoTracks = [track] })).VideoTracks).Clips);
        Assert.Equal(clip.ChromaKeyFeatherKeyframes.Select(key => (key.FrameOffset, key.Feather)),
            restored.ChromaKeyFeatherKeyframes.Select(key => (key.FrameOffset, key.Feather)));
        Assert.Equal(clip.ChromaKeyFeatherKeyframes.Select(key => (key.FrameOffset, key.Feather)),
            VideoTimelineEditing.Clone(clip).ChromaKeyFeatherKeyframes.Select(key => (key.FrameOffset, key.Feather)));

        TimelineClipProject right = Assert.IsType<TimelineClipProject>(
            VideoTimelineEditing.Split(track, clip, 49, 24));
        Assert.Equal(0, right.ChromaKeyFeatherKeyframes[0].FrameOffset);
        Assert.False(right.ChromaKeyFeatherKeyframes[0].Feather);
        Assert.True(VideoTimelineEditing.TryTrimStart(track, right, 61, 24));
        Assert.Equal(0, right.ChromaKeyFeatherKeyframes[0].FrameOffset);
        Assert.False(right.ChromaKeyFeatherKeyframes[0].Feather);
        VideoTimelineEditing.RescaleFrameRate([track], 24, 48);
        Assert.Null(VideoTimelineEditing.Validate([track], 48));

        TimelineClipProject overwriteSource = new()
        {
            Id = "overwrite-source",
            SourcePath = "old.mp4",
            SourceDurationSeconds = 6,
            ChromaKeyFeatherKeyframes =
            [
                new() { FrameOffset = 0, Feather = false },
                new() { FrameOffset = 143, Feather = true },
            ],
        };
        TimelineTrackProject overwriteTrack = new() { Kind = TimelineTrackKind.Video, Clips = [overwriteSource] };
        TimelineClipProject replacement = new() { Id = "replacement", SourcePath = "new.mp4", SourceDurationSeconds = 2 };
        Assert.True(VideoTimelinePlacement.TryPlace(
            overwriteTrack, replacement, 49, 24, VideoClipPlacementMode.Overwrite));
        TimelineClipProject[] remainders = overwriteTrack.Clips
            .Where(item => item.Id != replacement.Id).ToArray();
        Assert.Equal(new[] { 0, 47 }, remainders[0].ChromaKeyFeatherKeyframes.Select(key => key.FrameOffset));
        Assert.Equal(new[] { 0, 47 }, remainders[1].ChromaKeyFeatherKeyframes.Select(key => key.FrameOffset));
        Assert.False(remainders[1].ChromaKeyFeatherKeyframes[0].Feather);
        Assert.True(remainders[1].ChromaKeyFeatherKeyframes[1].Feather);
        Assert.Null(VideoTimelineEditing.Validate([overwriteTrack], 24));

        TimelineClipProject image = new()
        {
            SourcePath = "image.png",
            DurationSeconds = 4,
            ChromaKeyFeatherKeyframes =
            [
                new() { FrameOffset = 0, Feather = false },
                new() { FrameOffset = 72, Feather = true },
            ],
        };
        TimelineTrackProject imageTrack = new() { Kind = TimelineTrackKind.Image, Clips = [image] };
        Assert.True(VideoTimelineEditing.TrySetStillDuration(imageTrack, image, 2, 24));
        Assert.Equal(47, image.ChromaKeyFeatherKeyframes[^1].FrameOffset);
        Assert.False(image.ChromaKeyFeatherKeyframes[^1].Feather);
        Assert.Null(VideoTimelineEditing.Validate([imageTrack], 24));

        TimelineClipProject migrated = Assert.Single(Assert.Single(EditorProjectSerialiser.Deserialise(
            """{"Version":52,"VideoTracks":[{"Kind":"Video","Clips":[{"SourcePath":"scene.mp4","SourceDurationSeconds":2,"ChromaKeyFeather":false}]}],"Frames":[{"Number":1}],"SelectedFrameIndex":0}""").VideoTracks).Clips);
        Assert.False(migrated.ChromaKeyFeather);
        Assert.Empty(migrated.ChromaKeyFeatherKeyframes);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    public void Validation_RejectsUnsupportedTolerance(double tolerance)
    {
        var clip = new TimelineClipProject
        {
            SourcePath = "scene.mp4",
            SourceDurationSeconds = 3,
            ChromaKeyEnabled = true,
            ChromaKeyTolerance = tolerance,
        };

        Assert.NotNull(VideoChromaKey.Validate(clip, TimelineTrackKind.Video));
    }

    [Fact]
    public void Validation_RejectsKeyingTitlesAndUnsupportedColours()
    {
        var title = new TimelineClipProject { Text = "Title", ChromaKeyEnabled = true };
        var clip = new TimelineClipProject
        {
            SourcePath = "scene.mp4",
            SourceDurationSeconds = 3,
            ChromaKeyColour = 0xFFFFFFFF,
        };

        Assert.NotNull(VideoChromaKey.Validate(title, TimelineTrackKind.Text));
        Assert.NotNull(VideoChromaKey.Validate(clip, TimelineTrackKind.Video));
    }

    [Fact]
    public void Render_RemovesKeyColourAndCompositesForegroundOverBackground()
    {
        using var device = new CanvasDevice(true);
        byte[] foregroundPixels = new byte[16 * 16 * 4];
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                int offset = (y * 16 + x) * 4;
                if (x < 8)
                {
                    foregroundPixels[offset + 1] = 255;
                }

                else
                {
                    foregroundPixels[offset + 2] = 255;
                }

                foregroundPixels[offset + 3] = 255;
            }
        }

        using var foreground = CanvasBitmap.CreateFromBytes(
            device, foregroundPixels, 16, 16, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        using var target = new CanvasRenderTarget(device, 16, 16, 96);
        using var chromaKey = new VideoChromaKeyEffect();
        var clip = new TimelineClipProject
        {
            SourcePath = "green-screen.png",
            ChromaKeyEnabled = true,
            ChromaKeyTolerance = 0.1,
        };

        using (CanvasDrawingSession session = target.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Blue);
            TimelineCompositor.DrawFitted(
                session, foreground, foreground.Bounds, 16, 16, 1, 0.5, 0.5, 1,
                colourGrade: clip, chromaKeyEffect: chromaKey);
        }

        byte[] rendered = target.GetPixelBytes();
        int keyed = (8 * 16 + 4) * 4;
        int opaque = (8 * 16 + 12) * 4;
        Assert.InRange(rendered[keyed], 245, 255);
        Assert.InRange(rendered[keyed + 1], 0, 10);
        Assert.InRange(rendered[keyed + 2], 0, 10);
        Assert.InRange(rendered[opaque + 2], 245, 255);
        Assert.InRange(rendered[opaque], 0, 10);
    }

    [Fact]
    public void Render_AnimatedFeatherChangesKeyEdgeWithoutChangingKeyedPixels()
    {
        using var device = new CanvasDevice(true);
        byte[] pixels = new byte[16 * 16 * 4];
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                int offset = (y * 16 + x) * 4;
                pixels[offset + 1] = 255;
                pixels[offset + 2] = (byte)(x * 10);
                pixels[offset + 3] = 255;
            }
        }

        using var source = CanvasBitmap.CreateFromBytes(
            device, pixels, 16, 16, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        using var target = new CanvasRenderTarget(device, 16, 16, 96);
        using var effect = new VideoChromaKeyEffect();
        TimelineClipProject clip = new()
        {
            SourcePath = "scene.png",
            DurationSeconds = 2,
            ChromaKeyEnabled = true,
            ChromaKeyColour = 0xFF00FF00,
            ChromaKeyTolerance = 0.5,
            ChromaKeySpillSuppression = 0,
            ChromaKeyFeatherKeyframes =
            [
                new() { FrameOffset = 0, Feather = false },
                new() { FrameOffset = 23, Feather = true },
            ],
        };

        void Draw(int frameOffset)
        {
            using CanvasDrawingSession session = target.CreateDrawingSession();
            session.Clear(Microsoft.UI.Colors.Transparent);
            TimelineCompositor.DrawFitted(session, source, source.Bounds, 16, 16, 1, 0.5, 0.5, 1,
                colourGrade: clip, chromaKeyEffect: effect,
                animatedChromaKeyFeather: VideoChromaKeyFeatherKeyframes.GetValue(clip, frameOffset));
        }

        Draw(0);
        byte[] hardEdge = target.GetPixelBytes();
        Draw(23);
        byte[] softEdge = target.GetPixelBytes();
        Assert.False(hardEdge.SequenceEqual(softEdge));
        Assert.Equal(hardEdge.Take(4), softEdge.Take(4));
    }

    [Fact]
    public void Render_AnimatedToleranceChangesTheSharedCompositorMatte()
    {
        using var device = new CanvasDevice(true);
        byte[] pixels = new byte[16 * 16 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i + 1] = 220;
            pixels[i + 2] = 25;
            pixels[i + 3] = 255;
        }

        using var source = CanvasBitmap.CreateFromBytes(
            device, pixels, 16, 16, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        using var lowTarget = new CanvasRenderTarget(device, 16, 16, 96);
        using var highTarget = new CanvasRenderTarget(device, 16, 16, 96);
        using var lowEffect = new VideoChromaKeyEffect();
        using var highEffect = new VideoChromaKeyEffect();
        var clip = new TimelineClipProject
        {
            SourcePath = "near-green.png",
            ChromaKeyEnabled = true,
            ChromaKeyTolerance = 0,
        };

        using (CanvasDrawingSession session = lowTarget.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Blue);
            TimelineCompositor.DrawFitted(
                session, source, source.Bounds, 16, 16, 1, 0.5, 0.5, 1,
                colourGrade: clip, chromaKeyEffect: lowEffect, animatedChromaKeyTolerance: 0);
        }
        using (CanvasDrawingSession session = highTarget.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Blue);
            TimelineCompositor.DrawFitted(
                session, source, source.Bounds, 16, 16, 1, 0.5, 0.5, 1,
                colourGrade: clip, chromaKeyEffect: highEffect, animatedChromaKeyTolerance: 0.8);
        }

        Assert.False(lowTarget.GetPixelBytes().SequenceEqual(highTarget.GetPixelBytes()));
    }

    [Fact]
    public void Validation_RejectsInvalidToleranceKeyframes()
    {
        TimelineClipProject clip = new()
        {
            SourcePath = "scene.mp4",
            SourceDurationSeconds = 3,
            ChromaKeyKeyframes = [new() { FrameOffset = 0, Tolerance = 1.1 }],
        };
        TimelineTrackProject track = new() { Kind = TimelineTrackKind.Video, Clips = [clip] };
        Assert.NotNull(VideoTimelineEditing.Validate([track], 24));
    }
}
