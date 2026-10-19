using Xunit;

namespace InkMotion.Tests;

public sealed class InkSelectionEditingTests
{
    [Fact]
    public void Lasso_SelectsContainedAndCrossingStrokesButNotOutsideDots()
    {
        List<InkPoint> inside = [new() { X = 0.5, Y = 0.5 }];
        List<InkPoint> crossing = [new() { X = 0, Y = 0.5 }, new() { X = 1, Y = 0.5 }];
        List<InkPoint> outside = [new() { X = 0.9, Y = 0.9 }];
        List<InkPoint> polygon = [
            new() { X = 0.25, Y = 0.25 }, new() { X = 0.75, Y = 0.25 },
            new() { X = 0.75, Y = 0.75 }, new() { X = 0.25, Y = 0.75 }];
        List<List<InkPoint>> selected = InkSelectionEditing.Select([inside, crossing, outside], polygon);
        Assert.Equal(2, selected.Count);
        Assert.Same(inside, selected[0]);
        Assert.Same(crossing, selected[1]);
        Assert.Empty(InkSelectionEditing.Select([inside], polygon.Take(2).ToList()));
    }

    [Fact]
    public void Transform_UsesPixelAspectAndLeavesAppearanceAttributesUnchanged()
    {
        List<List<InkPoint>> selected = [[
            new() { X = 0.4, Y = 0.5, Pressure = 0.7, BrushWidth = 6, BrushColour = 0x80FF0000 },
            new() { X = 0.6, Y = 0.5 }]];
        Assert.True(InkSelectionEditing.Transform(selected, 0.1, 0, 1, 90, 200, 100));
        Assert.Equal(0.6, selected[0][0].X, 8);
        Assert.Equal(0.3, selected[0][0].Y, 8);
        Assert.Equal(0.7, selected[0][1].Y, 8);
        Assert.Equal(0.7, selected[0][0].Pressure);
        Assert.Equal(6, selected[0][0].BrushWidth);
        Assert.Equal(0x80FF0000u, selected[0][0].BrushColour);
    }

    [Fact]
    public void Transform_RejectsOutOfCanvasChangesAtomicallyAndClipboardIsIndependent()
    {
        List<List<InkPoint>> selected = [[new() { X = 0.1, Y = 0.5 }, new() { X = 0.9, Y = 0.5 }]];
        List<List<InkPoint>> copied = InkEditing.CloneStrokes(selected);
        Assert.False(InkSelectionEditing.Transform(selected, 0.2, 0, 1, 0, 100, 100));
        Assert.Equal(0.1, selected[0][0].X);
        Assert.Equal(0.9, selected[0][1].X);
        Assert.True(InkSelectionEditing.Transform(selected, 0, 0, 0.5, 0, 100, 100));
        Assert.Equal(0.3, selected[0][0].X, 8);
        Assert.Equal(0.1, copied[0][0].X);
        Assert.Throws<ArgumentOutOfRangeException>(() => InkSelectionEditing.Transform(selected, 0, 0, double.NaN, 0, 100, 100));
    }
}
