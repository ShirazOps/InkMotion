using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;
using Xunit;

namespace InkMotion.Tests;

public sealed class HdrColourTests
{
    private static readonly ProjectColourSettings Pq = new();
    private static readonly ProjectColourSettings Hlg = new(ProjectColourSpace.Hlg);

    [Fact]
    public void ReferenceWhiteMatchesBt2408SignalLevels()
    {
        (double pq, _, _) = HdrColour.EncodePixel(1, 1, 1, Pq);
        (double hlg, _, _) = HdrColour.EncodePixel(1, 1, 1, Hlg);

        Assert.InRange(pq, 0.575, 0.585);
        Assert.InRange(hlg, 0.745, 0.755);
        Assert.Equal(1, HdrColour.PqEncode(10000), 6);
        Assert.Equal(0, HdrColour.PqEncode(0), 5);
    }

    [Theory]
    [InlineData(ProjectColourSpace.Hdr10Pq, 0.25, 0.5, 0.75)]
    [InlineData(ProjectColourSpace.Hdr10Pq, 1.6, 1.2, 0.4)]
    [InlineData(ProjectColourSpace.Hlg, 0.25, 0.5, 0.75)]
    [InlineData(ProjectColourSpace.Hlg, 1.6, 1.2, 0.4)]
    public void WorkingPixelsRoundTripThroughEncodedSignal(ProjectColourSpace space, double r, double g, double b)
    {
        var settings = new ProjectColourSettings(space);
        (double er, double eg, double eb) = HdrColour.EncodePixel(r, g, b, settings);
        (double dr, double dg, double db) = HdrColour.DecodePixel(er, eg, eb,
            space == ProjectColourSpace.Hlg ? VideoSourceTransfer.Hlg : VideoSourceTransfer.Pq, settings);

        Assert.Equal(r, dr, 3);
        Assert.Equal(g, dg, 3);
        Assert.Equal(b, db, 3);
    }

    [Fact]
    public void HighlightsAboveReferenceWhiteStayBrighterAndClipAtMasteringPeak()
    {
        (double white, _, _) = HdrColour.EncodePixel(1, 1, 1, Pq);
        (double highlight, _, _) = HdrColour.EncodePixel(1.5, 1.5, 1.5, Pq);
        (double beyondPeak, _, _) = HdrColour.EncodePixel(50, 50, 50, Pq);

        Assert.True(highlight > white + 0.05);
        Assert.Equal(HdrColour.PqEncode(Pq.PeakNits), beyondPeak, 6);
    }

    [Fact]
    public void PreviewToneMapKeepsSdrRangeAndCompressesHighlights()
    {
        (double r, double g, double b) = HdrColour.ToneMapForPreview(0.5, 0.25, 0.75);
        (double hr, double hg, double hb) = HdrColour.ToneMapForPreview(3, 3, 1.5);

        Assert.Equal((0.5, 0.25, 0.75), (Math.Round(r, 6), Math.Round(g, 6), Math.Round(b, 6)));
        Assert.InRange(hr, 0.9, 1);
        Assert.Equal(hr, hg, 6);
        Assert.True(hb < hr);
    }

    [Fact]
    public void ScRgbDisplayConversionUsesReferenceWhiteAndPreservesExtendedRange()
    {
        Half[] working =
        [
            (Half)1, (Half)1, (Half)1, (Half)1,
            (Half)2, (Half)0.5, (Half)(-0.25), (Half)1,
            (Half)0.5, (Half)0.25, (Half)0.125, (Half)0.5,
            (Half)0, (Half)0, (Half)0, (Half)0,
        ];

        byte[] converted = HdrColour.ConvertWorkingFrameToScRgb(
            MemoryMarshal.AsBytes(working.AsSpan()).ToArray(), 203);
        Half[] pixels = MemoryMarshal.Cast<byte, Half>(converted).ToArray();

        Assert.Equal(203 / 80.0, (double)pixels[0], 2);
        Assert.Equal(HdrColour.SrgbToLinear(2) * 203 / 80.0, (double)pixels[4], 3);
        Assert.Equal(203 / 80.0 * 0.5, (double)pixels[8], 2);
        Assert.Equal((Half)0.5, pixels[11]);
        Assert.Equal((Half)0, pixels[15]);
    }

