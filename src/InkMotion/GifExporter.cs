using System.Buffers.Binary;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace InkMotion;

public sealed record GifExportSettings(VideoExportProfile Profile, double InkReferenceHeight)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Profile);
        if (!double.IsFinite(InkReferenceHeight) || InkReferenceHeight < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(InkReferenceHeight));
        }

        if (Profile.FrameRate is < 24 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(Profile), "GIF export supports frame rates from 24 to 50 fps.");
        }

        VideoExportProfile.Create(Profile.Width, Profile.Height, Profile.FrameRate);
    }
}

public sealed record GifExportResult(
    string OutputPath,
    int FrameCount,
    double DurationSeconds,
    IReadOnlyList<string> Warnings);

public static class GifExporter
{
    private static readonly byte[] LoopExtension =
        [0x21, 0xFF, 0x0B, .. "NETSCAPE2.0"u8, 0x03, 0x01, 0x00, 0x00, 0x00];

    public static async Task<GifExportResult> ExportAsync(
        EditorProject project,
        GifExportSettings settings,
        string outputPath,
        IProgress<VideoExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (!ExportPlanning.HasInk(project))
        {
            throw new InvalidOperationException("Nothing to export: add ink, cel text or a connected scene or particle node first.");
        }

        double duration = ExportPlanning.GetInkDurationSeconds(project);
        int frameCount = ExportPlanning.GetFrameCount(duration, settings.Profile.FrameRate);
        if (frameCount == 0)
        {
            throw new InvalidOperationException("Nothing to export: add ink, cel text or a connected scene or particle node first.");
        }

        string fullOutputPath = Path.GetFullPath(outputPath);
        ExportPlanning.ValidateOutputPath(project, fullOutputPath);
        string parentPath = Path.GetDirectoryName(fullOutputPath) ??
            throw new ArgumentException("Choose a GIF file inside an existing directory.", nameof(outputPath));
        string fileName = Path.GetFileName(fullOutputPath);
        if (string.IsNullOrWhiteSpace(fileName) ||
            !string.Equals(Path.GetExtension(fileName), ".gif", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Choose a GIF file with the .gif extension.", nameof(outputPath));
        }

        string stagingName = $".{fileName}.{Guid.NewGuid():N}.inkmotion";
        string stagingDirectory = Path.Combine(parentPath, stagingName);
        string stagingGif = Path.Combine(parentPath, $"{stagingName}.gif");
        bool stagingDirectoryCreated = false;
        bool stagingGifCreated = false;
        try
        {
            StorageFolder parent = await StorageFolder.GetFolderFromPathAsync(parentPath);
            StorageFolder framesDirectory = await parent.CreateFolderAsync(stagingName, CreationCollisionOption.FailIfExists);
            stagingDirectoryCreated = true;
            StorageFile gifFile = await parent.CreateFileAsync($"{stagingName}.gif", CreationCollisionOption.FailIfExists);
            stagingGifCreated = true;

            var request = new VideoExportRequest(project, settings.Profile, VideoExportCodec.H264,
                stagingGif, settings.InkReferenceHeight, PreferHardware: false);
            using var device = new CanvasDevice();
            var warnings = new List<string>();
            using (var renderer = new VideoExporter.FrameRenderer(device, request, warnings, inkOnly: true))
            {
                for (int frame = 0; frame < frameCount; frame++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    StorageFile png = await framesDirectory.CreateFileAsync($"frame_{frame + 1:D6}.png",
                        CreationCollisionOption.FailIfExists);
                    await renderer.SaveInkFrameAsync(frame, png);
                    progress?.Report(new VideoExportProgress(frame + 1, frameCount, "Rendering GIF frames"));
                }
            }

            var histogram = new long[4096];
            for (int frame = 0; frame < frameCount; frame++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                StorageFile png = await framesDirectory.GetFileAsync($"frame_{frame + 1:D6}.png");
                byte[] pixels = await DecodePngAsync(png);
                AddToHistogram(pixels, histogram, cancellationToken);
            }

            (RgbColour[] palette, byte[] paletteLookup) = BuildPalette(histogram);
            using (var output = new BinaryWriter(new FileStream(stagingGif, FileMode.Create, FileAccess.Write, FileShare.None)))
            {
                WriteHeader(output, settings.Profile.Width, settings.Profile.Height, palette);
                output.Write(LoopExtension);
                for (int frame = 0; frame < frameCount; frame++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    StorageFile png = await framesDirectory.GetFileAsync($"frame_{frame + 1:D6}.png");
                    byte[] pixels = await DecodePngAsync(png);
                    WriteFrame(output, pixels, settings.Profile.Width, settings.Profile.Height,
                        GetFrameDelayCentiseconds(frame, settings.Profile.FrameRate), paletteLookup, cancellationToken);
                    if (frame % 5 == 0 || frame == frameCount - 1)
                    {
                        progress?.Report(new VideoExportProgress(frame + 1, frameCount, "Encoding animated GIF"));
                    }
                }

                output.Write((byte)0x3B);
                output.Flush();
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.Delete(stagingDirectory, recursive: true);
            stagingDirectoryCreated = false;
            File.Move(stagingGif, fullOutputPath, overwrite: true);
            stagingGifCreated = false;
            return new GifExportResult(fullOutputPath, frameCount, duration, warnings);
        }
        catch (Exception ex)
        {
            List<string> cleanupFailures = Cleanup(stagingDirectory, stagingDirectoryCreated,
                stagingGif, stagingGifCreated);
            if (cleanupFailures.Count > 0)
            {
                throw new IOException(
                    $"GIF export failed, and partial output cleanup was incomplete: {string.Join("; ", cleanupFailures)}",
                    ex);
            }

            throw;
        }
    }

    public static ushort GetFrameDelayCentiseconds(int frame, double frameRate)
    {
        if (frame < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frame));
        }

        if (!double.IsFinite(frameRate) || frameRate is < 24 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(frameRate));
        }

