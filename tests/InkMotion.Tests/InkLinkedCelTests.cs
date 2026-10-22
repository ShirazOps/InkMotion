using Xunit;

namespace InkMotion.Tests;

public sealed class InkLinkedCelTests
{
    private static EditorProject Project() => new()
    {
        InkLayers = [new(), new() { Id = "ink-2", IsLocked = true }],
        Frames =
        [
            new()
            {
                IsKeyframe = true, ExposureFrames = 2, GroupName = "Walk",
                Strokes = [[new() { X = 0.1, Y = 0.5 }]],
                LayerStrokes = new() { ["ink-2"] = [[new() { X = 0.2, Y = 0.2 }]] },
                TextOverlays = [new() { Text = "Phase A" }],
            },
            new() { Number = 2, ExposureFrames = 3, GroupName = "Walk" },
            new()
            {
                Number = 3, IsKeyframe = true, GroupName = "Walk",
                Strokes = [[new() { X = 0.9, Y = 0.5 }]],
                LayerStrokes = new() { ["ink-2"] = [[new() { X = 0.8, Y = 0.2 }]] },
                TextOverlays = [new() { Text = "Phase C" }],
            },
        ],
    };

    private static List<EditorFrame> Snapshot(EditorProject project) =>
        project.Frames.Select(frame => InkEditing.Clone(frame, preserveDrawingLink: true)).ToList();

    [Fact]
    public void LinkedRepeatBakesDisplayedLayersWithoutChangingExposureOrAppearance()
    {
        EditorProject project = Project();
        double tween = InkAnimation.GetDisplayedStrokes(project.Frames, 1)[0][0].X;
        double other = InkAnimation.GetDisplayedStrokes(project.Frames, 1, f => f.LayerStrokes.GetValueOrDefault("ink-2") ?? [])[0][0].X;
        Assert.Equal(3, InkLinkedCels.Repeat(project.Frames, project.InkLayers, 0, 2, 2));
        Assert.Equal(9, project.Frames.Count);
        Assert.Equal(18, InkTimeline.GetTotalExposureFrames(project.Frames));
        Assert.All(project.Frames, frame => Assert.True(frame.IsKeyframe));
        Assert.Equal(tween, project.Frames[1].Strokes[0][0].X);
        Assert.Equal(other, project.Frames[1].LayerStrokes["ink-2"][0][0].X);
        for (int index = 0; index < 3; index++)
        {
            Assert.Equal(project.Frames[index].DrawingLinkId, project.Frames[index + 3].DrawingLinkId);
            Assert.Equal(project.Frames[index].DrawingLinkId, project.Frames[index + 6].DrawingLinkId);
        }
        Assert.Equal(3, project.Frames.Select(frame => frame.DrawingLinkId).Distinct().Count());
        InkLinkedCels.Validate(project.Frames);
        Assert.True(project.InkLayers[1].IsLocked);
    }

    [Fact]
    public void EditingAnyPartnerPropagatesAllDrawingAndTextStylingButNotTimingOrGroupLabels()
    {
        EditorProject project = Project();
        InkLinkedCels.Repeat(project.Frames, project.InkLayers, 0, 2, 1);
        List<EditorFrame> before = Snapshot(project);
        project.Frames[3].Strokes = [InkStrokeEditing.CreateShape(InkShape.Rectangle, 0.1, 0.1, 0.8, 0.8, 4, 0x80FF0000, true)];
        project.Frames[3].TextOverlays[0].Text = "Changed";
        InkTextEditing.ApplyStyle(project.Frames[3], project.Frames[3].TextOverlays[0], 1, 3,
            new() { Bold = true, Colour = 0xFF00FF00 });
        project.Frames[3].ExposureFrames = 7;
        project.Frames[3].GroupName = "Other";
        InkLinkedCels.Synchronise(project.Frames, before, 3);
        Assert.True(project.Frames[0].Strokes[0][0].IsFilled);
        Assert.Equal("Changed", project.Frames[0].TextOverlays[0].Text);
        Assert.True(project.Frames[0].TextOverlays[0].StyleRanges[0].Bold);
        Assert.Equal(2, project.Frames[0].ExposureFrames);
        Assert.Equal("Walk", project.Frames[0].GroupName);
        Assert.NotSame(project.Frames[3].Strokes, project.Frames[0].Strokes);
        Assert.Equal("Phase C", project.Frames[2].TextOverlays[0].Text);
        InkLinkedCels.Validate(project.Frames);
        EditorProject saved = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        Assert.Equal(project.Frames[0].DrawingLinkId, saved.Frames[3].DrawingLinkId);
        Assert.Equal("Changed", saved.Frames[0].TextOverlays[0].Text);
    }

