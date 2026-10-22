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
