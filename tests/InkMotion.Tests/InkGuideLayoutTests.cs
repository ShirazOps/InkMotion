using Xunit;

namespace InkMotion.Tests;

public sealed class InkGuideLayoutTests
{
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
