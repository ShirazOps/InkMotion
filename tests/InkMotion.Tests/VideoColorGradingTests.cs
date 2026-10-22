using System.Text.Json.Nodes;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;
using Xunit;

namespace InkMotion.Tests;

public sealed class VideoColorGradingTests
{
    [Fact]
    public void Adjustments_CloneAndRoundTripWithVideoAndImageClips()
    {
        TimelineClipProject video = new()
        {
            SourcePath = "video.mp4",
            SourceDurationSeconds = 4,
            Exposure = 1.25,
            Contrast = -0.3,
            WhiteBalanceTemperature = 0.4,
            WhiteBalanceTint = -0.2,
            Saturation = 1.5,
        };
        TimelineClipProject clone = VideoTimelineEditing.Clone(video);
        Assert.Equal(video.Exposure, clone.Exposure);
        Assert.Equal(video.Contrast, clone.Contrast);
        Assert.Equal(video.WhiteBalanceTemperature, clone.WhiteBalanceTemperature);
        Assert.Equal(video.WhiteBalanceTint, clone.WhiteBalanceTint);
        Assert.Equal(video.Saturation, clone.Saturation);

        EditorProject restored = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(
            new EditorProject
            {
                VideoTracks =
                [
                    new TimelineTrackProject { Kind = TimelineTrackKind.Video, Clips = [video] },
                    new TimelineTrackProject
                    {
                        Kind = TimelineTrackKind.Image,
                        Clips =
                        [
                            new TimelineClipProject
                            {
                                SourcePath = "image.png",
                                Exposure = -0.5,
                                Contrast = 0.25,
                                WhiteBalanceTemperature = -0.1,
                                WhiteBalanceTint = 0.2,
                                Saturation = 0,
                            },
                        ],
                    },
                ],
            }));

