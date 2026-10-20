using NAudio.Wave;
using Xunit;

namespace InkMotion.Tests;

public sealed class AudioDelayTests
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
        Enabled = true, DelayEnabled = true, DelayMilliseconds = 1, DelayFeedback = 0.5, DelayWetGain = 0.5,
    };

    [Theory]
    [InlineData(44100, 1)]
    [InlineData(48000, 2)]
    [InlineData(384000, 8)]
    public void ImpulseProducesSampleAccurateDecayingEchoesWithoutChannelLeakage(int rate, int channels)
    {
        int delay = (int)Math.Round(0.001 * rate);
        float[] signal = new float[(delay * 4 + 1) * channels];
        signal[0] = 0.8f;
        var provider = new AudioEffectsSampleProvider(new Samples(signal, rate, channels), Settings);
        float[] output = new float[signal.Length + 6];
        Array.Fill(output, -7);
        Assert.Equal(signal.Length, provider.Read(output, 3, signal.Length));
        Assert.All(output.Take(3).Concat(output.TakeLast(3)), sample => Assert.Equal(-7, sample));
        for (int frame = 0; frame < signal.Length / channels; frame++)
        {
            for (int channel = 0; channel < channels; channel++)
            {
                double expected = channel == 0 && frame % delay == 0
                    ? frame == 0 ? 0.8 : 0.4 * Math.Pow(0.5, frame / delay - 1)
                    : 0;
                Assert.Equal(expected, output[3 + frame * channels + channel], 6);
            }
        }

        Assert.Equal(0, provider.Read(output, 0, 1));
    }

    [Fact]
    public void DelayIsIndependentOfReadBlocksAndBypassIsBitExact()
    {
        float[] signal = Enumerable.Range(0, 2000).Select(index => (float)(0.1 * Math.Sin(index * 0.2))).ToArray();
        var whole = new AudioEffectsSampleProvider(new Samples(signal), Settings);
        float[] expected = new float[signal.Length];
        whole.Read(expected, 0, expected.Length);
        var blocked = new AudioEffectsSampleProvider(new Samples(signal), Settings);
        float[] actual = new float[signal.Length];
        int position = 0;
        while (position < actual.Length)
        {
            position += blocked.Read(actual, position, Math.Min(17, actual.Length - position));
        }

        Assert.Equal(expected, actual);
        var bypass = new AudioEffectsSampleProvider(new Samples(signal), Settings with { Enabled = false });
        bypass.Read(actual, 0, actual.Length);
        Assert.Equal(signal, actual);
        var dry = new AudioEffectsSampleProvider(new Samples(signal), Settings with { DelayWetGain = 0 });
        dry.Read(actual, 0, actual.Length);
        Assert.Equal(signal, actual);
    }

    [Theory]
    [InlineData("reset")]
    [InlineData("suppression")]
    [InlineData("settings")]
    [InlineData("bypass")]
    public void ResetMuteAndSettingChangesClearEchoHistory(string action)
    {
        float[] signal = new float[1000];
        signal[0] = 0.8f;
        var provider = new AudioEffectsSampleProvider(new Samples(signal), Settings);
        provider.Read(new float[24], 0, 24);
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
            case "settings": provider.Update(Settings with { DelayFeedback = 0.3 }); break;
            case "bypass":
                provider.Update(Settings with { Enabled = false });
                provider.Update(Settings);
                break;
        }

        float[] remaining = new float[900];
        provider.Read(remaining, 0, remaining.Length);
        Assert.All(remaining, value => Assert.Equal(0, value));
    }

    [Fact]
    public void LimiterCapsTheCombinedDryAndEchoSignal()
    {
        var provider = new AudioEffectsSampleProvider(new Samples(Enumerable.Repeat(0.8f, 4800).ToArray()),
            Settings with { DelayWetGain = 1, DelayFeedback = 0.95, LimiterEnabled = true, LimiterCeilingDecibels = -6 });
        float[] output = new float[4800];
        provider.Read(output, 0, output.Length);
        Assert.All(output, value => Assert.InRange(value, 0, (float)Math.Pow(10, -6d / 20) + 1e-7f));
    }

    [Fact]
    public void SettingsMigrateRoundTripCloneAndValidate()
    {
        EditorProject project = EditorProjectSerialiser.Deserialise("""{"Version":14,"AudioTracks":[{}]}""");
        Assert.Equal(EditorProject.CurrentVersion, project.Version);
        Assert.False(project.AudioTracks[0].Effects.DelayEnabled);
        project.AudioTracks[0].Effects = Settings;
        Assert.Equal(Settings, AudioClipEditing.Clone(project.AudioTracks[0]).Effects);
        Assert.Equal(Settings, EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project)).AudioTracks[0].Effects);
        AudioTrackProject clip = project.AudioTracks[0];
        clip.SourceOutSeconds = 3;
        AudioRangeParts parts = AudioRangeEditing.Partition(clip, 25, 49, 24, 3);
        Assert.Equal(Settings, parts.Before!.Effects);
        Assert.Equal(Settings, parts.Range.Effects);
        Assert.Equal(Settings, parts.After!.Effects);
        foreach (AudioEffectSettings invalid in new[]
        {
            Settings with { DelayMilliseconds = 0 },
            Settings with { DelayMilliseconds = 2001 },
            Settings with { DelayMilliseconds = double.NaN },
            Settings with { DelayFeedback = 1 },
            Settings with { DelayFeedback = -0.1 },
            Settings with { DelayWetGain = 1.1 },
            Settings with { DelayWetGain = double.PositiveInfinity },
        })
        {
            Assert.False(invalid.IsValid);
            Assert.Throws<ArgumentException>(() => new AudioEffectsSampleProvider(new Samples([]), invalid));
            project.AudioTracks[0].Effects = invalid;
            Assert.Throws<InvalidDataException>(() => EditorProjectSerialiser.Serialise(project));
        }
    }

    [Fact]
    public void ExportMatchesThePreviewProcessorWithoutExtendingTheClip()
    {
        string input = Path.Combine(Path.GetTempPath(), $"inkmotion-delay-{Guid.NewGuid():N}.wav");
        string output = Path.ChangeExtension(input, ".out.wav");
        try
        {
            float[] signal = new float[4800];
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
            var processor = new AudioEffectsSampleProvider(new Samples(signal), Settings);
            float[] expected = new float[signal.Length];
            processor.Read(expected, 0, expected.Length);
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
