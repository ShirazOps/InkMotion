using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Xunit;
using Xunit.Abstractions;

namespace InkMotion.Tests;

public sealed class VideoPreviewTests(ITestOutputHelper output)
{
    [Fact]
    public void Preview_UploadsOnlyDecodedFramesAndStillSeeksBackwards()
    {
        Assert.True(VideoExportTests.HasH264Encoder, "The preview regression requires the Windows H.264 encoder.");
        string path = Path.Combine(Path.GetTempPath(), $"inkmotion-preview-{Guid.NewGuid():N}.mp4");
        try
        {
            using (var writer = new MediaFoundationVideoWriter(path, 1920, 1080, 24,
                VideoExportCodec.H264, 4_000_000, 0, 0, false))
            {
                byte[] pixels = new byte[1920 * 1080 * 4];
                for (int frame = 0; frame < 24; frame++)
                {
                    for (int index = 0; index < pixels.Length; index += 4)
                    {
                        pixels[index + 2] = (byte)(frame * 10);
                        pixels[index + 3] = 255;
                    }
                    long start = (long)Math.Round(frame * 10_000_000d / 24);
                    long end = (long)Math.Round((frame + 1) * 10_000_000d / 24);
                    writer.WriteVideoFrame(pixels, start, end - start);
                }
                writer.Finish();
            }

            using var device = new CanvasDevice();
            using var source = new TimelineVideoFrameSource(path);
            var stopwatch = Stopwatch.StartNew();
            for (int request = 0; request < 240; request++)
                Assert.NotNull(source.GetFrame(device, request / 240d));
            stopwatch.Stop();
            output.WriteLine($"1080p/24: 240 requests, {source.BitmapUploadCount} uploads, " +
                $"{source.BitmapUploadCount * 1920L * 1080 * 4:N0} uploaded bytes, {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
            Assert.InRange(source.BitmapUploadCount, 24, 25);
            CanvasBitmap last = Assert.IsType<CanvasBitmap>(source.GetFrame(device, 23 / 24d));
            Assert.InRange(last.GetPixelBytes()[2], (byte)210, (byte)245);
            CanvasBitmap first = Assert.IsType<CanvasBitmap>(source.GetFrame(device, 0));
            Assert.InRange(first.GetPixelBytes()[2], (byte)0, (byte)15);
            Assert.Equal(255, first.GetPixelBytes()[3]);
            int uploads = source.BitmapUploadCount;
            Assert.Same(first, source.GetFrame(device, 0));
            Assert.Equal(uploads, source.BitmapUploadCount);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
