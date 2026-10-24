using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;
using Xunit;

namespace InkMotion.Tests;

public sealed class VideoClipMaskTests
{
    [Fact]
    public void VersionFiftyProject_MigratesWithoutChangingStaticMaskFeather()
    {
        EditorProject project = EditorProjectSerialiser.Deserialise(
            """{"Version":50,"VideoTracks":[{"Id":"video","Kind":"Video","Name":"Video 1","Clips":[{"Id":"clip","SourcePath":"scene.mp4","SourceDurationSeconds":3,"MaskShape":"Ellipse","MaskFeather":0.12}]}],"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""");

        TimelineClipProject clip = Assert.Single(Assert.Single(project.VideoTracks).Clips);
        Assert.Equal(EditorProject.CurrentVersion, project.Version);
        Assert.Equal(0.12, clip.MaskFeather);
        Assert.Empty(clip.MaskFeatherKeyframes);
    }

    [Fact]
    public void VersionTwentyNineProject_MigratesWithMaskDisabled()
    {
        EditorProject project = EditorProjectSerialiser.Deserialise(
            """{"Version":29,"VideoTracks":[{"Id":"video","Kind":"Video","Name":"Video 1","Clips":[{"Id":"clip","SourcePath":"scene.mp4","SourceDurationSeconds":3,"VignetteAmount":0.4}]}],"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""");

        TimelineClipProject clip = Assert.Single(Assert.Single(project.VideoTracks).Clips);
        Assert.Equal(EditorProject.CurrentVersion, project.Version);
        Assert.Equal(0.4, clip.VignetteAmount);
        Assert.Equal(VideoMaskShape.None, clip.MaskShape);
        Assert.Empty(clip.MaskKeyframes);
    }

