using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace InkMotion;

/// <summary>
/// The measurements HDR10+ metadata is derived from for one rendered frame. A second render of the same frame
/// must reproduce it exactly, otherwise the metadata would not describe the encoded pictures.
/// </summary>
internal readonly record struct Hdr10PlusFrameSignature(ushort MaxRed, ushort MaxGreen, ushort MaxBlue,
    long MaxRgbCodeSum, ulong HistogramHash);

/// <summary>
/// Measured static light levels in cd/m² (CTA-861.3 MaxCLL and MaxFALL) of the analysed frames. As CTA-861.3
/// Annex A specifies, both use each pixel's brightest linear component, maxRGB = max(R, G, B), rather than its
/// luminance: MaxCLL is the highest maxRGB and MaxFALL the highest frame-average maxRGB.
/// </summary>
public readonly record struct Hdr10LightLevel(int MaxCll, int MaxFall);

/// <summary>
/// Generates SMPTE ST 2094-40 (HDR10+) Application 4, version 1, Profile A dynamic metadata from rendered
/// rgb48le PQ Rec.2020 frames, in the LLC JSON layout read by x265's <c>dhdr10-info</c> option.
/// Statistics are exact for the 16-bit signal: maxRGB is measured per pixel from a 65,536-bin code histogram.
/// Scenes split where the PQ maxRGB distribution changes abruptly; every frame of a scene carries that
/// scene's statistics, as the standard describes metadata per scene.
/// </summary>
internal sealed class Hdr10PlusAnalyser
{
    public static readonly int[] DistributionPercentages = [1, 5, 10, 25, 50, 75, 90, 95, 99];
    public const double SceneCutDistance = 0.5;
    private const int Codes = 65536;
    private const int CoarseBins = 64;
    private static readonly double[] CodeNits = CreateCodeNits();

    private readonly int _pixels;
    private readonly List<SceneStatistics> _scenes = [];
    private readonly List<Hdr10PlusFrameSignature> _signatures = [];
    private readonly long[] _sceneHistogram = new long[Codes];
    private double[]? _previousCoarse;
    private SceneStatistics? _current;
    private int[][] _partitionHistograms = [];
    private int _maxCllCode;
    private double _maxFall;

    public Hdr10PlusAnalyser(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        _pixels = checked(width * height);
    }

    public int FrameCount => _signatures.Count;

    public IReadOnlyList<Hdr10PlusFrameSignature> Signatures => _signatures;

    public Hdr10LightLevel LightLevel => new((int)Math.Ceiling(CodeNits[_maxCllCode]), (int)Math.Ceiling(_maxFall));

    /// <summary>Measures one frame without retaining it. Frames must be added in output order.</summary>
    public Hdr10PlusFrameSignature Add(byte[] rgb48)
    {
        FrameStatistics frame = Measure(rgb48, _pixels, ref _partitionHistograms);
        double[] coarse = Coarse(frame.Histogram, _pixels);
        if (_current is null || _previousCoarse is null || Distance(_previousCoarse, coarse) > SceneCutDistance)
            StartScene();
        _previousCoarse = coarse;
        SceneStatistics scene = _current!;
        scene.Frames++;
        scene.MaxRed = Math.Max(scene.MaxRed, frame.Signature.MaxRed);
        scene.MaxGreen = Math.Max(scene.MaxGreen, frame.Signature.MaxGreen);
        scene.MaxBlue = Math.Max(scene.MaxBlue, frame.Signature.MaxBlue);
        scene.LinearSum += frame.LinearMaxRgbSum;
        for (int code = 0; code < Codes; code++)
            _sceneHistogram[code] += frame.Histogram[code];
        _maxCllCode = Math.Max(_maxCllCode, Math.Max(frame.Signature.MaxRed,
            Math.Max(frame.Signature.MaxGreen, frame.Signature.MaxBlue)));
        _maxFall = Math.Max(_maxFall, frame.LinearMaxRgbSum / _pixels);
        _signatures.Add(frame.Signature);
        return frame.Signature;
    }

    /// <summary>Recomputes a frame's signature for verifying that a later render matches the analysed one.</summary>
    public static Hdr10PlusFrameSignature Sign(byte[] rgb48, int width, int height)
    {
        int[][] partitions = [];
        return Measure(rgb48, checked(width * height), ref partitions).Signature;
    }