    [Fact]
    public void ProbeParsesHdrTransferAndIgnoresCoverArt()
    {
        const string diagnostic = """
            Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'clip.mp4':
              Stream #0:0[0x1](und): Video: hevc (Main 10) (hvc1 / 0x31637668), yuv420p10le(tv, bt2020nc/bt2020/smpte2084), 3840x2160 [SAR 1:1 DAR 16:9], 40000 kb/s, 29.97 fps, 29.97 tbr, 90k tbn (default)
              Stream #0:1: Video: mjpeg, yuvj420p(pc), 600x600, 90k tbr, 90k tbn (attached pic)
            """;
        const string hlg = "  Stream #0:0: Video: h264, yuv420p10le(tv, bt2020nc/bt2020/arib-std-b67), 1920x1080, 25 tbr";
        const string sdr = "  Stream #0:0: Video: h264, yuv420p(tv, bt709), 1920x1080, 25 fps";

        HdrVideoSourceInfo? info = HdrVideoProbe.Parse(diagnostic);

        Assert.NotNull(info);
        Assert.Equal(VideoSourceTransfer.Pq, info.Transfer);
        Assert.Equal((3840, 2160), (info.Width, info.Height));
        Assert.Equal(29.97, info.FrameRate, 3);
        Assert.Equal(VideoSourceTransfer.Hlg, HdrVideoProbe.Parse(hlg)!.Transfer);
        Assert.Equal(VideoSourceTransfer.Sdr, HdrVideoProbe.Parse(sdr)!.Transfer);
    }

    [Fact]
    public void ColourSettingsPersistValidateAndLegacyProjectsStaySdr()
    {
        var project = new EditorProject { Colour = new ProjectColourSettings(ProjectColourSpace.Hlg, 150, 2000) };

        EditorProject restored = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        EditorProject legacy = EditorProjectSerialiser.Deserialise(
            """{"Version":78,"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""");

        Assert.Equal(project.Colour, restored.Colour);
        Assert.Null(legacy.Colour);
        Assert.Equal(EditorProject.CurrentVersion, legacy.Version);
        Assert.DoesNotContain("\"Colour\"", EditorProjectSerialiser.Serialise(legacy));
        Assert.Throws<InvalidDataException>(() => new ProjectColourSettings(ReferenceWhiteNits: 500).Validate());
        Assert.Throws<InvalidDataException>(() => new ProjectColourSettings(PeakNits: 200).Validate());
        Assert.Throws<InvalidDataException>(() => EditorProjectSerialiser.Deserialise(
            """{"Version":79,"Colour":{"Space":"Hlg","ReferenceWhiteNits":203,"PeakNits":100000},"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}"""));
    }

    [Fact]
    public void FloatCompositeKeepsHighlightsAboveReferenceWhite()
    {
        using var device = new CanvasDevice(true);
        using var background = new CanvasRenderTarget(device, 32, 32, 96, DirectXPixelFormat.R16G16B16A16Float,
            CanvasAlphaMode.Premultiplied);
        Half[] bright = Enumerable.Range(0, 32 * 32).SelectMany(_ => new[] { (Half)2, (Half)1.5, (Half)0.5, (Half)1 }).ToArray();
        background.SetPixelBytes(MemoryMarshal.AsBytes(bright.AsSpan()).ToArray());
        var project = new EditorProject
        {
            InkLayers = [new() { Id = "ink-1", BlendMode = InkLayerBlendMode.Multiply }],
            Frames = [new()
            {
                Strokes = [[
                    new() { X = 0.0, Y = 0.5, BrushWidth = 40, Pressure = 1, BrushColour = 0xFFFFFFFF },
                    new() { X = 1.0, Y = 0.5, BrushWidth = 40, Pressure = 1, BrushColour = 0xFFFFFFFF },
                ]],
            }],
        };

        using CanvasRenderTarget composite = TimelineCompositor.CompositeInkFrame(
            device, background, 32, 32, project, 0, 32, 32, 1);
        Half[] pixel = MemoryMarshal.Cast<byte, Half>(composite.GetPixelBytes(16, 16, 1, 1).AsSpan()).ToArray();

        Assert.Equal(DirectXPixelFormat.R16G16B16A16Float, composite.Format);
        Assert.InRange((double)pixel[0], 1.95, 2.05);
        Assert.InRange((double)pixel[1], 1.45, 1.55);
    }

