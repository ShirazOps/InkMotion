using NAudio.Wave;
using Xunit;

namespace InkMotion.Tests;

public sealed class AudioTimeStretchTests
{
    private const int SampleRate = 48000;

    [Fact]
    public void UnitySettings_PassAudioThroughUnchanged()
    {
        var source = new SineSource(440, channels: 2);
        var reference = new SineSource(440, channels: 2);
        var stretch = new TimeStretchSampleProvider(source);
        var output = new float[4800];
        var expected = new float[4800];

        stretch.Read(output, 0, output.Length);
        reference.Read(expected, 0, expected.Length);

        Assert.Equal(expected, output);
        Assert.Equal(0.05, stretch.PositionSeconds, 6);
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(1.5)]
    [InlineData(0.75)]
    [InlineData(0.5)]
    public void Tempo_ChangesDurationButKeepsPitch(double tempo)
    {
        var source = new SineSource(440, channels: 2);
        var stretch = new TimeStretchSampleProvider(source);
        stretch.SetParameters(tempo, 0);

        float[] output = ReadSeconds(stretch, 1.0);

        Assert.Equal(440, EstimateFrequency(output, 2), 440 * 0.02);
        Assert.Equal(tempo, stretch.PositionSeconds, 3);
        Assert.InRange(source.FramesRead / (double)SampleRate, tempo, tempo + 0.1);
        Assert.True(Rms(output, 2) > 0.6, "Stretched audio lost too much level.");
    }

    [Theory]
    [InlineData(12, 880)]
    [InlineData(-12, 220)]
    [InlineData(7, 659.26)]
    public void Pitch_ShiftsFrequencyButKeepsDuration(double semitones, double expectedFrequency)
    {
        var source = new SineSource(440, channels: 1);
        var stretch = new TimeStretchSampleProvider(source);
        stretch.SetParameters(1, semitones);

        float[] output = ReadSeconds(stretch, 1.0);

        Assert.Equal(expectedFrequency, EstimateFrequency(output, 1), expectedFrequency * 0.02);
        Assert.Equal(1.0, stretch.PositionSeconds, 3);
        Assert.InRange(source.FramesRead / (double)SampleRate, 1.0, 1.1);
    }

    [Fact]
    public void TempoAndPitchCombine()
    {
        var stretch = new TimeStretchSampleProvider(new SineSource(440, channels: 2));
        stretch.SetParameters(1.25, -5);

        float[] output = ReadSeconds(stretch, 1.0);

        Assert.Equal(440 * Math.Pow(2, -5 / 12d), EstimateFrequency(output, 2), 440 * 0.02);
        Assert.Equal(1.25, stretch.PositionSeconds, 3);
    }

    [Fact]
    public void Seek_ResetsPositionAndSeeksSource()
    {
        double? sought = null;
        var stretch = new TimeStretchSampleProvider(new SineSource(440, channels: 2), seconds => sought = seconds);
        stretch.SetParameters(2, 3);
        ReadSeconds(stretch, 0.25);

        stretch.Seek(12.5);

        Assert.Equal(12.5, sought);
        Assert.Equal(12.5, stretch.PositionSeconds, 6);
        ReadSeconds(stretch, 0.5);
        Assert.Equal(13.5, stretch.PositionSeconds, 3);
    }

    [Fact]
    public void SetParameters_KeepsCurrentSourcePosition()
    {
        double? sought = null;
        var stretch = new TimeStretchSampleProvider(new SineSource(440, channels: 1), seconds => sought = seconds);
        ReadSeconds(stretch, 0.5);

        stretch.SetParameters(1.5, 0);

        Assert.Equal(0.5, sought!.Value, 6);
        Assert.Equal(0.5, stretch.PositionSeconds, 6);
        Assert.Throws<ArgumentOutOfRangeException>(() => stretch.SetParameters(0, 0));
    }

    [Fact]
    public void ClipEditing_UsesTimelineDurationForTempo()
    {
        var track = new AudioTrackProject { StartFrame = 1, TempoPercent = 200, FadeInSeconds = 1, FadeOutSeconds = 2 };

        Assert.Equal(5, AudioClipEditing.GetTimelineDuration(track, 10), 6);
        AudioClipGainSettings settings = AudioClipEditing.CreateGainSettings(track);
        Assert.Equal(2, settings.FadeInSeconds, 6);
        Assert.Equal(4, settings.FadeOutSeconds, 6);

        AudioTrackProject? right = AudioClipEditing.Split(track, playheadFrameNumber: 25, frameRate: 24, sourceDurationSeconds: 10);

        Assert.NotNull(right);
        Assert.Equal(2, track.SourceOutSeconds!.Value, 6);
        Assert.Equal(2, right.SourceInSeconds, 6);
        Assert.Equal(200, right.TempoPercent);
        Assert.Equal(4, AudioClipEditing.GetTimelineDuration(right, 10), 6);
    }

    [Fact]
    public void ClampFades_UsesTimelineDuration()
    {
        var track = new AudioTrackProject { TempoPercent = 200, FadeInSeconds = 4, FadeOutSeconds = 4 };

        AudioClipEditing.ClampFades(track, sourceDurationSeconds: 8);

        Assert.Equal(2, track.FadeInSeconds, 6);
        Assert.Equal(2, track.FadeOutSeconds, 6);
    }

    private static float[] ReadSeconds(ISampleProvider provider, double seconds)
    {
        int channels = provider.WaveFormat.Channels;
        var output = new float[(int)(seconds * SampleRate) * channels];
        int offset = 0;
        while (offset < output.Length)
        {
            int count = Math.Min(1024 * channels, output.Length - offset);
            offset += provider.Read(output, offset, count);
        }

        return output;
    }

    private static double EstimateFrequency(float[] interleaved, int channels)
    {
        int frames = interleaved.Length / channels;
        int skip = SampleRate / 10;
        int crossings = 0;
        int first = -1;
        int last = -1;
        for (int frame = skip + 1; frame < frames; frame++)
        {
            float previous = interleaved[(frame - 1) * channels];
            float current = interleaved[frame * channels];
            if (previous < 0 && current >= 0)
            {
                if (first < 0)
                {
                    first = frame;
                }
                else
                {
                    crossings++;
                }

                last = frame;
            }
        }

        return crossings / ((last - first) / (double)SampleRate);
    }

    private static double Rms(float[] interleaved, int channels)
    {
        double sum = 0;
        int count = 0;
        for (int index = SampleRate / 10 * channels; index < interleaved.Length; index += channels)
        {
            sum += interleaved[index] * interleaved[index];
            count++;
        }

        return Math.Sqrt(sum / count) * Math.Sqrt(2);
    }

    private sealed class SineSource(double frequency, int channels) : ISampleProvider
    {
        private long _frame;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, channels);

        public long FramesRead => _frame;

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / channels;
            for (int frame = 0; frame < frames; frame++, _frame++)
            {
                float value = (float)Math.Sin(2 * Math.PI * frequency * _frame / SampleRate);
                for (int channel = 0; channel < channels; channel++)
                {
                    buffer[offset + frame * channels + channel] = value;
                }
            }

            return frames * channels;
        }
    }
}