    /// <summary>Writes metadata for frames <paramref name="start"/> to <paramref name="end"/> (exclusive), indexed from zero.</summary>
    public void Write(string path, int start, int end)
    {
        Finish();
        if (start < 0 || end > FrameCount || start >= end) throw new ArgumentOutOfRangeException(nameof(start));
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
        json.WriteStartObject();
        json.WriteStartObject("JSONInfo");
        json.WriteString("HDR10plusProfile", "A");
        json.WriteString("Version", "1.0");
        json.WriteEndObject();
        json.WriteStartArray("SceneInfo");
        var firstFrames = new List<int>();
        var lengths = new List<int>();
        int sceneStart = 0;
        for (int id = 0; id < _scenes.Count; id++)
        {
            SceneStatistics scene = _scenes[id];
            int first = Math.Max(start, sceneStart);
            int last = Math.Min(end, sceneStart + scene.Frames);
            if (first < last)
            {
                firstFrames.Add(first - start);
                lengths.Add(last - first);
            }
            for (int frame = first; frame < last; frame++)
            {
                json.WriteStartObject();
                json.WriteStartObject("LuminanceParameters");
                json.WriteNumber("AverageRGB", scene.AverageMaxRgb);
                json.WriteStartObject("LuminanceDistributions");
                json.WriteStartArray("DistributionIndex");
                foreach (int percentage in DistributionPercentages) json.WriteNumberValue(percentage);
                json.WriteEndArray();
                json.WriteStartArray("DistributionValues");
                foreach (int value in scene.Percentiles!) json.WriteNumberValue(value);
                json.WriteEndArray();
                json.WriteEndObject();
                json.WriteStartArray("MaxScl");
                json.WriteNumberValue(ToMetadata(CodeNits[scene.MaxRed]));
                json.WriteNumberValue(ToMetadata(CodeNits[scene.MaxGreen]));
                json.WriteNumberValue(ToMetadata(CodeNits[scene.MaxBlue]));
                json.WriteEndArray();
                json.WriteEndObject();
                json.WriteNumber("NumberOfWindows", 1);
                // Profile A: no Bezier tone curve and no targeted display.
                json.WriteNumber("TargetedSystemDisplayMaximumLuminance", 0);
                json.WriteNumber("SceneFrameIndex", frame - sceneStart);
                json.WriteNumber("SceneId", id);
                json.WriteNumber("SequenceFrameIndex", frame - start);
                json.WriteEndObject();
            }
            sceneStart += scene.Frames;
        }
        json.WriteEndArray();
        json.WriteStartObject("SceneInfoSummary");
        json.WriteStartArray("SceneFirstFrameIndex");
        foreach (int first in firstFrames) json.WriteNumberValue(first);
        json.WriteEndArray();
        json.WriteStartArray("SceneFrameNumbers");
        foreach (int length in lengths) json.WriteNumberValue(length);
        json.WriteEndArray();
        json.WriteEndObject();
        json.WriteStartObject("ToolInfo");
        json.WriteString("Tool", "InkMotion");
        json.WriteString("Version", typeof(Hdr10PlusAnalyser).Assembly.GetName().Version?.ToString() ?? "unknown");
        json.WriteEndObject();
        json.WriteEndObject();
    }

