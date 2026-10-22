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
}
