using NAudio.Wave;
using Xunit;

namespace InkMotion.Tests;

public sealed class TruePeakLimiterTests
{
    private const int SampleRate = 48000;
    private const double Ceiling = 0.75;

    [Fact]
    public void LimiterIsBlockInvariantAndResetReproducesASeek()
    {
        float[] input = CreateSignal(2, 2400);
        float[] whole = ReadAll(new TruePeakLimiterSampleProvider(new ArraySamples(input, 2, 4800)), input.Length, 4800);
        float[] small = ReadAll(new TruePeakLimiterSampleProvider(new ArraySamples(input, 2, 1)), input.Length, 2);
        Assert.Equal(whole, small);
        var source = new ArraySamples(input, 2, 37);
        var stage = new AudioTruePeakStage(source, new()
        {
            Enabled = true, LimiterEnabled = true, LimiterTruePeak = true,
        });
        Assert.Equal(whole, ReadAll(stage, input.Length, 74));
        source.Rewind();
        stage.Reset();
        Assert.Equal(whole, ReadAll(stage, input.Length, 74));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TrackAndBusLimitersControlInterSamplePeaksInExportAndPreview(bool busLimiter)
    {
        string inputPath = Path.Combine(Path.GetTempPath(), $"inkmotion-route-true-peak-{Guid.NewGuid():N}.wav");
        try
        {
            float[] input = CreateSignal(2, 4800);
            using (var writer = new WaveFileWriter(inputPath, WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2)))
                writer.WriteSamples(input, 0, input.Length);
            var settings = new AudioEffectSettings
            {
                Enabled = true, LimiterEnabled = true, LimiterTruePeak = true,
                LimiterCeilingDecibels = -6, LimiterReleaseMilliseconds = 20,
            };
            var bus = new AudioBusProject { Effects = busLimiter ? settings : new(), GainDecibels = busLimiter ? 6 : 0 };
            var track = new AudioTrackProject { AudioPath = inputPath, BusId = bus.Id, Effects = busLimiter ? new() : settings };
            var project = new EditorProject { AudioTracks = [track], AudioBuses = [bus] };
            EditorProject reopened = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
            Assert.True((busLimiter ? reopened.AudioBuses[0].Effects : reopened.AudioTracks[0].Effects).LimiterTruePeak);
            using var mixer = new ExportAudioMixer(reopened);
            float[] export = new float[input.Length];
            Assert.Equal(export.Length, mixer.Read(export, 0, export.Length));
            Assert.True(MeasureSixteenTimesPeak(export, 2) <= Math.Pow(10, -6d / 20) + 0.005);
            using var preview = new AudioPreviewBus();
            using var submix = preview.CreateSubmix(AudioChannelLayout.Stereo, bus.Effects, bus.GainDecibels);
            using var player = new AudioClipPlayer(inputPath, track, AudioChannelLayout.Stereo, preview, submix: submix);
            player.Seek(0);
            player.Play();
            float[] heard = ReadAll(preview, input.Length, 74);
            Assert.True(MeasureSixteenTimesPeak(heard, 2) <= Math.Pow(10, -6d / 20) + 0.005);
            for (int index = 0; index < export.Length; index++) Assert.Equal(export[index], heard[index], 5);
        }
        finally { File.Delete(inputPath); }
    }

    [Fact]
    public void LegacyProjectsKeepSamplePeakModeAndBypassIsBitExact()
    {
        EditorProject project = EditorProjectSerialiser.Deserialise("""{"Version":96,"AudioTracks":[{"Effects":{"Enabled":true,"LimiterEnabled":true}}]}""");
        Assert.False(project.AudioTracks[0].Effects.LimiterTruePeak);
        float[] input = CreateSignal(2, 240);
        var stage = new AudioTruePeakStage(new ArraySamples(input, 2, 13), new());
        Assert.Equal(input, ReadAll(stage, input.Length, 26));
    }

