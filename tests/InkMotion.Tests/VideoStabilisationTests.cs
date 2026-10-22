using Xunit;

namespace InkMotion.Tests;

public sealed class VideoStabilisationTests
{
    [Fact]
    public void EstimateTranslation_FindsKnownHorizontalAndVerticalMotion()
    {
        const int width = 64;
        const int height = 36;
        byte[] previous = Enumerable.Range(0, width * height)
            .Select(index => (byte)((index * 73 + index / width * 29) % 251))
            .ToArray();
        byte[] current = new byte[previous.Length];
        const int expectedX = 3;
        const int expectedY = -2;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int sourceX = x - expectedX;
                int sourceY = y - expectedY;
                current[y * width + x] = sourceX is >= 0 and < width && sourceY is >= 0 and < height
                    ? previous[sourceY * width + sourceX]
                    : (byte)0;
            }
        }

        Assert.Equal((expectedX, expectedY),
            VideoStabilisation.EstimateTranslation(previous, current, width, height));
    }

    [Fact]
    public void EstimateTranslation_RejectsMismatchedBuffers()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VideoStabilisation.EstimateTranslation([0, 1], [0], 2, 2));
    }

    [Fact]
    public void EstimateTranslation_DoesNotInventMotionForFeaturelessFrames()
    {
        byte[] previous = Enumerable.Repeat((byte)80, 64 * 36).ToArray();
        byte[] current = Enumerable.Repeat((byte)82, 64 * 36).ToArray();

        Assert.Equal((0, 0), VideoStabilisation.EstimateTranslation(previous, current, 64, 36));
        Assert.Equal((0, 0, 0), VideoStabilisation.EstimateMotion(previous, current, 64, 36));
    }

    [Fact]
    public void EstimateMotion_DetectsSmallCameraRotation()
    {
        const int width = 64;
        const int height = 36;
        const double expectedRotation = 3;
        byte[] previous = Enumerable.Range(0, width * height)
            .Select(index => (byte)((index * 73 + index / width * 29 + index % width * 11) % 251))
            .ToArray();
        byte[] current = new byte[previous.Length];
        double radians = expectedRotation * Math.PI / 180;
        double cosine = Math.Cos(radians);
        double sine = Math.Sin(radians);
        double centreX = (width - 1) / 2d;
        double centreY = (height - 1) / 2d;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int sourceX = (int)Math.Round(centreX + (cosine * (x - centreX)) - (sine * (y - centreY)));
                int sourceY = (int)Math.Round(centreY + (sine * (x - centreX)) + (cosine * (y - centreY)));
                current[y * width + x] = (uint)sourceX < width && (uint)sourceY < height
                    ? previous[sourceY * width + sourceX]
                    : (byte)0;
            }
        }

        var motion = VideoStabilisation.EstimateMotion(previous, current, width, height);
        Assert.InRange(Math.Abs(motion.RotationDegrees), 2, 4);
    }
}
