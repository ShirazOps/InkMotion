using NAudio.Wave;

namespace InkMotion;

public sealed record SoundEffectDefinition(string Id, string Name, string Description, double DurationSeconds);

/// <summary>
/// Built-in sound effects synthesised in code. Every effect is original, deterministic and generated on this
/// device, so no third-party recordings are bundled, downloaded or redistributed.
/// </summary>
public static class SoundEffectLibrary
{
    public const int SampleRate = 48000;
    private const string Version = "v1";
    private const float Peak = 0.85f;

    public static IReadOnlyList<SoundEffectDefinition> All { get; } =
    [
        new("whoosh", "Whoosh", "Airy swept noise passing by", 0.9),
        new("swoosh-down", "Swoosh down", "Falling filtered sweep for exits", 0.7),
        new("pop", "Pop", "Short bubbly pitch drop", 0.15),
        new("click", "Click", "Crisp interface tick", 0.06),
        new("blip", "Blip", "Bright two-tone notification", 0.22),
        new("riser", "Riser", "Building noise and tone sweep", 2.5),
        new("impact", "Impact", "Low thud with a noisy hit", 1.4),
        new("chime", "Chime", "Bell-like inharmonic ring", 2.2),
        new("zap", "Zap", "Buzzy falling electric sweep", 0.4),
        new("sparkle", "Sparkle", "Glittering high random pings", 1.2),
    ];

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InkMotion", "SoundEffects");

    public static SoundEffectDefinition Get(string id) =>
        All.FirstOrDefault(effect => effect.Id == id) ??
        throw new ArgumentException($"Unknown sound effect '{id}'.", nameof(id));

    public static string GetFileName(string id) => $"inkmotion-{Get(id).Id}-{Version}.wav";

    /// <summary>Generates mono float samples in the range [-1, 1] for an effect.</summary>
    public static float[] Generate(string id)
    {
        SoundEffectDefinition effect = Get(id);
        var samples = new float[(int)Math.Round(effect.DurationSeconds * SampleRate)];
        var random = new Random(StableSeed(id));
        switch (id)
        {
            case "whoosh": SweptNoise(samples, random, 300, 3000, 400, rising: true); break;
            case "swoosh-down": SweptNoise(samples, random, 2500, 250, 300, rising: false); break;
            case "pop": Pop(samples); break;
            case "click": Click(samples, random); break;
            case "blip": Blip(samples); break;
            case "riser": Riser(samples, random); break;
            case "impact": Impact(samples, random); break;
            case "chime": Chime(samples); break;
            case "zap": Zap(samples); break;
            case "sparkle": Sparkle(samples, random); break;
        }

        Normalise(samples);
        return samples;
    }

    /// <summary>Writes the effect once to the library directory and returns its path.</summary>
    public static string EnsureFile(string id, string? directory = null)
    {
        directory ??= DefaultDirectory;
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, GetFileName(id));
        if (File.Exists(path) && new FileInfo(path).Length > 44)
        {
            return path;
        }

        float[] samples = Generate(id);
        string temporary = Path.Combine(directory, $"{Guid.NewGuid():N}.partial");
        try
        {
            using (var writer = new WaveFileWriter(temporary, WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1)))
            {
                writer.WriteSamples(samples, 0, samples.Length);
            }

            File.Move(temporary, path, overwrite: true);
            return path;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>Regenerates a missing built-in effect referenced by a project; other paths are left alone.</summary>
    public static bool TryRestore(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || File.Exists(path))
        {
            return false;
        }

        string? directory = Path.GetDirectoryName(path);
        SoundEffectDefinition? effect = All.FirstOrDefault(candidate =>
            string.Equals(Path.GetFileName(path), GetFileName(candidate.Id), StringComparison.OrdinalIgnoreCase));
        if (effect is null || directory is null ||
            !string.Equals(Path.GetFullPath(directory).TrimEnd('\\'), Path.GetFullPath(DefaultDirectory).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            EnsureFile(effect.Id, directory);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static int StableSeed(string id)
    {
        unchecked
        {
            int hash = 17;
            foreach (char character in id) hash = (hash * 31) + character;
            return hash;
        }
    }

    private static double Envelope(double t, double attack, double release, double duration)
    {
        if (t < attack) return t / attack;
        double remaining = duration - t;
        return remaining < release ? Math.Max(0, remaining / release) : 1;
    }

    private static void SweptNoise(float[] samples, Random random, double startHz, double endHz, double bandwidthHz, bool rising)
    {
        // A resonant band-pass (state-variable filter) swept across white noise, shaped by a hump envelope.
        double low = 0, band = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            double progress = (double)i / samples.Length;
            double centre = startHz * Math.Pow(endHz / startHz, progress);
            double f = 2 * Math.Sin(Math.PI * centre / SampleRate);
            double q = Math.Clamp(bandwidthHz / centre, 0.1, 1.5);
            double input = (random.NextDouble() * 2) - 1;
            low += f * band;
            double high = input - low - (q * band);
            band += f * high;
            double shape = rising ? Math.Sin(Math.PI * Math.Pow(progress, 0.7)) : Math.Sin(Math.PI * Math.Pow(progress, 1.4));
            samples[i] = (float)(band * shape * shape);
        }
    }

    private static void Pop(float[] samples)
    {
        double phase = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            double t = (double)i / SampleRate;
            double frequency = 150 + (650 * Math.Exp(-t * 40));
            phase += 2 * Math.PI * frequency / SampleRate;
            samples[i] = (float)(Math.Sin(phase) * Math.Exp(-t * 30) * Math.Min(1, t * 2000));
        }
    }

