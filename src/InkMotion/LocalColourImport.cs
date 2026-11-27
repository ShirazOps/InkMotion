using System.Buffers.Binary;

namespace InkMotion;

public sealed record PreparedColourStill(string Path, string Description);

public static class LocalColourImport
{
    public const string AcesConfig = "ocio://cg-config-v2.2.0_aces-v1.3_ocio-v2.4";
    public const string AcesDisplay = "sRGB - Display";
    public const string AcesView = "ACES 1.0 - SDR Video";
    public const long MaximumPixels = 64 * 1024 * 1024;

    public static PreparedColourStill PrepareAces(string sourcePath, string executable, string directory,
        string inputSpace, CancellationToken cancellationToken)
    {
        if (inputSpace is not ("ACEScg" or "ACES2065-1"))
            throw new ArgumentException("Choose ACEScg or ACES2065-1 explicitly; the input space is not inferred.", nameof(inputSpace));
        if (!string.Equals(System.IO.Path.GetExtension(sourcePath), ".exr", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ACES import accepts single RGB/RGBA OpenEXR stills only.");
        ValidateExecutable(executable, "an OpenColorIO-enabled OpenImageIO oiiotool.exe");
        using (var stream = File.OpenRead(sourcePath))
        {
            Span<byte> header = stackalloc byte[8];
            stream.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 20000630 ||
                (BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) & 0x1800) != 0)
                throw new InvalidDataException("Choose a genuine, single-part, non-deep RGB/RGBA OpenEXR still.");
        }
        string help = RunTool(executable, ["--help"], cancellationToken);
        if (!help.Contains("--ociodisplay", StringComparison.Ordinal) || !help.Contains("--colorconfig", StringComparison.Ordinal))
            throw new InvalidOperationException("The selected tool does not expose OpenImageIO's OCIO display conversion.");
        return Prepare(sourcePath, directory, ".png", (input, output) =>
        {
            RunTool(executable, ["--colorconfig", AcesConfig, input,
                $"--ociodisplay:from={inputSpace}:unpremult=1", AcesDisplay, AcesView,
                "--attrib", "oiio:ColorSpace", "sRGB", "-d", "uint16", "-o", output], cancellationToken);
            ValidatePng(output);
        }, $"ACES 1.3 display import: {System.IO.Path.GetFileName(sourcePath)}; {inputSpace} -> {AcesDisplay} / {AcesView}. " +
            "16-bit PNG editing copy; SDR timeline, not scene-linear ACES grading.", cancellationToken);
    }

    private static PreparedColourStill Prepare(string sourcePath, string directory, string extension,
        Action<string, string> convert, string description, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = new FileInfo(sourcePath);
        long length = source.Length;
        long modified = source.LastWriteTimeUtc.Ticks;
        if (length == 0 || length > 2L * 1024 * 1024 * 1024)
            throw new InvalidDataException("Colour imports require a non-empty source no larger than 2 GiB.");
        Directory.CreateDirectory(directory);
        string id = Guid.NewGuid().ToString("N");
        string input = System.IO.Path.Combine(directory, $".colour-{id}{source.Extension}");
        string name = System.IO.Path.GetFileNameWithoutExtension(sourcePath);
        if (name.Length > 40) name = name[..40];
        string output = System.IO.Path.Combine(directory, $"{name}-{id}{extension}");
        bool success = false;
        try
        {
            File.Copy(source.FullName, input);
            cancellationToken.ThrowIfCancellationRequested();
            convert(input, output);
            cancellationToken.ThrowIfCancellationRequested();
            source.Refresh();
            if (source.Length != length || source.LastWriteTimeUtc.Ticks != modified)
                throw new IOException("The source changed during conversion. Import it again.");
            if (!File.Exists(output) || new FileInfo(output).Length == 0)
                throw new InvalidDataException("The colour tool did not produce an editing copy.");
            success = true;
            return new(output, description);
        }
        finally
        {
            File.Delete(input);
            if (!success) File.Delete(output);
        }
    }

    internal static void ValidatePng(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[24];
        stream.ReadExactly(header);
        if (!header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            !header[12..16].SequenceEqual("IHDR"u8))
            throw new InvalidDataException("The colour tool did not produce a genuine PNG image.");
        uint width = BinaryPrimitives.ReadUInt32BigEndian(header[16..20]);
        uint height = BinaryPrimitives.ReadUInt32BigEndian(header[20..24]);
        if (width == 0 || height == 0 || (long)width * height > MaximumPixels)
            throw new InvalidDataException("The editing copy must contain between 1 and 64 megapixels (binary units).");
    }

    private static void ValidateExecutable(string executable, string name)
    {
        if (string.IsNullOrWhiteSpace(executable) || !System.IO.Path.IsPathFullyQualified(executable) ||
            !File.Exists(executable) || !string.Equals(System.IO.Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException($"Select an existing local {name}. Colour tools are optional and are not bundled or downloaded.", executable);
    }

    internal static string RunTool(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = FFmpegAudioEncoder.Start(executable, arguments);
        using var registration = cancellationToken.Register(() => FFmpegAudioEncoder.Terminate(process));
        Task<string> output = FFmpegAudioEncoder.ReadTailAsync(process.StandardOutput);
        Task<string> errors = FFmpegAudioEncoder.ReadTailAsync(process.StandardError);
        process.WaitForExit();
        cancellationToken.ThrowIfCancellationRequested();
        string standardOutput = output.GetAwaiter().GetResult();
        string diagnostic = errors.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{System.IO.Path.GetFileName(executable)} failed (exit {process.ExitCode}): " +
                $"{diagnostic}\n{standardOutput}");
        return standardOutput;
    }
}
