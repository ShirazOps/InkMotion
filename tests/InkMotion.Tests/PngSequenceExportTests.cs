using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Xunit;

namespace InkMotion.Tests;

public sealed class PngSequenceExportTests
{
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