    /// <summary>Saves the analysis so that a resumed export can reuse it instead of rendering everything again.</summary>
    public void Save(string path)
    {
        Finish();
        using var writer = new BinaryWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None),
            Encoding.UTF8);
        writer.Write(1);
        writer.Write(_pixels);
        writer.Write(_maxCllCode);
        writer.Write(_maxFall);
        writer.Write(_scenes.Count);
        foreach (SceneStatistics scene in _scenes)
        {
            writer.Write(scene.Frames);
            writer.Write(scene.MaxRed);
            writer.Write(scene.MaxGreen);
            writer.Write(scene.MaxBlue);
            writer.Write(scene.AverageMaxRgb);
            foreach (int value in scene.Percentiles!) writer.Write(value);
        }
        writer.Write(_signatures.Count);
        foreach (Hdr10PlusFrameSignature signature in _signatures)
        {
            writer.Write(signature.MaxRed);
            writer.Write(signature.MaxGreen);
            writer.Write(signature.MaxBlue);
            writer.Write(signature.MaxRgbCodeSum);
            writer.Write(signature.HistogramHash);
        }
    }

    public static Hdr10PlusAnalyser Load(string path, int width, int height, int expectedFrames)
    {
        var analyser = new Hdr10PlusAnalyser(width, height);
        using var reader = new BinaryReader(File.OpenRead(path), Encoding.UTF8);
        if (reader.ReadInt32() != 1 || reader.ReadInt32() != analyser._pixels)
            throw new InvalidDataException("The saved HDR10+ analysis does not match this export.");
        analyser._maxCllCode = reader.ReadInt32();
        analyser._maxFall = reader.ReadDouble();
        int scenes = reader.ReadInt32();
        int frames = 0;
        for (int i = 0; i < scenes; i++)
        {
            var scene = new SceneStatistics
            {
                Frames = reader.ReadInt32(), MaxRed = reader.ReadUInt16(), MaxGreen = reader.ReadUInt16(),
                MaxBlue = reader.ReadUInt16(), AverageMaxRgb = reader.ReadInt32(),
                Percentiles = new int[DistributionPercentages.Length],
            };
            for (int p = 0; p < scene.Percentiles.Length; p++) scene.Percentiles[p] = reader.ReadInt32();
            frames += scene.Frames;
            analyser._scenes.Add(scene);
        }
        int signatures = reader.ReadInt32();
        if (signatures != expectedFrames || frames != expectedFrames)
            throw new InvalidDataException("The saved HDR10+ analysis does not cover every output frame.");
        for (int i = 0; i < signatures; i++)
            analyser._signatures.Add(new(reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt16(),
                reader.ReadInt64(), reader.ReadUInt64()));
        return analyser;
    }

    /// <summary>HDR10+ luminance values are linear light normalised to 10,000 cd/m² in steps of 0.00001, i.e. 0.1 cd/m².</summary>
    public static int ToMetadata(double nits) => (int)Math.Clamp(Math.Round(nits * 10), 0, 100_000);

    /// <summary>Nearest-rank percentile: the smallest code at or below which the given percentage of samples falls.</summary>
    public static int PercentileCode(long[] histogram, long total, int percentage)
    {
        long rank = Math.Max(1, (long)Math.Ceiling(total * (percentage / 100.0)));
        long cumulative = 0;
        for (int code = 0; code < histogram.Length; code++)
        {
            cumulative += histogram[code];
            if (cumulative >= rank) return code;
        }
        return histogram.Length - 1;
    }

    public static double CodeToNits(int code) => CodeNits[code];

    private void StartScene()
    {
        Finish();
        _current = new SceneStatistics();
        _scenes.Add(_current);
    }

    private void Finish()
    {
        if (_current is null) return;
        if (_current.Frames > 0)
        {
            long total = (long)_pixels * _current.Frames;
            _current.AverageMaxRgb = ToMetadata(_current.LinearSum / total);
            _current.Percentiles = DistributionPercentages
                .Select(percentage => ToMetadata(CodeNits[PercentileCode(_sceneHistogram, total, percentage)]))
                .ToArray();
        }
        else
        {
            _scenes.Remove(_current);
        }
        Array.Clear(_sceneHistogram);
        _current = null;
    }

    private static FrameStatistics Measure(byte[] rgb48, int pixels, ref int[][] partitions)
    {
        ArgumentNullException.ThrowIfNull(rgb48);
        if (rgb48.Length != (long)pixels * 6)
            throw new ArgumentException("HDR10+ analysis needs one rgb48le frame of the export size.", nameof(rgb48));
        int count = Math.Clamp(Environment.ProcessorCount, 1, 16);
        if (partitions.Length != count)
            partitions = Enumerable.Range(0, count).Select(_ => new int[Codes]).ToArray();
        int[][] histograms = partitions;
        var maxima = new (ushort R, ushort G, ushort B)[count];
        Parallel.For(0, count, partition =>
        {
            int[] histogram = histograms[partition];
            Array.Clear(histogram);
            ReadOnlySpan<ushort> source = MemoryMarshal.Cast<byte, ushort>(rgb48.AsSpan());
            int start = (int)((long)pixels * partition / count);
            int end = (int)((long)pixels * (partition + 1) / count);
            ushort maxR = 0, maxG = 0, maxB = 0;
            for (int pixel = start; pixel < end; pixel++)
            {
                ushort r = source[pixel * 3], g = source[pixel * 3 + 1], b = source[pixel * 3 + 2];
                if (r > maxR) maxR = r;
                if (g > maxG) maxG = g;
                if (b > maxB) maxB = b;
                histogram[Math.Max(r, Math.Max(g, b))]++;
            }
            maxima[partition] = (maxR, maxG, maxB);
        });
        var merged = new int[Codes];
        foreach (int[] histogram in histograms)
            for (int code = 0; code < Codes; code++) merged[code] += histogram[code];
        long codeSum = 0;
        double linearSum = 0;
        ulong hash = 14695981039346656037UL;
        for (int code = 0; code < Codes; code++)
        {
            int n = merged[code];
            if (n == 0) continue;
            codeSum += (long)code * n;
            linearSum += CodeNits[code] * n;
            hash = (hash ^ (uint)code) * 1099511628211UL;
            hash = (hash ^ (uint)n) * 1099511628211UL;
        }
        var signature = new Hdr10PlusFrameSignature(maxima.Max(m => m.R), maxima.Max(m => m.G), maxima.Max(m => m.B),
            codeSum, hash);
        return new(signature, merged, linearSum);
    }

    private static double[] Coarse(int[] histogram, int pixels)
    {
        var coarse = new double[CoarseBins];
        for (int code = 0; code < Codes; code++)
            coarse[code * CoarseBins / Codes] += histogram[code];
        for (int bin = 0; bin < CoarseBins; bin++) coarse[bin] /= pixels;
        return coarse;
    }

    /// <summary>L1 distance between PQ maxRGB distributions, from 0 (identical) to 2 (disjoint).</summary>
    private static double Distance(double[] a, double[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return sum;
    }

    private static double[] CreateCodeNits()
    {
        var nits = new double[Codes];
        for (int code = 0; code < Codes; code++) nits[code] = HdrColour.PqDecode(code / 65535.0);
        return nits;
    }

    private sealed record FrameStatistics(Hdr10PlusFrameSignature Signature, int[] Histogram, double LinearMaxRgbSum);

    private sealed class SceneStatistics
    {
        public int Frames;
        public ushort MaxRed;
        public ushort MaxGreen;
        public ushort MaxBlue;
        public double LinearSum;
        public int AverageMaxRgb;
        public int[]? Percentiles;
    }
}
