using Xunit;

namespace InkMotion.Tests;

public sealed class VideoExportProfileTests
{
    [Theory]
    [InlineData(ExportResolution.P720, 1280, 720)]
    [InlineData(ExportResolution.P1080, 1920, 1080)]
    [InlineData(ExportResolution.P1440, 2560, 1440)]
    [InlineData(ExportResolution.Uhd4K, 3840, 2160)]
    public void Create_UsesRequestedDimensionsAndSupportedFrameRates(
        ExportResolution resolution,
        int expectedWidth,
        int expectedHeight)
    {
        foreach (double frameRate in ProjectFrameRates.Supported)
        {
            VideoExportProfile profile = VideoExportProfile.Create(resolution, frameRate);

            Assert.Equal(expectedWidth, profile.Width);
            Assert.Equal(expectedHeight, profile.Height);
            Assert.Equal(frameRate, profile.FrameRate);
        }
    }

    [Fact]
    public void Create_RejectsAnUnsupportedResolution()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VideoExportProfile.Create((ExportResolution)99, 24));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(23)]
    [InlineData(121)]
    [InlineData(double.NaN)]
    public void Create_RejectsAnUnsupportedFrameRate(double frameRate)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VideoExportProfile.Create(ExportResolution.P1080, frameRate));
    }

    [Theory]
    [InlineData(24)]
    [InlineData(25)]
    [InlineData(29.97)]
    [InlineData(60)]
    [InlineData(119.88)]
    [InlineData(120)]
    public void Create_AcceptsIndependentOutputFrameRates(double rate) =>
        Assert.Equal(rate, VideoExportProfile.Create(ExportResolution.P1080, rate).FrameRate);

    [Fact]
    public void Create_AcceptsCustomDimensionsWithoutChangingPresetProfiles()
    {
        VideoExportProfile custom = VideoExportProfile.Create(2048, 1536, 60);
        Assert.Equal(ExportResolution.Custom, custom.Resolution);
        Assert.Equal(2048, custom.Width);
        Assert.Equal(1536, custom.Height);
        Assert.Equal(60, custom.FrameRate);
        Assert.Equal((1920, 1080), (VideoExportProfile.Create(ExportResolution.P1080, 24).Width,
            VideoExportProfile.Create(ExportResolution.P1080, 24).Height));
    }

    [Theory]
    [InlineData(0, 1080)]
    [InlineData(8194, 1080)]
    [InlineData(1919, 1080)]
    [InlineData(8192, 8192)]
    public void Create_RejectsInvalidCustomDimensions(int width, int height) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => VideoExportProfile.Create(width, height, 24));

    [Fact]
    public void CapabilityProbeFrameRates_CoverCommonIntegerAndFractionalRates()
    {
        Assert.Equal(
            new[] { 24, 25, 29.97, 30, 48, 50, 59.94, 60, 100, 119.88, 120 },
            VideoExportCapabilities.CapabilityProbeFrameRates);
        Assert.All(VideoExportCapabilities.CapabilityProbeFrameRates,
            rate => Assert.InRange(rate, 24, 120));
    }
}
