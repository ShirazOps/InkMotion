using Xunit;

namespace InkMotion.Tests;

public sealed class InkSelectionEditingTests
{
    private static List<InkPoint> Square =>
    [
        new() { X = 0.25, Y = 0.25 }, new() { X = 0.75, Y = 0.25 },
        new() { X = 0.75, Y = 0.75 }, new() { X = 0.25, Y = 0.75 },
    ];

    [Fact]
    public void PartialLassoSplitsSparseCrossingPathsAndInterpolatesBoundaryStylingWithoutMutation()
    {
        List<InkPoint> crossing =
        [
            new() { X = 0, Y = 0.5, Pressure = 0.2, BrushWidth = 2, BrushColour = 0x80FF0000 },
            new() { X = 1, Y = 0.5, Pressure = 1, BrushWidth = 10, BrushColour = 0x80FF0000 },
        ];
        List<List<InkPoint>> drawing = [crossing];

        InkStrokePartition partition = Assert.Single(InkSelectionEditing.SelectPartial(drawing, Square));
        List<InkPoint> selected = Assert.Single(partition.Selected);

        Assert.Same(crossing, Assert.Single(drawing));
        Assert.Equal(2, crossing.Count);
        Assert.Equal(3, partition.Pieces.Count);
        Assert.Equal(0.25, selected[0].X, 10);
        Assert.Equal(0.75, selected[^1].X, 10);
        Assert.Equal(0.4, selected[0].Pressure, 10);
        Assert.Equal(4, selected[0].BrushWidth, 10);
        Assert.Equal(0x80FF0000u, selected[0].BrushColour);
        Assert.NotSame(crossing[0], selected[0]);
        List<List<InkPoint>> copy = InkEditing.CloneStrokes(partition.Selected);

        InkSelectionEditing.ApplyPartitions(drawing, [partition]);
        drawing.RemoveAll(partition.Selected.Contains);

        Assert.Equal(2, drawing.Count);
        Assert.Equal(0.25, drawing[0][^1].X);
        Assert.Equal(0.75, drawing[1][0].X);
        copy[0][0].X = 0.5;
        Assert.Equal(0.25, selected[0].X);
    }

    [Fact]
    public void PartialTransformMovesOnlySelectedPortionAndRoundTripsSplitPaths()
    {
        var project = new EditorProject
        {
            Frames = [new() { Strokes = [[new() { X = 0, Y = 0.5 }, new() { X = 1, Y = 0.5 }]] }],
        };
        string before = EditorProjectSerialiser.Serialise(project);
        List<List<InkPoint>> drawing = project.Frames[0].Strokes;
        List<InkStrokePartition> partitions = InkSelectionEditing.SelectPartial(drawing, Square);
        List<List<InkPoint>> selected = partitions.SelectMany(partition => partition.Selected).ToList();
        Assert.True(InkSelectionEditing.Transform(selected, 0, 0.1, 1, 0, 100, 100));
        InkSelectionEditing.ApplyPartitions(drawing, partitions);

        Assert.Equal(0.5, drawing[0][^1].Y);
        Assert.Equal(0.6, drawing[1][0].Y, 10);
        Assert.Equal(0.5, drawing[2][0].Y);
        EditorProject restored = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        Assert.Equal(3, restored.Frames[0].Strokes.Count);
        Assert.Equal(0.6, restored.Frames[0].Strokes[1][0].Y, 10);
        Assert.Single(EditorProjectSerialiser.Deserialise(before).Frames[0].Strokes);
    }

    [Fact]
    public void PartialLassoHandlesConcaveLoopsAndMultipleEntriesWithoutJoiningAcrossOutsidePortions()
    {
        List<InkPoint> polygon =
        [
            new() { X = 0.1, Y = 0.1 }, new() { X = 0.9, Y = 0.1 },
            new() { X = 0.9, Y = 0.9 }, new() { X = 0.7, Y = 0.9 },
            new() { X = 0.7, Y = 0.3 }, new() { X = 0.3, Y = 0.3 },
            new() { X = 0.3, Y = 0.9 }, new() { X = 0.1, Y = 0.9 },
        ];
        InkStrokePartition partition = Assert.Single(InkSelectionEditing.SelectPartial(
            [[new() { X = 0, Y = 0.5 }, new() { X = 1, Y = 0.5 }]], polygon));
        Assert.Equal(2, partition.Selected.Count);
        Assert.Equal(5, partition.Pieces.Count);
        Assert.Equal(0.1, partition.Selected[0][0].X, 10);
        Assert.Equal(0.3, partition.Selected[0][^1].X, 10);
        Assert.Equal(0.7, partition.Selected[1][0].X, 10);
        Assert.Equal(0.9, partition.Selected[1][^1].X, 10);
    }

    [Fact]
    public void PartialLassoHandlesDotsBoundaryPathsDuplicateVerticesAndEmptyStrokes()
    {
        List<InkPoint> polygon = Square;
        polygon.Add(polygon[0]);
        List<List<InkPoint>> drawing =
        [
            [],
            [new() { X = 0.5, Y = 0.5 }],
            [new() { X = 0.9, Y = 0.9 }],
            [new() { X = 0, Y = 0.25 }, new() { X = 1, Y = 0.25 }],
        ];
        List<InkStrokePartition> partitions = InkSelectionEditing.SelectPartial(drawing, polygon);
        Assert.Equal(2, partitions.Count);
        Assert.Single(partitions[0].Selected[0]);
        Assert.Equal(0.25, partitions[1].Selected[0][0].X, 10);
        Assert.Equal(0.75, partitions[1].Selected[0][^1].X, 10);
    }

    [Fact]
    public void PartialLassoPreservesContinuousInsideSegmentsAcrossSourceVertices()
    {
        InkStrokePartition partition = Assert.Single(InkSelectionEditing.SelectPartial(
            [[new() { X = 0, Y = 0.5 }, new() { X = 0.5, Y = 0.5 }, new() { X = 1, Y = 0.5 }]], Square));
        List<InkPoint> selected = Assert.Single(partition.Selected);
        Assert.Equal(3, selected.Count);
        Assert.Equal(0.5, selected[1].X);
        Assert.Equal(3, partition.Pieces.Count);
    }

    [Fact]
    public void PartialLassoRejectsDegenerateLoopsAndStalePartitionsWithoutMutatingDrawing()
    {
        List<List<InkPoint>> drawing = [[new() { X = 0, Y = 0.5 }, new() { X = 1, Y = 0.5 }]];
        Assert.Empty(InkSelectionEditing.SelectPartial(drawing, Square.Take(2).ToList()));
        List<InkStrokePartition> partitions = InkSelectionEditing.SelectPartial(drawing, Square);
        List<List<InkPoint>> replacement = InkEditing.CloneStrokes(drawing);
        Assert.Throws<InvalidOperationException>(() => InkSelectionEditing.ApplyPartitions(replacement, partitions));
        Assert.Single(replacement);
        Assert.Equal(0, replacement[0][0].X);
    }

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
