using System.Numerics;

namespace InkMotion;

public sealed record VideoShotMatch
{
    public double RedGain { get; init; } = 1;
    public double GreenGain { get; init; } = 1;
    public double BlueGain { get; init; } = 1;
    public double RedOffset { get; init; }
    public double GreenOffset { get; init; }
    public double BlueOffset { get; init; }
    public bool IsNeutral => this == new VideoShotMatch();
    public bool IsValid => new[] { RedGain, GreenGain, BlueGain }.All(v => double.IsFinite(v) && v is >= 0.25 and <= 4) &&
        new[] { RedOffset, GreenOffset, BlueOffset }.All(v => double.IsFinite(v) && v is >= -1 and <= 1);
}

public sealed record VideoShotStatistics(Vector3 Mean, Vector3 Deviation)
{
    public static VideoShotStatistics Analyse(byte[] bgra)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (bgra.Length == 0 || bgra.Length % 4 != 0)
            throw new ArgumentException("Supply a non-empty BGRA frame.", nameof(bgra));
        double weight = 0;
        Vector3 mean = Vector3.Zero, sum = Vector3.Zero;
        for (int i = 0; i < bgra.Length; i += 4)
        {
            double alpha = bgra[i + 3] / 255d;
            if (alpha < 1 / 16d) continue;
            var value = new Vector3(bgra[i + 2], bgra[i + 1], bgra[i]) / (float)(255 * alpha);
            weight += alpha;
            Vector3 delta = value - mean;
            mean += delta * (float)(alpha / weight);
            sum += delta * (value - mean) * (float)alpha;
        }
        if (weight == 0) throw new InvalidOperationException("The sampled shot contains no visible pixels.");
        Vector3 variance = Vector3.Max(sum / (float)weight, Vector3.Zero);
        return new(mean, new(MathF.Sqrt(variance.X), MathF.Sqrt(variance.Y), MathF.Sqrt(variance.Z)));
    }

    public VideoShotMatch MatchTo(VideoShotStatistics reference)
    {
        (double Gain, double Offset) Channel(float mean, float deviation, float targetMean, float targetDeviation)
        {
            double gain = deviation < 1 / 255f ? 1 : Math.Clamp(targetDeviation / deviation, 0.25, 4);
            return (gain, Math.Clamp(targetMean - mean * gain, -1, 1));
        }
        var r = Channel(Mean.X, Deviation.X, reference.Mean.X, reference.Deviation.X);
        var g = Channel(Mean.Y, Deviation.Y, reference.Mean.Y, reference.Deviation.Y);
        var b = Channel(Mean.Z, Deviation.Z, reference.Mean.Z, reference.Deviation.Z);
        return new() { RedGain = r.Gain, GreenGain = g.Gain, BlueGain = b.Gain,
            RedOffset = r.Offset, GreenOffset = g.Offset, BlueOffset = b.Offset };
    }
}
