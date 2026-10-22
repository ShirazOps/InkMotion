using Microsoft.Graphics.Canvas;
using Xunit;

namespace InkMotion.Tests;

public sealed class InkFillTests
{
    private static List<InkPoint> Shape(InkShape shape = InkShape.Rectangle, bool filled = true,
        uint colour = 0xFFFFFFFF) =>
        InkStrokeEditing.CreateShape(shape, 0.2, 0.2, 0.8, 0.8, 4, colour, filled);

    [Theory]
    [InlineData(InkShape.Rectangle)]
    [InlineData(InkShape.Ellipse)]
    public void ClosedShapesFillTheirInteriorButNotTheirBoundingBoxExterior(InkShape shape)
    {
        using var device = new CanvasDevice(true);
        var project = new EditorProject { Frames = [new() { Strokes = [Shape(shape)] }] };
        Assert.Equal(0xFFFFFFFFu, InkColourSampling.Sample(device, project, 0, 100, 100, 50, 50));
        Assert.Equal(0u, InkColourSampling.Sample(device, project, 0, 100, 100, 10, 50));
        if (shape == InkShape.Ellipse)
        {
            Assert.Equal(0u, InkColourSampling.Sample(device, project, 0, 100, 100, 21, 21));
        }

        project.Frames[0].Strokes = [Shape(shape, filled: false)];
        Assert.Equal(0u, InkColourSampling.Sample(device, project, 0, 100, 100, 50, 50));
    }

    [Fact]
    public void FillUsesOneAlphaCompositeAndIgnoresBrushTextureAndWidth()
    {
        using var device = new CanvasDevice(true);
        List<InkPoint> shape = Shape(colour: 0x80FFFFFF);
        shape.ForEach(point =>
        {
            point.BrushTexture = InkBrushTexture.Grain;
            point.BrushWidth = 64;
        });
        var project = new EditorProject { Frames = [new() { Strokes = [shape] }] };
        Assert.Equal(0x80FFFFFFu, InkColourSampling.Sample(device, project, 0, 100, 100, 50, 50));
        Assert.Equal(0x80FFFFFFu, InkColourSampling.Sample(device, project, 0, 100, 100, 21, 21));
        Assert.Equal(0u, InkColourSampling.Sample(device, project, 0, 100, 100, 15, 50));
        project.InkLayers[0].Opacity = 0.5;
        Assert.Equal(0x40FFFFFFu, InkColourSampling.Sample(device, project, 0, 100, 100, 50, 50));
        project.InkLayers[0].IsVisible = false;
        Assert.Equal(0u, InkColourSampling.Sample(device, project, 0, 100, 100, 50, 50));
    }

    [Fact]
    public void LineAndDefaultShapeCreationRemainUnfilled()
    {
        Assert.All(Shape(InkShape.Line), point => Assert.False(point.IsFilled));
        Assert.All(InkStrokeEditing.CreateShape(InkShape.Rectangle, 0.2, 0.2, 0.8, 0.8, 4, 0xFFFFFFFF),
            point => Assert.False(point.IsFilled));
    }

    [Theory]
    [InlineData(false, 50, 50, true)]
    [InlineData(true, 50, 50, true)]
    [InlineData(false, 19, 50, true)]
    [InlineData(false, 10, 50, false)]
    public void EraserTreatsFilledShapesAsWholeObjectsIncludingInteriorAndRadiusHits(
        bool whole, double x, double y, bool expected)
    {
        List<InkPoint> shape = Shape();
        List<InkPoint> untouched = [new() { X = 0.95, Y = 0.95 }];
        List<List<InkPoint>> strokes = [shape, untouched];
        Assert.Equal(expected, InkStrokeEditing.Erase(strokes, x, y, 100, 100, 2, whole));
        Assert.Contains(untouched, strokes);
        Assert.Equal(!expected, strokes.Contains(shape));
        Assert.Equal(expected ? 1 : 2, strokes.Count);
    }

    [Theory]
    [InlineData(0.4, 0.6, true)]
    [InlineData(0.1, 0.3, true)]
    [InlineData(0.0, 1.0, true)]
    [InlineData(0.0, 0.1, false)]
    public void BothLassoModesSelectWholeFilledObjectsWithoutSplitting(
        double start, double end, bool hit)
    {
        List<InkPoint> shape = Shape();
        List<InkPoint> lasso = InkStrokeEditing.CreateShape(InkShape.Rectangle,
            start, start, end, end, 4, 0xFFFFFFFF);
        Assert.Equal(hit ? 1 : 0, InkSelectionEditing.Select([shape], lasso).Count);
        List<InkStrokePartition> partitions = InkSelectionEditing.SelectPartial([shape], lasso);
        Assert.Equal(hit ? 1 : 0, partitions.Count);
        if (hit)
        {
            List<InkPoint> selected = Assert.Single(Assert.Single(partitions).Selected);
            Assert.Equal(shape.Count, selected.Count);
            Assert.All(selected, point => Assert.True(point.IsFilled));
            selected[0].X = 0.3;
            Assert.Equal(0.2, shape[0].X);
        }
    }

