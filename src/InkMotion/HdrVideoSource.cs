using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace InkMotion;

public sealed record HdrVideoSourceInfo(int Width, int Height, double FrameRate, string FrameRateText, VideoSourceTransfer Transfer);

/// <summary>How an HDR source clip is decoded for an HDR project.</summary>
/// <param name="WorkingFloat">True for export (half-float working space); false for tone-mapped 8-bit preview.</param>
internal sealed record HdrDecodeOptions(string FFmpegPath, HdrVideoSourceInfo Source, ProjectColourSettings Settings, bool WorkingFloat);

/// <summary>Reads a video stream's transfer characteristic from FFmpeg's stream summary.</summary>
public static partial class HdrVideoProbe
{
    private static readonly ConcurrentDictionary<string, (long Length, long Ticks, string Tool, HdrVideoSourceInfo? Info)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static HdrVideoSourceInfo? Parse(string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        foreach (string line in diagnostic.Split('\n'))
        {
            if (!line.Contains("Stream #", StringComparison.Ordinal) || !line.Contains(": Video:", StringComparison.Ordinal) ||
                line.Contains("attached pic", StringComparison.Ordinal))
            {
                continue;
            }

            Match size = SizePattern().Match(line);
            Match rate = FpsPattern().Match(line);
            if (!rate.Success) rate = TbrPattern().Match(line);
            if (!size.Success || !rate.Success ||
                !double.TryParse(rate.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double fps) ||
                !double.IsFinite(fps) || fps is <= 0 or > 1000)
            {
                return null;
            }

            VideoSourceTransfer transfer = line.Contains("smpte2084", StringComparison.Ordinal) ? VideoSourceTransfer.Pq
                : line.Contains("arib-std-b67", StringComparison.Ordinal) ? VideoSourceTransfer.Hlg
                : VideoSourceTransfer.Sdr;
            return new(int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(size.Groups[2].Value, CultureInfo.InvariantCulture), fps, rate.Groups[1].Value, transfer);
        }
        return null;
    }

    /// <summary>Probes once per file version and FFmpeg executable; returns null when the file cannot be read.</summary>
    public static HdrVideoSourceInfo? Probe(string ffmpegPath, string sourcePath)
    {
        var file = new FileInfo(sourcePath);
        if (!file.Exists || !File.Exists(ffmpegPath)) return null;
        if (Cache.TryGetValue(file.FullName, out var cached) && cached.Length == file.Length &&
            cached.Ticks == file.LastWriteTimeUtc.Ticks && string.Equals(cached.Tool, ffmpegPath, StringComparison.OrdinalIgnoreCase))
        {
            return cached.Info;
        }

        HdrVideoSourceInfo? info = null;
        try
        {
            using Process process = FFmpegAudioEncoder.Start(ffmpegPath, ["-hide_banner", "-nostdin", "-i", file.FullName]);
            Task<string> errors = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(15000))
            {
                FFmpegAudioEncoder.Terminate(process);
                return null;
            }
            info = Parse(errors.GetAwaiter().GetResult());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Trace.TraceWarning($"Could not probe {file.Name} for HDR: {ex.Message}");
        }
        Cache[file.FullName] = (file.Length, file.LastWriteTimeUtc.Ticks, ffmpegPath, info);
        return info;
    }

    /// <summary>Returns HDR decoding options only for an HDR project, a usable FFmpeg and a PQ/HLG source.</summary>
    internal static HdrDecodeOptions? GetDecodeOptions(ProjectColourSettings? colour, string? ffmpegPath, string sourcePath,
        bool workingFloat)
    {
        if (colour is null || string.IsNullOrWhiteSpace(ffmpegPath) || !Path.IsPathFullyQualified(ffmpegPath) ||
            string.IsNullOrWhiteSpace(sourcePath))
        {
            return null;
        }
        return Probe(ffmpegPath, sourcePath) is { Transfer: not VideoSourceTransfer.Sdr } info
            ? new(ffmpegPath, info, colour, workingFloat)
            : null;
    }

    [GeneratedRegex(@", (\d{2,5})x(\d{2,5})\b")]
    private static partial Regex SizePattern();

    [GeneratedRegex(@"([\d.]+) fps\b")]
    private static partial Regex FpsPattern();

    [GeneratedRegex(@"([\d.]+) tbr\b")]
    private static partial Regex TbrPattern();
}

