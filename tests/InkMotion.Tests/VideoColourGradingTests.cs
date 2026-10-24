using System.Text.Json.Nodes;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;
using Xunit;

namespace InkMotion.Tests;

public sealed class VideoColourGradingTests
{
    [Fact]
    public void GradingKeyframes_InterpolateAllValuesAndHoldEndpoints()
    {
        TimelineClipProject clip = new() { SourcePath = "video.mp4", SourceDurationSeconds = 4 };
        clip.ColourGradingKeyframes =
        [
            new() { FrameOffset = 12, Exposure = -1, Contrast = -0.5, Temperature = -0.25, Tint = 0.25, Saturation = 0.5, Gamma = 0.5 },
            new() { FrameOffset = 60, Exposure = 1, Contrast = 0.5, Temperature = 0.25, Tint = -0.25, Saturation = 1.5, Gamma = 1.5 },
        ];

        Assert.Equal(new VideoColourGradingValue(-1, -0.5, -0.25, 0.25, 0.5, 0.5),
            VideoColourGradingKeyframes.GetValue(clip, 0));
        Assert.Equal(new VideoColourGradingValue(0, 0, 0, 0, 1, 1),
            VideoColourGradingKeyframes.GetValue(clip, 36));
        Assert.Equal(new VideoColourGradingValue(1, 0.5, 0.25, -0.25, 1.5, 1.5),
            VideoColourGradingKeyframes.GetValue(clip, 90));
    }

    [Fact]
    public void GradingKeyframeEditsSplitTrimAndRescaleWithoutLosingTiming()
    {
        TimelineClipProject clip = new()
        {
            SourcePath = "video.mp4",
            SourceDurationSeconds = 4,
            ColourGradingKeyframes =
            [
                new() { FrameOffset = 0, Exposure = -1, Saturation = 0.5 },
                new() { FrameOffset = 48, Exposure = 1, Saturation = 1.5 },
                new() { FrameOffset = 95, Exposure = 0.5, Saturation = 2 },
            ],
        };
        TimelineTrackProject track = new() { Kind = TimelineTrackKind.Video, Clips = [clip] };

        Assert.True(VideoColourGradingKeyframes.TrySetAtFrame(clip, track.Kind, 24, 24));
        Assert.Equal(0, clip.ColourGradingKeyframes[1].Exposure);
        TimelineClipProject right = Assert.IsType<TimelineClipProject>(
            VideoTimelineEditing.Split(track, clip, 49, 24));
        Assert.Equal(1, right.ColourGradingKeyframes[0].Exposure);
        Assert.Equal(47, clip.ColourGradingKeyframes[^1].FrameOffset);
        Assert.True(VideoTimelineEditing.TryTrimStart(track, right, 61, 24));
        Assert.Equal(0, right.ColourGradingKeyframes[0].FrameOffset);
        Assert.Equal(1 - (12d / 47d) * 0.5, right.ColourGradingKeyframes[0].Exposure, 6);
        Assert.True(VideoTimelineEditing.TryTrimEnd(track, right, 85, 24));

        VideoTimelineEditing.RescaleFrameRate([track], 24, 48);
        Assert.All(right.ColourGradingKeyframes, key => Assert.InRange(key.FrameOffset, 0, 47));
        Assert.Null(VideoTimelineEditing.Validate([track], 48));
    }

    [Fact]
    public void GradingKeyframes_RoundTripCloneAndPreserveStaticCurves()
    {
        TimelineClipProject clip = new()
        {
            SourcePath = "image.png",
            DurationSeconds = 2,
            Exposure = 0.25,
            MasterCurve = [new(0, 0), new(0.5, 0.7), new(1, 1)],
            ColourGradingKeyframes =
            [
                new() { FrameOffset = 0, Exposure = -0.5, Saturation = 0.8 },
                new() { FrameOffset = 24, Exposure = 1.5, Saturation = 1.2 },
            ],
        };
        TimelineTrackProject track = new() { Kind = TimelineTrackKind.Image, Clips = [clip] };

        TimelineClipProject clone = VideoTimelineEditing.Clone(clip);
        EditorProject restored = EditorProjectSerialiser.Deserialise(
            EditorProjectSerialiser.Serialise(new EditorProject { VideoTracks = [track] }));
        TimelineClipProject saved = Assert.Single(Assert.Single(restored.VideoTracks).Clips);

        Assert.Equal(new[] { 0, 24 }, clone.ColourGradingKeyframes.Select(key => key.FrameOffset));
        Assert.Equal(new[] { 0, 24 }, saved.ColourGradingKeyframes.Select(key => key.FrameOffset));
        Assert.Equal(0.25, saved.Exposure);
        Assert.Equal(0.5, saved.MasterCurve[1].Input);
        Assert.Equal(0.5, VideoColourGradingKeyframes.GetValue(saved, 12).Exposure, 6);
        Assert.Null(VideoTimelineEditing.Validate(restored.VideoTracks, restored.FrameRate));
    }