    [Fact]
    public void FillSurvivesCloningTransformSaveLoadAndSnapshotRestoration()
    {
        var project = new EditorProject { Frames = [new() { Strokes = [Shape()] }] };
        string before = EditorProjectSerialiser.Serialise(project);
        EditorFrame clone = InkEditing.Clone(project.Frames[0]);
        Assert.True(InkSelectionEditing.Transform(clone.Strokes, 0.1, 0, 1, 0, 100, 100));
        Assert.All(clone.Strokes[0], point => Assert.True(point.IsFilled));
        Assert.Equal(0.2, project.Frames[0].Strokes[0][0].X);
        project.Frames[0] = clone;
        EditorProject reopened = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        Assert.True(reopened.Frames[0].Strokes[0][0].IsFilled);
        Assert.Equal(0.3, reopened.Frames[0].Strokes[0][0].X, 8);
        Assert.Equal(0.2, EditorProjectSerialiser.Deserialise(before).Frames[0].Strokes[0][0].X);
    }

    [Fact]
    public void FilledTweensRetainFillAndMixedOutlineFillKeyframesHoldRatherThanMorph()
    {
        List<InkPoint> start = Shape();
        List<InkPoint> end = Shape(InkShape.Ellipse);
        var frames = new List<EditorFrame>
        {
            new() { IsKeyframe = true, Strokes = [start] },
            new(),
            new() { IsKeyframe = true, Strokes = [end] },
        };
        List<InkPoint> tween = Assert.Single(InkAnimation.GetDisplayedStrokes(frames, 1));
        Assert.All(tween, point => Assert.True(point.IsFilled));
        Assert.Equal(65, tween.Count);
        end.ForEach(point => point.IsFilled = false);
        Assert.Same(start, Assert.Single(InkAnimation.GetDisplayedStrokes(frames, 1)));
    }

    [Fact]
    public void OldProjectsMigrateToOutlinesAndInvalidFillPathsAreRejected()
    {
        EditorProject project = EditorProjectSerialiser.Deserialise(
            """{"Version":37,"Frames":[{"Number":1,"Strokes":[[{"X":0.5,"Y":0.5}]]}],"SelectedFrameIndex":0}""");
        Assert.Equal(EditorProject.CurrentVersion, project.Version);
        Assert.False(project.Frames[0].Strokes[0][0].IsFilled);
        project.Frames[0].Strokes[0][0].IsFilled = true;
        Assert.Throws<InvalidDataException>(() => EditorProjectSerialiser.Serialise(project));
        project.Frames[0].Strokes = [Shape()];
        project.Frames[0].Strokes[0][1].IsFilled = false;
        Assert.Throws<InvalidDataException>(() => EditorProjectSerialiser.Serialise(project));
    }

    [Fact]
    public void VideoExportContainsTheFilledInteriorNotOnlyTheOutline()
    {
        if (!VideoExportTests.HasH264Encoder)
        {
            return;
        }

        string output = Path.Combine(Path.GetTempPath(), $"inkmotion-fill-{Guid.NewGuid():N}.mp4");
        try
        {
            var project = new EditorProject
            {
                Frames =
                [
                    new() { IsKeyframe = true, ExposureFrames = 6, Strokes = [Shape(filled: false)] },
                    new() { Number = 2, IsKeyframe = true, ExposureFrames = 6, Strokes = [Shape()] },
                ],
            };
            VideoExportResult result = VideoExporter.Export(new(project,
                VideoExportProfile.Create(ExportResolution.P720, 24), VideoExportCodec.H264, output, 720,
                PreferHardware: false), null, CancellationToken.None);
            Assert.Equal(12, result.FrameCount);
            using var reader = new MediaFoundationVideoReader(output);
            Assert.True(reader.TryGetFrame(0));
            int centre = ((reader.Height / 2) * reader.Width + reader.Width / 2) * 4;
            Assert.InRange(reader.Pixels[centre + 2], (byte)0, (byte)20);
            Assert.True(reader.TryGetFrame(0.3));
            Assert.InRange(reader.Pixels[centre + 2], (byte)230, (byte)255);
        }
        finally
        {
            File.Delete(output);
        }
    }
}
