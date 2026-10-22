using Microsoft.Graphics.Canvas;
using Xunit;

namespace InkMotion.Tests;

public sealed class InkBrushTextureTests
{
    private static List<InkPoint> Stroke(InkBrushTexture texture) =>
    [
        new() { X = 0.2, Y = 0.5, BrushWidth = 12, Pressure = 0.3, BrushTexture = texture },
        new() { X = 0.8, Y = 0.5, BrushWidth = 12, Pressure = 0.9, BrushTexture = texture },
    ];

    private static byte[] Render(CanvasDevice device, List<InkPoint> stroke)
    {
        using var target = new CanvasRenderTarget(device, 128, 128, 96);
        using (CanvasDrawingSession session = target.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Transparent);
            TimelineCompositor.DrawInkStroke(session, stroke, Microsoft.UI.Colors.White, 128, 128, 1);
        }

        return target.GetPixelBytes();
    }

    [Fact]
    public void GrainRenderingIsDeterministicAndActuallyDifferentFromSolidRoundInk()
    {
        using var device = new CanvasDevice(true);
        byte[] grain = Render(device, Stroke(InkBrushTexture.Grain));
        byte[] repeated = Render(device, Stroke(InkBrushTexture.Grain));
        byte[] round = Render(device, Stroke(InkBrushTexture.Round));
        Assert.Equal(grain, repeated);
        Assert.False(grain.SequenceEqual(round));
        Assert.Contains(grain, value => value > 0);
        Assert.True(grain.Where((_, index) => index % 4 == 3).Sum(value => (long)value) <
            round.Where((_, index) => index % 4 == 3).Sum(value => (long)value));
    }

    [Fact]
    public void TextureSurvivesCloningSaveLoadStabilisationAndPartialErasing()
    {
        List<InkPoint> stroke = Stroke(InkBrushTexture.Grain);
        var project = new EditorProject { Frames = [new() { Strokes = [stroke] }] };
        List<InkPoint> cloned = InkEditing.Clone(project.Frames[0]).Strokes[0];
        Assert.All(cloned, point => Assert.Equal(InkBrushTexture.Grain, point.BrushTexture));
        EditorProject restored = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        Assert.All(restored.Frames[0].Strokes[0], point => Assert.Equal(InkBrushTexture.Grain, point.BrushTexture));
        InkPoint stabilised = InkStrokeEditing.Stabilise(stroke[1], stroke[0], 0.5);
        Assert.Equal(InkBrushTexture.Grain, stabilised.BrushTexture);
        Assert.True(InkStrokeEditing.Erase(project.Frames[0].Strokes, 64, 64, 128, 128, 5, false));
        Assert.All(project.Frames[0].Strokes.SelectMany(piece => piece),
            point => Assert.Equal(InkBrushTexture.Grain, point.BrushTexture));
        Assert.Equal(2, project.Frames[0].Strokes.Count);
    }

    [Fact]
    public void TweenAndPartialLassoRetainStartingBrushTexture()
    {
        var frames = new List<EditorFrame>
        {
            new() { IsKeyframe = true, Strokes = [Stroke(InkBrushTexture.Grain)] },
            new(),
            new() { IsKeyframe = true, Strokes = [Stroke(InkBrushTexture.Round)] },
        };
        IReadOnlyList<List<InkPoint>> tween = InkAnimation.GetDisplayedStrokes(frames, 1);
        Assert.All(Assert.Single(tween), point => Assert.Equal(InkBrushTexture.Grain, point.BrushTexture));
        List<InkPoint> polygon =
        [
            new() { X = 0.3, Y = 0.3 }, new() { X = 0.7, Y = 0.3 },
            new() { X = 0.7, Y = 0.7 }, new() { X = 0.3, Y = 0.7 },
        ];
        InkStrokePartition partition = Assert.Single(InkSelectionEditing.SelectPartial(frames[0].Strokes, polygon));
        Assert.All(partition.Pieces.SelectMany(piece => piece),
            point => Assert.Equal(InkBrushTexture.Grain, point.BrushTexture));
    }

    [Fact]
    public void VersionThirtySixMigratesToRoundBrushesAndUnknownTexturesAreRejected()
    {
        EditorProject project = EditorProjectSerialiser.Deserialise(
            """{"Version":36,"Frames":[{"Number":1,"Strokes":[[{"X":0.5,"Y":0.5}]]}],"SelectedFrameIndex":0}""");
        Assert.Equal(EditorProject.CurrentVersion, project.Version);
        Assert.Equal(InkBrushTexture.Round, project.Frames[0].Strokes[0][0].BrushTexture);
        project.Frames[0].Strokes[0][0].BrushTexture = (InkBrushTexture)999;
        Assert.Throws<InvalidDataException>(() => EditorProjectSerialiser.Serialise(project));
    }

    [Fact]
    public void TexturedColourSamplingMatchesTheRenderedFrame()
    {
        using var device = new CanvasDevice(true);
        var project = new EditorProject
        {
            Frames = [new() { Strokes = [Stroke(InkBrushTexture.Grain)] }],
        };
        byte[] rendered = Render(device, project.Frames[0].Strokes[0]);
        project.Frames[0].Strokes[0].ForEach(point => point.BrushColour = 0xFFFFFFFF);
        for (int x = 40; x < 90; x += 7)
        {
            int index = (64 * 128 + x) * 4;
            uint expected = InkColourSampling.FromPremultipliedBgra(
                rendered[index], rendered[index + 1], rendered[index + 2], rendered[index + 3]);
            Assert.Equal(expected, InkColourSampling.Sample(device, project, 0, 128, 128, x, 64));
        }
    }

    [Fact]
    public void VideoExportPreservesTheDifferenceBetweenRoundAndGrainBrushes()
    {
        if (!VideoExportTests.HasH264Encoder)
        {
            return;
        }

        string output = Path.Combine(Path.GetTempPath(), $"inkmotion-grain-{Guid.NewGuid():N}.mp4");
        try
        {
            List<InkPoint> round = Stroke(InkBrushTexture.Round);
            List<InkPoint> grain = Stroke(InkBrushTexture.Grain);
            foreach (InkPoint point in round.Concat(grain))
            {
                point.BrushWidth = 48;
                point.BrushColour = 0xFFFFFFFF;
            }

            var project = new EditorProject
            {
                Frames =
                [
                    new() { IsKeyframe = true, ExposureFrames = 6, Strokes = [round] },
                    new() { Number = 2, IsKeyframe = true, ExposureFrames = 6, Strokes = [grain] },
                ],
            };
            VideoExportResult result = VideoExporter.Export(new(project,
                VideoExportProfile.Create(ExportResolution.P720, 24), VideoExportCodec.H264, output, 720,
                PreferHardware: false), null, CancellationToken.None);
            Assert.Equal(12, result.FrameCount);
            using var reader = new MediaFoundationVideoReader(output);
            Assert.True(reader.TryGetFrame(0));
            long solid = reader.Pixels.Where((_, index) => index % 4 == 2).Sum(value => (long)value);
            Assert.True(reader.TryGetFrame(0.3));
            long textured = reader.Pixels.Where((_, index) => index % 4 == 2).Sum(value => (long)value);
            Assert.True(textured > 0);
            Assert.True(textured < solid * 0.75,
                $"Expected visibly less coverage for grain; solid={solid}, grain={textured}.");
        }
        finally
        {
            File.Delete(output);
        }
    }
}
