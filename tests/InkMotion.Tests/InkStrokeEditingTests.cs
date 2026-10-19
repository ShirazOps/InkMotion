using Xunit;

namespace InkMotion.Tests;

public sealed class InkStrokeEditingTests
{
    [Fact]
    public void PartialErase_SplitsSparseSegmentAndInterpolatesPressure()
    {
        List<List<InkPoint>> strokes =
        [[new() { X = 0, Y = 0.5, Pressure = 0.2 }, new() { X = 1, Y = 0.5, Pressure = 0.8 }]];
        Assert.True(InkStrokeEditing.Erase(strokes, 50, 25, 100, 50, 8, wholeStroke: false));
        Assert.Equal(2, strokes.Count);
        Assert.Equal(0, strokes[0][0].X);
        Assert.Equal(0.391, strokes[0][^1].X, 8);
        Assert.Equal(0.609, strokes[1][0].X, 8);
        Assert.Equal(1, strokes[1][^1].X);
        Assert.Equal(0.4346, strokes[0][^1].Pressure, 8);
        Assert.Equal(4, strokes[1][0].BrushWidth);
        Assert.Equal(0xFFFFA500u, strokes[1][0].BrushColour);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Erase_PreservesUntouchedStrokesAndRemovesDots(bool wholeStroke)
    {
        List<InkPoint> untouched = [new() { X = 0.1, Y = 0.1 }];
        List<List<InkPoint>> strokes = [untouched, [new() { X = 0.5, Y = 0.5 }]];
        Assert.True(InkStrokeEditing.Erase(strokes, 50, 50, 100, 100, 8, wholeStroke));
        Assert.Same(untouched, Assert.Single(strokes));
        Assert.False(InkStrokeEditing.Erase(strokes, 50, 50, 100, 100, 8, wholeStroke));
    }

    [Fact]
    public void WholeErase_HitsBetweenSamples()
    {
        List<List<InkPoint>> strokes = [[new() { X = 0, Y = 0.5 }, new() { X = 1, Y = 0.5 }]];
        Assert.True(InkStrokeEditing.Erase(strokes, 50, 50, 100, 100, 4, wholeStroke: true));
        Assert.Empty(strokes);
    }

    [Fact]
    public void PartialErase_DoesNotReconnectAcrossErasedMiddle()
    {
        List<List<InkPoint>> strokes = [[
            new() { X = 0.1, Y = 0.5 }, new() { X = 0.5, Y = 0.5 },
            new() { X = 0.9, Y = 0.5 }, new() { X = 0.9, Y = 0.9 }]];
        Assert.True(InkStrokeEditing.Erase(strokes, 50, 50, 100, 100, 8, wholeStroke: false));
        Assert.Equal(2, strokes.Count);
        Assert.All(strokes[0], point => Assert.True(point.X <= 0.4 + 1e-8));
        Assert.All(strokes[1], point => Assert.True(point.X >= 0.6 - 1e-8));
        Assert.Equal(0.9, strokes[1][^1].Y);
        var project = new EditorProject { Frames = [new() { Strokes = strokes }] };
        EditorProject reopened = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        Assert.Equal(2, reopened.Frames[0].Strokes.Count);
    }

    [Fact]
    public void Stabilisation_PreservesStyleAndPressureAndCanBeDisabled()
    {
        var input = new InkPoint { X = 1, Y = 0.5, Pressure = 0.8, BrushColour = 0xFF123456, BrushWidth = 6 };
        var previous = new InkPoint { X = 0, Y = 0.5 };
        InkPoint stabilised = InkStrokeEditing.Stabilise(input, previous, 0.8);
        Assert.Equal(0.2, stabilised.X, 8);
        Assert.Equal(input.Pressure, stabilised.Pressure);
        Assert.Equal(input.BrushColour, stabilised.BrushColour);
        Assert.Equal(input.BrushWidth, stabilised.BrushWidth);
        Assert.Equal(1, InkStrokeEditing.Stabilise(input, previous, 0).X);
        Assert.Equal(1, InkStrokeEditing.Stabilise(input, null, 0.8).X);
        Assert.Throws<ArgumentOutOfRangeException>(() => InkStrokeEditing.Stabilise(input, previous, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => InkStrokeEditing.Erase([], 0, 0, 0, 100, 8, false));
    }
}