    [Theory]
    [InlineData(12000, 5)]
    [InlineData(18000, 100)]
    [InlineData(21000, 2000)]
    public void LimiterControlsIndependentlyReconstructedBursts(double frequency, double release)
    {
        float[] input = new float[2400];
        for (int frame = 800; frame < 1100; frame++)
            input[frame] = (float)(0.95 * Math.Cos(2 * Math.PI * frequency * (frame + 0.37) / SampleRate));
        float[] output = ReadAll(new TruePeakLimiterSampleProvider(new ArraySamples(input, 1, 37), -6, release),
            input.Length, 37);
        double peak = MeasureSixteenTimesPeak(output, 1);
        Assert.True(peak <= Math.Pow(10, -6d / 20) + 0.005, $"Reconstructed peak: {peak}");
    }

    private static float[] ReadAll(ISampleProvider provider, int length, int block)
    {
        float[] output = new float[length];
        int position = 0;
        while (position < length)
        {
            int read = provider.Read(output, position, Math.Min(block, length - position));
            Assert.True(read > 0);
            position += read;
        }
        return output;
    }

    // Independent, longer-kernel reconstruction, not the limiter's 4x detector.
    private static double MeasureSixteenTimesPeak(float[] samples, int channels)
    {
        double peak = 0;
        for (int frame = 32; frame < samples.Length / channels - 33; frame++)
        for (int phase = 0; phase < 16; phase++)
        for (int channel = 0; channel < channels; channel++)
        {
            double sum = 0, weights = 0;
            for (int tap = -31; tap <= 32; tap++)
            {
                double distance = tap - phase / 16d;
                double sinc = Math.Abs(distance) < 1e-12 ? 1 : Math.Sin(Math.PI * distance) / (Math.PI * distance);
                double weight = sinc * (0.5 + 0.5 * Math.Cos(Math.PI * distance / 33));
                sum += samples[(frame + tap) * channels + channel] * weight;
                weights += weight;
            }
            peak = Math.Max(peak, Math.Abs(sum / weights));
        }
        return peak;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(37)]
    [InlineData(512)]
    public void LimiterControlsFourTimesInterSamplePeaksAcrossReadSizes(int readFrames)
    {
        float[] input = CreateSignal(2, 4800);
        Assert.True(input.Max(Math.Abs) < Ceiling);
        Assert.True(MeasureFourTimesPeak(input, 2) > Ceiling);

        var limiter = new TruePeakLimiterSampleProvider(new ArraySamples(input, 2, readFrames), 20 * Math.Log10(Ceiling));
        float[] output = new float[input.Length];
        int position = 0;
        int bufferSize = readFrames * 2;
        while (position < output.Length)
        {
            int requested = Math.Min(bufferSize, output.Length - position);
            int read = limiter.Read(output, position, requested);
            if (read == 0)
            {
                break;
            }

            position += read;
        }

        Assert.Equal(input.Length, position);
        Assert.True(MeasureFourTimesPeak(output, 2) <= Ceiling + 0.005);
        for (int frame = 100; frame < 4700; frame++)
        {
            Assert.Equal(4, output[frame * 2] / output[frame * 2 + 1], 3);
        }
    }

