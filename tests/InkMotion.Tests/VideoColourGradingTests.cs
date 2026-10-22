using System.Text.Json.Nodes;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;
using Xunit;

namespace InkMotion.Tests;

public sealed class VideoColourGradingTests
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
            MasterCurve = [new(0, 0), new(0.5, 0.7), new(1, 1)],
            RedCurve = [new(0, 0), new(0.4, 0.2), new(1, 1)],
        };
        TimelineClipProject clone = VideoTimelineEditing.Clone(video);
        Assert.Equal(video.Exposure, clone.Exposure);
        Assert.Equal(video.Contrast, clone.Contrast);
        Assert.Equal(video.WhiteBalanceTemperature, clone.WhiteBalanceTemperature);
        Assert.Equal(video.WhiteBalanceTint, clone.WhiteBalanceTint);
        Assert.Equal(video.Saturation, clone.Saturation);
        Assert.Equal(video.MasterCurve.ToArray(), clone.MasterCurve.ToArray());
        Assert.Equal(video.RedCurve.ToArray(), clone.RedCurve.ToArray());
        Assert.NotSame(video.MasterCurve, clone.MasterCurve);

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
        Assert.Equal(video.MasterCurve.ToArray(), restored.VideoTracks[0].Clips[0].MasterCurve.ToArray());
        Assert.Equal(video.RedCurve.ToArray(), restored.VideoTracks[0].Clips[0].RedCurve.ToArray());
        Assert.Equal(0, restored.VideoTracks[1].Clips[0].Saturation);
    }

    [Fact]
    public void VersionTwentyThreeProjectsMigrateWithNeutralColourSettings()
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
        clipJson.Remove("MasterCurve");
        clipJson.Remove("RedCurve");
        clipJson.Remove("GreenCurve");
        clipJson.Remove("BlueCurve");

        EditorProject restored = EditorProjectSerialiser.Deserialise(json.ToJsonString());
        TimelineClipProject clip = restored.VideoTracks[0].Clips[0];
        Assert.Equal(EditorProject.CurrentVersion, restored.Version);
        Assert.True(VideoColourGrading.IsNeutral(clip));
    }

    [Fact]
    public void VersionTwentyFourProjectsMigrateWithIdentityCurves()
    {
        EditorProject project = new()
        {
            VideoTracks =
            [
                new TimelineTrackProject
                {
                    Kind = TimelineTrackKind.Video,
                    Clips = [new TimelineClipProject { SourcePath = "video.mp4" }],
                },
            ],
        };
        JsonObject json = JsonNode.Parse(EditorProjectSerialiser.Serialise(project))!.AsObject();
        json["Version"] = 24;
        JsonObject clipJson = json["VideoTracks"]![0]!["Clips"]![0]!.AsObject();
        clipJson.Remove("MasterCurve");
        clipJson.Remove("RedCurve");
        clipJson.Remove("GreenCurve");
        clipJson.Remove("BlueCurve");

        EditorProject restoredProject = EditorProjectSerialiser.Deserialise(json.ToJsonString());
        TimelineClipProject restored = restoredProject.VideoTracks[0].Clips[0];

        Assert.Equal(EditorProject.CurrentVersion, restoredProject.Version);
        Assert.True(VideoColourGrading.IsNeutralCurve(restored.MasterCurve));
        Assert.True(VideoColourGrading.IsNeutralCurve(restored.RedCurve));
        Assert.True(VideoColourGrading.IsNeutralCurve(restored.GreenCurve));
        Assert.True(VideoColourGrading.IsNeutralCurve(restored.BlueCurve));
    }

    [Fact]
    public void VersionTwentyFiveProjectsMigrateWithoutLut()
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
        json["Version"] = 25;
        json["VideoTracks"]![0]!["Clips"]![0]!.AsObject().Remove("ColourLutPath");

        EditorProject restored = EditorProjectSerialiser.Deserialise(json.ToJsonString());

        Assert.Equal(EditorProject.CurrentVersion, restored.Version);
        Assert.Null(restored.VideoTracks[0].Clips[0].ColourLutPath);
    }

    [Fact]
    public void CubeParser_ConvertsRedFastestCubeOrderToWin2DLayout()
    {
        string cube = """
            TITLE "Identity"
            LUT_3D_SIZE 2
            0 0 0
            1 0 0
            0 1 0
            1 1 0
            0 0 1
            1 0 1
            0 1 1
            1 1 1
            """;

        VideoColourLutData parsed = VideoColourLutData.Parse(cube, "identity.cube");

        Assert.Equal(2, parsed.Size);
        byte[] table = parsed.RgbaBytes;
        Assert.Equal([0, 0, 0, 255], table[..4]);
        Assert.Equal([0, 0, 255, 255], table[4..8]);
        Assert.Equal([0, 255, 0, 255], table[8..12]);
        Assert.Equal([255, 0, 0, 255], table[16..20]);
        Assert.Equal([255, 255, 255, 255], table[^4..]);
    }

    [Theory]
    [InlineData("LUT_1D_SIZE 2\n0 0 0\n1 1 1", "1D LUTs are not supported")]
    [InlineData("LUT_3D_SIZE 2\n0 0 0", "Expected 8 LUT entries")]
    [InlineData("LUT_3D_SIZE 2\nDOMAIN_MIN -1 0 0\n0 0 0", "Only the default DOMAIN_MIN")]
    public void CubeParser_RejectsUnsupportedOrIncompleteFiles(string cube, string message)
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => VideoColourLutData.Parse(cube, "invalid.cube"));

        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsOutOfRangeColourSettingsAndTitlesWithVideoGrading()
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
    public void Validate_RejectsInvalidCurvePoints()
    {
        TimelineClipProject clip = new()
        {
            SourcePath = "image.png",
            MasterCurve = [new(0, 0), new(0.5, 0.6), new(0.5, 0.8), new(1, 1)],
        };

        Assert.NotNull(VideoTimelineEditing.Validate(
            [new TimelineTrackProject { Kind = TimelineTrackKind.Image, Clips = [clip] }], 24));
    }

    [Fact]
    public void MasterCurve_ChangesRenderedBrightness()
    {
        using var device = new CanvasDevice(true);
        byte[] sourcePixels = [64, 64, 64, 255];
        using var source = CanvasBitmap.CreateFromBytes(
            device,
            sourcePixels,
            1,
            1,
            DirectXPixelFormat.B8G8R8A8UIntNormalized);
        TimelineClipProject clip = new()
        {
            SourcePath = "image.png",
            MasterCurve = [new(0, 0), new(0.5, 1), new(1, 1)],
        };

        (byte blue, byte green, byte red) = RenderCenterPixel(device, source, clip);
        Assert.InRange(blue, 115, 145);
        Assert.Equal(blue, green);
        Assert.Equal(green, red);
    }

    [Fact]
    public void RedCurve_ChangesOnlyTheRedOutputChannel()
    {
        using var device = new CanvasDevice(true);
        byte[] sourcePixels = [20, 80, 220, 255];
        using var source = CanvasBitmap.CreateFromBytes(
            device,
            sourcePixels,
            1,
            1,
            DirectXPixelFormat.B8G8R8A8UIntNormalized);
        TimelineClipProject clip = new()
        {
            SourcePath = "image.png",
            RedCurve = [new(0, 0), new(0.9, 0), new(1, 1)],
        };

        (byte originalBlue, byte originalGreen, byte originalRed) = RenderCenterPixel(
            device, source, new TimelineClipProject { SourcePath = "image.png" });
        (byte blue, byte green, byte red) = RenderCenterPixel(device, source, clip);
        Assert.InRange(Math.Abs(originalBlue - blue), 0, 4);
        Assert.InRange(Math.Abs(originalGreen - green), 0, 4);
        Assert.True(red < originalRed / 10, $"Expected red output {red} far below original red {originalRed}.");
    }

    [Fact]
    public void CubeLut_AppliesInSharedRendererAndPreservesSourceAlpha()
    {
        string path = Path.Combine(Path.GetTempPath(), $"inkmotion-lut-{Guid.NewGuid():N}.cube");
        try
        {
            File.WriteAllText(path, """
                LUT_3D_SIZE 2
                1 1 1
                0 1 1
                1 0 1
                0 0 1
                1 1 0
                0 1 0
                1 0 0
                0 0 0
                """);
            using var device = new CanvasDevice(true);
            byte[] sourcePixels = [10, 40, 110, 128];
            using var source = CanvasBitmap.CreateFromBytes(
                device,
                sourcePixels,
                1,
                1,
                DirectXPixelFormat.B8G8R8A8UIntNormalized);
            using var target = new CanvasRenderTarget(device, 16, 16, 96);
            TimelineClipProject clip = new() { SourcePath = "image.png", ColourLutPath = path };

            using (CanvasDrawingSession session = target.CreateDrawingSession())
            {
                session.Clear(Microsoft.UI.Colors.Transparent);
                TimelineCompositor.DrawFitted(
                    session, source, source.Bounds, 16, 16, 1, 0.5, 0.5, 1, colourGrade: clip);
            }

            byte[] rendered = target.GetPixelBytes();
            int offset = (8 * 16 + 8) * 4;
            Assert.InRange(rendered[offset + 3], 115, 135);
            Assert.InRange(rendered[offset], 100, 135);
            Assert.InRange(rendered[offset + 1], 70, 90);
            Assert.InRange(rendered[offset + 2], 10, 25);
        }
        finally
        {
            File.Delete(path);
        }
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
            TimelineCompositor.DrawFitted(session, source, source.Bounds, 16, 16, 1, 0.5, 0.5, 1, colourGrade: clip);
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
            TimelineCompositor.DrawFitted(session, source, source.Bounds, 16, 16, 1, 0.5, 0.5, 1, colourGrade: clip);
        }

        byte[] rendered = target.GetPixelBytes();
        Assert.InRange(rendered[(8 * 16 + 8) * 4], 100, 254);
    }

    [Theory]
    [InlineData("contrast")]
    [InlineData("temperature")]
    [InlineData("tint")]
    public void ContrastAndWhiteBalanceAdjustments_ChangeRenderedColour(string setting)
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
            TimelineCompositor.DrawFitted(session, source, source.Bounds, 16, 16, 1, 0.5, 0.5, 1, colourGrade: clip);
        }

        byte[] pixels = target.GetPixelBytes();
        int offset = (8 * 16 + 8) * 4;
        return (pixels[offset], pixels[offset + 1], pixels[offset + 2]);
    }
}