    [Fact]
    public void UnlinkAndOrdinaryCopyCreateIndependentDrawingsAndDeletingSourceDoesNotBreakLinks()
    {
        EditorProject project = Project();
        InkLinkedCels.Repeat(project.Frames, project.InkLayers, 0, 2, 2);
        List<EditorFrame> before = Snapshot(project);
        Assert.True(InkLinkedCels.Unlink(project.Frames, 0, 0));
        Assert.False(InkLinkedCels.Unlink(project.Frames, 0, 0));
        project.Frames[0].TextOverlays[0].Text = "Independent";
        InkLinkedCels.Synchronise(project.Frames, before, 0);
        Assert.Equal("Phase A", project.Frames[3].TextOverlays[0].Text);
        Assert.Empty(InkEditing.Clone(project.Frames[3]).DrawingLinkId);
        Assert.All(InkEditing.CopyCelRange(project.Frames, 3, 5), frame => Assert.Empty(frame.DrawingLinkId));
        InkEditing.DeleteCelRange(project.Frames, 0, 2);
        before = Snapshot(project);
        project.Frames[3].TextOverlays[0].Text = "Survives";
        InkLinkedCels.Synchronise(project.Frames, before, 3);
        Assert.Equal("Survives", project.Frames[0].TextOverlays[0].Text);
        InkLinkedCels.Validate(project.Frames);
    }

    [Fact]
    public void BatchLayerEditsRemainConsistentWithoutDoubleApplyingTransforms()
    {
        EditorProject project = Project();
        InkLinkedCels.Repeat(project.Frames, project.InkLayers, 0, 2, 1);
        List<EditorFrame> before = Snapshot(project);
        Assert.True(InkEditing.FlipLayer(project.InkLayers, project.Frames, "ink-1", true));
        InkLinkedCels.Synchronise(project.Frames, before, 0);
        Assert.Equal(0.9, project.Frames[0].Strokes[0][0].X, 8);
        Assert.Equal(0.9, project.Frames[3].Strokes[0][0].X, 8);
        InkLinkedCels.Validate(project.Frames);
    }

    [Fact]
    public void InvalidRepeatsAndInconsistentSavedLinksAreRejected()
    {
        EditorProject project = Project();
        string before = EditorProjectSerialiser.Serialise(project);
        Assert.Throws<InvalidOperationException>(() => InkLinkedCels.Repeat(project.Frames, project.InkLayers, 1, 2, 1));
        Assert.Throws<InvalidOperationException>(() => InkLinkedCels.Repeat(project.Frames, project.InkLayers, 0, 2, 101));
        Assert.Equal(before, EditorProjectSerialiser.Serialise(project));
        InkLinkedCels.Repeat(project.Frames, project.InkLayers, 0, 2, 1);
        before = EditorProjectSerialiser.Serialise(project);
        Assert.Throws<InvalidOperationException>(() => InkLinkedCels.Repeat(project.Frames, project.InkLayers, 0, 2, 1));
        Assert.Equal(before, EditorProjectSerialiser.Serialise(project));
        project.Frames[3].TextOverlays[0].Text = "Inconsistent";
        Assert.Throws<InvalidDataException>(() => EditorProjectSerialiser.Serialise(project));
        project.Frames[3].TextOverlays[0].Text = "Phase A";
        project.Frames[0].IsKeyframe = false;
        Assert.Throws<InvalidDataException>(() => EditorProjectSerialiser.Serialise(project));
    }

