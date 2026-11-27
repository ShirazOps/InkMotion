using System.Text;
using Microsoft.Graphics.Canvas;
using Xunit;

namespace InkMotion.Tests;

public sealed class LibRawImportFactAttribute : FactAttribute
{
    public LibRawImportFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("INKMOTION_TEST_LIBRAW")))
            Skip = "Optional sensor-decode test: set INKMOTION_TEST_LIBRAW to local LibRaw dcraw_emu.exe.";
    }
}

public sealed class RawStillImportTests
{
    [Fact]
    public void ReportsMissingDecoderAndRejectsMotionRawAndInvalidExposure()
    {
        Assert.Throws<FileNotFoundException>(() => LocalColourImport.PrepareRaw("source.dng",
            @"C:\missing-inkmotion-libraw.exe", Path.GetTempPath(), true, 0, CancellationToken.None));
        Assert.Throws<InvalidDataException>(() => LocalColourImport.PrepareRaw("source.braw",
            @"C:\missing-inkmotion-libraw.exe", Path.GetTempPath(), true, 0, CancellationToken.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => LocalColourImport.PrepareRaw("source.dng",
            @"C:\missing-inkmotion-libraw.exe", Path.GetTempPath(), true, double.NaN, CancellationToken.None));
    }

    [LibRawImportFact]
    public async Task RealLibRawDemosaicsSensorOnlyDngAndExposureChangesTheEditingCopy()
    {
        string tool = Environment.GetEnvironmentVariable("INKMOTION_TEST_LIBRAW")!;
        string directory = Path.Combine(Path.GetTempPath(), $"inkmotion-raw-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "synthetic sensor with spaces.dng");
        try
        {
            WriteSensorDng(source);
            byte[] original = File.ReadAllBytes(source);
            PreparedColourStill neutral = LocalColourImport.PrepareRaw(source, tool, directory, true, 0, CancellationToken.None);
            PreparedColourStill brighter = LocalColourImport.PrepareRaw(source, tool, directory, false, 1, CancellationToken.None);
            using var device = new CanvasDevice(true);
            using var neutralBitmap = await CanvasBitmap.LoadAsync(device, neutral.Path);
            using var brighterBitmap = await CanvasBitmap.LoadAsync(device, brighter.Path);
            Assert.Equal(64u, neutralBitmap.SizeInPixels.Width);
            Assert.Equal(64u, neutralBitmap.SizeInPixels.Height);
            byte[] pixels = neutralBitmap.GetPixelBytes();
            byte[] brightPixels = brighterBitmap.GetPixelBytes();
            int centre = (32 * 64 + 32) * 4;
            Assert.True(pixels[centre] > 0);
            Assert.True(brightPixels[centre + 1] > pixels[centre + 1] + 10);
            Assert.All(pixels.Where((_, i) => i % 4 == 3), alpha => Assert.Equal(255, alpha));
            Assert.Equal(original, File.ReadAllBytes(source));
            Assert.Contains("AHD demosaic", neutral.Description);
            Assert.Equal(3, Directory.GetFiles(directory).Length);
        }
        finally
        {
            foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    [LibRawImportFact]
    public void RealLibRawReportsCorruptInputAndCancellationWithoutLeavingCopies()
    {
        string tool = Environment.GetEnvironmentVariable("INKMOTION_TEST_LIBRAW")!;
        string directory = Path.Combine(Path.GetTempPath(), $"inkmotion-raw-failure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "corrupt.dng");
        try
        {
            File.WriteAllBytes(source, new byte[128]);
            Exception? error = Record.Exception(() => LocalColourImport.PrepareRaw(source, tool, directory, false, 0, CancellationToken.None));
            Assert.NotNull(error);
            Assert.True(error is InvalidOperationException or IOException, error.ToString());
            Assert.Single(Directory.GetFiles(directory));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => LocalColourImport.PrepareRaw(
                source, tool, directory, false, 0, cancelled.Token));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally
        {
            File.Delete(source);
            Directory.Delete(directory);
        }
    }

    // A single uncompressed RGGB sensor IFD, with no preview/thumbnail IFD to fall back to.
    private static void WriteSensorDng(string path)
    {
        var tags = new SortedDictionary<ushort, (ushort Type, uint Count, byte[] Data)>();
        void Short(ushort tag, params ushort[] values) =>
            tags.Add(tag, (3, (uint)values.Length, values.SelectMany(BitConverter.GetBytes).ToArray()));
        void Long(ushort tag, params uint[] values) =>
            tags.Add(tag, (4, (uint)values.Length, values.SelectMany(BitConverter.GetBytes).ToArray()));
        void Bytes(ushort tag, params byte[] values) => tags.Add(tag, (1, (uint)values.Length, values));
        void Text(ushort tag, string text)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text + "\0");
            tags.Add(tag, (2, (uint)bytes.Length, bytes));
        }
        Long(254, 0); Long(256, 64); Long(257, 64); Short(258, 16); Short(259, 1);
        Short(262, 32803); Text(271, "InkMotion"); Text(272, "Synthetic Bayer");
        Long(273, 0); Short(274, 1); Short(277, 1); Long(278, 64); Long(279, 64 * 64 * 2);
        Short(284, 1); Short(33421, 2, 2); Bytes(33422, 0, 1, 1, 2);
        Bytes(50706, 1, 4, 0, 0); Bytes(50707, 1, 1, 0, 0); Text(50708, "InkMotion synthetic RGGB");
        Short(50714, 0); Long(50717, 65535); Long(50719, 0, 0); Long(50720, 64, 64);
        int[] matrix = [1, 1, 0, 1, 0, 1, 0, 1, 1, 1, 0, 1, 0, 1, 0, 1, 1, 1];
        tags.Add(50721, (10, 9, matrix.SelectMany(BitConverter.GetBytes).ToArray()));
        uint[] neutral = [1, 1, 1, 1, 1, 1];
        tags.Add(50728, (5, 3, neutral.SelectMany(BitConverter.GetBytes).ToArray()));
        Short(50778, 21); Long(50829, 0, 0, 64, 64);
        int payloadStart = 8 + 2 + tags.Count * 12 + 4;
        uint pixelStart = (uint)(payloadStart + tags.Values.Where(t => t.Data.Length > 4).Sum(t => t.Data.Length));
        tags[273] = (4, 1, BitConverter.GetBytes(pixelStart));
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write((ushort)0x4949); writer.Write((ushort)42); writer.Write(8u);
        writer.Write((ushort)tags.Count);
        int offset = payloadStart;
        foreach (var (tag, value) in tags)
        {
            writer.Write(tag); writer.Write(value.Type); writer.Write(value.Count);
            if (value.Data.Length <= 4)
            {
                writer.Write(value.Data);
                writer.Write(new byte[4 - value.Data.Length]);
            }
            else
            {
                writer.Write((uint)offset);
                offset += value.Data.Length;
            }
        }
        writer.Write(0u);
        foreach (var value in tags.Values.Where(t => t.Data.Length > 4)) writer.Write(value.Data);
        for (int y = 0; y < 64; y++)
        for (int x = 0; x < 64; x++)
            writer.Write((ushort)(2000 + (x / 2) * 800 + (y / 2) * 200));
    }
}
