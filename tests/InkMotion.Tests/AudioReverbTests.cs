using NAudio.Wave;
using Xunit;

namespace InkMotion.Tests;

public sealed class AudioReverbTests
{
    private sealed class Samples(float[] data, int rate = 48000, int channels = 1) : ISampleProvider
    {
        private int _position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
        public int Read(float[] buffer, int offset, int count)
        {
            int read = Math.Min(count, data.Length - _position);
            Array.Copy(data, _position, buffer, offset, read);
            _position += read;
            return read;
        }
    }

    private static AudioEffectSettings Settings => new()
    {
        Enabled = true, ReverbEnabled = true, ReverbWetGain = 0.5,
    };

    [Theory]
    [InlineData(44100, 1, 0.5)]
    [InlineData(48000, 2, 1)]
    [InlineData(384000, 8, 2)]
    public void ImpulseRetainsDryAndProducesSampleRoundedReverbWithoutChannelLeakage(int rate, int channels, double scale)
    {
        int firstReflection = (int)Math.Round(0.0297 * scale * rate);
        float[] signal = new float[(firstReflection + 1) * channels];
        signal[0] = 0.8f;
        var provider = new AudioEffectsSampleProvider(new Samples(signal, rate, channels),
            Settings with { ReverbRoomScale = scale });
        float[] output = Enumerable.Repeat(-7f, signal.Length + 6).ToArray();
        Assert.Equal(signal.Length, provider.Read(output, 3, signal.Length));
        Assert.All(output.Take(3).Concat(output.TakeLast(3)), value => Assert.Equal(-7, value));
        for (int frame = 0; frame <= firstReflection; frame++)
        {
            for (int channel = 0; channel < channels; channel++)
            {
                double expected = channel != 0 ? 0 : frame == 0 ? 0.8 : frame == firstReflection ? 0.025 : 0;
                Assert.Equal(expected, output[3 + frame * channels + channel], 6);
            }
        }

        Assert.Equal(0, provider.Read(output, 0, channels));
    }

    [Fact]
    public void BlockSizeDoesNotChangeReverbAndBypassOrZeroWetGainIsBitExact()
    {
        float[] signal = Enumerable.Range(0, 16000).Select(index => (float)(0.1 * Math.Sin(index * 0.2))).ToArray();
        var whole = new AudioEffectsSampleProvider(new Samples(signal), Settings);
        float[] expected = new float[signal.Length];
        whole.Read(expected, 0, expected.Length);
        var blocked = new AudioEffectsSampleProvider(new Samples(signal), Settings);
        float[] actual = new float[signal.Length];
        int position = 0;
        while (position < actual.Length)
        {
            position += blocked.Read(actual, position, Math.Min(137, actual.Length - position));
        }

        Assert.Equal(expected, actual);
        foreach (AudioEffectSettings drySettings in new[]
        {
            Settings with { Enabled = false },
            Settings with { ReverbEnabled = false },
            Settings with { ReverbWetGain = 0 },
        })
        {
            var dry = new AudioEffectsSampleProvider(new Samples(signal), drySettings);
            dry.Read(actual, 0, actual.Length);
            Assert.Equal(signal, actual);
        }
    }

    [Theory]
    [InlineData("reset")]
    [InlineData("suppression")]
    [InlineData("settings")]
    [InlineData("bypass")]
    public void ResetMuteAndSettingChangesClearReverbHistory(string action)
    {
        float[] signal = new float[16000];
        signal[0] = 0.8f;
        var provider = new AudioEffectsSampleProvider(new Samples(signal), Settings);
        provider.Read(new float[100], 0, 100);
        switch (action)
        {
            case "reset": provider.Reset(); break;
            case "suppression":
                provider.SetSuppressed(true);
                float[] muted = new float[24];
                provider.Read(muted, 0, muted.Length);
                Assert.All(muted, value => Assert.Equal(0, value));
                provider.SetSuppressed(false);
                break;
            case "settings": provider.Update(Settings with { ReverbDecaySeconds = 3 }); break;
            case "bypass":
                provider.Update(Settings with { Enabled = false });
                provider.Update(Settings);
                break;
        }

        float[] remaining = new float[16000];
        provider.Read(remaining, 0, remaining.Length);
        Assert.All(remaining, value => Assert.Equal(0, value));
    }

    [Fact]
    public void DecayChangesTailLengthAndDampingReducesHighFrequencyTailEnergy()
    {
        float[] impulse = new float[48000 * 3];
        impulse[0] = 0.8f;
        float[] Process(double decay, double damping)
        {
            var provider = new AudioEffectsSampleProvider(new Samples(impulse),
                Settings with { ReverbDecaySeconds = decay, ReverbDamping = damping });
            float[] result = new float[impulse.Length];
            provider.Read(result, 0, result.Length);
            Assert.All(result, value => Assert.True(float.IsFinite(value)));
            return result;
        }

        static double Energy(float[] signal, int start, int end) =>
            signal.Skip(start).Take(end - start).Sum(value => (double)value * value);
        static double HighFrequencyEnergy(float[] signal) =>
            Enumerable.Range(4801, signal.Length - 4801).Sum(i => Math.Pow(signal[i] - signal[i - 1], 2));

        float[] shortTail = Process(0.1, 0);
        float[] longTail = Process(3, 0);
        float[] damped = Process(3, 0.9);
        Assert.True(Energy(longTail, 48000, 96000) > Energy(shortTail, 48000, 96000) * 1000);
        Assert.True(Energy(shortTail, 96000, impulse.Length) < Energy(shortTail, 2400, 9600) * 1e-8);
        Assert.True(HighFrequencyEnergy(damped) < HighFrequencyEnergy(longTail));
    }

