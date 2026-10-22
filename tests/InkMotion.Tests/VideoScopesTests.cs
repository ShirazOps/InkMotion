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

    [Fact]
    public void LumaWaveform_MapsImagePositionAndLumaIntoPremultipliedPixels()
    {
        byte[] pixels =
        [
            0, 0, 0, 255,
            255, 255, 255, 255,
            0, 0, 255, 255,
            0, 255, 0, 255,
        ];

        VideoLumaWaveformData waveform = VideoLumaWaveformData.Analyse(pixels, 2, 2);

        Assert.Equal(4, waveform.SampleCount);
        Assert.Equal(128 * 128 * 4, waveform.BgraPixels.Length);
        Assert.True(waveform.BgraPixels[((127 * 128) * 4) + 3] > 0);
        Assert.True(waveform.BgraPixels[((0 * 128 + 64) * 4) + 3] > 0);
        Assert.True(waveform.BgraPixels[((101 * 128) * 4) + 3] > 0);
        Assert.True(waveform.BgraPixels[((37 * 128 + 64) * 4) + 3] > 0);
        for (int offset = 0; offset < waveform.BgraPixels.Length; offset += 4)
        {
            Assert.True(waveform.BgraPixels[offset] <= waveform.BgraPixels[offset + 3]);
            Assert.True(waveform.BgraPixels[offset + 1] <= waveform.BgraPixels[offset + 3]);
            Assert.True(waveform.BgraPixels[offset + 2] <= waveform.BgraPixels[offset + 3]);
        }
    }

    [Fact]
    public void LumaWaveform_RejectsMismatchedSourceDimensions()
    {
        Assert.Throws<ArgumentException>(() => VideoLumaWaveformData.Analyse([0, 0, 0, 255], 2, 1));
    }

    [Fact]
    public void Vectorscope_MapsRec709ChromaAndIgnoresTransparentPixels()
    {
        byte[] pixels =
        [
            0, 0, 255, 255,
            0, 255, 0, 255,
            255, 0, 0, 255,
            0, 0, 0, 0,
        ];

        VideoVectorscopeData vectorscope = VideoVectorscopeData.Analyse(pixels);

        Assert.Equal(3, vectorscope.SampleCount);
        Assert.Equal(1, vectorscope.Density[(11 * VideoVectorscopeData.Width) + 51]);
        Assert.Equal(1, vectorscope.Density[(112 * VideoVectorscopeData.Width) + 23]);
        Assert.Equal(1, vectorscope.Density[(68 * VideoVectorscopeData.Width) + 116]);
    }

    [Fact]
    public void Vectorscope_CombinesNeutralSamplesAtCentreAndProducesPremultipliedPixels()
    {
        byte[] pixels =
        [
            128, 128, 128, 255,
            64, 64, 64, 128,
        ];

        VideoVectorscopeData vectorscope = VideoVectorscopeData.Analyse(pixels);

        Assert.Equal(2, vectorscope.SampleCount);
        Assert.Equal(2, vectorscope.Density[(64 * VideoVectorscopeData.Width) + 64]);
        for (int offset = 0; offset < vectorscope.BgraPixels.Length; offset += 4)
        {
            Assert.True(vectorscope.BgraPixels[offset] <= vectorscope.BgraPixels[offset + 3]);
            Assert.True(vectorscope.BgraPixels[offset + 1] <= vectorscope.BgraPixels[offset + 3]);
            Assert.True(vectorscope.BgraPixels[offset + 2] <= vectorscope.BgraPixels[offset + 3]);
        }
    }

    [Fact]
    public void Vectorscope_RejectsIncompleteBgraPixels()
    {
        Assert.Throws<ArgumentException>(() => VideoVectorscopeData.Analyse([1, 2, 3]));
    }
}
