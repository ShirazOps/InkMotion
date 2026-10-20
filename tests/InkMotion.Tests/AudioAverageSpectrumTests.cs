using NAudio.Wave;
using Xunit;

namespace InkMotion.Tests;

public sealed class AudioAverageSpectrumTests
{
    private static float[] Tone(int size, int bin, float amplitude) => Enumerable.Range(0, size)
        .Select(index => amplitude * (float)Math.Sin(2 * Math.PI * bin * index / size)).ToArray();

    private static void WithSource(float[] samples, int channels, Action<string> test)
    {
        string path = Path.Combine(Path.GetTempPath(), $"inkmotion-average-spectrum-{Guid.NewGuid():N}.wav");
        try
        {
            using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(48000, channels)))
            {
                writer.WriteSamples(samples, 0, samples.Length);
            }

            test(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AveragesPowerAcrossTheEntireRangeRatherThanOnlyItsFirstWindowOrDbLevels()
    {
        const int size = AudioSpectrum.MaximumFrames;
        float[] first = Tone(size, 100, 0.25f);
        float[] second = Tone(size, 2000, 0.75f);
        WithSource(first.Concat(second).ToArray(), 1, path =>
        {
            AudioSpectrumResult result = AudioSpectrum.ReadAverage(path, 0, size * 2d / 48000, default);
            Assert.Equal(size, result.Frames);
            Assert.Equal(size * 2, result.AnalysedFrames);
            Assert.Equal(2, result.Windows);
            Assert.Equal(2000d * 48000 / size, result.DominantFrequencyHz, 6);
            AudioSpectrumResult firstSpectrum = AudioSpectrum.Analyse(first, 1, 48000);
            AudioSpectrumResult secondSpectrum = AudioSpectrum.Analyse(second, 1, 48000);
            foreach (int bin in new[] { 100, 2000 })
            {
                double power = (Math.Pow(10, firstSpectrum.Decibels[bin] / 10) +
                    Math.Pow(10, secondSpectrum.Decibels[bin] / 10)) / 2;
                Assert.Equal(10 * Math.Log10(power), result.Decibels[bin], 8);
            }

            Assert.InRange(result.Decibels[100] - 10 * Math.Log10(0.25 * 0.25 / 2), -0.01, 0.01);
            Assert.InRange(result.Decibels[2000] - 10 * Math.Log10(0.75 * 0.75 / 2), -0.01, 0.01);
            Assert.Equal(Math.Sqrt((0.25 * 0.25 + 0.75 * 0.75) / 4), result.Rms, 5);
            Assert.InRange(result.Peak, 0.749, 0.751);
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(1023)]
    public void IncludesFinalPartialWindowInPeakRmsAndCounts(int tailFrames)
    {
        const int size = AudioSpectrum.MaximumFrames;
        float[] source = new float[size + tailFrames + 100];
        Array.Fill(source, 0.5f, size, tailFrames);
        Array.Fill(source, 1f, size + tailFrames, 100);
        WithSource(source, 1, path =>
        {
            AudioSpectrumResult result = AudioSpectrum.ReadAverage(path, 0, (size + tailFrames) / 48000d, default);
            Assert.Equal(size + tailFrames, result.AnalysedFrames);
            Assert.Equal(2, result.Windows);
            Assert.Equal(0.5, result.Peak);
            Assert.Equal(Math.Sqrt(0.25 * tailFrames / (size + tailFrames)), result.Rms, 6);
            Assert.All(result.Decibels, level => Assert.True(double.IsFinite(level)));
        });
    }

    [Fact]
    public void UsesSelectedSourceRangeAndAveragesChannelPowerWithoutPhaseCancellation()
    {
        const int size = 1024;
        float[] first = Tone(size, 64, 0.25f);
        float[] second = Tone(size, 128, 0.5f);
        float[] stereo = first.Concat(second).SelectMany(value => new[] { value, -value }).ToArray();
        WithSource(stereo, 2, path =>
        {
            AudioSpectrumResult result = AudioSpectrum.ReadAverage(path, size / 48000d, size / 48000d, default);
            Assert.Equal(size, result.AnalysedFrames);
            Assert.Equal(6000, result.DominantFrequencyHz);
            Assert.Equal(20 * Math.Log10(0.5), result.Decibels[128], 3);
            Assert.Equal(0.5 / Math.Sqrt(2), result.Rms, 5);
        });
    }

    [Fact]
    public void MatchesSingleSnapshotForOneCompleteWindowAndHandlesSilence()
    {
        float[] signal = Tone(1024, 64, 0.5f);
        AudioSpectrumResult expected = AudioSpectrum.Analyse(signal, 1, 48000);
        WithSource(signal, 1, path =>
        {
            AudioSpectrumResult result = AudioSpectrum.ReadAverage(path, 0, 1024d / 48000, default);
            Assert.Equal(expected.Peak, result.Peak);
            Assert.Equal(expected.Rms, result.Rms);
            Assert.Equal(expected.Decibels, result.Decibels);
        });
        WithSource(new float[AudioSpectrum.MaximumFrames * 2], 1, path =>
        {
            AudioSpectrumResult result = AudioSpectrum.ReadAverage(path, 0, 10, default);
            Assert.Equal(AudioSpectrum.MaximumFrames * 2, result.AnalysedFrames);
            Assert.Equal(0, result.Peak);
            Assert.Equal(0, result.Rms);
            Assert.All(result.Decibels, level => Assert.Equal(-240, level, 8));
        });
    }

    [Fact]
    public void RejectsInvalidRangesCorruptSamplesAndCancellation()
    {
        WithSource(new float[64], 1, path =>
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => AudioSpectrum.ReadAverage(path, -1, 1, default));
            Assert.Throws<ArgumentOutOfRangeException>(() => AudioSpectrum.ReadAverage(path, 0, double.NaN, default));
            Assert.Throws<ArgumentException>(() => AudioSpectrum.ReadAverage(path, 1, 1, default));
            Assert.Throws<ArgumentException>(() => AudioSpectrum.ReadAverage(path, 0, 1d / 48000, default));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => AudioSpectrum.ReadAverage(path, 0, 1, cancellation.Token));
        });
        float[] corrupt = new float[AudioSpectrum.MaximumFrames + 100];
        corrupt[^1] = float.NaN;
        WithSource(corrupt, 1, path =>
            Assert.Throws<InvalidDataException>(() => AudioSpectrum.ReadAverage(path, 0, 10, default)));
    }
}