    [Fact]
    public void SepiaNodePreservesHdrHighlights()
    {
        using var device = new CanvasDevice(true);
        using var background = new CanvasRenderTarget(device, 32, 32, 96, DirectXPixelFormat.R16G16B16A16Float,
            CanvasAlphaMode.Premultiplied);
        Half[] bright = Enumerable.Range(0, 32 * 32).SelectMany(_ => new[] { (Half)2, (Half)1.5, (Half)0.5, (Half)1 }).ToArray();
        background.SetPixelBytes(MemoryMarshal.AsBytes(bright.AsSpan()).ToArray());
        InkCompositeNode picture = InkCompositeGraphs.CreateNode(InkCompositeNodeKind.Picture, "picture", 0, 0);
        InkCompositeNode sepia = InkCompositeGraphs.CreateNode(InkCompositeNodeKind.Sepia, "sepia", 0, 0);
        sepia.Inputs[0] = picture.Id;
        InkCompositeNode output = InkCompositeGraphs.CreateNode(InkCompositeNodeKind.Output, "output", 0, 0);
        output.Inputs[0] = sepia.Id;
        var project = new EditorProject
        {
            CompositeGraph = new InkCompositeGraph { Enabled = true, Nodes = [picture, sepia, output] },
        };

        using CanvasRenderTarget composite = TimelineCompositor.CompositeInkFrame(
            device, background, 32, 32, project, 0, 32, 32, 1);
        Half[] pixel = MemoryMarshal.Cast<byte, Half>(composite.GetPixelBytes(16, 16, 1, 1).AsSpan()).ToArray();
        Assert.InRange((double)pixel[0], 2.0, 2.1);
        Assert.Equal(1, (double)pixel[3], 3);
    }

    [Fact]
    public void HalfFloatPixelationAveragesWithoutClipping()
    {
        Half[] pixels = [(Half)3, (Half)0, (Half)0, (Half)1, (Half)1, (Half)0, (Half)0, (Half)1];
        byte[] bytes = MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray();

        InkLayerEffects.PixelateHalf(bytes, 2, 1, 2);
        Half[] result = MemoryMarshal.Cast<byte, Half>(bytes.AsSpan()).ToArray();

        Assert.Equal(2, (double)result[0], 3);
        Assert.Equal(2, (double)result[4], 3);
    }

    [Fact]
    public void HdrExportRefusesSdrOnlyCodecsAndMediaFoundation()
    {
        var project = new EditorProject { Colour = Pq };
        var profile = VideoExportProfile.Create(ExportResolution.P720, 30);
        var settings = new FFmpegVideoSettings(@"C:\ffmpeg.exe", FFmpegVideoEncoder.Software,
            FFmpegVideoContainer.Mp4, FFmpegVideoAudioCodec.Aac);

        Assert.Throws<ArgumentException>(() => FFmpegVideoExporter.ValidateColour(
            new(project, profile, VideoExportCodec.H264, "out.mp4", 720, FFmpeg: settings)));
        FFmpegVideoExporter.ValidateColour(new(project, profile, VideoExportCodec.Hevc, "out.mp4", 720, FFmpeg: settings));
        Assert.Throws<NotSupportedException>(() => VideoExporter.Export(
            new(project, profile, VideoExportCodec.Hevc, "must-not-be-created.mp4", 720), null, CancellationToken.None));
    }

