using NAudio.Wave;
using Xunit;

namespace InkMotion.Tests;

public sealed class AudioNoiseReductionTests
{
    private sealed class Samples(float[] data, int channels = 1) : ISampleProvider
    {
        private int _position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, channels);

        public int Read(float[] buffer, int offset, int count)
        {
            int length = Math.Min(count, data.Length - _position);
            Array.Copy(data, _position, buffer, offset, length);
            _position += length;
            return length;
        }
    }

    private static AudioNoiseProfile Profile(double magnitude, int channels = 1) =>
        new(@"C:\Media\speech.wav", 48000, channels, AudioNoiseProfile.DefaultFftSize,
            Enumerable.Repeat(magnitude, channels * (AudioNoiseProfile.DefaultFftSize / 2 + 1)).ToArray());

    private static float[] Process(float[] input, AudioNoiseProfile profile, double strength = 12)
    {
        var provider = new AudioEffectsSampleProvider(new Samples(input), new AudioEffectSettings
        {
            Enabled = true,
            NoiseReductionEnabled = true,
            NoiseReductionStrengthDecibels = strength,
            NoiseProfile = profile,
        });
        var output = new float[input.Length];
        int read = 0;
        while (read < output.Length)
        {
            int count = provider.Read(output, read, Math.Min(317, output.Length - read));
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        Assert.Equal(output.Length, read);
        Assert.All(output, value => Assert.True(float.IsFinite(value)));
        return output;
    }

    [Fact]
    public void SpectralReduction_WithZeroProfilePreservesAudioAndExactLength()
    {
        float[] input = Enumerable.Range(0, 5173)
            .Select(index => (float)(0.2 * Math.Sin(index * 2 * Math.PI * 440 / 48000)))
            .ToArray();

        float[] output = Process(input, Profile(0));

        Assert.Equal(input.Length, output.Length);
        for (int index = 0; index < input.Length; index++)
        {
            Assert.InRange(Math.Abs(output[index] - input[index]), 0, 0.0001f);
        }
    }

    [Fact]
    public void SpectralReduction_AttenuatesProfiledNoiseAndCanBeSetToZeroStrength()
    {
        float[] input = Enumerable.Range(0, 6144)
            .Select(index => (float)(0.2 * Math.Sin(index * 2 * Math.PI * 440 / 48000)))
            .ToArray();
        AudioNoiseProfile profile = Profile(10);

        float[] reduced = Process(input, profile);
        float[] unchanged = Process(input, profile, 0);
        double inputRms = Math.Sqrt(input.Average(value => (double)value * value));
        double outputRms = Math.Sqrt(reduced.Average(value => (double)value * value));

        Assert.True(outputRms < inputRms * 0.4, $"Noise reduction RMS {outputRms} was not sufficiently below {inputRms}.");
        for (int index = 0; index < input.Length; index++)
        {
            Assert.InRange(Math.Abs(unchanged[index] - input[index]), 0, 0.0001f);
        }
    }

    [Fact]
    public void SpectralReduction_SmoothsGainRecoveryAfterTransientNoise()
    {
        const int firstLength = 8192;
        const int noisyLength = 8192;
        const int recoveryLength = 16384;
        float[] input = Enumerable.Range(0, firstLength + noisyLength + recoveryLength)
            .Select(index =>
            {
                double amplitude = index < firstLength ? 0.5 :
                    index < firstLength + noisyLength ? 0.03 : 0.5;
                return (float)(amplitude * Math.Sin(index * 2 * Math.PI * 440 / 48000));
            })
            .ToArray();

        double[] magnitudes = new double[AudioNoiseProfile.DefaultFftSize / 2 + 1];
        for (int bin = 8; bin <= 11; bin++)
        {
            magnitudes[bin] = 0.000001;
        }
        var noiseProfile = new AudioNoiseProfile(@"C:\Media\speech.wav", 48000, 1,
            AudioNoiseProfile.DefaultFftSize, magnitudes);
        float[] output = Process(input, noiseProfile, 24);
        static double Rms(float[] samples, int first, int count) =>
            Math.Sqrt(samples.Skip(first).Take(count).Average(value => (double)value * value));

        double initialGain = Rms(output, 4096, 1024) / Rms(input, 4096, 1024);
        double noiseGain = Rms(output, 12288, 1024) / Rms(input, 12288, 1024);
        double earlyRecoveryGain = Rms(output, 16896, 1024) / Rms(input, 16896, 1024);
        double settledGain = Rms(output, 28672, 1024) / Rms(input, 28672, 1024);

        Assert.True(noiseGain < initialGain,
            $"Transient noise was not attenuated: initial={initialGain}, noise={noiseGain}.");
        Assert.True(earlyRecoveryGain > noiseGain + 0.01,
            $"Gain did not recover smoothly after the noise: noise={noiseGain}, early recovery={earlyRecoveryGain}.");
        Assert.True(earlyRecoveryGain < settledGain,
            $"Gain recovery should continue over subsequent hops: early={earlyRecoveryGain}, settled={settledGain}.");
        Assert.InRange(Math.Abs(settledGain - initialGain), 0, 0.08);
    }

    [Fact]
    public void SpectralReduction_SettingsAndSuppressionChangesPreserveStreamLength()
    {
        float[] input = Enumerable.Range(0, 8197)
            .Select(index => (float)(0.2 * Math.Sin(index * 2 * Math.PI * 440 / 48000)))
            .ToArray();
        var provider = new AudioEffectsSampleProvider(new Samples(input), new AudioEffectSettings
        {
            Enabled = true,
            NoiseReductionEnabled = true,
            NoiseReductionStrengthDecibels = 12,
            NoiseProfile = Profile(0),
        });
        var output = new float[input.Length];
        int firstRead = provider.Read(output, 0, 317);
        provider.Update(new AudioEffectSettings
        {
            Enabled = true,
            NoiseReductionEnabled = true,
            NoiseReductionStrengthDecibels = 6,
            NoiseProfile = Profile(0),
        });
        provider.SetSuppressed(true);
        int suppressedRead = provider.Read(output, firstRead, 512);
        provider.SetSuppressed(false);

        int totalRead = firstRead + suppressedRead;
        while (totalRead < output.Length)
        {
            int count = provider.Read(output, totalRead, Math.Min(317, output.Length - totalRead));
            if (count == 0)
            {
                break;
            }

            totalRead += count;
        }

        Assert.Equal(output.Length, totalRead);
    }

    [Fact]
    public void Capture_ProfilesOnlyTheRequestedSourceSampleRange()
    {
        string path = Path.Combine(Path.GetTempPath(), $"inkmotion-noise-profile-{Guid.NewGuid():N}.wav");
        float[] samples = Enumerable.Range(0, 4096)
            .Select(index => index < 1024 ? 0f : (float)(0.1 * Math.Sin(index * 2 * Math.PI * 1000 / 48000)))
            .ToArray();
        try
        {
            using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(48000, 1)))
            {
                writer.WriteSamples(samples, 0, samples.Length);
            }

            AudioNoiseProfile profile = AudioNoiseProfile.Capture(path, 1024, 4096);

            Assert.True(profile.IsValid);
            Assert.True(profile.MatchesSource(path, 48000, 1));
            Assert.Equal(513, profile.Magnitudes.Length);
            Assert.Contains(profile.Magnitudes, magnitude => magnitude > 0);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

        }
    }

    [Fact]
    public void Capture_RejectsRangesShorterThanOneAnalysisHop()
    {
        string path = Path.Combine(Path.GetTempPath(), $"inkmotion-noise-profile-{Guid.NewGuid():N}.wav");
        try
        {
            using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(48000, 1)))
            {
                writer.WriteSamples(new float[AudioNoiseProfile.HopSize], 0, AudioNoiseProfile.HopSize);
            }

            Assert.Throws<ArgumentOutOfRangeException>(() => AudioNoiseProfile.Capture(path, 0, 100));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