    [Fact]
    public void ExportSettingAppliesTheTruePeakLimiterAndKeepsItOptIn()
    {
        string inputPath = Path.Combine(Path.GetTempPath(), $"inkmotion-true-peak-{Guid.NewGuid():N}.wav");
        string outputPath = Path.ChangeExtension(inputPath, ".out.wav");
        try
        {
            float[] samples = CreateSignal(1, 4800);
            using (var writer = new WaveFileWriter(inputPath, WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1)))
            {
                writer.WriteSamples(samples, 0, samples.Length);
            }

            var project = new EditorProject
            {
                AudioMixLayout = AudioChannelLayout.Mono,
                AudioTracks = [new() { AudioPath = inputPath }],
            };
            AudioExportSettings defaultSettings = new(AudioExportCodec.FloatWave, SampleRate, 32);
            Assert.False(defaultSettings.TruePeakLimiterEnabled);
            Assert.Equal(samples.Max(Math.Abs), ExportSamples(project, defaultSettings, outputPath).Max(Math.Abs), 6);

            AudioExportSettings limitedSettings = defaultSettings with
            {
                TruePeakLimiterEnabled = true,
                TruePeakCeilingDecibels = -6,
            };
            float[] limited = ExportSamples(project, limitedSettings, outputPath);
            Assert.True(MeasureFourTimesPeak(limited, 1) <= Math.Pow(10, -6d / 20) + 0.005);
        }
        finally
        {
            File.Delete(inputPath);
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void LimiterUsesLookAheadForAHighFrequencyBurst()
    {
        float[] input = new float[2 * 2400];
        double angularFrequency = 2 * Math.PI * 12000 / SampleRate;
        for (int frame = 900; frame < 1100; frame++)
        {
            float sample = (float)(0.9 * Math.Cos(angularFrequency * (frame + 0.5)));
            input[frame * 2] = sample;
            input[frame * 2 + 1] = sample * 0.25f;
        }

        var limiter = new TruePeakLimiterSampleProvider(new ArraySamples(input, 2, 41), 20 * Math.Log10(Ceiling));
        float[] output = new float[input.Length];
        int position = 0;
        while (position < output.Length)
        {
            int read = limiter.Read(output, position, Math.Min(82, output.Length - position));
            if (read == 0)
            {
                break;
            }

            position += read;
        }

        Assert.Equal(input.Length, position);
        Assert.True(MeasureFourTimesPeak(output, 2) <= Ceiling + 0.01);
    }

    [Theory]
    [InlineData(-25)]
    [InlineData(1)]
    public void ExportSettingsRejectTruePeakCeilingsOutsideTheSupportedRange(double ceiling) =>
        Assert.Throws<ArgumentException>(() =>
            new AudioExportSettings(AudioExportCodec.PcmWave, SampleRate,
                TruePeakCeilingDecibels: ceiling).Validate());

    private static float[] ExportSamples(EditorProject project, AudioExportSettings settings, string path)
    {
        AudioExporter.Export(project, settings, path, null, CancellationToken.None);
        using var reader = new AudioFileReader(path);
        float[] samples = new float[(int)(reader.Length / sizeof(float))];
        int read = reader.Read(samples, 0, samples.Length);
        return samples[..read];
    }

    private static float[] CreateSignal(int channels, int frames)
    {
        float[] signal = new float[channels * frames];
        double angularFrequency = 2 * Math.PI * 12000 / SampleRate;
        for (int frame = 0; frame < frames; frame++)
        {
            float sample = (float)(0.9 * Math.Cos(angularFrequency * (frame + 0.5)));
            for (int channel = 0; channel < channels; channel++)
            {
                signal[frame * channels + channel] = channel == 0 ? sample : sample * 0.25f;
            }
        }

        return signal;
    }

    private static double MeasureFourTimesPeak(float[] samples, int channels)
    {
        double peak = 0;
        int frames = samples.Length / channels;
        for (int frame = 5; frame < frames - 7; frame++)
        {
            for (int channel = 0; channel < channels; channel++)
            {
                double first = samples[frame * channels + channel];
                double second = samples[(frame + 1) * channels + channel];
                peak = Math.Max(peak, Math.Max(Math.Abs(first), Math.Abs(second)));
                for (int phase = 1; phase < 4; phase++)
                {
                    double fraction = phase / 4d;
                    double sum = 0;
                    double weightSum = 0;
                    for (int tap = 0; tap < 12; tap++)
                    {
                        double distance = -5 + tap - fraction;
                        double absoluteDistance = Math.Abs(distance);
                        double window = absoluteDistance >= 6
                            ? 0
                            : 0.42 + 0.5 * Math.Cos(Math.PI * distance / 6) +
                                0.08 * Math.Cos(Math.PI * distance / 3);
                        double sinc = absoluteDistance < 1e-12
                            ? 1
                            : Math.Sin(Math.PI * distance) / (Math.PI * distance);
                        double weight = sinc * window;
                        sum += samples[(frame - 5 + tap) * channels + channel] * weight;
                        weightSum += weight;
                    }

                    double interpolated = sum / weightSum;
                    peak = Math.Max(peak, Math.Abs(interpolated));
                }
            }
        }

        return peak;
    }

    private sealed class ArraySamples(float[] samples, int channels, int maxReadFrames) : ISampleProvider
    {
        private int _position;
        public void Rewind() => _position = 0;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, channels);

        public int Read(float[] buffer, int offset, int count)
        {
            int maximum = maxReadFrames * channels;
            int read = Math.Min(Math.Min(count, maximum), samples.Length - _position);
            read -= read % channels;
            Array.Copy(samples, _position, buffer, offset, read);
            _position += read;
            return read;
        }
    }
}
