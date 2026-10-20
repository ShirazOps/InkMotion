using NAudio.Dsp;
using NAudio.Wave;

namespace InkMotion;

public sealed record AudioSpectrumResult(int SampleRate, int Frames, double Peak, double Rms, double[] Decibels)
{
    public long AnalysedFrames { get; init; } = Frames;
    public long Windows { get; init; } = 1;
    public double BinWidthHz => (double)SampleRate / Frames;
    public double DominantFrequencyHz => Decibels.Skip(1).Select((level, index) => (level, bin: index + 1))
        .MaxBy(item => item.level).bin * BinWidthHz;
}

public static class AudioSpectrum
{
    public const int MaximumFrames = 32768;

    public static AudioSpectrumResult ReadAverage(string path, double startSeconds, double durationSeconds,
        CancellationToken cancellationToken)
    {
        if (!double.IsFinite(startSeconds) || startSeconds < 0 ||
            !double.IsFinite(durationSeconds) || durationSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startSeconds), "Choose a non-empty source range.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var reader = new AudioFileReader(path);
        long sourceFrames = reader.Length / reader.WaveFormat.BlockAlign;
        double firstFrame = Math.Floor(startSeconds * reader.WaveFormat.SampleRate + 1e-7);
        if (firstFrame >= sourceFrames)
        {
            throw new ArgumentException("Choose a range starting inside the source audio.");
        }

        long remaining = (long)Math.Min(sourceFrames - (long)firstFrame,
            Math.Floor(durationSeconds * reader.WaveFormat.SampleRate + 1e-7));
        if (remaining < 32)
        {
            throw new ArgumentException("Spectrum analysis needs at least 32 source sample frames.");
        }

        reader.Position = (long)firstFrame * reader.WaveFormat.BlockAlign;
        int size = 32;
        while (size * 2 <= Math.Min(remaining, MaximumFrames))
        {
            size *= 2;
        }

        int channels = reader.WaveFormat.Channels;
        float[] buffer = new float[size * channels];
        double[] powers = new double[size / 2 + 1];
        long total = 0;
        long windows = 0;
        double peak = 0;
        double squares = 0;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int requested = (int)Math.Min(size, remaining) * channels;
            int read = 0;
            while (read < requested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = reader.Read(buffer, read, requested - read);
                if (count == 0)
                {
                    break;
                }

                read += count;
            }

            if (read == 0)
            {
                break;
            }

            if (read % channels != 0)
            {
                throw new InvalidDataException("Source audio ended with an incomplete sample frame.");
            }

            int frames = read / channels;
            AudioSpectrumResult window = AnalyseWindow(buffer.AsSpan(0, read), channels,
                reader.WaveFormat.SampleRate, size, cancellationToken);
            for (int bin = 0; bin < powers.Length; bin++)
            {
                powers[bin] += Math.Pow(10, window.Decibels[bin] / 10) * frames;
            }

            squares += window.Rms * window.Rms * frames;
            peak = Math.Max(peak, window.Peak);
            total += frames;
            windows++;
            remaining -= frames;
        }

        if (total < 32)
        {
            throw new InvalidDataException("Source audio ended before 32 sample frames could be analysed.");
        }

        return new(reader.WaveFormat.SampleRate, size, peak, Math.Sqrt(squares / total),
            powers.Select(power => 10 * Math.Log10(Math.Max(power / total, 1e-24))).ToArray())
        {
            AnalysedFrames = total,
            Windows = windows,
        };
    }

    public static AudioSpectrumResult Read(string path, double startSeconds, double durationSeconds, CancellationToken cancellationToken)
    {
        if (!double.IsFinite(startSeconds) || startSeconds < 0 ||
            !double.IsFinite(durationSeconds) || durationSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startSeconds), "Choose a non-empty source range.");
        }

        using var reader = new AudioFileReader(path);
        reader.CurrentTime = TimeSpan.FromSeconds(startSeconds);
        int channels = reader.WaveFormat.Channels;
        int frames = (int)Math.Min(MaximumFrames, Math.Floor(durationSeconds * reader.WaveFormat.SampleRate));
        float[] samples = new float[frames * channels];
        int read = 0;
        while (read < samples.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = reader.Read(samples, read, Math.Min(4096 * channels, samples.Length - read));
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        return Analyse(samples.AsSpan(0, read), channels, reader.WaveFormat.SampleRate, cancellationToken);
    }

    public static AudioSpectrumResult Analyse(ReadOnlySpan<float> samples, int channels, int sampleRate, CancellationToken cancellationToken = default)
    {
        if (channels <= 0 || sampleRate <= 0 || samples.Length % channels != 0 || samples.Length / channels < 32)
        {
            throw new ArgumentException("Spectrum analysis needs at least 32 complete source sample frames and a valid format.");
        }

        int size = 32;
        while (size * 2 <= Math.Min(samples.Length / channels, MaximumFrames))
        {
            size *= 2;
        }

        return AnalyseWindow(samples[..(size * channels)], channels, sampleRate, size, cancellationToken);
    }

    private static AudioSpectrumResult AnalyseWindow(ReadOnlySpan<float> samples, int channels, int sampleRate,
        int size, CancellationToken cancellationToken)
    {
        int available = samples.Length / channels;
        int exponent = (int)Math.Log2(size);
        double peak = 0;
        double squares = 0;
        double[] powers = new double[size / 2 + 1];
        double[] window = Enumerable.Range(0, available)
            .Select(index => available == 1 ? 1 : 0.5 - 0.5 * Math.Cos(2 * Math.PI * index / available)).ToArray();
        double windowMean = window.Sum() / size;
        var fft = new Complex[size];
        for (int channel = 0; channel < channels; channel++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Array.Clear(fft);
            for (int index = 0; index < available; index++)
            {
                float sample = samples[index * channels + channel];
                if (!float.IsFinite(sample))
                {
                    throw new InvalidDataException("Source audio contains non-finite samples.");
                }

                peak = Math.Max(peak, Math.Abs(sample));
                squares += (double)sample * sample;
                fft[index] = new Complex { X = (float)(sample * window[index]) };
            }

            FastFourierTransform.FFT(true, exponent, fft);
            for (int bin = 0; bin < powers.Length; bin++)
            {
                double scale = (bin == 0 || bin == size / 2 ? 1 : 2) / windowMean;
                powers[bin] += (fft[bin].X * (double)fft[bin].X + fft[bin].Y * (double)fft[bin].Y) * scale * scale / channels;
            }
        }

        return new(sampleRate, size, peak, Math.Sqrt(squares / (available * (double)channels)),
            powers.Select(power => 10 * Math.Log10(Math.Max(power, 1e-24))).ToArray()) { AnalysedFrames = available };
    }
}
