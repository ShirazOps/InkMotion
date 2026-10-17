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
    [InlineData(25)]
    [InlineData(120)]
    public void Create_RejectsAnUnsupportedFrameRate(double frameRate)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VideoExportProfile.Create(ExportResolution.P1080, frameRate));
    }
}
