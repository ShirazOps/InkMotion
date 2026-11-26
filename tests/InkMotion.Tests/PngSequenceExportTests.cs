using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Xunit;

namespace InkMotion.Tests;

public sealed class PngSequenceExportTests
{
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

    [Fact]
    public async Task Export_DoesNotOverwriteAnExistingDestination()
    {
        string root = Path.Combine(Path.GetTempPath(), $"InkMotion PNG sequence {Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string output = Path.Combine(root, "sequence");
        Directory.CreateDirectory(output);
        string sentinel = Path.Combine(output, "keep.txt");
        File.WriteAllText(sentinel, "existing");

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => PngSequenceExporter.ExportAsync(
                new EditorProject { Frames = [new() { Strokes = [[new() { X = 0.5, Y = 0.5 }]] }] },
                new(VideoExportProfile.Create(32, 32, 24), 32),
                output,
                null,
                CancellationToken.None));

            Assert.Equal("existing", File.ReadAllText(sentinel));
        }
        finally
        {
            Directory.Delete(output, recursive: true);
            Directory.Delete(root);
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