    [Fact]
    public void GradingKeyframes_RejectInvalidValuesAndVersion45DefaultsToStaticSettings()
    {
        TimelineClipProject clip = new() { SourcePath = "video.mp4", SourceDurationSeconds = 4 };
        TimelineTrackProject track = new() { Kind = TimelineTrackKind.Video, Clips = [clip] };
        clip.ColourGradingKeyframes = [new() { FrameOffset = 0, Exposure = 2.1 }];
        Assert.NotNull(VideoTimelineEditing.Validate([track], 24));

        EditorProject original = new()
        {
            VideoTracks =
            [
                new TimelineTrackProject
                {
                    Kind = TimelineTrackKind.Video,
                    Clips = [new TimelineClipProject { SourcePath = "video.mp4", SourceDurationSeconds = 4, Exposure = 0.5 }],
                },
            ],
        };
        JsonObject legacy = JsonNode.Parse(EditorProjectSerialiser.Serialise(original))!.AsObject();
        legacy["Version"] = 45;
        legacy["VideoTracks"]![0]!["Clips"]![0]!.AsObject().Remove("ColourGradingKeyframes");

        EditorProject migrated = EditorProjectSerialiser.Deserialise(legacy.ToJsonString());
        TimelineClipProject migratedClip = Assert.Single(Assert.Single(migrated.VideoTracks).Clips);
        Assert.Equal(EditorProject.CurrentVersion, migrated.Version);
        Assert.Equal(0.5, migratedClip.Exposure);
        Assert.Empty(migratedClip.ColourGradingKeyframes);
    }

    [Fact]
    public void Adjustments_CloneAndRoundTripWithVideoAndImageClips()
    {
        TimelineClipProject video = new()
        {
            SourcePath = "video.mp4",
            SourceDurationSeconds = 4,
            Exposure = 1.25,
            Gamma = 1.7,
            Contrast = -0.3,
            WhiteBalanceTemperature = 0.4,
            WhiteBalanceTint = -0.2,
            Saturation = 1.5,
            MasterCurve = [new(0, 0), new(0.5, 0.7), new(1, 1)],
            RedCurve = [new(0, 0), new(0.4, 0.2), new(1, 1)],
        };
        TimelineClipProject clone = VideoTimelineEditing.Clone(video);
        Assert.Equal(video.Exposure, clone.Exposure);
        Assert.Equal(video.Gamma, clone.Gamma);
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
        Assert.Equal(1.7, restored.VideoTracks[0].Clips[0].Gamma);
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
        clipJson.Remove("Gamma");
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
    public void VersionSixtyProjectsMigrateWithNeutralGamma()
    {
        EditorProject original = new()
        {
            VideoTracks =
            [
                new TimelineTrackProject
                {
                    Kind = TimelineTrackKind.Image,
                    Clips = [new TimelineClipProject { SourcePath = "image.png", Gamma = 1.8 }],
                },
            ],
        };
        JsonObject json = JsonNode.Parse(EditorProjectSerialiser.Serialise(original))!.AsObject();
        json["Version"] = 60;
        JsonObject clipJson = json["VideoTracks"]![0]!["Clips"]![0]!.AsObject();
        clipJson.Remove("Gamma");

        EditorProject restored = EditorProjectSerialiser.Deserialise(json.ToJsonString());
        TimelineClipProject clip = Assert.Single(Assert.Single(restored.VideoTracks).Clips);
        Assert.Equal(EditorProject.CurrentVersion, restored.Version);
        Assert.Equal(1, clip.Gamma);
        Assert.Empty(clip.ColourGradingKeyframes);
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

    [Fact]
    public void GammaCorrection_DarkensOrBrightensThroughSharedCompositor()
    {
        using var device = new CanvasDevice(true);
        byte[] sourcePixels = Enumerable.Range(0, 16)
            .SelectMany(_ => new byte[] { 64, 64, 64, 255 })
            .ToArray();
        using var source = CanvasBitmap.CreateFromBytes(
            device,
            sourcePixels,
            4,
            4,
            DirectXPixelFormat.B8G8R8A8UIntNormalized);
        using var target = new CanvasRenderTarget(device, 16, 16, 96);
        var clip = new TimelineClipProject { SourcePath = "image.png" };

        using (CanvasDrawingSession session = target.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Transparent);
            TimelineCompositor.DrawFitted(session, source, source.Bounds, 16, 16, 1, 0.5, 0.5, 1, colourGrade: clip);
        }

        byte[] neutral = target.GetPixelBytes();
        clip.Gamma = 0.5;
        using (CanvasDrawingSession session = target.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Transparent);
            TimelineCompositor.DrawFitted(session, source, source.Bounds, 16, 16, 1, 0.5, 0.5, 1, colourGrade: clip);
        }

        byte[] rendered = target.GetPixelBytes();
        int pixel = (8 * 16 + 8) * 4;
        Assert.InRange(rendered[pixel], 14, 22);
        Assert.Equal(neutral[pixel + 3], rendered[pixel + 3]);
    }

    [Fact]
    public void AnimatedExposure_UsesInterpolatedValueInSharedCompositor()
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
        TimelineClipProject clip = new()
        {
            SourcePath = "image.png",
            DurationSeconds = 2,
            ColourGradingKeyframes =
            [
                new() { FrameOffset = 0, Exposure = 0, Saturation = 1 },
                new() { FrameOffset = 24, Exposure = 1, Saturation = 1 },
            ],
        };
        VideoColourGradingValue value = VideoColourGradingKeyframes.GetValueAtTime(clip, 0.5, 24);

        using (CanvasDrawingSession session = target.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Transparent);
            TimelineCompositor.DrawFitted(
                session, source, source.Bounds, 16, 16, 1, 0.5, 0.5, 1,
                colourGrade: clip, animatedColourGrade: value);
        }

        byte[] rendered = target.GetPixelBytes();
        Assert.InRange(rendered[(8 * 16 + 8) * 4], 78, 100);
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
