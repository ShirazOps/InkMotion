using NAudio.Dsp;
using NAudio.Wave;

namespace InkMotion;

public sealed record AudioSpectrumResult(int SampleRate, int Frames, double Peak, double Rms, double[] Decibels)
{
    public double BinWidthHz => (double)SampleRate / Frames;
    public double DominantFrequencyHz => Decibels.Skip(1).Select((level, index) => (level, bin: index + 1))
        .MaxBy(item => item.level).bin * BinWidthHz;
}

public static class AudioSpectrum
{
    public const int MaximumFrames = 32768;

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
        int exponent = 5;
        while (size * 2 <= Math.Min(samples.Length / channels, MaximumFrames))
        {
            size *= 2;
            exponent++;
        }

        double peak = 0;
        double squares = 0;
        double[] powers = new double[size / 2 + 1];
        double[] window = Enumerable.Range(0, size).Select(index => 0.5 - 0.5 * Math.Cos(2 * Math.PI * index / size)).ToArray();
        double windowMean = window.Average();
        var fft = new Complex[size];
        for (int channel = 0; channel < channels; channel++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int index = 0; index < size; index++)
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

        return new(sampleRate, size, peak, Math.Sqrt(squares / (size * (double)channels)),
            powers.Select(power => 10 * Math.Log10(Math.Max(power, 1e-24))).ToArray());
    }
}
