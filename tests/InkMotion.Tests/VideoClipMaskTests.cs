using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;
using Xunit;

namespace InkMotion.Tests;

public sealed class VideoClipMaskTests
{
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
}
