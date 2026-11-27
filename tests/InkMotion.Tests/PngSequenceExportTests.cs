using Microsoft.Graphics.Canvas;
using NAudio.Wave;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Xunit;

namespace InkMotion.Tests;

public sealed class PngSequenceExportTests
{
    [Theory]
    [InlineData(false, 48)]
    [InlineData(true, 48)]
    [InlineData(false, 29.97)]
    [InlineData(true, 120)]
    public async Task FullTimeline_MatchesVideoCompositorIncludingGapsCompoundsAndAudioTail(bool withInk, double frameRate)
    {
        string root = Path.Combine(Path.GetTempPath(), $"InkMotion timeline PNG {Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string output = Path.Combine(root, "sequence");
        string image = Path.Combine(root, "image.png");
        string audio = Path.Combine(root, "audio.wav");
        var profile = VideoExportProfile.Create(96, 48, frameRate);
        using var device = new CanvasDevice();
        using (var bitmap = new CanvasRenderTarget(device, 32, 32, 96))
        {
            using (CanvasDrawingSession session = bitmap.CreateDrawingSession())
                session.Clear(Microsoft.UI.Colors.Red);
            await bitmap.SaveAsync(image, CanvasBitmapFileFormat.Png);
        }
        using (var writer = new WaveFileWriter(audio, new WaveFormat(48000, 16, 1)))
            writer.WriteSamples(new float[12000], 0, 12000);
        var nested = new EditorProject
        {
            VideoTracks = [new()
            {
                Kind = TimelineTrackKind.Subtitle,
                Clips = [new() { Text = "Nested", DurationSeconds = 2 / 24d }],
            }],
        };
        var project = new EditorProject
        {
            FrameRate = 24,
            CanvasSize = new(32, 32),
            Frames = withInk
                ? [new() { Strokes = [InkStrokeEditing.CreateShape(InkShape.Rectangle,
                    0.1, 0.1, 0.4, 0.4, 1, 0xFF00FF00, filled: true)] }]
                : [new()],
            InkLayers = [new() { BlendMode = InkLayerBlendMode.Screen }],
            AudioTracks = [new() { AudioPath = audio }],
            VideoTracks =
            [
                new() { Kind = TimelineTrackKind.Text,
                    Clips = [new() { Text = "Title", StartFrame = 2, DurationSeconds = 2 / 24d }] },
                new() { Kind = TimelineTrackKind.Compound, Clips = [new()
                {
                    NestedProject = nested, StartFrame = 2, DurationSeconds = 2 / 24d,
                    SourceDurationSeconds = 2 / 24d,
                }] },
                new() { Kind = TimelineTrackKind.Image, Clips = [new()
                {
                    SourcePath = image, StartFrame = 2, DurationSeconds = 2 / 24d,
                    Scale = 0.8, Opacity = 0.6,
                    TransformKeyframes = [new() { FrameOffset = 0, Scale = 0.8 },
                        new() { FrameOffset = 1, Scale = 0.5, RotationDegrees = 30 }],
                }] },
                new() { Kind = TimelineTrackKind.Text, IsVisible = false,
                    Clips = [new() { Text = "Hidden", DurationSeconds = 1 / 24d }] },
            ],
        };
        try
        {
            var updates = new List<VideoExportProgress>();
            PngSequenceExportResult result = await PngSequenceExporter.ExportAsync(project,
                new(profile, 48, PngSequenceExportMode.FullTimeline), output,
                new InlineProgress(updates.Add), CancellationToken.None);
            Assert.Empty(result.Warnings);
            Assert.Equal(0.25, result.DurationSeconds, 6);
            int expectedCount = (int)Math.Ceiling(0.25 * frameRate);
            Assert.Equal(expectedCount, result.FrameCount);
            Assert.Equal(Enumerable.Range(1, expectedCount).Select(index => $"frame_{index:D6}.png"),
                Directory.GetFiles(output).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            Assert.Equal(expectedCount, updates[^1].Frame);
            Assert.Equal(1d, updates[^1].Fraction);
            using var renderer = new VideoExporter.FrameRenderer(device,
                new(project, profile, VideoExportCodec.H264, string.Empty, 48), []);
            byte[][] frames = new byte[result.FrameCount][];
            for (int frame = 0; frame < result.FrameCount; frame++)
            {
                frames[frame] = await ReadFrameAsync(output, frame, profile);
                AssertPixelsMatch(renderer.Render(frame), frames[frame]);
                Assert.All(frames[frame].Where((_, index) => index % 4 == 3), alpha => Assert.Equal(255, alpha));
                Assert.Equal(new byte[] { 0, 0, 0, 255 }, frames[frame][..4]);
            }
            Assert.False(frames[0].SequenceEqual(frames[(int)Math.Ceiling(frameRate / 24)]));
            Assert.Equal(frames[0], frames[^1]);
            Assert.Equal(0.6, project.VideoTracks[2].Clips[0].Opacity);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FullTimeline_RendersRetimedReversedFootageInOutputFrameOrder()
    {
        if (!VideoExportTests.HasH264Encoder) return;
        string root = Path.Combine(Path.GetTempPath(), $"InkMotion video PNG {Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "source.mp4");
        string output = Path.Combine(root, "sequence");
        var profile = VideoExportProfile.Create(64, 64, 24);
        var sourceProject = new EditorProject
        {
            Frames = new uint[] { 0xFFFF0000, 0xFF0000FF }.Select(colour => new EditorFrame
            {
                ExposureFrames = 3,
                Strokes = [InkStrokeEditing.CreateShape(InkShape.Rectangle,
                    0, 0, 1, 1, 1, colour, filled: true)],
            }).ToList(),
        };
        try
        {
            VideoExporter.Export(new(sourceProject, profile, VideoExportCodec.H264, source, 64,
                PreferHardware: false), null, CancellationToken.None);
            var project = new EditorProject { VideoTracks = [new()
            {
                Kind = TimelineTrackKind.Video, IsMuted = true, Clips = [new()
                {
                    SourcePath = source, StartFrame = 2, SourceInSeconds = 1 / 24d,
                    SourceOutSeconds = 6 / 24d, SourceDurationSeconds = 6 / 24d,
                    PlaybackSpeed = 2, IsReversed = true,
                }],
            }] };
            profile = VideoExportProfile.Create(64, 64, 48);
            PngSequenceExportResult result = await PngSequenceExporter.ExportAsync(project,
                new(profile, 64, PngSequenceExportMode.FullTimeline), output, null, CancellationToken.None);
            Assert.Equal(7, result.FrameCount);
            using var device = new CanvasDevice();
            var warnings = new List<string>();
            using var renderer = new VideoExporter.FrameRenderer(device,
                new(project, profile, VideoExportCodec.H264, string.Empty, 64), warnings);
            var frames = new List<byte[]>();
            for (int frame = 0; frame < result.FrameCount; frame++)
            {
                byte[] pixels = await ReadFrameAsync(output, frame, profile);
                AssertPixelsMatch(renderer.Render(frame), pixels);
                frames.Add(pixels);
            }
            Assert.Empty(result.Warnings);
            Assert.Empty(warnings);
            int centre = (32 * 64 + 32) * 4;
            Assert.True(frames[2][centre] > 180, "The first source frame should be blue after reversing.");
            Assert.True(frames[^1][centre + 2] > 180, "The final source frame should be red after reversing.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FullTimeline_MissingMediaIsReportedAndProducesOpaqueBlackFrames()
    {
        string root = Path.Combine(Path.GetTempPath(), $"InkMotion missing PNG {Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string output = Path.Combine(root, "sequence");
        var profile = VideoExportProfile.Create(32, 32, 24);
        var project = new EditorProject { VideoTracks =
        [
            new() { Kind = TimelineTrackKind.Image,
                Clips = [new() { SourcePath = Path.Combine(root, "missing.png"), DurationSeconds = 2 / 24d }] },
            new() { Kind = TimelineTrackKind.Video, IsMuted = true, Clips = [new()
            {
                SourcePath = Path.Combine(root, "missing.mp4"), SourceDurationSeconds = 2 / 24d,
                SourceOutSeconds = 2 / 24d,
            }] },
        ] };
        try
        {
            PngSequenceExportResult result = await PngSequenceExporter.ExportAsync(project,
                new(profile, 32, PngSequenceExportMode.FullTimeline), output, null, CancellationToken.None);
            Assert.Equal(2, result.FrameCount);
            Assert.Contains(result.Warnings, warning => warning.Contains("Skipped image missing.png"));
            Assert.Contains(result.Warnings, warning => warning.Contains("Skipped video missing.mp4"));
            byte[] pixels = await ReadFrameAsync(output, 0, profile);
            for (int index = 0; index < pixels.Length; index += 4)
                Assert.Equal(new byte[] { 0, 0, 0, 255 }, pixels[index..(index + 4)]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullTimeline_CancellationOrDestinationRaceNeverPublishesPartialFrames(bool destinationRace)
    {
        string root = Path.Combine(Path.GetTempPath(), $"InkMotion cancelled PNG {Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string output = Path.Combine(root, "sequence");
        using var cancellation = new CancellationTokenSource();
        var project = new EditorProject { VideoTracks = [new()
        {
            Kind = TimelineTrackKind.Text, Clips = [new() { Text = "Title", DurationSeconds = 2 / 24d }],
        }] };
        try
        {
            var progress = new InlineProgress(update =>
            {
                if (update.Frame != update.FrameCount) return;
                if (destinationRace)
                {
                    Directory.CreateDirectory(output);
                    File.WriteAllText(Path.Combine(output, "keep.txt"), "existing");
                }
                else cancellation.Cancel();
            });
            Task Export() => PngSequenceExporter.ExportAsync(project,
                new(VideoExportProfile.Create(32, 32, 24), 32, PngSequenceExportMode.FullTimeline),
                output, progress, cancellation.Token);
            if (destinationRace)
            {
                await Assert.ThrowsAsync<IOException>(Export);
                Assert.Equal("existing", File.ReadAllText(Path.Combine(output, "keep.txt")));
                Assert.Single(Directory.GetFiles(output));
            }
            else
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(Export);
                Assert.False(Directory.Exists(output));
            }
            Assert.Empty(Directory.GetDirectories(root).Where(path => path != output));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullTimeline_RejectsHdrIncludingNestedTimelinesBeforeWriting(bool nested)
    {
        string output = Path.Combine(Path.GetTempPath(), $"InkMotion HDR PNG {Guid.NewGuid():N}");
        var hdr = new EditorProject { Colour = new() };
        var project = nested ? new EditorProject { VideoTracks = [new()
        {
            Kind = TimelineTrackKind.Compound,
            Clips = [new() { NestedProject = hdr, DurationSeconds = 1, SourceDurationSeconds = 1 }],
        }] } : hdr;
        await Assert.ThrowsAsync<NotSupportedException>(() => PngSequenceExporter.ExportAsync(project,
            new(VideoExportProfile.Create(32, 32, 24), 32, PngSequenceExportMode.FullTimeline),
            output, null, CancellationToken.None));
        Assert.False(Directory.Exists(output));
    }

    private static async Task<byte[]> ReadFrameAsync(string output, int frame, VideoExportProfile profile)
    {
        StorageFile file = await StorageFile.GetFileFromPathAsync(Path.Combine(output, $"frame_{frame + 1:D6}.png"));
        using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        Assert.Equal((uint)profile.Width, decoder.PixelWidth);
        Assert.Equal((uint)profile.Height, decoder.PixelHeight);
        return (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage)).DetachPixelData();
    }

    private static void AssertPixelsMatch(byte[] expected, byte[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int index = 0; index < actual.Length; index++)
            Assert.True(Math.Abs(expected[index] - actual[index]) <= 1,
                $"Channel {index}: expected {expected[index]}, got {actual[index]}.");
    }

    private sealed class InlineProgress(Action<VideoExportProgress> report) : IProgress<VideoExportProgress>
    {
        public void Report(VideoExportProgress value) => report(value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Export_ParticleEffectsMatchPreviewAtTheSameTimes(bool sceneInteraction)
    {
        string root = Path.Combine(Path.GetTempPath(), $"InkMotion particles {Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string output = Path.Combine(root, "sequence");
        InkCompositeNode particles = InkCompositeGraphs.CreateNode(InkCompositeNodeKind.Particles, "particles", 0, 0);
        particles.Particles!.Rate = 1;
        particles.Particles.Speed = 2;
        particles.Particles.Spread = 0;
        particles.Particles.Size = 0.15;
        InkCompositeNode invert = InkCompositeGraphs.CreateNode(InkCompositeNodeKind.InvertColours, "invert", 0, 0);
        invert.Inputs[0] = particles.Id;
        InkCompositeNode result = InkCompositeGraphs.CreateNode(InkCompositeNodeKind.Output, "output", 0, 0);
        result.Inputs[0] = invert.Id;
        var nodes = new List<InkCompositeNode> { particles, invert, result };
        if (sceneInteraction)
        {
            InkCompositeNode scene = InkCompositeGraphs.CreateNode(InkCompositeNodeKind.Scene3D, "scene", 0, 0);
            scene.Scene3D!.CameraYaw = 0;
            scene.Scene3D.CameraPitch = 0;
            scene.Scene3D.LightYaw = 45;
            scene.Scene3D.LightPitch = 0;
            scene.Scene3D.Shadows = true;
            scene.Scene3D.Objects =
            [
                new() { Shape = InkComposite3DShape.Plane, ScaleX = 4, ScaleY = 4, CastsShadow = false },
                new() { Shape = InkComposite3DShape.Cube, Z = 0.6,
                    Texture = new InkMeshTexture { Width = 1, Height = 1, Pixels = [30, 60, 90, 255] } },
            ];
            particles.Particles.InteractionSceneId = scene.Id;
            particles.Particles.InteractionForce = -0.5;
            particles.Particles.CollideWithObjects = true;
            InkCompositeNode merge = InkCompositeGraphs.CreateNode(InkCompositeNodeKind.Merge, "merge", 0, 0);
            merge.Inputs[0] = scene.Id;
            merge.Inputs[1] = particles.Id;
            invert.Inputs[0] = merge.Id;
            nodes.Add(scene);
            nodes.Add(merge);
        }
        var project = new EditorProject
        {
            Frames = [new() { ExposureFrames = 2 }],
            CompositeGraph = new InkCompositeGraph { Enabled = true, Nodes = nodes },
        };

        try
        {
            PngSequenceExportResult exported = await PngSequenceExporter.ExportAsync(
                project, new(VideoExportProfile.Create(32, 32, 24), 32), output, null, CancellationToken.None);
            Assert.Equal(2, exported.FrameCount);
            using var device = new CanvasDevice();
            using var backdrop = new CanvasRenderTarget(device, 32, 32, 96);
            for (int index = 0; index < 2; index++)
            {
                StorageFile frame = await StorageFile.GetFileFromPathAsync(
                    Path.Combine(output, $"frame_{index + 1:000000}.png"));
                using IRandomAccessStream stream = await frame.OpenAsync(FileAccessMode.Read);
                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
                byte[] pixels = (await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform(),
                    ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
                using CanvasRenderTarget preview = TimelineCompositor.CompositeInkFrame(
                    device, backdrop, 32, 32, project, 0, 32, 32, 1, index / 24d);
                byte[] expected = preview.GetPixelBytes();
                Assert.Equal(expected.Length, pixels.Length);
                for (int offset = 0; offset < pixels.Length; offset++)
                    Assert.True(Math.Abs(expected[offset] - pixels[offset]) <= 1,
                        $"Frame {index + 1}, pixel {offset / 4}, channel {offset % 4}: preview {expected[offset]}, export {pixels[offset]}.");
            }
        }
        finally
        {
            foreach (string file in Directory.Exists(output) ? Directory.GetFiles(output) : [])
                File.Delete(file);
            if (Directory.Exists(output)) Directory.Delete(output);
            Directory.Delete(root);
        }
    }

    [Fact]
    public async Task Export_SceneOnlyGraphWritesThePreviewedPixels()
    {
        string root = Path.Combine(Path.GetTempPath(), $"InkMotion 3D scene {Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string output = Path.Combine(root, "sequence");
        InkCompositeNode scene = InkCompositeGraphs.CreateNode(InkCompositeNodeKind.Scene3D, "scene", 0, 0);
        scene.Scene3D!.CameraYaw = 0;
        scene.Scene3D.CameraPitch = 0;
        scene.Scene3D.AmbientLight = 1;
        scene.Scene3D.Objects[0].Colour = 0xFF00FF00;
        InkCompositeNode result = InkCompositeGraphs.CreateNode(InkCompositeNodeKind.Output, "output", 0, 0);
        result.Inputs[0] = scene.Id;
        var project = new EditorProject
        {
            Frames = [new() { ExposureFrames = 2 }],
            CompositeGraph = new InkCompositeGraph { Enabled = true, Nodes = [scene, result] },
        };

        try
        {
            PngSequenceExportResult exported = await PngSequenceExporter.ExportAsync(
                project, new(VideoExportProfile.Create(32, 32, 24), 32), output, null, CancellationToken.None);
            Assert.Equal(2, exported.FrameCount);
            StorageFile frame = await StorageFile.GetFileFromPathAsync(Path.Combine(output, "frame_000001.png"));
            using IRandomAccessStream stream = await frame.OpenAsync(FileAccessMode.Read);
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
            byte[] pixels = (await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
            int centre = ((16 * 32) + 16) * 4;
            Assert.Equal(0, pixels[3]);
            Assert.Equal(0, pixels[centre]);
            Assert.Equal(255, pixels[centre + 1]);
            Assert.Equal(0, pixels[centre + 2]);
            Assert.Equal(255, pixels[centre + 3]);
        }
        finally
        {
            foreach (string file in Directory.Exists(output) ? Directory.GetFiles(output) : [])
                File.Delete(file);
            if (Directory.Exists(output)) Directory.Delete(output);
            Directory.Delete(root);
        }
    }

    [Theory]
    [InlineData(false, InkLayerBlendMode.Normal)]
    [InlineData(true, InkLayerBlendMode.Normal)]
    [InlineData(true, InkLayerBlendMode.Screen)]
    public async Task Export_FixedCanvasPreservesAspectAndTransparentOutputPadding(
        bool fixedCanvas, InkLayerBlendMode blendMode)
    {
        string root = Path.Combine(Path.GetTempPath(), $"InkMotion canvas {Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string output = Path.Combine(root, "sequence");
        var project = new EditorProject
        {
            CanvasSize = fixedCanvas ? new(31, 31) : null,
            InkLayers = [new() { BlendMode = blendMode }],
            Frames = [new()
            {
                Strokes = [InkStrokeEditing.CreateShape(InkShape.Rectangle,
                    0, 0, 1, 1, 1, 0xFFFFFFFF, filled: true)],
            }],
        };
        try
        {
            await PngSequenceExporter.ExportAsync(
                project, new(VideoExportProfile.Create(96, 48, 24), 48), output, null, CancellationToken.None);
            StorageFile frame = await StorageFile.GetFileFromPathAsync(Path.Combine(output, "frame_000001.png"));
            using IRandomAccessStream stream = await frame.OpenAsync(FileAccessMode.Read);
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
            Assert.Equal(96u, decoder.PixelWidth);
            Assert.Equal(48u, decoder.PixelHeight);
            byte[] pixels = (await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
            byte Alpha(int x, int y) => pixels[((y * 96) + x) * 4 + 3];
            Assert.Equal(fixedCanvas ? 0 : 255, Alpha(12, 24));
            Assert.Equal(255, Alpha(30, 24));
            Assert.Equal(255, Alpha(65, 24));
            Assert.Equal(fixedCanvas ? 0 : 255, Alpha(84, 24));
        }
        finally
        {
            foreach (string file in Directory.Exists(output) ? Directory.GetFiles(output) : [])
                File.Delete(file);
            if (Directory.Exists(output)) Directory.Delete(output);
            Directory.Delete(root);
        }
    }

    [Theory]
    [InlineData(PngSequenceExportMode.TransparentInk, false)]
    [InlineData(PngSequenceExportMode.FullTimeline, false)]
    [InlineData(PngSequenceExportMode.FullTimeline, true)]
    public async Task Export_DoesNotOverwriteAnExistingDestination(PngSequenceExportMode mode, bool existingFile)
    {
        string root = Path.Combine(Path.GetTempPath(), $"InkMotion PNG sequence {Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string output = Path.Combine(root, "sequence");
        if (!existingFile) Directory.CreateDirectory(output);
        string sentinel = existingFile ? output : Path.Combine(output, "keep.txt");
        File.WriteAllText(sentinel, "existing");

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => PngSequenceExporter.ExportAsync(
                new EditorProject { Frames = [new() { Strokes = [[new() { X = 0.5, Y = 0.5 }]] }] },
                new(VideoExportProfile.Create(32, 32, 24), 32, mode),
                output,
                null,
                CancellationToken.None));

            Assert.Equal("existing", File.ReadAllText(sentinel));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Export_WritesNumberedFramesWithTransparentBackground()
    {
        string root = Path.Combine(Path.GetTempPath(), $"InkMotion PNG sequence {Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string output = Path.Combine(root, "sequence");
        var project = new EditorProject
        {
            FrameRate = 24,
            Colour = new(),
            Frames =
            [
                new()
                {
                    ExposureFrames = 2,
                    Strokes =
                    [
                        [
                            new() { X = 0.4, Y = 0.5, BrushWidth = 8, BrushColour = 0xFFFF0000 },
                            new() { X = 0.6, Y = 0.5, BrushWidth = 8, BrushColour = 0xFFFF0000 },
                        ],
                    ],
                },
            ],
        };

        try
        {
            PngSequenceExportResult result = await PngSequenceExporter.ExportAsync(
                project,
                new(VideoExportProfile.Create(32, 32, 24), 32),
                output,
                null,
                CancellationToken.None);

            Assert.Equal(2, result.FrameCount);
            Assert.True(File.Exists(Path.Combine(output, "frame_000001.png")));
            Assert.True(File.Exists(Path.Combine(output, "frame_000002.png")));

            StorageFile frame = await StorageFile.GetFileFromPathAsync(Path.Combine(output, "frame_000001.png"));
            using IRandomAccessStream stream = await frame.OpenAsync(FileAccessMode.Read);
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
            Assert.Equal(32u, decoder.PixelWidth);
            Assert.Equal(32u, decoder.PixelHeight);
            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
            byte[] pixels = pixelData.DetachPixelData();
            Assert.Equal(0, pixels[3]);
            Assert.True(pixels[((16 * 32) + 16) * 4 + 3] > 0);
        }
        finally
        {
            foreach (string file in Directory.Exists(output) ? Directory.GetFiles(output) : [])
            {
                File.Delete(file);
            }

            if (Directory.Exists(output))
            {
                Directory.Delete(output);
            }

            Directory.Delete(root);
        }
    }

    [Fact]
    public async Task Export_AppliesBlendModeAgainstLowerInkLayerAndKeepsTransparency()
    {
        string root = Path.Combine(Path.GetTempPath(), $"InkMotion PNG blend {Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string output = Path.Combine(root, "sequence");
        var project = new EditorProject
        {
            InkLayers = [new(), new() { Id = "top", BlendMode = InkLayerBlendMode.Multiply }],
            Frames =
            [
                new()
                {
                    Strokes =
                    [
                        [new() { X = 0.1, Y = 0.5, BrushWidth = 4, Pressure = 1, BrushColour = 0xFFFF0000 },
                         new() { X = 0.9, Y = 0.5, BrushWidth = 4, Pressure = 1, BrushColour = 0xFFFF0000 }],
                    ],
                    LayerStrokes = new()
                    {
                        ["top"] =
                        [
                            [new() { X = 0.1, Y = 0.5, BrushWidth = 4, Pressure = 1, BrushColour = 0xFF0000FF },
                             new() { X = 0.9, Y = 0.5, BrushWidth = 4, Pressure = 1, BrushColour = 0xFF0000FF }],
                        ],
                    },
                },
            ],
        };

        try
        {
            await PngSequenceExporter.ExportAsync(
                project,
                new(VideoExportProfile.Create(32, 32, 24), 32),
                output,
                null,
                CancellationToken.None);

            StorageFile frame = await StorageFile.GetFileFromPathAsync(Path.Combine(output, "frame_000001.png"));
            using IRandomAccessStream stream = await frame.OpenAsync(FileAccessMode.Read);
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
            byte[] pixels = pixelData.DetachPixelData();

            int centre = ((16 * 32) + 16) * 4;
            Assert.Equal(0, pixels[centre]);
            Assert.Equal(0, pixels[centre + 1]);
            Assert.Equal(0, pixels[centre + 2]);
            Assert.True(pixels[centre + 3] > 0);
            Assert.Equal(0, pixels[3]);
        }
        finally
        {
            foreach (string file in Directory.Exists(output) ? Directory.GetFiles(output) : [])
            {
                File.Delete(file);
            }

            if (Directory.Exists(output))
            {
                Directory.Delete(output);
            }

            Directory.Delete(root);
        }
    }

    [Fact]
    public async Task Export_CancellationRemovesTheStagingSequence()
    {
        string root = Path.Combine(Path.GetTempPath(), $"InkMotion PNG sequence {Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string output = Path.Combine(root, "sequence");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var project = new EditorProject
        {
            Frames =
            [
                new()
                {
                    Strokes =
                    [
                        [new() { X = 0.4, Y = 0.5 }, new() { X = 0.6, Y = 0.5 }],
                    ],
                },
            ],
        };

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PngSequenceExporter.ExportAsync(
                project,
                new(VideoExportProfile.Create(32, 32, 24), 32),
                output,
                null,
                cancellation.Token));

            Assert.False(Directory.Exists(output));
            Assert.Empty(Directory.GetFiles(root));
            Assert.Empty(Directory.GetDirectories(root));
        }
        finally
        {
            Directory.Delete(root);
        }
    }
}