    [Fact]
    public void LinkedInkAndStyledCelTextExportOverVideoWithoutChangingTheSource()
    {
        if (!VideoExportTests.HasH264Encoder)
        {
            return;
        }

        string source = Path.Combine(Path.GetTempPath(), $"inkmotion-reference-{Guid.NewGuid():N}.mp4");
        string output = Path.Combine(Path.GetTempPath(), $"inkmotion-linked-overlay-{Guid.NewGuid():N}.mp4");
        try
        {
            var background = new EditorProject
            {
                Frames = [new()
                {
                    IsKeyframe = true, ExposureFrames = 24,
                    Strokes = [InkStrokeEditing.CreateShape(InkShape.Rectangle, 0, 0, 1, 1, 4, 0xFF00FF00, true)],
                }],
            };
            var profile = VideoExportProfile.Create(ExportResolution.P720, 24);
            VideoExporter.Export(new(background, profile, VideoExportCodec.H264, source, 400, PreferHardware: false),
                null, CancellationToken.None);
            byte[] original = File.ReadAllBytes(source);
            var text = new InkTextOverlay { Text = "MMMM", FontSize = 100, Y = 0.3 };
            var project = new EditorProject
            {
                VideoTracks = [new()
                {
                    Kind = TimelineTrackKind.Video,
                    Clips = [new() { SourcePath = source, SourceDurationSeconds = 1, DurationSeconds = 1 }],
                }],
                Frames = [new()
                {
                    IsKeyframe = true, ExposureFrames = 12, TextOverlays = [text],
                    Strokes = [InkStrokeEditing.CreateShape(InkShape.Rectangle, 0.3, 0.65, 0.7, 0.85, 4, 0xFFFFFFFF, true)],
                }],
            };
            InkLinkedCels.Repeat(project.Frames, project.InkLayers, 0, 0, 1);
            List<EditorFrame> before = Snapshot(project);
            project.Frames[1].Strokes[0].ForEach(point => point.BrushColour = 0xFFFF0000);
            InkTextEditing.ApplyStyle(project.Frames[1], project.Frames[1].TextOverlays[0], 1, 4,
                new() { FontSize = 100, Colour = 0xFF0000FF, Bold = true });
            InkLinkedCels.Synchronise(project.Frames, before, 1);
            project = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
            VideoExporter.Export(new(project, profile, VideoExportCodec.H264, output, 400, PreferHardware: false),
                null, CancellationToken.None);

            using (var reader = new MediaFoundationVideoReader(output))
            {
                foreach (double seconds in new[] { 0.1, 0.7 })
                {
                    Assert.True(reader.TryGetFrame(seconds));
                    byte[] pixels = reader.Pixels;
                    int green = (reader.Width / 10 + reader.Height / 10 * reader.Width) * 4;
                    Assert.True(pixels[green + 1] > 180 && pixels[green + 2] < 60 && pixels[green] < 60);
                    int red = (reader.Width / 2 + reader.Height * 3 / 4 * reader.Width) * 4;
                    Assert.True(pixels[red + 2] > 180 && pixels[red + 1] < 60 && pixels[red] < 60);
                    Assert.True(Enumerable.Range(0, pixels.Length / 4).Count(i =>
                        pixels[i * 4] > 180 && pixels[i * 4 + 1] < 60 && pixels[i * 4 + 2] < 60) > 100);
                }
            }
            Assert.Equal(original, File.ReadAllBytes(source));
            using var sourceReader = new MediaFoundationVideoReader(source);
            Assert.True(sourceReader.TryGetFrame(0.7));
            Assert.DoesNotContain(Enumerable.Range(0, sourceReader.Pixels.Length / 4), i =>
                sourceReader.Pixels[i * 4 + 2] > 180 || sourceReader.Pixels[i * 4] > 180);
        }
        finally
        {
            File.Delete(source);
            File.Delete(output);
        }
    }

    [Fact]
    public void OldCelsMigrateWithoutLinksAndHistorySnapshotsRetainThem()
    {
        EditorProject old = EditorProjectSerialiser.Deserialise(
            """{"Version":40,"Frames":[{"Number":1}],"SelectedFrameIndex":0}""");
        Assert.Empty(old.Frames[0].DrawingLinkId);
        EditorProject project = Project();
        InkLinkedCels.Repeat(project.Frames, project.InkLayers, 0, 2, 1);
        string before = EditorProjectSerialiser.Serialise(project);
        InkLinkedCels.Unlink(project.Frames, 0, 5);
        EditorProject undone = EditorProjectSerialiser.Deserialise(before);
        Assert.All(undone.Frames, frame => Assert.NotEmpty(frame.DrawingLinkId));
        InkLinkedCels.Validate(undone.Frames);
    }
}