    [Fact]
    public void LimiterCapsCombinedDryReverbAndDelayOutput()
    {
        var provider = new AudioEffectsSampleProvider(new Samples(Enumerable.Repeat(0.8f, 48000).ToArray()),
            Settings with
            {
                ReverbWetGain = 1, ReverbDecaySeconds = 10, DelayEnabled = true,
                DelayFeedback = 0.95, DelayWetGain = 1, LimiterEnabled = true, LimiterCeilingDecibels = -6,
            });
        float[] output = new float[48000];
        provider.Read(output, 0, output.Length);
        Assert.All(output, value => Assert.InRange(value, -(float)Math.Pow(10, -6d / 20) - 1e-7f, (float)Math.Pow(10, -6d / 20) + 1e-7f));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(14)]
    [InlineData(15)]
    public void OlderProjectsMigrateWithReverbDisabled(int version)
    {
        EditorProject project = EditorProjectSerialiser.Deserialise($$$"""{"Version":{{{version}}},"AudioTracks":[{"Effects":{"Enabled":true}}]}""");
        Assert.Equal(EditorProject.CurrentVersion, project.Version);
        Assert.False(project.AudioTracks[0].Effects.ReverbEnabled);
    }

    [Fact]
    public void SettingsRoundTripClonePartitionAndValidate()
    {
        var settings = Settings with
        {
            ReverbRoomScale = 1.7, ReverbDecaySeconds = 3, ReverbDamping = 0.8, ReverbWetGain = 0.7,
        };
        var project = new EditorProject { AudioTracks = [new() { SourceOutSeconds = 3, Effects = settings }] };
        Assert.Equal(settings, EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project)).AudioTracks[0].Effects);
        Assert.Equal(settings, AudioClipEditing.Clone(project.AudioTracks[0]).Effects);
        AudioRangeParts parts = AudioRangeEditing.Partition(project.AudioTracks[0], 25, 49, 24, 3);
        Assert.All(new[] { parts.Before!, parts.Range, parts.After! }, clip => Assert.Equal(settings, clip.Effects));
        foreach (AudioEffectSettings invalid in new[]
        {
            settings with { ReverbRoomScale = 0.49 },
            settings with { ReverbRoomScale = 2.01 },
            settings with { ReverbRoomScale = double.NaN },
            settings with { ReverbDecaySeconds = 0 },
            settings with { ReverbDecaySeconds = 10.1 },
            settings with { ReverbDecaySeconds = double.PositiveInfinity },
            settings with { ReverbDamping = -0.1 },
            settings with { ReverbDamping = 1 },
            settings with { ReverbDamping = double.NaN },
            settings with { ReverbWetGain = -0.1 },
            settings with { ReverbWetGain = 1.1 },
            settings with { ReverbWetGain = double.NaN },
        })
        {
            Assert.False(invalid.IsValid);
            Assert.Throws<ArgumentException>(() => new AudioEffectsSampleProvider(new Samples([]), invalid));
            project.AudioTracks[0].Effects = invalid;
            Assert.Throws<InvalidDataException>(() => EditorProjectSerialiser.Serialise(project));
        }
    }

    [Fact]
    public void FloatWaveExportMatchesPreviewAndDoesNotExtendClipForReverbTail()
    {
        string input = Path.Combine(Path.GetTempPath(), $"inkmotion-reverb-{Guid.NewGuid():N}.wav");
        string output = Path.ChangeExtension(input, ".out.wav");
        try
        {
            float[] signal = new float[9600];
            signal[0] = 0.2f;
            signal[^1] = 0.3f;
            using (var writer = new WaveFileWriter(input, WaveFormat.CreateIeeeFloatWaveFormat(48000, 1)))
            {
                writer.WriteSamples(signal, 0, signal.Length);
            }

            var project = new EditorProject
            {
                AudioMixLayout = AudioChannelLayout.Mono,
                AudioTracks = [new() { AudioPath = input, Effects = Settings }],
            };
            AudioExporter.Export(project, new(AudioExportCodec.FloatWave, 48000, 32), output, null, CancellationToken.None);
            float[] expected = new float[signal.Length];
            var provider = new AudioEffectsSampleProvider(new Samples(signal), Settings);
            provider.Read(expected, 0, expected.Length);
            Assert.Contains(expected.Skip(1).Take(signal.Length - 2), value => value != 0);
            using var reader = new AudioFileReader(output);
            float[] actual = new float[signal.Length];
            Assert.Equal(actual.Length, reader.Read(actual, 0, actual.Length));
            Assert.Equal(0, reader.Read(new float[100], 0, 100));
            Assert.Equal(expected, actual);
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }
}
