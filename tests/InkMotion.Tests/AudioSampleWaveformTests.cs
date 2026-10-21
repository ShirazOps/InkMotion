using NAudio.Wave;
using Xunit;

namespace InkMotion.Tests;

public sealed class AudioSampleWaveformTests : IDisposable
{
    private readonly List<string> _files = [];

    private string WriteSource(int rate, int channels, float[] samples)
    {
        string path = Path.Combine(Path.GetTempPath(), $"inkmotion-sample-view-{Guid.NewGuid():N}.wav");
        _files.Add(path);
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(rate, channels));
        writer.WriteSamples(samples, 0, samples.Length);
        return path;
    }

    [Theory]
    [InlineData(44100, 1)]
    [InlineData(48000, 2)]
    [InlineData(192000, 6)]
    public void ReadsExactDecodedFramesAndKeepsChannelsSeparate(int rate, int channels)
    {
        float[] samples = Enumerable.Range(0, 100 * channels).Select(index => index / 1024f).ToArray();
        string path = WriteSource(rate, channels, samples);
        AudioSampleWaveformResult result = AudioSampleWaveform.Read(path, 17, 23, CancellationToken.None);
        Assert.Equal(17, result.FirstSampleFrame);
        Assert.Equal(40, result.EndSampleFrame);
        Assert.Equal(23, result.SampleFrames);
        Assert.Equal(rate, result.SampleRate);
        Assert.Equal(channels, result.Channels);
        Assert.Equal(samples.Skip(17 * channels).Take(23 * channels), result.Samples);

        AudioSampleWaveformResult final = AudioSampleWaveform.Read(path, 99, 1, CancellationToken.None);
        Assert.Equal(samples.TakeLast(channels), final.Samples);
        Assert.Equal(100, final.EndSampleFrame);
    }

    [Fact]
    public void ReadsTheMaximumWindowWithMultipleDecodeChunksAndReleasesTheSource()
    {
        float[] samples = Enumerable.Range(0, AudioSampleWaveform.MaximumWindowFrames * 2 + 10)
            .Select(index => (index % 97 - 48) / 64f).ToArray();
        string path = WriteSource(48000, 2, samples);
        AudioSampleWaveformResult result = AudioSampleWaveform.Read(path, 5,
            AudioSampleWaveform.MaximumWindowFrames, CancellationToken.None);
        Assert.Equal(samples.Skip(10), result.Samples);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData(-1, 100)]
    [InlineData(0, 0)]
    [InlineData(0, 32769)]
    [InlineData(10, 1)]
    [InlineData(9, 2)]
    public void RejectsInvalidOrUnavailableWindows(long first, int count)
    {
        string path = WriteSource(48000, 1, new float[10]);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AudioSampleWaveform.Read(path, first, count, CancellationToken.None));
    }

    [Fact]
    public void SurfacesMissingSourcesAndNonFiniteSamples()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"inkmotion-missing-samples-{Guid.NewGuid():N}.wav");
        Assert.Throws<FileNotFoundException>(() => AudioSampleWaveform.Read(missing, 0, 1, CancellationToken.None));
        string path = WriteSource(48000, 1, [float.NaN]);
        Assert.Throws<InvalidDataException>(() => AudioSampleWaveform.Read(path, 0, 1, CancellationToken.None));
    }

    [Fact]
    public void CancellationPreventsOpeningTheSource()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => AudioSampleWaveform.Read("not-opened.wav", 0, 1, cancellation.Token));
    }

    [Theory]
    [InlineData(20, 16, 20, 16)]
    [InlineData(0, 16, 20, 16)]
    [InlineData(100, 16, 84, 16)]
    [InlineData(90, 32768, 20, 80)]
    [InlineData(100, 1, 99, 1)]
    public void ClampsNavigationAndZoomToTrimmedSourceBounds(long first, int count, long expectedFirst, int expectedCount)
    {
        Assert.Equal((expectedFirst, expectedCount), AudioSampleWaveform.GetWindow(first, count, 20, 100));
    }

    [Fact]
    public void SupportsSingleFrameClipsAndLargeExactSourcePositions()
    {
        const long end = 9007199254740991L;
        Assert.Equal((end - 1, 1), AudioSampleWaveform.GetWindow(end, 32768, end - 1, end));
        Assert.Equal((end - 1, end), AudioRangeEditing.GetSourceSampleDragRange(end - 1, end, 0, 100, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioSampleWaveform.GetWindow(0, 1, 10, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioSampleWaveform.GetWindow(0, 32769, 0, 100000));
    }

    [Theory]
    [InlineData(12.5, 37.5, 101, 103)]
    [InlineData(37.5, 12.5, 101, 103)]
    [InlineData(-100, 200, 100, 108)]
    [InlineData(100, 100, 107, 108)]
    [InlineData(31.25, 31.25, 102, 103)]
    public void WindowSelectionUsesSourceCoordinatesAndAnExcludedEnd(double from, double to, long first, long end)
    {
        Assert.Equal((first, end), AudioRangeEditing.GetSourceSampleDragRange(100, 108, from, to, 100));
    }

    [Fact]
    public void RejectsInvalidSelectionCoordinates()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioRangeEditing.GetSourceSampleDragRange(10, 10, 0, 1, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioRangeEditing.GetSourceSampleDragRange(0, 10, double.NaN, 1, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioRangeEditing.GetSourceSampleDragRange(0, 10, 0, 1, 0));
    }

    public void Dispose()
    {
        foreach (string path in _files)
        {
            File.Delete(path);
        }
    }
}
