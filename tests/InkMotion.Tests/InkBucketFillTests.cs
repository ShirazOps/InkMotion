using Microsoft.Graphics.Canvas;
using Xunit;

namespace InkMotion.Tests;

public sealed class InkBucketFillTests
{
    private static bool[] Mask(int width, int height) =>
        Enumerable.Range(0, width * height)
            .Select(index => index % width == 0 || index % width == width - 1 ||
                index / width == 0 || index / width == height - 1).ToArray();

    private static byte[] Render(CanvasDevice device, List<InkPoint> fill, int width, int height)
    {
        using var target = new CanvasRenderTarget(device, width, height, 96);
        using (CanvasDrawingSession session = target.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Transparent);
            TimelineCompositor.DrawInkStroke(session, fill,
                TimelineCompositor.ToColour(fill[0].BrushColour), width, height, 1);
        }
        return target.GetPixelBytes();
    }

    [Fact]
    public void RectangleRegionBecomesOneCompactSavedFilledPolygon()
    {
        bool[] mask = Mask(16, 12);
        List<InkPoint> fill = InkBucketFill.CreateFromMask(mask, 16, 12, 5, 5, 0x80FFFFFF);
        Assert.InRange(fill.Count, 4, 8);
        Assert.All(fill, point =>
        {
            Assert.True(point.IsFilled);
            Assert.Equal(0x80FFFFFFu, point.BrushColour);
        });
        var project = new EditorProject { Frames = [new() { Strokes = [fill] }] };
        EditorProject reopened = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        using var device = new CanvasDevice(true);
        Assert.Equal(Render(device, fill, 16, 12), Render(device, reopened.Frames[0].Strokes[0], 16, 12));
    }

    [Fact]
    public void HolesAndConcaveContoursRenderExactlyTheFloodedCells()
    {
        using var device = new CanvasDevice(true);
        var random = new Random(713);
        for (int trial = 0; trial < 40; trial++)
        {
            const int size = 16;
            bool[] mask = Mask(size, size);
            for (int y = 1; y < size - 1; y++)
            {
                for (int x = 1; x < size - 1; x++)
                {
                    mask[y * size + x] = random.NextDouble() < 0.32;
                }
            }
            mask[size + 1] = false;
            var expected = new HashSet<int> { size + 1 };
            var queue = new Queue<int>(expected);
            while (queue.TryDequeue(out int current))
            {
                foreach (int next in new[] { current - 1, current + 1, current - size, current + size })
                {
                    if (!mask[next] && expected.Add(next))
                    {
                        queue.Enqueue(next);
                    }
                }
            }

            List<InkPoint> fill = InkBucketFill.CreateFromMask(mask, size, size, 1, 1, 0xFFFFFFFF);
            byte[] rendered = Render(device, fill, size, size);
            for (int index = 0; index < mask.Length; index++)
            {
                Assert.Equal(expected.Contains(index) ? (byte)255 : (byte)0, rendered[index * 4 + 3]);
                Assert.Equal(expected.Contains(index),
                    InkSelectionEditing.ContainsPoint(fill, (index % size + 0.5) / size, (index / size + 0.5) / size));
            }
        }
    }

    [Fact]
    public void HoleInteriorIsNotSelectedOrErasedEvenWhereDoubledBridgeCrossesIt()
    {
        // The doubled bridge crosses the hole; it must not become a hit-testable edge.
        List<InkPoint> fill = new (double X, double Y)[]
        {
            (0.1, 0.1), (0.9, 0.1), (0.9, 0.9), (0.1, 0.9), (0.1, 0.1),
            (0.7, 0.7), (0.7, 0.3), (0.3, 0.3), (0.3, 0.7), (0.7, 0.7), (0.1, 0.1),
        }.Select(p => new InkPoint { X = p.X, Y = p.Y, IsFilled = true }).ToList();
        Assert.False(InkSelectionEditing.ContainsPoint(fill, 0.5, 0.5));
        List<List<InkPoint>> strokes = [fill];
        Assert.False(InkStrokeEditing.Erase(strokes, 50, 50, 100, 100, 1, false));
        List<InkPoint> lasso = InkStrokeEditing.CreateShape(InkShape.Rectangle, 0.45, 0.45, 0.55, 0.55, 4, 0xFFFFFFFF);
        Assert.Empty(InkSelectionEditing.Select(strokes, lasso));
        Assert.Empty(InkSelectionEditing.SelectPartial(strokes, lasso));
        Assert.True(InkStrokeEditing.Erase(strokes, 20, 50, 100, 100, 1, false));
        Assert.Empty(strokes);
    }

    [Fact]
    public void RasterisedStrokeBoundariesFillOnlyTheClickedEnclosureAndKeepInnerIslands()
    {
        using var device = new CanvasDevice(true);
        List<InkPoint> outer = InkStrokeEditing.CreateShape(InkShape.Rectangle, 0.1, 0.1, 0.9, 0.9, 4, 0xFFFFFFFF);
        List<InkPoint> inner = InkStrokeEditing.CreateShape(InkShape.Rectangle, 0.4, 0.4, 0.6, 0.6, 4, 0xFFFFFFFF);
        List<InkPoint> fill = InkBucketFill.Create(device, [outer, inner], 200, 100, 50, 50, 0x80FFFFFF);
        var project = new EditorProject { Frames = [new() { Strokes = [fill] }] };
        Assert.Equal(0x80FFFFFFu, InkColourSampling.Sample(device, project, 0, 200, 100, 50, 50));
        Assert.Equal(0u, InkColourSampling.Sample(device, project, 0, 200, 100, 100, 50));
        Assert.Equal(0u, InkColourSampling.Sample(device, project, 0, 200, 100, 5, 5));
        Assert.All(outer, point => Assert.False(point.IsFilled));
    }

    [Fact]
    public void OpenAreasStrokeSeedsInvisibleColoursAndInvalidCoordinatesFailExplicitly()
    {
        using var device = new CanvasDevice(true);
        Assert.Throws<InvalidOperationException>(() => InkBucketFill.Create(device, [], 100, 100, 50, 50, 0xFFFFFFFF));
        List<InkPoint> outline = InkStrokeEditing.CreateShape(InkShape.Rectangle, 0.1, 0.1, 0.9, 0.9, 4, 0xFFFFFFFF);
        Assert.Throws<InvalidOperationException>(() => InkBucketFill.Create(device, [outline], 100, 100, 10, 50, 0xFFFFFFFF));
        Assert.Throws<InvalidOperationException>(() => InkBucketFill.Create(device, [outline], 100, 100, 50, 50, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => InkBucketFill.Create(device, [outline], 100, 100, double.NaN, 50, 0xFFFFFFFF));
        Assert.Throws<ArgumentException>(() => InkBucketFill.CreateFromMask([], 10, 10, 1, 1, 0xFFFFFFFF));
    }

    [Fact]
    public void LargeCanvasUsesBoundedSamplingButRetainsNormalisedOutputCoordinates()
    {
        using var device = new CanvasDevice(true);
        List<InkPoint> outline = InkStrokeEditing.CreateShape(InkShape.Rectangle, 0.1, 0.1, 0.9, 0.9, 12, 0xFFFFFFFF);
        List<InkPoint> fill = InkBucketFill.Create(device, [outline], 3840, 2160, 1920, 1080, 0xFFFFFFFF);
        Assert.True(InkSelectionEditing.ContainsPoint(fill, 0.5, 0.5));
        Assert.False(InkSelectionEditing.ContainsPoint(fill, 0.05, 0.5));
        Assert.All(fill, point =>
        {
            Assert.InRange(point.X, 0, 1);
            Assert.InRange(point.Y, 0, 1);
        });
    }

    [Fact]
    public void ExcessivelyComplexAreasAreRejectedWithoutChangingTheMask()
    {
        const int size = 384;
        bool[] mask = Mask(size, size);
        for (int y = 1; y < size - 1; y += 2)
        {
            for (int x = 1; x < size - 1; x += 2)
            {
                mask[y * size + x] = true;
            }
        }
        bool[] before = mask.ToArray();
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            InkBucketFill.CreateFromMask(mask, size, size, 2, 2, 0xFFFFFFFF));
        Assert.Contains("too complex", error.Message);
        Assert.Equal(before, mask);
    }

    [Fact]
    public void BucketFilledHolesSurviveEncodedVideoExport()
    {
        if (!VideoExportTests.HasH264Encoder)
        {
            return;
        }
        string output = Path.Combine(Path.GetTempPath(), $"inkmotion-bucket-{Guid.NewGuid():N}.mp4");
        try
        {
            using var device = new CanvasDevice(true);
            List<InkPoint> outer = InkStrokeEditing.CreateShape(InkShape.Rectangle, 0.1, 0.1, 0.9, 0.9, 4, 0xFFFFFFFF);
            List<InkPoint> inner = InkStrokeEditing.CreateShape(InkShape.Rectangle, 0.4, 0.4, 0.6, 0.6, 4, 0xFFFFFFFF);
            List<InkPoint> fill = InkBucketFill.Create(device, [outer, inner], 200, 100, 50, 50, 0xFFFFFFFF);
            var project = new EditorProject { Frames = [new() { IsKeyframe = true, ExposureFrames = 6, Strokes = [fill] }] };
            VideoExporter.Export(new(project, VideoExportProfile.Create(ExportResolution.P720, 24),
                VideoExportCodec.H264, output, 720, PreferHardware: false), null, CancellationToken.None);
            using var reader = new MediaFoundationVideoReader(output);
            Assert.True(reader.TryGetFrame(0));
            int hole = ((reader.Height / 2) * reader.Width + reader.Width / 2) * 4;
            int filled = ((reader.Height / 2) * reader.Width + reader.Width / 4) * 4;
            Assert.InRange(reader.Pixels[hole + 2], (byte)0, (byte)20);
            Assert.InRange(reader.Pixels[filled + 2], (byte)230, (byte)255);
        }
        finally
        {
            File.Delete(output);
        }
    }
}
