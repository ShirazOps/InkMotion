using NAudio.Wave;
using Xunit;

namespace InkMotion.Tests;

public sealed class ReverseSampleProviderTests
{
    [Theory]
    [InlineData(0.5, 96000)]
    [InlineData(2, 24000)]
    public void PlaybackSpeedProvider_ChangesOutputDuration(double speed, int expectedSamples)
    {
        var source = new SeekableMemorySampleProvider(new float[48000], sampleRate: 48000, channels: 1);
        var provider = new PlaybackSpeedSampleProvider(source, speed);
        float[] buffer = new float[4096];
        int totalSamples = 0;

        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            totalSamples += read;
        }

        Assert.Equal(48000, provider.WaveFormat.SampleRate);
        Assert.InRange(totalSamples, expectedSamples - 2, expectedSamples + 2);
    }

    [Fact]
    public void Read_ReturnsSourceRangeInReverseFrameOrder()
    {
        var source = new SeekableMemorySampleProvider(
            [0, 1, 2, 3, 4, 5, 6, 7],
            sampleRate: 4,
            channels: 1);
        var provider = new ReverseSampleProvider(
            source,
            seconds => source.Position = (int)(seconds * source.WaveFormat.SampleRate),
            startSeconds: 1,
            endSeconds: 2);
        float[] output = new float[8];

        int read = provider.Read(output, 0, output.Length);

        Assert.Equal(4, read);
        Assert.Equal([7, 6, 5, 4], output[..read]);
    }

    private sealed class SeekableMemorySampleProvider(float[] samples, int sampleRate, int channels) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        public int Position { get; set; }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = Math.Min(count, samples.Length - Position);
            Array.Copy(samples, Position, buffer, offset, read);
            Position += read;
            return read;
        }
    }
}