    [Theory]
    [InlineData(ProjectColourSpace.Hdr10Pq, FFmpegVideoEncoder.Software, "smpte2084", "yuv420p10le")]
    [InlineData(ProjectColourSpace.Hlg, FFmpegVideoEncoder.Nvidia, "arib-std-b67", "p010le")]
    public void HdrSegmentsPipeRgb48AndTagRec2020(ProjectColourSpace space, FFmpegVideoEncoder encoder,
        string transfer, string format)
    {
        var request = new VideoExportRequest(new EditorProject { Colour = new ProjectColourSettings(space) },
            VideoExportProfile.Create(ExportResolution.P720, 30), VideoExportCodec.Hevc, "out.mp4", 720,
            FFmpeg: new(@"C:\ffmpeg.exe", encoder, FFmpegVideoContainer.Mp4, FFmpegVideoAudioCodec.Aac));

        IReadOnlyList<string> arguments = FFmpegVideoExporter.BuildSegmentArguments(request, "segment.mp4");
        string joined = string.Join(' ', arguments);

        Assert.Contains("-pixel_format rgb48le", joined);
        Assert.Contains("-color_primaries bt2020", joined);
        Assert.Contains($"-color_trc {transfer}", joined);
        Assert.Contains($"format={format}", joined);
        Assert.Equal(space == ProjectColourSpace.Hdr10Pq, joined.Contains("master-display=", StringComparison.Ordinal));
        Assert.Contains("-pix_fmt yuv420p", string.Join(' ', FFmpegVideoExporter.BuildSegmentArguments(
            request with { Project = new EditorProject(), Codec = VideoExportCodec.H264 }, "segment.mp4")));
    }