    [Fact]
    public void MaskSettingsAndTrackingKeys_RoundTripAndClone()
    {
        var clip = new TimelineClipProject
        {
            SourcePath = "scene.mp4",
            SourceDurationSeconds = 3,
            MaskShape = VideoMaskShape.Ellipse,
            MaskX = 0.4,
            MaskY = 0.6,
            MaskWidth = 0.7,
            MaskHeight = 0.5,
            MaskInverted = true,
            MaskFeather = 0.08,
            MaskKeyframes = [new VideoMaskKeyframe { FrameOffset = 8, X = 0.7, Y = 0.4, Width = 0.4, Height = 0.3 }],
        };
        EditorProject project = new()
        {
            VideoTracks = [new TimelineTrackProject { Kind = TimelineTrackKind.Video, Clips = [clip] }],
        };

        TimelineClipProject clone = VideoTimelineEditing.Clone(clip);
        TimelineClipProject restored = Assert.Single(Assert.Single(
            EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project)).VideoTracks).Clips);

        Assert.Equal(VideoMaskShape.Ellipse, clone.MaskShape);
        Assert.Equal(0.4, clone.MaskX);
        Assert.True(clone.MaskInverted);
        Assert.Equal(0.08, clone.MaskFeather);
        Assert.Equal(0.4, Assert.Single(clone.MaskKeyframes).Width);
        Assert.Equal(clip.MaskKeyframes, restored.MaskKeyframes);
    }

    [Fact]
    public void GetValue_InterpolatesTrackedMaskGeometry()
    {
        var clip = new TimelineClipProject
        {
            SourcePath = "scene.mp4",
            SourceDurationSeconds = 3,
            MaskShape = VideoMaskShape.Rectangle,
            MaskKeyframes =
            [
                new() { FrameOffset = 0, X = 0.2, Y = 0.3, Width = 0.4, Height = 0.5 },
                new() { FrameOffset = 10, X = 0.8, Y = 0.7, Width = 0.6, Height = 0.3 },
            ],
        };

        Assert.Equal(new VideoMaskValue(0.5, 0.5, 0.5, 0.4), VideoClipMask.GetValue(clip, 5));
    }

    [Fact]
    public void ImageMaskKeyframes_InterpolatePersistAndFollowSplitAndTrim()
    {
        var clip = new TimelineClipProject
        {
            SourcePath = "still.png",
            DurationSeconds = 4,
            StartFrame = 121,
            MaskShape = VideoMaskShape.Rectangle,
            MaskKeyframes =
            [
                new() { FrameOffset = 0, X = 0.2, Y = 0.3, Width = 0.4, Height = 0.5 },
                new() { FrameOffset = 48, X = 0.8, Y = 0.7, Width = 0.6, Height = 0.3 },
            ],
        };
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Image, Clips = [clip] };

        Assert.Null(VideoTimelineEditing.Validate([track], 24));
        Assert.True(VideoClipMask.SetKeyframe(
            clip, track.Kind, 24, 24, new VideoMaskValue(0.5, 0.5, 0.5, 0.4)));
        Assert.Equal(new VideoMaskValue(0.5, 0.5, 0.5, 0.4), VideoClipMask.GetValue(clip, 24));
        Assert.True(VideoClipMask.CanSetKeyframe(clip, track.Kind, 24, 24));
        Assert.False(VideoClipMask.CanSetKeyframe(clip, track.Kind, 96, 24));

        EditorProject restored = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(
            new EditorProject { VideoTracks = [track] }));
        TimelineTrackProject restoredTrack = Assert.Single(restored.VideoTracks);
        TimelineClipProject restoredClip = Assert.Single(restoredTrack.Clips);
        Assert.Equal(3, restoredClip.MaskKeyframes.Count);
        Assert.Equal(new VideoMaskValue(0.5, 0.5, 0.5, 0.4), VideoClipMask.GetValue(restoredClip, 24));

        TimelineClipProject right = Assert.IsType<TimelineClipProject>(
            VideoTimelineEditing.Split(restoredTrack, restoredClip, 145, 24));
        Assert.Equal(0, right.MaskKeyframes[0].FrameOffset);
        Assert.Equal(0.5, right.MaskKeyframes[0].X);
        Assert.True(VideoTimelineEditing.TryTrimStart(restoredTrack, right, 157, 24));
        Assert.Equal(0, right.MaskKeyframes[0].FrameOffset);
        Assert.Equal(0.65, right.MaskKeyframes[0].X, 6);
        Assert.Null(VideoTimelineEditing.Validate([restoredTrack], 24));
    }

    [Fact]
    public void RemoveOnlyMaskKeyframe_BakesItsEvaluatedValueIntoStaticMask()
    {
        var clip = new TimelineClipProject
        {
            SourcePath = "still.png",
            DurationSeconds = 2,
            MaskShape = VideoMaskShape.Ellipse,
            MaskX = 0.25,
            MaskKeyframes = [new() { FrameOffset = 12, X = 0.75, Y = 0.4, Width = 0.5, Height = 0.6 }],
        };

        Assert.True(VideoClipMask.TryRemoveKeyframe(clip, 12));
        Assert.Empty(clip.MaskKeyframes);
        Assert.Equal(0.75, clip.MaskX);
        Assert.Equal(0.4, clip.MaskY);
        Assert.Equal(new VideoMaskValue(0.75, 0.4, 0.5, 0.6), VideoClipMask.GetValue(clip, 0));
    }

    [Fact]
    public void ImageMaskKeyframes_FollowFrameRateAndStillDurationChanges()
    {
        var clip = new TimelineClipProject
        {
            SourcePath = "still.png",
            DurationSeconds = 2,
            MaskShape = VideoMaskShape.Ellipse,
            MaskKeyframes =
            [
                new() { FrameOffset = 12, X = 0.25 },
                new() { FrameOffset = 24, X = 0.75 },
            ],
        };
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Image, Clips = [clip] };

        VideoTimelineEditing.RescaleFrameRate([track], 24, 48);
        Assert.Equal(new[] { 24, 48 }, clip.MaskKeyframes.Select(key => key.FrameOffset));
        Assert.Null(VideoTimelineEditing.Validate([track], 48));

        Assert.True(VideoTimelineEditing.TrySetStillDuration(track, clip, 0.75, 48));
        Assert.All(clip.MaskKeyframes, key => Assert.InRange(key.FrameOffset, 0, 35));
        Assert.Null(VideoTimelineEditing.Validate([track], 48));
    }

    [Fact]
    public void MaskFeatherKeyframes_InterpolatePersistAndFollowSplitTrimAndOverwrite()
    {
        TimelineClipProject clip = new()
        {
            SourcePath = "scene.mp4",
            SourceDurationSeconds = 6,
            MaskShape = VideoMaskShape.Rectangle,
            MaskFeather = 0.03,
            MaskFeatherKeyframes =
            [
                new() { FrameOffset = 0, Feather = 0 },
                new() { FrameOffset = 143, Feather = 0.24 },
            ],
        };
        TimelineTrackProject track = new() { Kind = TimelineTrackKind.Video, Clips = [clip] };

        Assert.Equal(0.12, VideoMaskFeatherKeyframes.GetValue(clip, 71.5), 6);
        Assert.True(VideoMaskFeatherKeyframes.SetValueAtFrame(clip, track.Kind, 72, 24, 0.18));
        Assert.Equal(0.18, VideoMaskFeatherKeyframes.GetValue(clip, 72));
        Assert.Null(VideoTimelineEditing.Validate([track], 24));

        TimelineClipProject restored = Assert.Single(Assert.Single(
            EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(
                new EditorProject { VideoTracks = [track] })).VideoTracks).Clips);
        Assert.Equal(clip.MaskFeatherKeyframes, restored.MaskFeatherKeyframes);

        TimelineClipProject right = Assert.IsType<TimelineClipProject>(
            VideoTimelineEditing.Split(track, clip, 49, 24));
        Assert.Equal(0, right.MaskFeatherKeyframes[0].FrameOffset);
        Assert.Equal(0.12, right.MaskFeatherKeyframes[0].Feather, 6);
        Assert.True(VideoTimelineEditing.TryTrimStart(track, right, 61, 24));
        Assert.Equal(0, right.MaskFeatherKeyframes[0].FrameOffset);
        Assert.Equal(0.15, right.MaskFeatherKeyframes[0].Feather, 6);
        VideoTimelineEditing.RescaleFrameRate([track], 24, 48);
        Assert.Null(VideoTimelineEditing.Validate([track], 48));

        TimelineClipProject still = new()
        {
            SourcePath = "still.png",
            DurationSeconds = 4,
            MaskShape = VideoMaskShape.Ellipse,
            MaskFeatherKeyframes =
            [
                new() { FrameOffset = 0, Feather = 0 },
                new() { FrameOffset = 72, Feather = 0.24 },
            ],
        };
        TimelineTrackProject imageTrack = new() { Kind = TimelineTrackKind.Image, Clips = [still] };
        Assert.True(VideoTimelineEditing.TrySetStillDuration(imageTrack, still, 2, 24));
        Assert.Equal(47, still.MaskFeatherKeyframes[^1].FrameOffset);
        Assert.Equal(0.24 * 47 / 72, still.MaskFeatherKeyframes[^1].Feather, 6);
        Assert.Null(VideoTimelineEditing.Validate([imageTrack], 24));

        TimelineClipProject overwriteSource = new()
        {
            Id = "overwrite-source",
            SourcePath = "old.mp4",
            SourceDurationSeconds = 6,
            MaskShape = VideoMaskShape.Ellipse,
            MaskFeatherKeyframes =
            [
                new() { FrameOffset = 0, Feather = 0 },
                new() { FrameOffset = 143, Feather = 0.24 },
            ],
        };
        TimelineTrackProject overwriteTrack = new() { Kind = TimelineTrackKind.Video, Clips = [overwriteSource] };
        TimelineClipProject replacement = new()
        {
            Id = "replacement",
            SourcePath = "new.mp4",
            SourceDurationSeconds = 2,
        };

        Assert.True(VideoTimelinePlacement.TryPlace(
            overwriteTrack, replacement, 49, 24, VideoClipPlacementMode.Overwrite));
        TimelineClipProject[] remainders = overwriteTrack.Clips
            .Where(item => item.Id != replacement.Id).ToArray();
        Assert.Equal(2, remainders.Length);
        Assert.Equal(new[] { 0, 47 }, remainders[0].MaskFeatherKeyframes.Select(key => key.FrameOffset));
        Assert.Equal(new[] { 0, 47 }, remainders[1].MaskFeatherKeyframes.Select(key => key.FrameOffset));
        Assert.Equal(0.24 * 47 / 143, remainders[0].MaskFeatherKeyframes[^1].Feather, 6);
        Assert.Equal(0.24 * 96 / 143, remainders[1].MaskFeatherKeyframes[0].Feather, 6);
        Assert.Null(VideoTimelineEditing.Validate([overwriteTrack], 24));
    }

    [Fact]
    public void MaskInversionKeyframes_HoldPersistAndFollowTimelineEdits()
    {
        TimelineClipProject clip = new()
        {
            SourcePath = "scene.mp4",
            SourceDurationSeconds = 6,
            MaskShape = VideoMaskShape.Rectangle,
            MaskInversionKeyframes =
            [
                new() { FrameOffset = 0, Inverted = false },
                new() { FrameOffset = 72, Inverted = true },
                new() { FrameOffset = 96, Inverted = false },
            ],
        };
        TimelineTrackProject track = new() { Kind = TimelineTrackKind.Video, Clips = [clip] };

        Assert.False(VideoMaskInversionKeyframes.GetValue(clip, 71));
        Assert.True(VideoMaskInversionKeyframes.GetValue(clip, 72));
        Assert.True(VideoMaskInversionKeyframes.GetValue(clip, 95));
        Assert.False(VideoMaskInversionKeyframes.GetValue(clip, 96));
        Assert.True(VideoMaskInversionKeyframes.SetValueAtTime(
            clip, track.Kind, 3.5, 24, false));
        Assert.False(VideoMaskInversionKeyframes.GetValue(clip, 84));
        Assert.Null(VideoTimelineEditing.Validate([track], 24));

        TimelineClipProject right = Assert.IsType<TimelineClipProject>(
            VideoTimelineEditing.Split(track, clip, 49, 24));
        Assert.False(right.MaskInversionKeyframes[0].Inverted);
        Assert.True(VideoTimelineEditing.TryTrimStart(track, right, 61, 24));
        Assert.Equal(0, right.MaskInversionKeyframes[0].FrameOffset);
        VideoTimelineEditing.RescaleFrameRate([track], 24, 60);
        Assert.Null(VideoTimelineEditing.Validate([track], 60));

        TimelineClipProject restored = Assert.Single(Assert.Single(EditorProjectSerialiser.Deserialise(
            EditorProjectSerialiser.Serialise(new EditorProject { FrameRate = 60, VideoTracks = [track] }))
            .VideoTracks).Clips, item => item.Id == clip.Id);
        Assert.Equal(clip.MaskInversionKeyframes.Select(key => (key.FrameOffset, key.Inverted)),
            VideoTimelineEditing.Clone(clip).MaskInversionKeyframes.Select(key => (key.FrameOffset, key.Inverted)));
        Assert.Equal(clip.MaskInversionKeyframes.Select(key => (key.FrameOffset, key.Inverted)),
            restored.MaskInversionKeyframes.Select(key => (key.FrameOffset, key.Inverted)));

        TimelineClipProject still = new()
        {
            SourcePath = "still.png",
            DurationSeconds = 4,
            MaskShape = VideoMaskShape.Ellipse,
            MaskInversionKeyframes =
            [
                new() { FrameOffset = 0, Inverted = false },
                new() { FrameOffset = 72, Inverted = true },
            ],
        };
        TimelineTrackProject imageTrack = new() { Kind = TimelineTrackKind.Image, Clips = [still] };
        Assert.True(VideoTimelineEditing.TrySetStillDuration(imageTrack, still, 2, 24));
        Assert.Equal(47, still.MaskInversionKeyframes[^1].FrameOffset);
        Assert.False(still.MaskInversionKeyframes[^1].Inverted);
        Assert.Null(VideoTimelineEditing.Validate([imageTrack], 24));

        TimelineClipProject migrated = Assert.Single(Assert.Single(EditorProjectSerialiser.Deserialise(
            """{"Version":55,"VideoTracks":[{"Kind":"Video","Clips":[{"SourcePath":"scene.mp4","SourceDurationSeconds":2,"MaskInverted":true}]}],"Frames":[{"Number":1}],"SelectedFrameIndex":0}""").VideoTracks).Clips);
        Assert.True(migrated.MaskInverted);
        Assert.Empty(migrated.MaskInversionKeyframes);
    }

    [Fact]
    public void MaskInversionKeyframes_PreserveOverwriteAndRejectJoins()
    {
        TimelineClipProject source = new()
        {
            Id = "source",
            SourcePath = "old.mp4",
            SourceDurationSeconds = 6,
            MaskShape = VideoMaskShape.Ellipse,
            MaskInversionKeyframes =
            [
                new() { FrameOffset = 0, Inverted = false },
                new() { FrameOffset = 143, Inverted = true },
            ],
        };
        TimelineTrackProject track = new() { Kind = TimelineTrackKind.Video, Clips = [source] };
        TimelineClipProject replacement = new() { Id = "replacement", SourcePath = "new.mp4", SourceDurationSeconds = 2 };
        Assert.True(VideoTimelinePlacement.TryPlace(
            track, replacement, 49, 24, VideoClipPlacementMode.Overwrite));
        TimelineClipProject[] remainders = track.Clips.Where(clip => clip.Id != replacement.Id).ToArray();
        Assert.Equal(new[] { 0, 47 }, remainders[0].MaskInversionKeyframes.Select(key => key.FrameOffset));
        Assert.False(remainders[0].MaskInversionKeyframes[^1].Inverted);
        Assert.Equal(new[] { 0, 47 }, remainders[1].MaskInversionKeyframes.Select(key => key.FrameOffset));
        Assert.True(remainders[1].MaskInversionKeyframes[^1].Inverted);
        Assert.Null(VideoTimelineEditing.Validate([track], 24));

        TimelineClipProject left = new()
        {
            SourcePath = @"C:\Media\source.mp4",
            SourceOutSeconds = 2,
            SourceDurationSeconds = 4,
        };
        TimelineTrackProject joinTrack = new() { Kind = TimelineTrackKind.Video, Clips = [left] };
        TimelineClipProject right = Assert.IsType<TimelineClipProject>(
            VideoTimelineEditing.Split(joinTrack, left, 25, 24));
        right.MaskInversionKeyframes = [new() { FrameOffset = 0, Inverted = true }];
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => VideoClipJoining.Join(joinTrack, left, right, 24));
        Assert.Contains("inversion", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Render_AnimatedMaskInversionReversesTheVisibleArea()
    {
        using var device = new CanvasDevice(true);
        byte[] pixels = new byte[64 * 64 * 4];
        for (int index = 0; index < pixels.Length; index += 4)
        {
            pixels[index + 2] = 255;
            pixels[index + 3] = 255;
        }

        using var source = CanvasBitmap.CreateFromBytes(
            device, pixels, 64, 64, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        using var target = new CanvasRenderTarget(device, 64, 64, 96);
        TimelineClipProject clip = new()
        {
            SourcePath = "red.png",
            DurationSeconds = 2,
            MaskShape = VideoMaskShape.Rectangle,
            MaskWidth = 0.5,
            MaskHeight = 0.5,
            MaskInversionKeyframes =
            [
                new() { FrameOffset = 0, Inverted = false },
                new() { FrameOffset = 23, Inverted = true },
            ],
        };

        void Draw(int frameOffset)
        {
            using CanvasDrawingSession session = target.CreateDrawingSession();
            session.Clear(Microsoft.UI.Colors.Blue);
            VideoClipMask.DrawMaskedImage(session, source,
                new Windows.Foundation.Rect(0, 0, 64, 64), source.Bounds, 1, clip, frameOffset, 64, 64);
        }

        Draw(0);
        byte[] normal = target.GetPixelBytes();
        Draw(23);
        byte[] inverted = target.GetPixelBytes();
        int centre = (32 * 64 + 32) * 4;
        int corner = 4;
        Assert.True(normal[centre + 2] > 245);
        Assert.True(normal[corner] > 245);
        Assert.True(inverted[centre] > 245);
        Assert.True(inverted[corner + 2] > 245);
    }

    [Fact]
    public void Render_FeatheredInvertedMaskFadesTowardItsInterior()
    {
        using var device = new CanvasDevice(true);
        byte[] pixels = new byte[64 * 64 * 4];
        for (int index = 0; index < pixels.Length; index += 4)
        {
            pixels[index + 2] = 255;
            pixels[index + 3] = 255;
        }

        using var source = CanvasBitmap.CreateFromBytes(
            device, pixels, 64, 64, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        using var target = new CanvasRenderTarget(device, 64, 64, 96);
        TimelineClipProject clip = new()
        {
            SourcePath = "red.png",
            DurationSeconds = 2,
            MaskShape = VideoMaskShape.Rectangle,
            MaskWidth = 0.5,
            MaskHeight = 0.5,
            MaskFeather = 0.2,
            MaskInversionKeyframes = [new() { FrameOffset = 0, Inverted = true }],
        };

        using (CanvasDrawingSession session = target.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Blue);
            VideoClipMask.DrawMaskedImage(session, source,
                new Windows.Foundation.Rect(0, 0, 64, 64), source.Bounds, 1, clip, 0, 64, 64);
        }

        byte[] rendered = target.GetPixelBytes();
        int nearEdge = (32 * 64 + 17) * 4;
        int interior = (32 * 64 + 32) * 4;
        Assert.True(rendered[nearEdge + 2] > rendered[nearEdge]);
        Assert.True(rendered[interior] > rendered[interior + 2]);
    }

    [Fact]
    public void RemovingOnlyMaskFeatherKeyframe_BakesItsValueIntoStaticFeather()
    {
        TimelineClipProject clip = new()
        {
            SourcePath = "still.png",
            DurationSeconds = 2,
            MaskShape = VideoMaskShape.Ellipse,
            MaskFeather = 0.04,
            MaskFeatherKeyframes = [new() { FrameOffset = 12, Feather = 0.2 }],
        };

        Assert.True(VideoMaskFeatherKeyframes.TryRemoveAtFrame(clip, 12));
        Assert.Empty(clip.MaskFeatherKeyframes);
        Assert.Equal(0.2, clip.MaskFeather);
        Assert.Equal(0.2, VideoMaskFeatherKeyframes.GetValue(clip, 0));
    }

    [Fact]
    public void Render_EllipseMaskRevealsBackgroundOutsideShape()
    {
        using var device = new CanvasDevice(true);
        byte[] pixels = new byte[64 * 64 * 4];
        for (int index = 0; index < pixels.Length; index += 4)
        {
            pixels[index + 2] = 255;
            pixels[index + 3] = 255;
        }

        using var source = CanvasBitmap.CreateFromBytes(
            device, pixels, 64, 64, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        using var target = new CanvasRenderTarget(device, 64, 64, 96);
        var clip = new TimelineClipProject
        {
            SourcePath = "red.png",
            MaskShape = VideoMaskShape.Ellipse,
            MaskWidth = 0.5,
            MaskHeight = 0.5,
        };

        using (CanvasDrawingSession session = target.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Blue);
            TimelineCompositor.DrawFitted(
                session, source, source.Bounds, 64, 64, 1, 0.5, 0.5, 1, colourGrade: clip);
        }

        byte[] rendered = target.GetPixelBytes();
        int centre = (32 * 64 + 32) * 4;
        int corner = 4;
        Assert.InRange(rendered[centre + 2], 245, 255);
        Assert.InRange(rendered[centre], 0, 10);
        Assert.InRange(rendered[corner], 245, 255);
        Assert.InRange(rendered[corner + 2], 0, 10);
    }

    [Fact]
    public void Render_AnimatedImageMaskMovesAcrossFrames()
    {
        using var device = new CanvasDevice(true);
        byte[] pixels = new byte[64 * 64 * 4];
        for (int index = 0; index < pixels.Length; index += 4)
        {
            pixels[index + 2] = 255;
            pixels[index + 3] = 255;
        }

        using var source = CanvasBitmap.CreateFromBytes(
            device, pixels, 64, 64, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        using var target = new CanvasRenderTarget(device, 64, 64, 96);
        var clip = new TimelineClipProject
        {
            SourcePath = "still.png",
            DurationSeconds = 2,
            MaskShape = VideoMaskShape.Rectangle,
            MaskWidth = 0.25,
            MaskHeight = 0.5,
            MaskKeyframes =
            [
                new() { FrameOffset = 0, X = 0.25, Y = 0.5, Width = 0.25, Height = 0.5 },
                new() { FrameOffset = 23, X = 0.75, Y = 0.5, Width = 0.25, Height = 0.5 },
            ],
        };

        using (CanvasDrawingSession session = target.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Blue);
            VideoClipMask.DrawMaskedImage(
                session, source, new Windows.Foundation.Rect(0, 0, 64, 64), source.Bounds,
                1, clip, 0, 64, 64);
        }
        byte[] first = target.GetPixelBytes();

        using (CanvasDrawingSession session = target.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Blue);
            VideoClipMask.DrawMaskedImage(
                session, source, new Windows.Foundation.Rect(0, 0, 64, 64), source.Bounds,
                1, clip, 23, 64, 64);
        }
        byte[] last = target.GetPixelBytes();

        static bool IsRed(byte[] frame, int x) => frame[((32 * 64) + x) * 4 + 2] > 200;
        Assert.True(IsRed(first, 16));
        Assert.False(IsRed(first, 48));
        Assert.False(IsRed(last, 16));
        Assert.True(IsRed(last, 48));
    }

    [Fact]
    public void Render_InvertedFeatheredMaskBlendsAroundItsEdge()
    {
        using var device = new CanvasDevice(true);
        byte[] pixels = new byte[64 * 64 * 4];
        for (int index = 0; index < pixels.Length; index += 4)
        {
            pixels[index + 2] = 255;
            pixels[index + 3] = 255;
        }

        using var source = CanvasBitmap.CreateFromBytes(
            device, pixels, 64, 64, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        using var target = new CanvasRenderTarget(device, 64, 64, 96);
        var clip = new TimelineClipProject
        {
            SourcePath = "red.png",
            MaskShape = VideoMaskShape.Rectangle,
            MaskWidth = 0.5,
            MaskHeight = 0.5,
            MaskInverted = true,
            MaskFeather = 0.15,
        };

        using (CanvasDrawingSession session = target.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Blue);
            TimelineCompositor.DrawFitted(
                session, source, source.Bounds, 64, 64, 1, 0.5, 0.5, 1, colourGrade: clip);
        }

        byte[] rendered = target.GetPixelBytes();
        int outside = 4;
        int centre = (32 * 64 + 32) * 4;
        int feather = (32 * 64 + 20) * 4;
        Assert.InRange(rendered[outside + 2], 245, 255);
        Assert.InRange(rendered[outside], 0, 10);
        Assert.InRange(rendered[centre], 245, 255);
        Assert.InRange(rendered[centre + 2], 0, 10);
        Assert.InRange(rendered[feather], 20, 235);
        Assert.InRange(rendered[feather + 2], 20, 235);
    }

    [Fact]
    public void Render_AnimatedMaskFeatherChangesTheComposite()
    {
        using var device = new CanvasDevice(true);
        byte[] pixels = new byte[64 * 64 * 4];
        for (int index = 0; index < pixels.Length; index += 4)
        {
            pixels[index + 2] = 255;
            pixels[index + 3] = 255;
        }

        using var source = CanvasBitmap.CreateFromBytes(
            device, pixels, 64, 64, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        using var target = new CanvasRenderTarget(device, 64, 64, 96);
        TimelineClipProject clip = new()
        {
            SourcePath = "red.png",
            DurationSeconds = 2,
            MaskShape = VideoMaskShape.Rectangle,
            MaskWidth = 0.5,
            MaskHeight = 0.5,
            MaskFeatherKeyframes =
            [
                new() { FrameOffset = 0, Feather = 0 },
                new() { FrameOffset = 23, Feather = 0.25 },
            ],
        };

        using (CanvasDrawingSession session = target.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Blue);
            VideoClipMask.DrawMaskedImage(
                session, source, new Windows.Foundation.Rect(0, 0, 64, 64), source.Bounds,
                1, clip, 0, 64, 64);
        }
        byte[] unfeathered = target.GetPixelBytes();

        using (CanvasDrawingSession session = target.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Blue);
            VideoClipMask.DrawMaskedImage(
                session, source, new Windows.Foundation.Rect(0, 0, 64, 64), source.Bounds,
                1, clip, 23, 64, 64);
        }
        byte[] feathered = target.GetPixelBytes();

        Assert.False(unfeathered.SequenceEqual(feathered));
    }
}
