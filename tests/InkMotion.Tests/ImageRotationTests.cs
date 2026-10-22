using System.Numerics;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;
using Xunit;

namespace InkMotion.Tests;

public sealed class ImageRotationTests
{
    [Fact]
    public void RotatesRenderedImageAboutItsPositionAndRestoresDrawingTransform()
    {
        using var device = new CanvasDevice(true);
        byte[] bytes = Enumerable.Repeat((byte)255, 4 * 2 * 4).ToArray();
        using var image = CanvasBitmap.CreateFromBytes(device, bytes, 4, 2, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        using var target = new CanvasRenderTarget(device, 100, 100, 96);
        using (var session = target.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Transparent);
            Matrix3x2 transform = Matrix3x2.CreateTranslation(5, 0);
            session.Transform = transform;
            TimelineCompositor.DrawFitted(session, image, image.Bounds, 100, 100, 0.4, 0.5, 0.5, 1, 90);
            Assert.Equal(transform, session.Transform);
        }
        byte[] output = target.GetPixelBytes();
        Assert.Equal(255, output[(65 * 100 + 55) * 4 + 3]);
        Assert.Equal(0, output[(50 * 100 + 70) * 4 + 3]);
    }

    [Fact]
    public void PositiveAnglesRotateClockwiseAroundAnOffCentrePosition()
    {
        using var device = new CanvasDevice(true);
        byte[] bytes = [0, 0, 255, 255, 0, 0, 255, 255, 255, 0, 0, 255, 255, 0, 0, 255];
        using var image = CanvasBitmap.CreateFromBytes(device, bytes, 4, 1, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        using var target = new CanvasRenderTarget(device, 100, 100, 96);
        using (var session = target.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Transparent);
            TimelineCompositor.DrawFitted(session, image, image.Bounds, 100, 100, 0.4, 0.3, 0.4, 1, 90);
        }
        byte[] output = target.GetPixelBytes();
        Assert.Equal(255, output[(27 * 100 + 30) * 4 + 2]);
        Assert.Equal(0, output[(27 * 100 + 30) * 4]);
        Assert.Equal(255, output[(53 * 100 + 30) * 4]);
        Assert.Equal(0, output[(53 * 100 + 30) * 4 + 2]);
    }

    [Fact]
    public void RotationSurvivesCloneSplitAndProjectRoundTrip()
    {
        var clip = new TimelineClipProject { SourcePath = "image.png", DurationSeconds = 4, RotationDegrees = -37 };
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Image, Clips = [clip] };
        var project = new EditorProject { VideoTracks = [track] };
        Assert.Equal(-37, VideoTimelineEditing.Clone(clip).RotationDegrees);
        TimelineClipProject right = Assert.IsType<TimelineClipProject>(VideoTimelineEditing.Split(track, clip, 49, 24));
        Assert.Equal(-37, right.RotationDegrees);
        EditorProject restored = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        Assert.All(restored.VideoTracks[0].Clips, item => Assert.Equal(-37, item.RotationDegrees));
    }

    [Fact]
    public void VersionSeventeenProjectsMigrateWithNeutralRotation()
    {
        var project = new EditorProject
        {
            VideoTracks = [new() { Kind = TimelineTrackKind.Image,
                Clips = [new() { SourcePath = "image.png" }] }],
        };
        string json = EditorProjectSerialiser.Serialise(project)
            .Replace("\"Version\": 18", "\"Version\": 17");
        EditorProject restored = EditorProjectSerialiser.Deserialise(json);
        Assert.Equal(EditorProject.CurrentVersion, restored.Version);
        Assert.Equal(0, restored.VideoTracks[0].Clips[0].RotationDegrees);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-181)]
    [InlineData(181)]
    public void RejectsUnsupportedImageRotation(double rotation)
    {
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Image,
            Clips = [new() { SourcePath = "image.png", RotationDegrees = rotation }] };
        Assert.NotNull(VideoTimelineEditing.Validate([track], 24));
    }

    [Fact]
    public void VideoRotationIsValidForRenderedMedia()
    {
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Video,
            Clips = [new() { SourcePath = "video.mp4", SourceDurationSeconds = 4, RotationDegrees = 30 }] };
        Assert.Null(VideoTimelineEditing.Validate([track], 24));
    }
}