        long start = (long)Math.Round(frame * 100d / frameRate, MidpointRounding.AwayFromZero);
        long end = (long)Math.Round((frame + 1d) * 100 / frameRate, MidpointRounding.AwayFromZero);
        return checked((ushort)Math.Max(2, end - start));
    }

    private static async Task<byte[]> DecodePngAsync(StorageFile file)
    {
        using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        return pixelData.DetachPixelData();
    }

    private static void AddToHistogram(byte[] pixels, long[] histogram, CancellationToken cancellationToken)
    {
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            if ((offset & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            byte alpha = pixels[offset + 3];
            if (alpha < 128)
            {
                continue;
            }

            byte blue = Unpremultiply(pixels[offset], alpha);
            byte green = Unpremultiply(pixels[offset + 1], alpha);
            byte red = Unpremultiply(pixels[offset + 2], alpha);
            histogram[(red >> 4 << 8) | (green >> 4 << 4) | (blue >> 4)]++;
        }
    }

    private static (RgbColour[] Palette, byte[] Lookup) BuildPalette(long[] histogram)
    {
        List<PaletteColour> colours = [];
        for (int bin = 0; bin < histogram.Length; bin++)
        {
            if (histogram[bin] == 0)
            {
                continue;
            }

            colours.Add(new PaletteColour(
                (byte)(((bin >> 8) & 0xF) * 17),
                (byte)(((bin >> 4) & 0xF) * 17),
                (byte)((bin & 0xF) * 17),
                histogram[bin]));
        }

        List<List<PaletteColour>> boxes = colours.Count == 0 ? [] : [colours];
        while (boxes.Count < 255)
        {
            int splitIndex = -1;
            int splitRange = 0;
            for (int index = 0; index < boxes.Count; index++)
            {
                if (boxes[index].Count < 2)
                {
                    continue;
                }

                int range = GetColourRange(boxes[index]);
                if (range > splitRange)
                {
                    splitRange = range;
                    splitIndex = index;
                }
            }

            if (splitIndex < 0)
            {
                break;
            }

            List<PaletteColour> box = boxes[splitIndex];
            int channel = WidestChannel(box);
            box.Sort((left, right) => Component(left, channel).CompareTo(Component(right, channel)));
            long totalWeight = box.Sum(colour => colour.Count);
            long halfWeight = (totalWeight + 1) / 2;
            long accumulated = 0;
            int middle = 1;
            for (; middle < box.Count; middle++)
            {
                accumulated += box[middle - 1].Count;
                if (accumulated >= halfWeight)
                {
                    break;
                }
            }

            middle = Math.Min(middle, box.Count - 1);
            boxes[splitIndex] = box.GetRange(0, middle);
            boxes.Add(box.GetRange(middle, box.Count - middle));
        }

        var palette = new RgbColour[256];
        for (int index = 0; index < boxes.Count; index++)
        {
            List<PaletteColour> box = boxes[index];
            long weight = box.Sum(colour => (long)colour.Count);
            palette[index + 1] = new RgbColour(
                (byte)(box.Sum(colour => (long)colour.Red * colour.Count) / weight),
                (byte)(box.Sum(colour => (long)colour.Green * colour.Count) / weight),
                (byte)(box.Sum(colour => (long)colour.Blue * colour.Count) / weight));
        }

        var lookup = new byte[4096];
        for (int bin = 0; bin < lookup.Length; bin++)
        {
            if (histogram[bin] == 0)
            {
                continue;
            }

            byte red = (byte)(((bin >> 8) & 0xF) * 17);
            byte green = (byte)(((bin >> 4) & 0xF) * 17);
            byte blue = (byte)((bin & 0xF) * 17);
            int bestDistance = int.MaxValue;
            for (int index = 1; index <= boxes.Count; index++)
            {
                RgbColour colour = palette[index];
                int redDistance = red - colour.Red;
                int greenDistance = green - colour.Green;
                int blueDistance = blue - colour.Blue;
                int distance = redDistance * redDistance + greenDistance * greenDistance + blueDistance * blueDistance;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    lookup[bin] = (byte)index;
                }
            }
        }

        return (palette, lookup);
    }

    private static int GetColourRange(List<PaletteColour> box)
    {
        int red = ComponentRange(box, 0);
        int green = ComponentRange(box, 1);
        int blue = ComponentRange(box, 2);
        return Math.Max(red, Math.Max(green, blue));
    }

    private static int WidestChannel(List<PaletteColour> box)
    {
        int red = ComponentRange(box, 0);
        int green = ComponentRange(box, 1);
        int blue = ComponentRange(box, 2);
        return red >= green && red >= blue ? 0 : green >= blue ? 1 : 2;
    }

    private static int ComponentRange(List<PaletteColour> box, int channel)
    {
        int minimum = 255;
        int maximum = 0;
        foreach (PaletteColour colour in box)
        {
            int component = Component(colour, channel);
            minimum = Math.Min(minimum, component);
            maximum = Math.Max(maximum, component);
        }

        return maximum - minimum;
    }

    private static int Component(PaletteColour colour, int channel) => channel switch
    {
        0 => colour.Red,
        1 => colour.Green,
        _ => colour.Blue,
    };

    private static void WriteHeader(BinaryWriter output, int width, int height, RgbColour[] palette)
    {
        output.Write("GIF89a"u8);
        WriteUInt16(output, width);
        WriteUInt16(output, height);
        output.Write((byte)0xF7);
        output.Write((byte)0);
        output.Write((byte)0);
        foreach (RgbColour colour in palette)
        {
            output.Write(colour.Red);
            output.Write(colour.Green);
            output.Write(colour.Blue);
        }
    }

    private static void WriteFrame(
        BinaryWriter output,
        byte[] pixels,
        int width,
        int height,
        ushort delay,
        byte[] paletteLookup,
        CancellationToken cancellationToken)
    {
        output.Write((byte)0x21);
        output.Write((byte)0xF9);
        output.Write((byte)0x04);
        output.Write((byte)0x09);
        WriteUInt16(output, delay);
        output.Write((byte)0);
        output.Write((byte)0);
        output.Write((byte)0x2C);
        WriteUInt16(output, 0);
        WriteUInt16(output, 0);
        WriteUInt16(output, width);
        WriteUInt16(output, height);
        output.Write((byte)0);
        output.Write((byte)8);

        var blocks = new GifSubBlockWriter(output);
        var encoder = new GifLzwEncoder(blocks.WriteByte);
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            if ((offset & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            byte alpha = pixels[offset + 3];
            if (alpha < 128)
            {
                encoder.WriteIndex(0);
            }
            else
            {
                byte blue = Unpremultiply(pixels[offset], alpha);
                byte green = Unpremultiply(pixels[offset + 1], alpha);
                byte red = Unpremultiply(pixels[offset + 2], alpha);
                int bin = (red >> 4 << 8) | (green >> 4 << 4) | (blue >> 4);
                encoder.WriteIndex(paletteLookup[bin]);
            }
        }

        encoder.Finish();
        blocks.Finish();
    }

    private static byte Unpremultiply(byte colour, byte alpha) =>
        (byte)Math.Min(255, (colour * 255 + alpha / 2) / alpha);

    private static void WriteUInt16(BinaryWriter output, int value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, checked((ushort)value));
        output.Write(bytes);
    }

    private static List<string> Cleanup(
        string stagingDirectory,
        bool stagingDirectoryCreated,
        string stagingGif,
        bool stagingGifCreated)
    {
        var failures = new List<string>();
        if (stagingDirectoryCreated)
        {
            try
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{Path.GetFileName(stagingDirectory)}: {ex.Message}");
            }
        }

        if (stagingGifCreated)
        {
            try
            {
                File.Delete(stagingGif);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{Path.GetFileName(stagingGif)}: {ex.Message}");
            }
        }

        return failures;
    }

    private sealed record PaletteColour(byte Red, byte Green, byte Blue, long Count);
    private readonly record struct RgbColour(byte Red, byte Green, byte Blue);

    private sealed class GifSubBlockWriter(BinaryWriter output)
    {
        private readonly byte[] _buffer = new byte[255];
        private int _length;

        public void WriteByte(byte value)
        {
            _buffer[_length++] = value;
            if (_length == _buffer.Length)
            {
                FlushBlock();
            }
        }

        public void Finish()
        {
            FlushBlock();
            output.Write((byte)0);
        }

        private void FlushBlock()
        {
            if (_length == 0)
            {
                return;
            }

            output.Write((byte)_length);
            output.Write(_buffer, 0, _length);
            _length = 0;
        }
    }

    private sealed class GifLzwEncoder(Action<byte> writeByte)
    {
        private readonly Dictionary<int, int> _dictionary = new();
        private int _codeSize = 9;
        private int _nextCode = 258;
        private int _bitBuffer;
        private int _bitCount;
        private int _prefix = -1;

        public void WriteIndex(byte index)
        {
            if (_prefix < 0)
            {
                _prefix = index;
                WriteCode(256);
                return;
            }

            int key = (_prefix << 8) | index;
            if (_dictionary.TryGetValue(key, out int code))
            {
                _prefix = code;
                return;
            }

            WriteCode(_prefix);
            if (_nextCode < 4096)
            {
                _dictionary[key] = _nextCode++;
                if (_nextCode == 1 << _codeSize && _codeSize < 12)
                {
                    _codeSize++;
                }
            }
            else
            {
                WriteCode(256);
                _dictionary.Clear();
                _codeSize = 9;
                _nextCode = 258;
            }

            _prefix = index;
        }

        public void Finish()
        {
            if (_prefix >= 0)
            {
                WriteCode(_prefix);
                WriteCode(257);
                if (_bitCount > 0)
                {
                    writeByte((byte)_bitBuffer);
                    _bitBuffer = 0;
                    _bitCount = 0;
                }
            }
        }

        private void WriteCode(int code)
        {
            _bitBuffer |= code << _bitCount;
            _bitCount += _codeSize;
            while (_bitCount >= 8)
            {
                writeByte((byte)_bitBuffer);
                _bitBuffer >>= 8;
                _bitCount -= 8;
            }
        }
    }
}
