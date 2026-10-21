using NAudio.Wave;

namespace InkMotion;

public sealed record AudioSpectrogramResult(
    int SampleRate, int FftFrames, long FirstSampleFrame, long SampleFrames,
    float[][] Decibels, long[] ColumnFrames)
{
    public double BinWidthHz => (double)SampleRate / FftFrames;
}

public static class AudioSpectrogram
{
    public const int MaximumColumns = 256;
    public const int MaximumFftFrames = 1024;

    public static AudioSpectrogramResult Read(string path, double startSeconds, double durationSeconds,
        CancellationToken cancellationToken)
    {
        if (!double.IsFinite(startSeconds) || startSeconds < 0 ||
            !double.IsFinite(durationSeconds) || durationSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startSeconds), "Choose a non-empty source range.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var reader = new AudioFileReader(path);
        int rate = reader.WaveFormat.SampleRate;
        int channels = reader.WaveFormat.Channels;
        long available = reader.Length / reader.WaveFormat.BlockAlign;
        double rawFirst = Math.Round(startSeconds * rate);
        if (rawFirst >= available)
        {
            throw new ArgumentException("Choose a range starting inside the source audio.");
        }

        long first = (long)rawFirst;
        long frames = (long)Math.Min(available - first, Math.Round(durationSeconds * rate));
        if (frames < 32)
        {
            throw new ArgumentException("Spectrogram analysis needs at least 32 source sample frames.");
        }

        reader.Position = checked(first * reader.WaveFormat.BlockAlign);
        int size = 32;
        while (size * 2 <= Math.Min(frames, MaximumFftFrames))
        {
            size *= 2;
        }

        long windows = (frames - 1) / size + 1;
        int columns = (int)Math.Min(MaximumColumns, windows);
        double[][] powers = Enumerable.Range(0, columns).Select(_ => new double[size / 2 + 1]).ToArray();
        long[] weights = new long[columns];
        var buffer = new float[size * channels];
        long processed = 0;
        for (long window = 0; window < windows; window++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = (int)Math.Min(size, frames - processed) * channels;
            int read = 0;
            while (read < count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int next = reader.Read(buffer, read, count - read);
                if (next == 0)
                {
                    throw new InvalidDataException("Source audio ended before the selected range could be analysed.");
                }
                read += next;
            }

            AudioSpectrumResult spectrum = AudioSpectrum.AnalyseWindow(buffer.AsSpan(0, read), channels, rate, size, cancellationToken);
            int column = (int)(window * columns / windows);
            int windowFrames = read / channels;
            for (int bin = 0; bin < powers[column].Length; bin++)
            {
                powers[column][bin] += Math.Pow(10, spectrum.Decibels[bin] / 10) * windowFrames;
            }
            weights[column] += windowFrames;
            processed += windowFrames;
        }

        cancellationToken.ThrowIfCancellationRequested();
        float[][] levels = powers.Select((bins, column) =>
            bins.Select(power => (float)(10 * Math.Log10(Math.Max(power / weights[column], 1e-24)))).ToArray()).ToArray();
        return new(rate, size, first, frames, levels, weights);
    }
}
