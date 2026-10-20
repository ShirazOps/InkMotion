using Xunit;

namespace InkMotion.Tests;

public sealed class InkOnionSkinTests
{
    [Fact]
    public void PlansIndependentDirectionsFarToNearWithExponentialFalloff()
    {
        Assert.Equal(new[]
        {
            new InkOnionGuide(1, 0.1, true),
            new InkOnionGuide(2, 0.2, true),
            new InkOnionGuide(6, 0.2, false),
            new InkOnionGuide(3, 0.4, true),
            new InkOnionGuide(5, 0.4, false),
        }, InkOnionSkin.Plan(8, 4, 3, 2, 0.4, 0.5));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void ClampsToTimelineEdgesAndNeverIncludesCurrentCel(int selected)
    {
        IReadOnlyList<InkOnionGuide> guides = InkOnionSkin.Plan(5, selected, 8, 8, 0.25, 1);
        Assert.Equal(4, guides.Count);
        Assert.DoesNotContain(guides, guide => guide.FrameIndex == selected);
        Assert.All(guides, guide =>
        {
            Assert.InRange(guide.FrameIndex, 0, 4);
            Assert.Equal(0.25, guide.Opacity);
            Assert.Equal(guide.FrameIndex < selected, guide.IsPrevious);
        });
    }

    [Fact]
    public void SupportsOneDirectionNoGuidesAndSingleCelProjects()
    {
        Assert.Empty(InkOnionSkin.Plan(1, 0, 8, 8, 0.25, 0.5));
        Assert.Empty(InkOnionSkin.Plan(5, 2, 0, 0, 0.25, 0.5));
        Assert.All(InkOnionSkin.Plan(5, 2, 2, 0, 0.25, 0.5), guide => Assert.True(guide.IsPrevious));
        Assert.All(InkOnionSkin.Plan(5, 2, 0, 2, 0.25, 0.5), guide => Assert.False(guide.IsPrevious));
        Assert.All(InkOnionSkin.Plan(int.MaxValue, int.MaxValue - 1, 8, 8, 0.25, 1),
            guide => Assert.InRange(guide.FrameIndex, int.MaxValue - 9, int.MaxValue - 2));
    }

    [Theory]
    [InlineData(0, 0, 1, 1, 0.25, 1)]
    [InlineData(5, 5, 1, 1, 0.25, 1)]
    [InlineData(5, -1, 1, 1, 0.25, 1)]
    [InlineData(5, 2, -1, 1, 0.25, 1)]
    [InlineData(5, 2, 1, 9, 0.25, 1)]
    [InlineData(5, 2, 1, 1, double.NaN, 1)]
    [InlineData(5, 2, 1, 1, 0.25, 0)]
    [InlineData(5, 2, 1, 1, 0.25, double.PositiveInfinity)]
    public void RejectsInvalidInputs(int count, int selected, int previous, int next, double opacity, double falloff) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => InkOnionSkin.Plan(count, selected, previous, next, opacity, falloff));
}