        Assert.Equal(EditorProject.CurrentVersion, restored.Version);
        Assert.Equal(1.25, restored.VideoTracks[0].Clips[0].Exposure);
        Assert.Equal(-0.3, restored.VideoTracks[0].Clips[0].Contrast);
        Assert.Equal(0.4, restored.VideoTracks[0].Clips[0].WhiteBalanceTemperature);
        Assert.Equal(-0.2, restored.VideoTracks[0].Clips[0].WhiteBalanceTint);
        Assert.Equal(1.5, restored.VideoTracks[0].Clips[0].Saturation);
        Assert.Equal(0, restored.VideoTracks[1].Clips[0].Saturation);
    }

    [Fact]
    public void VersionTwentyThreeProjectsMigrateWithNeutralColorSettings()
    {
        EditorProject project = new()
        {
            VideoTracks =
            [
                new TimelineTrackProject
                {
                    Kind = TimelineTrackKind.Image,
                    Clips = [new TimelineClipProject { SourcePath = "image.png" }],
                },
            ],
        };
        JsonObject json = JsonNode.Parse(EditorProjectSerialiser.Serialise(project))!.AsObject();
        json["Version"] = 23;
        JsonObject clipJson = json["VideoTracks"]![0]!["Clips"]![0]!.AsObject();
        clipJson.Remove("Exposure");
        clipJson.Remove("Contrast");
        clipJson.Remove("WhiteBalanceTemperature");
        clipJson.Remove("WhiteBalanceTint");
        clipJson.Remove("Saturation");

        EditorProject restored = EditorProjectSerialiser.Deserialise(json.ToJsonString());
        TimelineClipProject clip = restored.VideoTracks[0].Clips[0];
        Assert.Equal(EditorProject.CurrentVersion, restored.Version);
        Assert.True(VideoColorGrading.IsNeutral(clip));
    }

    [Fact]
    public void Validate_RejectsOutOfRangeColorSettingsAndTitlesWithVideoGrading()
    {
        TimelineTrackProject track = new()
        {
            Kind = TimelineTrackKind.Video,
            Clips = [new TimelineClipProject { SourcePath = "video.mp4", Exposure = 2.1 }],
        };
        Assert.NotNull(VideoTimelineEditing.Validate([track], 24));

        track.Kind = TimelineTrackKind.Text;
        track.Clips[0].SourcePath = null;
        track.Clips[0].Text = "Title";
        track.Clips[0].Saturation = 0.5;
        Assert.NotNull(VideoTimelineEditing.Validate([track], 24));
    }

    [Fact]
    public void SaturationZero_RendersNeutralChannelValues()
    {
        using var device = new CanvasDevice(true);
        byte[] sourcePixels = [20, 80, 220, 255];
        using var source = CanvasBitmap.CreateFromBytes(
            device,
            sourcePixels,
            1,
            1,
            DirectXPixelFormat.B8G8R8A8UIntNormalized);
        using var target = new CanvasRenderTarget(device, 16, 16, 96);
        var clip = new TimelineClipProject { SourcePath = "image.png", Saturation = 0 };

        using (CanvasDrawingSession session = target.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Transparent);
            TimelineCompositor.DrawFitted(session, source, source.Bounds, 16, 16, 1, 0.5, 0.5, 1, colorGrade: clip);
        }

        byte[] rendered = target.GetPixelBytes();
        int pixel = (8 * 16 + 8) * 4;
        Assert.InRange(Math.Abs(rendered[pixel] - rendered[pixel + 1]), 0, 2);
        Assert.InRange(Math.Abs(rendered[pixel + 1] - rendered[pixel + 2]), 0, 2);
    }

    [Fact]
    public void ExposurePlusOneEv_BrightensTheSource()
    {
        using var device = new CanvasDevice(true);
        byte[] sourcePixels = [64, 64, 64, 255];
        using var source = CanvasBitmap.CreateFromBytes(
            device,
            sourcePixels,
            1,
            1,
            DirectXPixelFormat.B8G8R8A8UIntNormalized);
        using var target = new CanvasRenderTarget(device, 16, 16, 96);
        var clip = new TimelineClipProject { SourcePath = "image.png", Exposure = 1 };

        using (CanvasDrawingSession session = target.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Transparent);
            TimelineCompositor.DrawFitted(session, source, source.Bounds, 16, 16, 1, 0.5, 0.5, 1, colorGrade: clip);
        }

        byte[] rendered = target.GetPixelBytes();
        Assert.InRange(rendered[(8 * 16 + 8) * 4], 100, 254);
    }

    [Theory]
    [InlineData("contrast")]
    [InlineData("temperature")]
    [InlineData("tint")]
    public void ContrastAndWhiteBalanceAdjustments_ChangeRenderedColor(string setting)
    {
        using var device = new CanvasDevice(true);
        byte[] sourcePixels = [20, 80, 220, 255];
        using var source = CanvasBitmap.CreateFromBytes(
            device,
            sourcePixels,
            1,
            1,
            DirectXPixelFormat.B8G8R8A8UIntNormalized);
        var neutral = new TimelineClipProject { SourcePath = "image.png" };
        var adjusted = VideoTimelineEditing.Clone(neutral);
        switch (setting)
        {
            case "contrast": adjusted.Contrast = 0.75; break;
            case "temperature": adjusted.WhiteBalanceTemperature = 1; break;
            case "tint": adjusted.WhiteBalanceTint = 1; break;
        }

        Assert.NotEqual(RenderCenterPixel(device, source, neutral), RenderCenterPixel(device, source, adjusted));
    }

    private static (byte B, byte G, byte R) RenderCenterPixel(
        CanvasDevice device,
        CanvasBitmap source,
        TimelineClipProject clip)
    {
        using var target = new CanvasRenderTarget(device, 16, 16, 96);
        using (CanvasDrawingSession session = target.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Transparent);
            TimelineCompositor.DrawFitted(session, source, source.Bounds, 16, 16, 1, 0.5, 0.5, 1, colorGrade: clip);
        }

        byte[] pixels = target.GetPixelBytes();
        int offset = (8 * 16 + 8) * 4;
        return (pixels[offset], pixels[offset + 1], pixels[offset + 2]);
    }
}
