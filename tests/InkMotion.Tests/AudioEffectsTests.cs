using NAudio.Wave;
using Xunit;

namespace InkMotion.Tests;

public sealed class AudioEffectsTests
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

    private static float[] Tone(double frequency) => Enumerable.Range(0, 48000)
        .Select(index => (float)(0.1 * Math.Sin(index * 2 * Math.PI * frequency / 48000))).ToArray();

    private static double Rms(float[] samples) => Math.Sqrt(samples.Skip(samples.Length / 2).Average(value => (double)value * value));

    private static float[] Process(float[] input, AudioEffectSettings settings)
    {
        var provider = new AudioEffectsSampleProvider(new Samples(input), settings);
        float[] output = new float[input.Length];
        Assert.Equal(output.Length, provider.Read(output, 0, output.Length));
        Assert.All(output, sample => Assert.True(float.IsFinite(sample)));
        return output;
    }

    [Fact]
    public void Bypass_IsBitExactAndIdentityEnabledEffectsRemainNeutral()
    {
        float[] tone = Tone(440);
        Assert.Equal(tone, Process(tone, new() { BassDecibels = 12 }));
        Assert.Equal(tone, Process(tone, new() { Enabled = true }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Filters_AttenuateStopBandAndPreservePassBand(bool highPass)
    {
        var settings = new AudioEffectSettings { Enabled = true, HighPassHz = highPass ? 1000 : 0, LowPassHz = highPass ? 0 : 1000 };
        double pass = Rms(Process(Tone(highPass ? 8000 : 100), settings));
        double stop = Rms(Process(Tone(highPass ? 100 : 8000), settings));
        Assert.InRange(pass, 0.065, 0.075);
        Assert.True(stop < pass / 50, $"Stop band {stop} was not sufficiently below pass band {pass}.");
    }

    [Fact]
    public void Eq_ChangesEachBandByTheRequestedGain()
    {
        Assert.InRange(Rms(Process(Tone(30), new() { Enabled = true, BassDecibels = 12 })) / Rms(Tone(30)), 3.5, 4.2);
        Assert.InRange(Rms(Process(Tone(1000), new() { Enabled = true, MidDecibels = -12 })) / Rms(Tone(1000)), 0.24, 0.27);
        Assert.InRange(Rms(Process(Tone(15000), new() { Enabled = true, TrebleDecibels = 12 })) / Rms(Tone(15000)), 3.5, 4.2);
    }

    [Fact]
    public void Compressor_IsLinkedAcrossChannelsAndHonoursThresholdRatio()
    {
        float[] samples = Enumerable.Range(0, 48000).SelectMany(_ => new[] { 0.8f, 0.2f }).ToArray();
        var provider = new AudioEffectsSampleProvider(new Samples(samples, 2), new()
        {
            Enabled = true,
            CompressorThresholdDecibels = -20,
            CompressorRatio = 4,
            AttackMilliseconds = 1,
        });
        float[] output = new float[samples.Length];
        Assert.Equal(output.Length, provider.Read(output, 0, output.Length));
        Assert.InRange(output[^2], 0.16f, 0.18f);
        Assert.Equal(4, output[^2] / output[^1], 5);
    }

    [Fact]
    public void FilterChannelsHaveIndependentStateAndResetClearsTails()
    {
        float[] samples = new float[2000];
        samples[0] = 1;
        var provider = new AudioEffectsSampleProvider(new Samples(samples, 2), new() { Enabled = true, LowPassHz = 1000 });
        float[] first = new float[20];
        provider.Read(first, 0, first.Length);
        Assert.Contains(first, value => value != 0);
        Assert.All(first.Where((_, index) => index % 2 == 1), value => Assert.Equal(0, value));
        provider.Reset();
        float[] second = new float[200];
        provider.Read(second, 0, second.Length);
        Assert.All(second, value => Assert.Equal(0, value));
    }

    [Fact]
    public void Suppression_IsExactSilenceAndClearsFilterHistory()
    {
        float[] samples = new float[1000];
        samples[0] = 1;
        var provider = new AudioEffectsSampleProvider(new Samples(samples), new() { Enabled = true, LowPassHz = 1000 });
        float[] buffer = new float[10];
        provider.Read(buffer, 0, buffer.Length);
        Assert.Contains(buffer, sample => sample != 0);
        provider.SetSuppressed(true);
        provider.Read(buffer, 0, buffer.Length);
        Assert.All(buffer, sample => Assert.Equal(0, sample));
        provider.SetSuppressed(false);
        provider.Read(buffer, 0, buffer.Length);
        Assert.All(buffer, sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void Compressor_AttackAndReleaseFollowTheSelectedTimeConstants()
    {
        float[] signal = Enumerable.Repeat(0.8f, 4800).Concat(Enumerable.Repeat(0.01f, 48000)).ToArray();
        var settings = new AudioEffectSettings
        {
            Enabled = true, CompressorThresholdDecibels = -20, CompressorRatio = 4,
            AttackMilliseconds = 10, ReleaseMilliseconds = 100,
        };
        float[] output = Process(signal, settings);
        Assert.True(output[0] > 0.79f);
        Assert.InRange(output[4799], 0.16f, 0.18f);
        Assert.True(output[4800] < 0.003f);
        Assert.InRange(output[^1], 0.0099f, 0.0101f);
    }

    [Fact]
    public void Gate_SuppressesQuietPassagesOpensSmoothlyAndReleasesAfterHold()
    {
        var settings = new AudioEffectSettings
        {
            Enabled = true, GateEnabled = true, GateThresholdDecibels = -20,
            GateAttackMilliseconds = 1, GateHoldMilliseconds = 20, GateReleaseMilliseconds = 5,
        };
        float[] signal = Enumerable.Repeat(0.01f, 4800)
            .Concat(Enumerable.Repeat(0.5f, 4800))
            .Concat(Enumerable.Repeat(0.01f, 4800)).ToArray();
        float[] output = Process(signal, settings);
        Assert.All(output.Take(4800), sample => Assert.Equal(0, sample));
        Assert.InRange(output[4800], 0, 0.02f);
        Assert.InRange(output[9599], 0.499f, 0.501f);
        Assert.InRange(output[9600 + 900], 0.0099f, 0.0101f);
        Assert.True(output[^1] < 1e-7f);
    }

    [Fact]
    public void Gate_HysteresisAvoidsChatterAndLinksStereoChannels()
    {
        float[] signal = Enumerable.Range(0, 4800).SelectMany(_ => new[] { 0.5f, 0.005f })
            .Concat(Enumerable.Range(0, 4800).SelectMany(_ => new[] { 0.08f, 0.0008f })).ToArray();
        var provider = new AudioEffectsSampleProvider(new Samples(signal, 2), new()
        {
            Enabled = true, GateEnabled = true, GateThresholdDecibels = -20, GateAttackMilliseconds = 1,
        });
        float[] output = new float[signal.Length];
        provider.Read(output, 0, output.Length);
        Assert.InRange(output[^2], 0.0799f, 0.0801f);
        Assert.Equal(100, output[^2] / output[^1], 3);
    }

    [Fact]
    public void Limiter_ImmediatelyCapsLinkedPeaksAndReleasesSmoothly()
    {
        float[] signal = Enumerable.Range(0, 4800).SelectMany(_ => new[] { 2f, -0.5f })
            .Concat(Enumerable.Range(0, 48000).SelectMany(_ => new[] { 0.1f, -0.025f })).ToArray();
        var provider = new AudioEffectsSampleProvider(new Samples(signal, 2), new()
        {
            Enabled = true, LimiterEnabled = true, LimiterCeilingDecibels = -6, LimiterReleaseMilliseconds = 10,
        });
        float[] output = new float[signal.Length];
        provider.Read(output, 0, output.Length);
        float ceiling = (float)Math.Pow(10, -6d / 20);
        Assert.All(output, sample => Assert.True(Math.Abs(sample) <= ceiling + 1e-7));
        Assert.Equal(ceiling, output[0], 6);
        Assert.Equal(-4, output[0] / output[1], 5);
        Assert.InRange(output[9600], 0.025f, 0.026f);
        Assert.InRange(output[^2], 0.0999f, 0.1001f);
        Assert.Equal(signal, Process(signal, new() { LimiterEnabled = true }));
    }

    [Fact]
    public void Dynamics_ProduceIdenticalResultsAcrossReadBlockSizesAndResetTheirState()
    {
        float[] signal = Enumerable.Repeat(0.5f, 9600).Concat(Enumerable.Repeat(0.001f, 9600)).ToArray();
        var settings = new AudioEffectSettings
        {
            Enabled = true, GateEnabled = true, LimiterEnabled = true,
            CompressorRatio = 4, CompressorThresholdDecibels = -20,
        };
        float[] expected = Process(signal, settings);
        var provider = new AudioEffectsSampleProvider(new Samples(signal), settings);
        float[] actual = new float[signal.Length];
        int read = 0;
        while (read < actual.Length)
        {
            read += provider.Read(actual, read, Math.Min(127, actual.Length - read));
        }

        Assert.Equal(expected, actual);
        provider = new AudioEffectsSampleProvider(new Samples(Enumerable.Repeat(0.5f, 9600).ToArray()), settings);
        var first = new float[4800];
        provider.Read(first, 0, first.Length);
        provider.Reset();
        var second = new float[4800];
        provider.Read(second, 0, second.Length);
        Assert.Equal(first, second);
    }

    [Fact]
    public void VersionTwelve_DefaultsToDisabledGateAndLimiterAndRetainsTheirSettingsOnRoundTrip()
    {
        EditorProject project = EditorProjectSerialiser.Deserialise("""{"Version":12,"AudioTracks":[{}]}""");
        Assert.Equal(EditorProject.CurrentVersion, project.Version);
        Assert.False(project.AudioTracks[0].Effects.GateEnabled);
        Assert.False(project.AudioTracks[0].Effects.LimiterEnabled);
        var effects = project.AudioTracks[0].Effects with
        {
            Enabled = true, GateEnabled = true, GateThresholdDecibels = -30,
            GateAttackMilliseconds = 2, GateHoldMilliseconds = 30, GateReleaseMilliseconds = 50,
            LimiterEnabled = true, LimiterCeilingDecibels = -3, LimiterReleaseMilliseconds = 20,
        };
        project.AudioTracks[0].Effects = effects;
        EditorProject reopened = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        Assert.Equal(effects, reopened.AudioTracks[0].Effects);
        Assert.Equal(effects, AudioClipEditing.Clone(project.AudioTracks[0]).Effects);
        Assert.False((effects with { GateHoldMilliseconds = double.NaN }).IsValid);
        Assert.False((effects with { GateThresholdDecibels = -100 }).IsValid);
        Assert.False((effects with { LimiterCeilingDecibels = 1 }).IsValid);
        Assert.False((effects with { LimiterReleaseMilliseconds = 0 }).IsValid);
    }

    [Fact]
    public void Settings_RoundTripAndOlderProjectsDefaultToBypass()
    {
        EditorProject old = EditorProjectSerialiser.Deserialise("""{"Version":10,"AudioTracks":[{"AudioPath":"voice.wav"}]}""");
        Assert.Equal(EditorProject.CurrentVersion, old.Version);
        Assert.False(old.AudioTracks[0].Effects.Enabled);
        var effects = new AudioEffectSettings { Enabled = true, HighPassHz = 80, BassDecibels = -3, CompressorRatio = 4 };
        old.AudioTracks[0].Effects = effects;
        EditorProject reopened = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(old));
        Assert.Equal(effects, reopened.AudioTracks[0].Effects);
        Assert.Equal(effects, AudioClipEditing.Clone(old.AudioTracks[0]).Effects);
        old.AudioTracks[0].SourceOutSeconds = 3;
        AudioRangeParts parts = AudioRangeEditing.Partition(old.AudioTracks[0], 25, 49, 24, 3);
        Assert.Equal(effects, parts.Before!.Effects);
        Assert.Equal(effects, parts.Range.Effects);
        Assert.Equal(effects, parts.After!.Effects);
        Assert.False((effects with { LowPassHz = 40 }).IsValid);
        Assert.False((effects with { MidDecibels = double.NaN }).IsValid);
        Assert.Throws<InvalidDataException>(() => EditorProjectSerialiser.Deserialise(
            """{"Version":11,"AudioTracks":[{"Effects":null}]}"""));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Export_AppliesTheSameEffectsAsTheSampleProvider(bool dynamics)
    {
        string input = Path.Combine(Path.GetTempPath(), $"inkmotion-effects-{Guid.NewGuid():N}.wav");
        string output = Path.ChangeExtension(input, ".out.wav");
        try
        {
            float[] tone = Tone(8000);
            using (var writer = new WaveFileWriter(input, WaveFormat.CreateIeeeFloatWaveFormat(48000, 1)))
            {
                writer.WriteSamples(tone, 0, tone.Length);
            }

            var effects = dynamics
                ? new AudioEffectSettings { Enabled = true, GateEnabled = true, GateThresholdDecibels = -40, LimiterEnabled = true, LimiterCeilingDecibels = -24 }
                : new AudioEffectSettings { Enabled = true, LowPassHz = 1000 };
            var project = new EditorProject
            {
                AudioMixLayout = AudioChannelLayout.Mono,
                AudioTracks = [new() { AudioPath = input, Effects = effects }],
            };
            AudioExporter.Export(project, new(AudioExportCodec.FloatWave, 48000, 32), output, null, CancellationToken.None);
            using var reader = new AudioFileReader(output);
            float[] decoded = new float[48000];
            Assert.Equal(decoded.Length, reader.Read(decoded, 0, decoded.Length));
            double expected = Rms(Process(tone, effects));
            Assert.InRange(Rms(decoded) / expected, 0.99, 1.01);
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }
}
