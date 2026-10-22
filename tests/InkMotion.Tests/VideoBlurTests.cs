using Microsoft.Graphics.Canvas;
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
}
