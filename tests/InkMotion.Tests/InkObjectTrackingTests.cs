using System.Numerics;
using Microsoft.Graphics.Canvas;
using Xunit;

namespace InkMotion.Tests;

public sealed class InkObjectTrackingTests
{
    private static InkObjectTracking Tracking() => new()
    {
        SourceClipId = "video-1", StartSeconds = 0, EndSeconds = 2, ReferenceSeconds = 0,
        AnchorX = 0.25, AnchorY = 0.5, BoxWidth = 0.2, BoxHeight = 0.3,
        Keys = [new(0, 0.25, 0.5), new(1, 0.75, 0.5, 2, 90)],
    };

    [Fact]
    public void SimilarityFit_RecoversTranslationRotationScaleAndRejectsOutliers()
    {
        Matrix3x2 expected = Matrix3x2.CreateScale(1.08f) * Matrix3x2.CreateRotation(0.12f) *
            Matrix3x2.CreateTranslation(7, -3);
        var pairs = new List<(Vector2 From, Vector2 To)>();
        foreach (Vector2 point in new Vector2[] { new(10, 10), new(40, 10), new(10, 40), new(40, 40), new(25, 25) })
            pairs.Add((point, Vector2.Transform(point, expected)));
        pairs.Add((new(80, 90), new(2, 3)));
        Assert.True(InkObjectTracker.TryFitSimilarity(pairs, out Matrix3x2 actual, out double scale, out double angle));
        Assert.InRange(scale, 1.079, 1.081);
        Assert.InRange(angle, 6.87, 6.89);
        Assert.InRange(Vector2.Distance(Vector2.Transform(new(20, 30), actual),
            Vector2.Transform(new(20, 30), expected)), 0, 0.001);
        Assert.False(InkObjectTracker.TryFitSimilarity(pairs.Take(2).ToArray(), out _, out _, out _));
    }

    [Fact]
    public void Tracking_PersistsIndependentlyAndDoesNotPropagateToNewOrDuplicatedLayers()
    {
        var project = new EditorProject();
        project.InkLayers[0].ObjectTracking = Tracking();
        project.InkLayers.Add(new() { Id = "ink-2", Name = "Other" });
        EditorProject reopened = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        Assert.Equal(66, reopened.Version);
        Assert.NotNull(reopened.InkLayers[0].ObjectTracking);
        Assert.Null(reopened.InkLayers[1].ObjectTracking);
        InkLayerProject clone = InkEditing.CloneLayer(reopened.InkLayers[0]);
        clone.ObjectTracking!.Keys.Clear();
        Assert.Equal(2, reopened.InkLayers[0].ObjectTracking!.Keys.Count);
        InkLayerProject duplicate = InkEditing.DuplicateLayer(reopened.InkLayers, reopened.Frames, "ink-1");
        Assert.Null(duplicate.ObjectTracking);
        string old = EditorProjectSerialiser.Serialise(new EditorProject()).Replace("\"Version\": 66", "\"Version\": 65");
        Assert.All(EditorProjectSerialiser.Deserialise(old).InkLayers, layer => Assert.Null(layer.ObjectTracking));
    }

    [Fact]
    public void SharedRendering_TracksOnlySelectedLayerAndHidesAtLossAndOutsideRange()
    {
        using var device = new CanvasDevice();
        var project = new EditorProject();
        project.InkLayers[0].ObjectTracking = Tracking();
        project.InkLayers.Add(new() { Id = "ink-2", Name = "Untracked" });
        project.Frames[0].Strokes = [[new() { X = 0.25, Y = 0.5, BrushWidth = 8, BrushColour = 0xFFFF0000 }]];
        project.Frames[0].LayerStrokes["ink-2"] = [[new() { X = 0.1, Y = 0.2, BrushWidth = 8, BrushColour = 0xFF00FF00 }]];
        uint Sample(double seconds, double x, double y) =>
            InkColourSampling.Sample(device, project, 0, 200, 100, x, y, seconds);
        Assert.Equal(0xFFFF0000, Sample(1, 150, 50));
        Assert.Equal(0xFF00FF00, Sample(1, 20, 20));
        Assert.Equal(0u, Sample(1, 50, 50));
        project.InkLayers[0].ObjectTracking!.LostSeconds = 1;
        Assert.Equal(0u, Sample(1, 150, 50));
        Assert.Equal(0u, Sample(2, 150, 50));
        Assert.Equal(0xFF00FF00, Sample(2, 20, 20));
        project.InkLayers[0].ObjectTracking!.Enabled = false;
        Assert.Equal(0xFFFF0000, Sample(1, 50, 50));
    }