/// <summary>
/// Decodes PQ/HLG video through FFmpeg as 16-bit Rec.2020 RGB, then converts frames to the project working space.
/// Frames are read sequentially; backward seeks and long jumps restart the decoder at the requested time.
/// </summary>
internal sealed class FFmpegHdrFrameReader : IDisposable
{
    private readonly HdrDecodeOptions _options;
    private readonly string _sourcePath;
    private readonly byte[] _raw;
    private Process? _process;
    private Task<string>? _errors;
    private long _nextIndex;
    private long _currentIndex = -1;
    private bool _ended;

    public FFmpegHdrFrameReader(string sourcePath, HdrDecodeOptions options)
    {
        _sourcePath = sourcePath;
        _options = options;
        Width = options.Source.Width;
        Height = options.Source.Height;
        _raw = new byte[checked(Width * Height * 6)];
        Pixels = new byte[checked(Width * Height * (options.WorkingFloat ? 8 : 4))];
    }

    public int Width { get; }
    public int Height { get; }
    /// <summary>RGBA half-float working pixels when decoding for export; otherwise tone-mapped BGRA.</summary>
    public byte[] Pixels { get; }
    public long FrameVersion { get; private set; }

    public bool TryGetFrame(double sourceSeconds)
    {
        double fps = _options.Source.FrameRate;
        long index = Math.Max(0, (long)Math.Floor(sourceSeconds * fps + 1e-6));
        if (index == _currentIndex) return true;
        long maximumSkip = Math.Max(8, (long)Math.Ceiling(fps * 2));
        if (_process is null || index < _nextIndex || index - _nextIndex > maximumSkip ||
            (_ended && index >= _nextIndex))
        {
            if (_ended && _currentIndex >= 0 && index >= _nextIndex) return true;
            Restart(index);
        }

        bool read = false;
        while (_nextIndex <= index)
        {
            if (!ReadRaw())
            {
                _ended = true;
                break;
            }
            read = true;
            _nextIndex++;
        }
        if (!read) return _currentIndex >= 0;

        if (_options.WorkingFloat)
            HdrColour.DecodeToWorking(_raw, Pixels, _options.Source.Transfer, _options.Settings);
        else
            HdrColour.DecodeToPreview(_raw, Pixels, _options.Source.Transfer, _options.Settings);
        _currentIndex = _nextIndex - 1;
        FrameVersion++;
        return true;
    }

    private void Restart(long index)
    {
        Stop();
        string start = (index / _options.Source.FrameRate).ToString("0.######", CultureInfo.InvariantCulture);
        _process = FFmpegAudioEncoder.Start(_options.FFmpegPath,
        [
            "-hide_banner", "-loglevel", "error", "-nostdin", "-ss", start, "-i", _sourcePath,
            "-map", "0:v:0", "-an", "-sn", "-dn",
            "-vf", $"fps={_options.Source.FrameRateText},scale=in_color_matrix=bt2020:out_color_matrix=bt2020,format=rgb48le",
            "-f", "rawvideo", "pipe:1",
        ]);
        _errors = FFmpegAudioEncoder.ReadTailAsync(_process.StandardError);
        _nextIndex = index;
        _ended = false;
    }

    private bool ReadRaw()
    {
        Stream output = _process!.StandardOutput.BaseStream;
        int filled = 0;
        while (filled < _raw.Length)
        {
            int read = output.Read(_raw, filled, _raw.Length - filled);
            if (read == 0)
            {
                if (filled == 0 && _currentIndex < 0)
                {
                    _process.WaitForExit(2000);
                    string diagnostic = _errors?.Wait(2000) == true ? _errors.Result : string.Empty;
                    if (!string.IsNullOrWhiteSpace(diagnostic))
                        Trace.TraceWarning($"FFmpeg HDR decode of {Path.GetFileName(_sourcePath)}: {diagnostic}");
                }
                return false;
            }
            filled += read;
        }
        return true;
    }

    private void Stop()
    {
        if (_process is null) return;
        FFmpegAudioEncoder.Terminate(_process);
        _process.Dispose();
        _process = null;
        _errors = null;
    }

    public void Dispose() => Stop();
}
