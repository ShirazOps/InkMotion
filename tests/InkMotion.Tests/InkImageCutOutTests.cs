using Xunit;

namespace InkMotion.Tests;

public sealed class InkImageCutOutTests
{
    [Fact]
    public void ApplyMaskToAlpha_MakesBackgroundTransparentAndPreservesForegroundAlpha()
    {
        byte[] source = [10, 20, 30, 200, 40, 50, 60, 128];

        byte[] result = InkImageCutOut.ApplyMaskToAlpha(source, 2, 1, [true, false]);

        Assert.Equal([10, 20, 30, 200, 40, 50, 60, 0], result);
        Assert.Equal(128, source[^1]);
    }

    [Fact]
    public void ApplyMaskToAlpha_RejectsMismatchedDimensions()
    {
        Assert.Throws<ArgumentException>(() => InkImageCutOut.ApplyMaskToAlpha([0, 0, 0, 255], 2, 1, [true]));
    }
}