    private static void Click(float[] samples, Random random)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            double t = (double)i / SampleRate;
            double tick = Math.Sin(2 * Math.PI * 2200 * t) * Math.Exp(-t * 300);
            double noise = ((random.NextDouble() * 2) - 1) * Math.Exp(-t * 900);
            samples[i] = (float)((tick * 0.7) + (noise * 0.5));
        }
    }

    private static void Blip(float[] samples)
    {
        int half = samples.Length / 2;
        for (int i = 0; i < samples.Length; i++)
        {
            double t = (double)i / SampleRate;
            double local = (double)(i < half ? i : i - half) / SampleRate;
            double frequency = i < half ? 880 : 1320;
            double square = Math.Sign(Math.Sin(2 * Math.PI * frequency * t));
            double softened = (0.6 * Math.Sin(2 * Math.PI * frequency * t)) + (0.25 * square);
            samples[i] = (float)(softened * Envelope(local, 0.004, 0.03, half / (double)SampleRate));
        }
    }

    private static void Riser(float[] samples, Random random)
    {
        double phase = 0, low = 0, band = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            double progress = (double)i / samples.Length;
            double frequency = 180 * Math.Pow(12, progress);
            phase += 2 * Math.PI * frequency / SampleRate;
            double f = 2 * Math.Sin(Math.PI * Math.Min(frequency * 4, 12000) / SampleRate);
            double input = (random.NextDouble() * 2) - 1;
            low += f * band;
            band += f * (input - low - (0.6 * band));
            double swell = Math.Pow(progress, 2) * Math.Min(1, (samples.Length - i) / (SampleRate * 0.02));
            samples[i] = (float)(((0.5 * Math.Sin(phase)) + (0.6 * band)) * swell);
        }
    }

    private static void Impact(float[] samples, Random random)
    {
        double phase = 0, low = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            double t = (double)i / SampleRate;
            double frequency = 45 + (70 * Math.Exp(-t * 18));
            phase += 2 * Math.PI * frequency / SampleRate;
            low += 0.08 * ((((random.NextDouble() * 2) - 1)) - low);
            double body = Math.Sin(phase) * Math.Exp(-t * 3.2);
            double hit = low * 3 * Math.Exp(-t * 25);
            samples[i] = (float)((body + hit) * Math.Min(1, t * 1500));
        }
    }

    private static void Chime(float[] samples)
    {
        (double Ratio, double Gain, double Decay)[] partials =
            [(1, 1, 1.6), (2.76, 0.5, 2.6), (5.4, 0.3, 4), (8.93, 0.15, 6)];
        for (int i = 0; i < samples.Length; i++)
        {
            double t = (double)i / SampleRate;
            double value = 0;
            foreach ((double ratio, double gain, double decay) in partials)
            {
                value += gain * Math.Sin(2 * Math.PI * 880 * ratio * t) * Math.Exp(-t * decay);
            }

            samples[i] = (float)(value * Math.Min(1, t * 1000));
        }
    }

    private static void Zap(float[] samples)
    {
        double phase = 0, modulatorPhase = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            double t = (double)i / SampleRate;
            double progress = (double)i / samples.Length;
            double frequency = 1800 * Math.Pow(80 / 1800d, progress);
            modulatorPhase += 2 * Math.PI * 55 / SampleRate;
            phase += 2 * Math.PI * frequency * (1 + (0.3 * Math.Sin(modulatorPhase))) / SampleRate;
            double saw = (phase / Math.PI % 2) - 1;
            samples[i] = (float)(saw * Envelope(t, 0.003, 0.08, samples.Length / (double)SampleRate));
        }
    }

    private static void Sparkle(float[] samples, Random random)
    {
        const int pings = 14;
        double duration = samples.Length / (double)SampleRate;
        for (int ping = 0; ping < pings; ping++)
        {
            double start = random.NextDouble() * duration * 0.75;
            double frequency = 2500 + (random.NextDouble() * 4500);
            double gain = 0.4 + (random.NextDouble() * 0.6);
            for (int i = (int)(start * SampleRate); i < samples.Length; i++)
            {
                double local = (i / (double)SampleRate) - start;
                double decay = Math.Exp(-local * 14);
                if (decay < 0.001) break;
                samples[i] += (float)(gain * Math.Sin(2 * Math.PI * frequency * local) * decay * Math.Min(1, local * 3000));
            }
        }
    }

    private static void Normalise(float[] samples)
    {
        float max = 0;
        foreach (float sample in samples) max = Math.Max(max, Math.Abs(sample));
        if (max <= 0) return;
        float scale = Peak / max;
        for (int i = 0; i < samples.Length; i++) samples[i] *= scale;
    }
}
