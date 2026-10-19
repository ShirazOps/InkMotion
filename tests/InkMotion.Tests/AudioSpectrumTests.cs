using NAudio.Wave;
using Xunit;

namespace InkMotion.Tests;

public sealed class AudioSpectrumTests
{
    private static float[] Tone(int frames, int bin, float amplitude) => Enumerable.Range(0, frames)
        .Select(index => amplitude * (float)Math.Sin(2 * Math.PI * bin * index / frames)).ToArray();

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(384000)]
    public void Spectrum_FindsBinFrequencyAndCorrectFullScaleAmplitude(int rate)
    {
        const int size = 32768;
        AudioSpectrumResult result = AudioSpectrum.Analyse(Tone(size, 1000, 0.5f), 1, rate);
        Assert.Equal(size, result.Frames);
        Assert.Equal(1000 * (double)rate / size, result.DominantFrequencyHz, 6);
        Assert.Equal(20 * Math.Log10(0.5), result.Decibels[1000], 3);
        Assert.Equal(0.5 / Math.Sqrt(2), result.Rms, 5);
        Assert.InRange(result.Peak, 0.499, 0.501);
    }

    [Fact]
    public void Spectrum_AveragesChannelPowerWithoutOppositePhaseCancellation()
    {
        float[] mono = Tone(1024, 64, 0.5f);
        float[] stereo = mono.SelectMany(value => new[] { value, -value }).ToArray();
        AudioSpectrumResult result = AudioSpectrum.Analyse(stereo, 2, 48000);
        Assert.Equal(3000, result.DominantFrequencyHz);
        Assert.Equal(20 * Math.Log10(0.5), result.Decibels[64], 3);
    }

    [Fact]
    public void Spectrum_HandlesSilenceDcAndShortWindowsAndRejectsInvalidSamples()
    {
        AudioSpectrumResult silent = AudioSpectrum.Analyse(new float[100], 1, 48000);
        Assert.Equal(64, silent.Frames);
        Assert.Equal(0, silent.Rms);
        Assert.All(silent.Decibels, value => Assert.Equal(-240, value));
        AudioSpectrumResult dc = AudioSpectrum.Analyse(Enumerable.Repeat(0.25f, 1024).ToArray(), 1, 48000);
        Assert.Equal(20 * Math.Log10(0.25), dc.Decibels[0], 3);
        Assert.Throws<ArgumentException>(() => AudioSpectrum.Analyse(new float[31], 1, 48000));
        Assert.Throws<ArgumentException>(() => AudioSpectrum.Analyse(new float[65], 2, 48000));
        Assert.Throws<ArgumentException>(() => AudioSpectrum.Analyse(new float[64], 0, 48000));
        float[] invalid = new float[64];
        invalid[20] = float.NaN;
        Assert.Throws<InvalidDataException>(() => AudioSpectrum.Analyse(invalid, 1, 48000));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => AudioSpectrum.Analyse(new float[64], 1, 48000, cancellation.Token));
    }

    [Fact]
    public void Read_UsesTheRequestedSourceRangeAndDoesNotReadBeyondIt()
    {
        string path = Path.Combine(Path.GetTempPath(), $"inkmotion-spectrum-{Guid.NewGuid():N}.wav");
        try
        {
            float[] first = Tone(32768, 100, 0.5f);
            float[] second = Tone(32768, 2000, 0.25f);
            using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(48000, 1)))
            {
                writer.WriteSamples(first, 0, first.Length);
                writer.WriteSamples(second, 0, second.Length);
            }

            AudioSpectrumResult result = AudioSpectrum.Read(path, 32768d / 48000, 1024d / 48000, CancellationToken.None);
            Assert.Equal(1024, result.Frames);
            Assert.InRange(result.DominantFrequencyHz, 2900, 3000);
            Assert.InRange(result.Peak, 0.24, 0.26);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
