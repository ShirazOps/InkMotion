using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Xunit;

namespace InkMotion.Tests;

public sealed class GifExportTests
{
    [Fact]
    public async Task Export_WritesLoopingAnimatedGifWithTransparentBackground()
    {
        string root = Path.Combine(Path.GetTempPath(), $"InkMotion GIF {Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string output = Path.Combine(root, "animation.gif");
        File.WriteAllText(output, "replace this");
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
            GifExportResult result = await GifExporter.ExportAsync(project,
                new(VideoExportProfile.Create(32, 32, 24), 32), output, null, CancellationToken.None);

            Assert.Equal(2, result.FrameCount);
            Assert.Equal(output, result.OutputPath);
            Assert.Contains("NETSCAPE2.0", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(output)));

            StorageFile file = await StorageFile.GetFileFromPathAsync(output);
            using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read);
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
            Assert.Equal(2u, decoder.FrameCount);
            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
            byte[] pixels = pixelData.DetachPixelData();
            Assert.Equal(0, pixels[3]);
            Assert.True(pixels[((16 * 32) + 16) * 4 + 3] > 0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Export_EncodesManyColoursAndPreservesExistingOutputWhenCancelled()
    {
        string root = Path.Combine(Path.GetTempPath(), $"InkMotion GIF palette {Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string output = Path.Combine(root, "palette.gif");
        var strokes = Enumerable.Range(0, 128)
            .Select(index =>
            {
                uint colour = 0xFF000000u | (((uint)(index * 2) & 0xFF) << 16) |
                    (((uint)(index * 3) & 0xFF) << 8) | ((uint)(index * 5) & 0xFF);
                return new List<InkPoint>
                {
                    new() { X = index / 127d, Y = 0.05, BrushWidth = 2, BrushColour = colour },
                    new() { X = index / 127d, Y = 0.95, BrushWidth = 2, BrushColour = colour },
                };
            }).ToList();
        var project = new EditorProject { Frames = [new() { Strokes = strokes }] };

        try
        {
            await GifExporter.ExportAsync(project, new(VideoExportProfile.Create(128, 128, 24), 128),
                output, null, CancellationToken.None);
            StorageFile file = await StorageFile.GetFileFromPathAsync(output);
            using (IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read))
            {
                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
                var pixelData = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Straight,
                    new BitmapTransform(),
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.DoNotColorManage);
                Assert.Equal(128u, decoder.PixelWidth);
                Assert.True(pixelData.DetachPixelData().Length > 0);
            }

            string cancelledOutput = Path.Combine(root, "cancelled.gif");
            File.WriteAllText(cancelledOutput, "keep existing output");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GifExporter.ExportAsync(project,
                new(VideoExportProfile.Create(128, 128, 24), 128), cancelledOutput, null, cancellation.Token));
            Assert.Equal("keep existing output", File.ReadAllText(cancelledOutput));
            Assert.Equal(2, Directory.GetFiles(root).Length);
            Assert.Empty(Directory.GetDirectories(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Settings_RespectGifTimingPrecision()
    {
        Assert.Equal((ushort)4, GifExporter.GetFrameDelayCentiseconds(0, 24));
        Assert.Equal((ushort)5, GifExporter.GetFrameDelayCentiseconds(2, 24));
        Assert.Equal((ushort)2, GifExporter.GetFrameDelayCentiseconds(0, 50));
        Assert.Throws<ArgumentOutOfRangeException>(() => GifExporter.GetFrameDelayCentiseconds(0, 51));

        GifExportSettings unsupportedRate = new(VideoExportProfile.Create(32, 32, 60), 32);
        Assert.Throws<ArgumentOutOfRangeException>(unsupportedRate.Validate);
    }
}
