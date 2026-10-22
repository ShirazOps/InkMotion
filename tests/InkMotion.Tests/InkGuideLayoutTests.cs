using Xunit;

namespace InkMotion.Tests;

public sealed class InkGuideLayoutTests
{
    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(3840, 2160)]
    public void RulersMapExactOutputPixelCoordinatesAtEveryResolution(int width, int height)
    {
        foreach (int pixels in new[] { width, height })
        {
            foreach (double zoom in new[] { 0.25, 1, 4 })
            {
                IReadOnlyList<InkRulerTick> ticks = InkGuideLayout.CreateRuler(640, pixels, zoom);
                Assert.Equal(new InkRulerTick(0, 0, true), ticks[0]);
                Assert.Equal(new InkRulerTick(pixels, 1, true), ticks[^1]);
                Assert.Equal(ticks.Count, ticks.Select(tick => tick.Pixel).Distinct().Count());
                Assert.All(ticks, tick =>
                {
                    Assert.Equal((double)tick.Pixel / pixels, tick.Position);
                    Assert.InRange(tick.Position, 0, 1);
                });
                Assert.True(ticks.Zip(ticks.Skip(1)).All(pair => pair.First.Pixel < pair.Second.Pixel));
            }
        }
    }

    [Fact]
    public void RulersAdaptDensityToCanvasSizeAndZoomWithoutChangingCalibration()
    {
        IReadOnlyList<InkRulerTick> small = InkGuideLayout.CreateRuler(400, 3840, 1);
        IReadOnlyList<InkRulerTick> zoomed = InkGuideLayout.CreateRuler(400, 3840, 4);
        IReadOnlyList<InkRulerTick> wide = InkGuideLayout.CreateRuler(1600, 3840, 1);
        Assert.True(zoomed.Count > small.Count);
        Assert.Equal(zoomed, wide);
        Assert.Equal(small[^1], zoomed[^1]);
        Assert.All(small.Where(tick => tick.IsMajor), tick => Assert.Contains(tick, zoomed));
    }

    [Theory]
    [InlineData(1, 1, 0.25)]
    [InlineData(1000000, int.MaxValue, 4)]
    public void RulerTickCountsStayBounded(double canvas, int pixels, double zoom)
    {
        IReadOnlyList<InkRulerTick> ticks = InkGuideLayout.CreateRuler(canvas, pixels, zoom);
        Assert.InRange(ticks.Count, 2, 602);
        Assert.Equal(pixels, ticks[^1].Pixel);
    }

    [Theory]
    [InlineData(0, 1920, 1)]
    [InlineData(double.NaN, 1920, 1)]
    [InlineData(double.PositiveInfinity, 1920, 1)]
    [InlineData(640, 0, 1)]
    [InlineData(640, -1, 1)]
    [InlineData(640, 1920, 0)]
    [InlineData(640, 1920, 5)]
    [InlineData(640, 1920, double.NaN)]
    public void RulersRejectInvalidDimensionsAndZoom(double canvas, int pixels, double zoom) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => InkGuideLayout.CreateRuler(canvas, pixels, zoom));

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(64)]
    public void GridDividesBothAxesWithinCanvasWithoutDrawingAnOuterBorder(int divisions)
    {
        IReadOnlyList<InkGuideLine> guides = InkGuideLayout.Create(true, divisions, false);
        Assert.Equal(2 * (divisions - 1), guides.Count);
        for (int index = 1; index < divisions; index++)
        {
            double position = (double)index / divisions;
            Assert.Contains(new InkGuideLine(position, 0, position, 1, false), guides);
            Assert.Contains(new InkGuideLine(0, position, 1, position, false), guides);
        }
    }

    [Theory]
    [InlineData(3, 6)]
    [InlineData(8, 14)]
    public void CentreLinesAreHighlightedWithoutDuplicateGridLines(int divisions, int count)
    {
        IReadOnlyList<InkGuideLine> guides = InkGuideLayout.Create(true, divisions, true);
        Assert.Equal(count, guides.Count);
        Assert.Equal(2, guides.Count(line => line.IsCentre));
        Assert.Equal(guides.Count, guides.Select(line => (line.X1, line.Y1, line.X2, line.Y2)).Distinct().Count());
    }

    [Fact]
    public void HiddenGridAndIndependentCentreToggleHaveExpectedGeometry()
    {
        Assert.Empty(InkGuideLayout.Create(false, 8, false));
        Assert.Equal(new[]
        {
            new InkGuideLine(0.5, 0, 0.5, 1, true),
            new InkGuideLine(0, 0.5, 1, 0.5, true),
        }, InkGuideLayout.Create(false, 8, true));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65)]
    public void InvalidDivisionsAreExplicitlyRejected(int divisions) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => InkGuideLayout.Create(true, divisions, false));
}
