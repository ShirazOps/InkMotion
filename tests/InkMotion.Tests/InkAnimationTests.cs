using Xunit;

namespace InkMotion.Tests;

public sealed class InkAnimationTests
{
    [Fact]
    public void GetDisplayedStrokes_InterpolatesBetweenKeyframesWithDifferentPointCounts()
    {
        List<EditorFrame> frames =
        [
            Frame(1, true, Stroke((0.1, 0.2, 0.3, 3.0), (0.3, 0.2, 0.3, 3.0))),
            Frame(2, false),
            Frame(3, false),
            Frame(4, true, Stroke((0.5, 0.6, 0.9, 9.0), (0.7, 0.6, 0.9, 9.0), (0.9, 0.6, 0.9, 9.0))),
        ];

        IReadOnlyList<List<InkPoint>> strokes = InkAnimation.GetDisplayedStrokes(frames, 1);

        Assert.True(InkAnimation.IsInterpolated(frames, 1));
        Assert.Single(strokes);
        Assert.Equal(3, strokes[0].Count);
        Assert.Equal(0.23333333333333334, strokes[0][0].X, precision: 10);
        Assert.Equal(0.3333333333333333, strokes[0][0].Y, precision: 10);
        Assert.Equal(0.5, strokes[0][^1].X, precision: 10);
        Assert.Equal(0.3333333333333333, strokes[0][^1].Y, precision: 10);
        Assert.Equal(0.5, strokes[0][0].Pressure, precision: 10);
        Assert.Equal(5.0, strokes[0][0].BrushWidth, precision: 10);
        Assert.Empty(frames[1].Strokes);
    }

    [Fact]
    public void GetDisplayedStrokes_HoldsPreviousDrawingWhenStrokeCountsDiffer()
    {
        List<EditorFrame> frames =
        [
            Frame(1, true, Stroke((0.2, 0.3))),
            Frame(2, false),
            Frame(3, true, Stroke((0.4, 0.5)), Stroke((0.7, 0.8))),
        ];

        IReadOnlyList<List<InkPoint>> strokes = InkAnimation.GetDisplayedStrokes(frames, 1);

        Assert.False(InkAnimation.IsInterpolated(frames, 1));
        Assert.Single(strokes);
        Assert.Equal(0.2, strokes[0][0].X);
        Assert.Equal(0.3, strokes[0][0].Y);
    }

    [Fact]
    public void GetDisplayedStrokes_HoldsLastKeyframeAndPreservesHandDrawnExposure()
    {
        List<EditorFrame> frames =
        [
            Frame(1, true, Stroke((0.2, 0.3))),
            Frame(2, false),
            Frame(3, false, Stroke((0.8, 0.9))),
        ];

        IReadOnlyList<List<InkPoint>> held = InkAnimation.GetDisplayedStrokes(frames, 1);
        IReadOnlyList<List<InkPoint>> authored = InkAnimation.GetDisplayedStrokes(frames, 2);

        Assert.Equal(0.2, held[0][0].X);
        Assert.Equal(0.8, authored[0][0].X);
    }

    [Fact]
    public void GetInterpolatedFrameIndices_OnlyIncludesCompatibleEmptyInbetweens()
    {
        List<EditorFrame> frames =
        [
            Frame(1, true, Stroke((0.1, 0.1))),
            Frame(2, false),
            Frame(3, false, Stroke((0.3, 0.3))),
            Frame(4, false),
            Frame(5, true, Stroke((0.5, 0.5))),
            Frame(6, false),
        ];

        Assert.Equal([1, 3], InkAnimation.GetInterpolatedFrameIndices(frames));
    }

    [Fact]
    public void GetDisplayedStrokes_InterpolatesSelectedNonBaseInkLayer()
    {
        List<EditorFrame> frames =
        [
            new EditorFrame
            {
                Number = 1,
                IsKeyframe = true,
                Strokes = [Stroke((0.9, 0.9))],
                LayerStrokes = new Dictionary<string, List<List<InkPoint>>>
                {
                    ["ink-colour"] = [Stroke((0.1, 0.2))],
                },
            },
            new EditorFrame { Number = 2 },
            new EditorFrame
            {
                Number = 3,
                IsKeyframe = true,
                Strokes = [Stroke((0.8, 0.8))],
                LayerStrokes = new Dictionary<string, List<List<InkPoint>>>
                {
                    ["ink-colour"] = [Stroke((0.5, 0.6))],
                },
            },
        ];

        IReadOnlyList<List<InkPoint>> displayed = InkAnimation.GetDisplayedStrokes(
            frames,
            1,
            frame => frame.LayerStrokes.TryGetValue("ink-colour", out List<List<InkPoint>>? strokes)
                ? strokes
                : []);

        Assert.True(InkAnimation.IsInterpolated(
            frames,
            1,
            frame => frame.LayerStrokes.TryGetValue("ink-colour", out List<List<InkPoint>>? strokes)
                ? strokes
                : []));
        Assert.Equal(0.3, displayed[0][0].X, precision: 10);
        Assert.Equal(0.4, displayed[0][0].Y, precision: 10);
        Assert.Empty(frames[1].LayerStrokes);
    }

    [Theory]
    [InlineData(InkInterpolationMode.Linear, 0.25)]
    [InlineData(InkInterpolationMode.EaseIn, 0.0625)]
    [InlineData(InkInterpolationMode.EaseOut, 0.4375)]
    [InlineData(InkInterpolationMode.EaseInOut, 0.125)]
    public void GetDisplayedStrokes_AppliesSelectedTweenMode(
        InkInterpolationMode mode,
        double expectedX)
    {
        List<EditorFrame> frames =
        [
            new EditorFrame
            {
                Number = 1,
                IsKeyframe = true,
                InterpolationMode = mode,
                Strokes = [Stroke((0.0, 0.5))],
            },
            Frame(2, false),
            Frame(3, false),
            Frame(4, false),
            new EditorFrame
            {
                Number = 5,
                IsKeyframe = true,
                Strokes = [Stroke((1.0, 0.5))],
            },
        ];

        IReadOnlyList<List<InkPoint>> displayed = InkAnimation.GetDisplayedStrokes(frames, 1);

        Assert.Equal(expectedX, displayed[0][0].X, precision: 10);
    }

    [Fact]
    public void GetDisplayedStrokes_RejectsInvalidFrameIndex()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => InkAnimation.GetDisplayedStrokes([Frame(1, true)], 1));
    }

    private static EditorFrame Frame(int number, bool isKeyframe, params List<InkPoint>[] strokes) =>
        new() { Number = number, IsKeyframe = isKeyframe, Strokes = [.. strokes] };

    private static List<InkPoint> Stroke(params (double X, double Y)[] points) =>
        points.Select(point => new InkPoint { X = point.X, Y = point.Y }).ToList();

    private static List<InkPoint> Stroke(params (double X, double Y, double Pressure)[] points) =>
        points.Select(point => new InkPoint
        {
            X = point.X,
            Y = point.Y,
            Pressure = point.Pressure,
        }).ToList();

    private static List<InkPoint> Stroke(params (double X, double Y, double Pressure, double BrushWidth)[] points) =>
        points.Select(point => new InkPoint
        {
            X = point.X,
            Y = point.Y,
            Pressure = point.Pressure,
            BrushWidth = point.BrushWidth,
        }).ToList();
}
