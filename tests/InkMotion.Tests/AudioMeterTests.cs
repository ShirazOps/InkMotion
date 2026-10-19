using NAudio.Wave;
using Xunit;

namespace InkMotion.Tests;

public sealed class AudioMeterTests
{
    private sealed class Samples : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public int Read(float[] buffer, int offset, int count)
        {
            float[] data = [0.5f, -0.5f, 1.2f, -0.2f];
            Array.Copy(data, 0, buffer, offset, data.Length);
            return data.Length;
        }
    }

    [Fact]
    public void Meter_AccumulatesCorrectPeakRmsAndLatchedClippingWithoutChangingSamples()
    {
        var meter = new AudioMeterSampleProvider(new Samples());
        float[] buffer = new float[10];
        Assert.Equal(4, meter.Read(buffer, 3, 4));
        Assert.Equal(new[] { 0.5f, -0.5f, 1.2f, -0.2f }, buffer.Skip(3).Take(4));
        AudioMeterReading reading = meter.TakeReading();
        Assert.Equal(1.2f, reading.Peak);
        Assert.Equal(Math.Sqrt((0.25 + 0.25 + 1.44 + 0.04) / 4), reading.Rms, 6);
        Assert.True(reading.Clipped);
        Assert.True(reading.PeakDecibels > 0);
        Assert.True(meter.TakeReading().Clipped);
        meter.TakeReading(resetClip: true);
        AudioMeterReading empty = meter.TakeReading();
        Assert.False(empty.Clipped);
        Assert.Equal(double.NegativeInfinity, empty.PeakDecibels);
        Assert.Equal(double.NegativeInfinity, empty.RmsDecibels);
    }
}
