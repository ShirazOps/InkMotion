using NAudio.Wave;

namespace InkMotion;

public static class AudioWaveformReader
{
    public const int DefaultPeakCount = 1600;

    public static float[] ReadPeaks(string audioPath, int peakCount = DefaultPeakCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(peakCount);

        using var reader = new AudioFileReader(audioPath);
        int channels = reader.WaveFormat.Channels;
        int sampleRate = reader.WaveFormat.SampleRate;
        if (channels < 1 || sampleRate < 1)
        {
            throw new InvalidDataException("The audio source has an invalid sample format.");
        }

        long totalFrames = (long)Math.Ceiling(reader.TotalTime.TotalSeconds * sampleRate);
        if (totalFrames < 1)
        {
            return [];
        }

        var peaks = new float[peakCount];
        var buffer = new float[channels * 4096];
        long frameIndex = 0;
        int samplesRead;
        while ((samplesRead = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            int completeFrames = samplesRead / channels;
            for (int frame = 0; frame < completeFrames; frame++, frameIndex++)
            {
                int peakIndex = (int)Math.Min(peakCount - 1, frameIndex * peakCount / totalFrames);
                float framePeak = 0;
                int firstSample = frame * channels;
                for (int channel = 0; channel < channels; channel++)
                {
                    float sample = buffer[firstSample + channel];
                    if (float.IsFinite(sample))
                    {
                        framePeak = Math.Max(framePeak, Math.Abs(sample));
                    }
                }

                peaks[peakIndex] = Math.Max(peaks[peakIndex], Math.Min(framePeak, 1));
            }
        }

        return peaks;
    }

    public static float ReadPeak(string audioPath, double startSeconds = 0, double? endSeconds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioPath);
        using var reader = new AudioFileReader(audioPath);
        int channels = reader.WaveFormat.Channels;
        int sampleRate = reader.WaveFormat.SampleRate;
        if (channels < 1 || sampleRate < 1)
        {
            throw new InvalidDataException("The audio source has an invalid sample format.");
        }

        double start = Math.Clamp(startSeconds, 0, reader.TotalTime.TotalSeconds);
        reader.CurrentTime = TimeSpan.FromSeconds(start);
        long remainingFrames = endSeconds is double end
            ? (long)Math.Ceiling(Math.Max(0, end - start) * sampleRate)
            : long.MaxValue;
        var buffer = new float[channels * 4096];
        float peak = 0;
        int samplesRead;
        while (remainingFrames > 0 && (samplesRead = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            int frames = (int)Math.Min(samplesRead / channels, remainingFrames);
            for (int sample = 0; sample < frames * channels; sample++)
            {
                if (float.IsFinite(buffer[sample]))
                {
                    peak = Math.Max(peak, Math.Abs(buffer[sample]));
                }
            }

            remainingFrames -= frames;
        }

        return peak;
    }
}
