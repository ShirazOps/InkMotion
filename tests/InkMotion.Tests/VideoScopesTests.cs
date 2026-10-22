using Xunit;

namespace InkMotion.Tests;

public sealed class VideoScopesTests
{
    [Fact]
    public void Analyse_SeparatesBgraChannelsAndIgnoresTransparentPixels()
    {
        byte[] pixels =
        [
            30, 20, 10, 255,
            0, 0, 0, 0,
        ];

        VideoRgbHistogramData histogram = VideoRgbHistogramData.Analyse(pixels);

        Assert.Equal(1, histogram.SampleCount);
        Assert.Equal(1, histogram.Red[10]);
        Assert.Equal(1, histogram.Green[20]);
        Assert.Equal(1, histogram.Blue[30]);
        Assert.Equal(0, histogram.Red[0]);
    }

    [Fact]
    public void Analyse_UnpremultipliesPartiallyTransparentPixels()
    {
        byte[] pixels = [25, 50, 75, 128];

        VideoRgbHistogramData histogram = VideoRgbHistogramData.Analyse(pixels);

        Assert.Equal(1, histogram.Red[149]);
        Assert.Equal(1, histogram.Green[100]);
        Assert.Equal(1, histogram.Blue[50]);
    }

    [Fact]
    public void Analyse_RejectsIncompleteBgraPixels()
    {
        Assert.Throws<ArgumentException>(() => VideoRgbHistogramData.Analyse([1, 2, 3]));
    }
}
