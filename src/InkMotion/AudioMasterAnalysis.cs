namespace InkMotion;

public sealed record AudioMasterAnalysisResult(
    int SampleRate,
    long SampleFrames,
    AudioChannelLayout Layout,
    AudioMeterReading Overall,
    IReadOnlyList<AudioMeterReading> Channels,
    double? IntegratedLufs,
    IReadOnlyList<string> Warnings);

public static class AudioMasterAnalysis
{
    public static AudioMasterAnalysisResult Analyse(
        EditorProject project, CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EditorProjectValidation.Validate(project);
        using var mixer = new ExportAudioMixer(project, reportVideoReadFailures: true);
        if (mixer.SourceCount == 0)
        {
            throw new InvalidOperationException("Nothing to analyse: add an available audio source." +
                (mixer.Warnings.Count > 0 ? "\n" + string.Join("\n", mixer.Warnings) : string.Empty));
        }

        long frames = checked((long)Math.Round(mixer.DurationSeconds * mixer.SampleRate));
        if (frames <= 0)
        {
            throw new InvalidOperationException("The audio mix has no sample frames to analyse.");
        }

        var peaks = new float[mixer.Channels];
        var squares = new double[mixer.Channels];
        AudioIntegratedLoudness loudness = new(mixer.SampleRate, AudioChannelLayouts.Get(mixer.Layout).Speakers);
        var buffer = new float[4096 * mixer.Channels];
        var frameSamples = new float[mixer.Channels];
        long processed = 0;
        int reportedPercent = -1;
        while (processed < frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = (int)Math.Min(4096, frames - processed) * mixer.Channels;
            int read = mixer.Read(buffer, 0, count);
            if (read != count)
            {
                throw new InvalidOperationException("The audio mix stopped before the end of the analysis.");
            }

            for (int index = 0; index < read; index++)
            {
                float sample = buffer[index];
                if (!float.IsFinite(sample))
                {
                    throw new InvalidDataException("The audio mix contains a non-finite sample.");
                }

                int channel = index % mixer.Channels;
                peaks[channel] = Math.Max(peaks[channel], Math.Abs(sample));
                squares[channel] += (double)sample * sample;
                frameSamples[channel] = sample;
                if (channel == mixer.Channels - 1)
                {
                    loudness.AddFrame(frameSamples);
                }
            }

            processed += read / mixer.Channels;
            int percent = (int)((double)processed / frames * 100);
            if (percent > reportedPercent)
            {
                reportedPercent = percent;
                progress?.Report((double)processed / frames);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        AudioMeterReading[] channels = Enumerable.Range(0, mixer.Channels)
            .Select(channel => new AudioMeterReading(peaks[channel], Math.Sqrt(squares[channel] / frames), peaks[channel] >= 1))
            .ToArray();
        float peak = peaks.Max();
        return new(mixer.SampleRate, frames, mixer.Layout,
            new(peak, Math.Sqrt(squares.Sum() / frames / mixer.Channels), peak >= 1),
            Array.AsReadOnly(channels), loudness.GetIntegratedLufs(), mixer.Warnings.ToArray());
    }
}