    [Fact]
    public void Motion_RefitsObjectBoundDrawingBetweenViewerAndExportAspectRatios()
    {
        InkObjectTracking tracking = Tracking();
        tracking.CanvasAspectRatio = 4;
        tracking.AnchorX = 0.5;
        tracking.Keys = [new(0, 0.5, 0.5, 1, 0, PictureAspectRatio: 2),
            new(1, 0.625, 0.5, 1, 90, PictureAspectRatio: 2)];
        // In the wide reference viewer the source occupies the central half of its width.
        // A point 10 reference pixels right of the anchor rotates to 10 export pixels below it.
        Vector2 point = Vector2.Transform(new(105, 50), InkObjectMotion.Transform(tracking, 1, 200, 100));
        Assert.InRange(point.X, 149.99f, 150.01f);
        Assert.InRange(point.Y, 59.99f, 60.01f);
        Vector2 initial = Vector2.Transform(new(105, 50), InkObjectMotion.Transform(tracking, 0, 200, 100));
        Assert.InRange(initial.X, 109.99f, 110.01f);
        tracking.SourceSignature = "{}";
        Assert.False(InkObjectMotion.IsSourceCurrent(tracking, []));
        tracking.Enabled = false;
        Assert.True(InkObjectMotion.IsSourceCurrent(tracking, []));
    }

