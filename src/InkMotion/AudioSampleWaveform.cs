using NAudio.Wave;

namespace InkMotion;

public sealed record AudioSampleWaveformResult(
    long FirstSampleFrame, int SampleRate, int Channels, float[] Samples)
{
    public int SampleFrames => Samples.Length / Channels;
    public long EndSampleFrame => FirstSampleFrame + SampleFrames;
}

public static class AudioSampleWaveform
{
    public const int MaximumWindowFrames = 32768;

    public static (long Start, int Count) GetWindow(long first, int count, long clipStart, long clipEnd)
    {
        if (clipStart < 0 || clipEnd <= clipStart || clipEnd > 9007199254740991L ||
            count < 1 || count > MaximumWindowFrames)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Use 1-32768 sample frames inside a supported source range.");
        }

        int length = (int)Math.Min(count, clipEnd - clipStart);
        return (Math.Clamp(first, clipStart, clipEnd - length), length);
    }

    public static AudioSampleWaveformResult Read(string path, long first, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (first < 0 || first > 9007199254740991L || count < 1 || count > MaximumWindowFrames)
        {
            throw new ArgumentOutOfRangeException(nameof(first), "Use a supported non-negative source position and a bounded sample window.");
        }

        using var reader = new AudioFileReader(path);
        long available = reader.Length / reader.WaveFormat.BlockAlign;
        if (first >= available || count > available - first)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "The sample window extends beyond the decoded source.");
        }

        reader.Position = checked(first * reader.WaveFormat.BlockAlign);
        int channels = reader.WaveFormat.Channels;
        var samples = new float[checked(count * channels)];
        int total = 0;
        while (total < samples.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = reader.Read(samples, total, Math.Min(4096 * channels, samples.Length - total));
            if (read == 0)
            {
                throw new InvalidDataException("The source ended before the requested sample window was decoded.");
            }

            total += read;
        }

        if (samples.Any(sample => !float.IsFinite(sample)))
        {
            throw new InvalidDataException("The source contains non-finite samples.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new(first, reader.WaveFormat.SampleRate, channels, samples);
    }
}