    [FFmpegFact]
    public void HdrSourceExportsAsTaggedTenBitVideoWithHighlightsPreserved()
    {
        string ffmpeg = Environment.GetEnvironmentVariable("INKMOTION_TEST_FFMPEG")!;
        FFmpegVideoEncoder encoder = Enum.TryParse(Environment.GetEnvironmentVariable("INKMOTION_TEST_HDR_ENCODER"),
            ignoreCase: true, out FFmpegVideoEncoder configured) ? configured : FFmpegVideoEncoder.Software;
        string directory = Path.Combine(Path.GetTempPath(), $"inkmotion-hdr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            // A flat PQ source: signal 192/255 is about 1000 nits, well above 203-nit reference white.
            string source = Path.Combine(directory, "source.mkv");
            Run(ffmpeg, "-y", "-v", "error", "-f", "lavfi", "-i", "color=c=0xC0C0C0:s=320x180:r=30:d=1",
                "-vf", "format=rgb24,scale=out_color_matrix=bt2020:out_range=tv,format=yuv444p10le," +
                "setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc:range=tv",
                "-color_primaries", "bt2020", "-color_trc", "smpte2084", "-colorspace", "bt2020nc", "-color_range", "tv",
                "-c:v", "ffv1", source);
            var project = new EditorProject
            {
                FrameRate = 30,
                Colour = Pq,
                VideoTracks = [new TimelineTrackProject
                {
                    Kind = TimelineTrackKind.Video,
                    Clips = [new TimelineClipProject { SourcePath = source, SourceDurationSeconds = 1, SourceOutSeconds = 1 }],
                }],
            };
            Assert.Equal(VideoSourceTransfer.Pq, HdrVideoProbe.Probe(ffmpeg, source)!.Transfer);
            var settings = new FFmpegVideoSettings(ffmpeg, encoder, FFmpegVideoContainer.Matroska, FFmpegVideoAudioCodec.Flac);
            string output = Path.Combine(directory, "hdr.mkv");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

            VideoExportResult result = VideoExporter.Export(new(project, VideoExportProfile.Create(ExportResolution.P720, 30),
                VideoExportCodec.Hevc, output, 720, FFmpeg: settings), null, timeout.Token);

            Assert.Equal(30, result.FrameCount);
            HdrVideoSourceInfo info = HdrVideoProbe.Probe(ffmpeg, output)!;
            Assert.Equal(VideoSourceTransfer.Pq, info.Transfer);
            string decoded = Path.Combine(directory, "decoded.rgb48");
            Run(ffmpeg, "-y", "-v", "error", "-i", output, "-frames:v", "1",
                "-vf", "scale=in_color_matrix=bt2020:out_color_matrix=bt2020,scale=16:9,format=rgb48le",
                "-f", "rawvideo", decoded);
            ushort[] pixels = MemoryMarshal.Cast<byte, ushort>(File.ReadAllBytes(decoded).AsSpan()).ToArray();
            double signal = pixels[(4 * 16 + 8) * 3 + 1] / 65535.0;
            Assert.InRange(HdrColour.PqDecode(signal), 800, 1010);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [FFmpegFact]
    public void AutomaticHdr10PlusMetadataIsCarriedFrameAccuratelyAcrossSegments()
    {
        string ffmpeg = Environment.GetEnvironmentVariable("INKMOTION_TEST_FFMPEG")!;
        string directory = Path.Combine(Path.GetTempPath(), $"inkmotion-hdr10plus-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            // Two 3-second PQ shots, about 1000 and 90 cd/m², at 12 frames/s: a scene cut at frame 36 and
            // two encoder segments of 60 and 12 frames.
            string bright = PqSource(ffmpeg, directory, "bright.mkv", "0xC0C0C0");
            string dark = PqSource(ffmpeg, directory, "dark.mkv", "0x808080");
            var project = new EditorProject
            {
                FrameRate = 12,
                Colour = Pq,
                VideoTracks = [new TimelineTrackProject
                {
                    Kind = TimelineTrackKind.Video,
                    Clips =
                    [
                        new TimelineClipProject { SourcePath = bright, SourceDurationSeconds = 3, SourceOutSeconds = 3 },
                        new TimelineClipProject { StartFrame = 37, SourcePath = dark, SourceDurationSeconds = 3, SourceOutSeconds = 3 },
                    ],
                }],
            };
            var request = new VideoExportRequest(project, VideoExportProfile.Create(320, 180, 12),
                VideoExportCodec.Hevc, Path.Combine(directory, "hdr10plus.mkv"), 180,
                FFmpeg: new(ffmpeg, FFmpegVideoEncoder.Software, FFmpegVideoContainer.Matroska,
                    FFmpegVideoAudioCodec.Flac, GenerateHdr10Plus: true));
            string checkpoints = Path.Combine(directory, "checkpoints");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));

            VideoExportResult result = VideoExporter.Export(request with { CheckpointDirectory = checkpoints },
                null, timeout.Token);

            Assert.Equal(72, result.FrameCount);
            Assert.Empty(Directory.Exists(checkpoints) ? Directory.GetFiles(checkpoints, "hdr10plus*") : []);
            // Independently measure the same frames to know what every frame's metadata must be.
            var analyser = new Hdr10PlusAnalyser(320, 180);
            using (var device = new CanvasDevice())
            using (var renderer = new VideoExporter.FrameRenderer(device, request, []))
            {
                for (int frame = 0; frame < 72; frame++) analyser.Add(renderer.Render(frame));
            }
            string expectedPath = Path.Combine(directory, "expected.json");
            analyser.Write(expectedPath, 0, 72);
            JsonElement[] expected = JsonDocument.Parse(File.ReadAllText(expectedPath)).RootElement
                .GetProperty("SceneInfo").EnumerateArray().ToArray();
            Assert.Equal([0, 1], expected.Select(e => e.GetProperty("SceneId").GetInt32()).Distinct());

            // FFmpeg's HEVC decoder parses the ST 2094-40 SEI of every frame and reports it in 1/10,000 units.
            var start = new System.Diagnostics.ProcessStartInfo(ffmpeg)
            {
                RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (string argument in new[] { "-hide_banner", "-i", request.OutputPath, "-vf", "showinfo", "-f", "null", "-" })
                start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)!;
            string log = System.Text.RegularExpressions.Regex.Replace(process.StandardError.ReadToEnd(), @"\s+", " ");
            process.WaitForExit();
            var decoded = System.Text.RegularExpressions.Regex.Matches(log,
                @"SMPTE2094-40 \(HDR10\+\): application version: 1, num_windows: 1, targeted_system_display_maximum_luminance: 0\.0000, " +
                @"window 0 \{maxscl: \{([\d.]+),([\d.]+),([\d.]+)\}, average_maxrgb: ([\d.]+), distribution_maxrgb: \{((?:\(\d+,[\d.]+\))+)\}");
            Assert.Equal(72, decoded.Count);
            for (int frame = 0; frame < 72; frame++)
            {
                JsonElement luminance = expected[frame].GetProperty("LuminanceParameters");
                var groups = decoded[frame].Groups;
                int[] maxScl = luminance.GetProperty("MaxScl").EnumerateArray().Select(v => v.GetInt32()).ToArray();
                for (int channel = 0; channel < 3; channel++)
                    AssertMetadata(maxScl[channel], groups[channel + 1].Value, frame);
                AssertMetadata(luminance.GetProperty("AverageRGB").GetInt32(), groups[4].Value, frame);
                string[] distribution = System.Text.RegularExpressions.Regex.Matches(groups[5].Value, @"\((\d+),([\d.]+)\)")
                    .Select(m => m.Groups[2].Value).ToArray();
                int[] values = luminance.GetProperty("LuminanceDistributions").GetProperty("DistributionValues")
                    .EnumerateArray().Select(v => v.GetInt32()).ToArray();
                Assert.Equal(values.Length, distribution.Length);
                for (int i = 0; i < values.Length; i++) AssertMetadata(values[i], distribution[i], frame);
            }
            Assert.InRange(HdrMetadataNits(decoded[0].Groups[1].Value), 800, 1010);
            Assert.InRange(HdrMetadataNits(decoded[71].Groups[1].Value), 50, 150);

            // Optionally compare exact integers with the independent hdr10plus_tool parser.
            if (Environment.GetEnvironmentVariable("INKMOTION_TEST_HDR10PLUS_TOOL") is { Length: > 0 } tool)
            {
                string hevc = Path.Combine(directory, "stream.hevc"), extracted = Path.Combine(directory, "extracted.json");
                Run(ffmpeg, "-y", "-v", "error", "-i", request.OutputPath, "-map", "0:v:0", "-c:v", "copy",
                    "-bsf:v", "hevc_mp4toannexb", "-f", "hevc", hevc);
                Run(tool, "extract", hevc, "-o", extracted);
                JsonElement[] actual = JsonDocument.Parse(File.ReadAllText(extracted)).RootElement
                    .GetProperty("SceneInfo").EnumerateArray().ToArray();
                Assert.Equal(72, actual.Length);
                for (int frame = 0; frame < 72; frame++)
                {
                    Assert.Equal(expected[frame].GetProperty("LuminanceParameters").GetRawText().Replace(" ", ""),
                        LuminanceJson(actual[frame].GetProperty("LuminanceParameters")));
                }
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string LuminanceJson(JsonElement luminance)
    {
        JsonElement distributions = luminance.GetProperty("LuminanceDistributions");
        string Join(JsonElement array) => string.Join(',', array.EnumerateArray().Select(v => v.GetInt32()));
        return $$"""{"AverageRGB":{{luminance.GetProperty("AverageRGB").GetInt32()}},"LuminanceDistributions":{"DistributionIndex":[{{Join(distributions.GetProperty("DistributionIndex"))}}],"DistributionValues":[{{Join(distributions.GetProperty("DistributionValues"))}}]},"MaxScl":[{{Join(luminance.GetProperty("MaxScl"))}}]}""";
    }

    private static double HdrMetadataNits(string normalised) =>
        double.Parse(normalised, System.Globalization.CultureInfo.InvariantCulture) * 10_000;

    private static void AssertMetadata(int expected, string normalised, int frame)
    {
        // showinfo prints four decimal places of the 10,000 cd/m² scale, i.e. whole cd/m²; metadata is in 0.1 cd/m².
        double actual = HdrMetadataNits(normalised) * 10;
        Assert.True(Math.Abs(actual - expected) <= 5, $"Frame {frame}: expected {expected}, decoded {actual}.");
    }

    private static string PqSource(string ffmpeg, string directory, string name, string colour)
    {
        string path = Path.Combine(directory, name);
        Run(ffmpeg, "-y", "-v", "error", "-f", "lavfi", "-i", $"color=c={colour}:s=320x180:r=12:d=3",
            "-vf", "format=rgb24,scale=out_color_matrix=bt2020:out_range=tv,format=yuv444p10le," +
            "setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc:range=tv",
            "-color_primaries", "bt2020", "-color_trc", "smpte2084", "-colorspace", "bt2020nc", "-color_range", "tv",
            "-c:v", "ffv1", path);
        return path;
    }

    private static void Run(string executable, params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo(executable)
        {
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        string errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, errors);
    }
}
