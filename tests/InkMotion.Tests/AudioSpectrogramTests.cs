using NAudio.Wave;
using Xunit;

namespace InkMotion.Tests;

public sealed class AudioSpectrogramTests : IDisposable
{
    private readonly List<string> _paths = [];

    private string Write(float[] samples, int channels = 1)
    {
        string path = Path.Combine(Path.GetTempPath(), $"inkmotion-spectrogram-{Guid.NewGuid():N}.wav");
        _paths.Add(path);
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(48000, channels));
        writer.WriteSamples(samples, 0, samples.Length);
        return path;
    }

    private static float Tone(int frame, int bin) =>
        (float)(0.5 * Math.Sin(2 * Math.PI * bin * frame / 1024d));

    [Fact]
    public void SeparatesFrequencyChangesOverTimeWithoutCancellingStereo()
    {
        float[] samples = new float[4096];
        for (int frame = 0; frame < 2048; frame++)
        {
            float value = Tone(frame, frame < 1024 ? 16 : 128);
            samples[frame * 2] = value;
            samples[frame * 2 + 1] = -value;
        }
        AudioSpectrogramResult result = AudioSpectrogram.Read(Write(samples, 2), 0, 2048d / 48000, CancellationToken.None);
        Assert.Equal(1024, result.FftFrames);
        Assert.Equal(2048, result.SampleFrames);
        Assert.Equal(new long[] { 1024, 1024 }, result.ColumnFrames);
        Assert.Equal(2, result.Decibels.Length);
        Assert.Equal(16, StrongestBin(result.Decibels[0]));
        Assert.Equal(128, StrongestBin(result.Decibels[1]));
        Assert.Equal(20 * Math.Log10(0.5), result.Decibels[0][16], 4);
        Assert.Equal(46.875, result.BinWidthHz);
    }

    [Fact]
    public void BoundsTimeColumnsAndIncludesTheFinalPartialWindow()
    {
        const int frames = 1024 * 300 + 17;
        float[] samples = new float[frames];
        Array.Fill(samples, 0.5f, frames - 17, 17);
        AudioSpectrogramResult result = AudioSpectrogram.Read(Write(samples), 0, frames / 48000d, CancellationToken.None);
        Assert.Equal(AudioSpectrogram.MaximumColumns, result.Decibels.Length);
        Assert.Equal(frames, result.ColumnFrames.Sum());
        Assert.All(result.ColumnFrames, count => Assert.True(count > 0));
        Assert.All(result.Decibels, column => Assert.Equal(513, column.Length));
        Assert.True(result.Decibels[^1][0] > -60);
        Assert.All(result.Decibels[0], level => Assert.Equal(-240f, level));
    }

    [Fact]
    public void AggregatesConsecutiveWindowPowerInLinearUnits()
    {
        const int windows = 512;
        float[] samples = new float[windows * 1024];
        for (int frame = 0; frame < samples.Length; frame++)
        {
            float amplitude = frame / 1024 % 2 == 0 ? 0.25f : 0.75f;
            samples[frame] = amplitude * 2 * Tone(frame, 16);
        }
        AudioSpectrogramResult result = AudioSpectrogram.Read(Write(samples), 0,
            samples.Length / 48000d, CancellationToken.None);
        double expected = 10 * Math.Log10((0.25 * 0.25 + 0.75 * 0.75) / 2);
        Assert.All(result.ColumnFrames, count => Assert.Equal(2048, count));
        Assert.All(result.Decibels, column => Assert.Equal(expected, column[16], 4));
    }

    [Fact]
    public void SeeksExactSourceSampleAndClampsAtTheDecodedEnd()
    {
        float[] samples = new float[2100];
        for (int index = 0; index < 1024; index++)
        {
            samples[1076 + index] = Tone(index, 64);
        }
        AudioSpectrogramResult result = AudioSpectrogram.Read(Write(samples), 1076d / 48000, 1, CancellationToken.None);
        Assert.Equal(1076, result.FirstSampleFrame);
        Assert.Equal(1024, result.SampleFrames);
        Assert.Equal(64, StrongestBin(Assert.Single(result.Decibels)));
    }

    [Fact]
    public void ShortRangesRetainAllSamplesInFixedBins()
    {
        AudioSpectrogramResult result = AudioSpectrogram.Read(Write(new float[63]), 0, 63d / 48000, CancellationToken.None);
        Assert.Equal(32, result.FftFrames);
        Assert.Equal(new long[] { 32, 31 }, result.ColumnFrames);
        Assert.Equal(63, result.SampleFrames);
        Assert.All(result.Decibels, column => Assert.All(column, level => Assert.Equal(-240f, level)));
    }

    [Fact]
    public void RejectsInvalidRangesAndNonFiniteSamples()
    {
        string path = Write(new float[32]);
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioSpectrogram.Read(path, double.NaN, 1, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioSpectrogram.Read(path, 0, 0, default));
        Assert.Throws<ArgumentException>(() => AudioSpectrogram.Read(path, 1, 1, default));
        Assert.Throws<ArgumentException>(() => AudioSpectrogram.Read(path, 0, 31d / 48000, default));
        string invalid = Write(Enumerable.Repeat(float.NaN, 32).ToArray());
        Assert.Throws<InvalidDataException>(() => AudioSpectrogram.Read(invalid, 0, 1, default));
    }

    [Fact]
    public void CancellationPreventsSourceAccess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => AudioSpectrogram.Read("not-opened.wav", 0, 1, cancellation.Token));
    }

    private static int StrongestBin(float[] levels) =>
        Enumerable.Range(1, levels.Length - 1).MaxBy(bin => levels[bin]);

    public void Dispose()
    {
        foreach (string path in _paths)
        {
            File.Delete(path);
        }
    }
}