    [Fact]
    public void EmptyCels_HoldReferenceDrawingButExistingAnimationRemains()
    {
        var project = new EditorProject();
        project.InkLayers[0].ObjectTracking = Tracking();
        project.Frames[0].Strokes = [[new() { X = 0.2, Y = 0.3 }]];
        project.Frames.Add(new() { Number = 2 });
        Assert.Single(InkObjectMotion.Strokes(project.Frames, 1, project.InkLayers[0], 24));
        project.Frames[1].Strokes = [[new() { X = 0.7, Y = 0.8 }]];
        Assert.Equal(0.7, InkObjectMotion.Strokes(project.Frames, 1, project.InkLayers[0], 24)[0][0].X);
        project.InkLayers[0].ObjectTracking!.Keys[0] = new(double.NaN, 0, 0);
        Assert.False(InkObjectMotion.IsValid(project.InkLayers[0].ObjectTracking!));
        Assert.Throws<InvalidDataException>(() => EditorProjectSerialiser.Serialise(project));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tracker_FollowsMovingRotatingScalingTextureAndStopsWhenItDisappears(bool disappear)
    {
        if (!VideoExportTests.HasH264Encoder) return;
        string path = Path.Combine(Path.GetTempPath(), $"inkmotion-object-{Guid.NewGuid():N}.mp4");
        const int width = 384, height = 216, count = 18;
        var random = new Random(17);
        byte[] texture = new byte[96 * 80];
        random.NextBytes(texture);
        try
        {
            using (var writer = new MediaFoundationVideoWriter(path, width, height, 24,
                VideoExportCodec.H264, 4_000_000, 0, 0, false))
            {
                for (int frame = 0; frame < count; frame++)
                {
                    var pixels = new byte[width * height * 4];
                    Matrix3x2 transform = Matrix3x2.CreateScale(1 + frame * 0.004f) *
                        Matrix3x2.CreateRotation(frame * 0.008f) *
                        Matrix3x2.CreateTranslation(150 + frame, 108);
                    Matrix3x2.Invert(transform, out Matrix3x2 inverse);
                    for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        Vector2 local = Vector2.Transform(new(x, y), inverse) + new Vector2(48, 40);
                        byte value = 20;
                        if ((!disappear || frame < 10) && local.X >= 0 && local.X < 96 && local.Y >= 0 && local.Y < 80)
                            value = texture[(int)local.Y * 96 + (int)local.X];
                        int pixel = (y * width + x) * 4;
                        pixels[pixel] = pixels[pixel + 1] = pixels[pixel + 2] = value;
                        pixels[pixel + 3] = 255;
                    }
                    writer.WriteVideoFrame(pixels, ExportPlanning.GetFrameTime(frame, 24),
                        ExportPlanning.GetFrameTime(frame + 1, 24) - ExportPlanning.GetFrameTime(frame, 24));
                }
                writer.Finish();
            }
            var clip = new TimelineClipProject { SourcePath = path, SourceOutSeconds = count / 24d,
                SourceDurationSeconds = count / 24d };
            var project = new EditorProject { VideoTracks = [new() { Kind = TimelineTrackKind.Video, Clips = [clip] }] };
            InkTrackingResult result = InkObjectTracker.Analyse(project, clip.Id, 0, count / 24d,
                new(150d / width, 0.5, 0.24, 0.35), new(0, 150d / width, 0.5), width / (double)height, null, CancellationToken.None);
            if (disappear)
            {
                Assert.NotNull(result.LostSeconds);
                Assert.InRange(result.LostSeconds!.Value, 9 / 24d, 12 / 24d);
                Assert.True(result.Keys.Count > 4);
            }
            else
            {
                Assert.Null(result.LostSeconds);
                InkObjectPose last = result.Keys[^1];
                Assert.InRange(last.X * width, 162, 172);
                Assert.InRange(last.Scale, 1.03, 1.13);
                Assert.InRange(last.Rotation, 4, 12);
                var tracking = new InkObjectTracking
                {
                    SourceClipId = clip.Id, StartSeconds = 0, EndSeconds = count / 24d,
                    CanvasAspectRatio = width / (double)height, AnchorX = 150d / width, AnchorY = 0.5,
                    BoxWidth = 0.24, BoxHeight = 0.35, Keys = result.Keys.ToList(),
                };
                const int restart = 8;
                double correctedScale = 1 + restart * 0.004;
                InkTrackingResult resumed = InkObjectTracker.Analyse(project, clip.Id, restart / 24d, count / 24d,
                    new(0.5 + ((150d + restart) / width - 0.5) / 2, 0.5,
                        0.12 * correctedScale, 0.35 * correctedScale),
                    new(restart / 24d, 0, 0, Rotation: restart * 0.008 * 180 / Math.PI),
                    width * 2d / height, null, CancellationToken.None, tracking);
                Assert.Null(resumed.LostSeconds);
                Assert.InRange(resumed.Keys[^1].X * width, 162, 172);
                Assert.InRange(resumed.Keys[^1].Scale, 1.03, 1.13);
                project.InkLayers[0].IsVisible = true;
                project.InkLayers[0].ObjectTracking = tracking;
                project.Frames[0].Strokes = [[new() { X = 150d / width, Y = 0.5, BrushWidth = 8, BrushColour = 0xFFFF0000 }]];
                var profile = new VideoExportProfile(ExportResolution.P720, 192, 192, 24);
                string output = path + ".tracked.mp4";
                try
                {
                    VideoExporter.Export(new(project, profile, VideoExportCodec.H264, output, 192, PreferHardware: false),
                        null, CancellationToken.None);
                    using var reader = new MediaFoundationVideoReader(output);
                    Assert.True(reader.TryGetFrame((count - 1) / 24d));
                    byte[] rendered = reader.Pixels;
                    int centrePixel = (96 * reader.Width + (int)Math.Round(last.X * 192)) * 4;
                    Assert.True(rendered[centrePixel + 2] > 170 && rendered[centrePixel + 1] < 70,
                        "Object-bound ink must remain on the tracked centre in a differently shaped encoded export.");
                }
                finally { File.Delete(output); }
            }
        }
        finally { File.Delete(path); }
    }
}
