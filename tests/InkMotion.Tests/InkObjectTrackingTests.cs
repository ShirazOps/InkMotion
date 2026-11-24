using System.Numerics;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using Xunit;

namespace InkMotion.Tests;

public sealed class InkObjectTrackingTests
{
    [Fact]
    public void UntrackedVideoSerialization_PreservesLegacySourceSignature()
    {
        var clip = new TimelineClipProject { Id = "video-1", SourcePath = "source.mp4", SourceDurationSeconds = 2 };
        string signature = JsonSerializer.Serialize(clip);
        Assert.DoesNotContain("ObjectTracking", signature);
        var project = new EditorProject
        {
            VideoTracks = [new() { Kind = TimelineTrackKind.Video, Clips = [clip] }],
        };
        InkObjectTracking tracking = Tracking();
        tracking.SourceSignature = signature;
        project.InkLayers[0].ObjectTracking = tracking;
        EditorProject reopened = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        Assert.True(InkObjectMotion.IsSourceCurrent(reopened.InkLayers[0].ObjectTracking, reopened.VideoTracks));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HiddenTrackedTitle_DoesNotBlockExportOrReportTrackingLoss(bool staleSource)
    {
        if (!VideoExportTests.HasH264Encoder) return;
        InkObjectTracking tracking = Tracking();
        tracking.LostSeconds = 1;
        tracking.SourceSignature = staleSource ? "{}" : "";
        var project = new EditorProject
        {
            VideoTracks = [new()
            {
                Kind = TimelineTrackKind.Text,
                IsVisible = false,
                Clips = [new() { Text = "Hidden", DurationSeconds = 0.25, ObjectTracking = tracking }],
            }],
        };
        var profile = new VideoExportProfile(ExportResolution.P720, 200, 100, 24);
        string path = Path.Combine(Path.GetTempPath(), $"inkmotion-hidden-title-{Guid.NewGuid():N}.mp4");
        try
        {
            VideoExportResult result = VideoExporter.Export(
                new(project, profile, VideoExportCodec.H264, path, 100, PreferHardware: false),
                null, CancellationToken.None);
            Assert.True(File.Exists(path));
            Assert.Empty(result.Warnings);
        }
        finally { File.Delete(path); }
    }

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

    private static readonly Vector2[] PlanarSource = [new(20, 20), new(120, 20), new(120, 80), new(20, 80)];
    private static readonly Vector2[] PlanarTarget = [new(30, 25), new(110, 40), new(105, 70), new(25, 90)];

    private static InkObjectTracking PlanarTracking(double[] corners) => new()
    {
        SourceClipId = "video-1", StartSeconds = 0, EndSeconds = 2, ReferenceSeconds = 0, Perspective = true,
        AnchorX = 0.35, AnchorY = 0.5, BoxWidth = 0.5, BoxHeight = 0.6,
        Keys = [new(0, 0.35, 0.5, Corners: [0.1, 0.2, 0.6, 0.2, 0.6, 0.8, 0.1, 0.8]), new(1, 0.5, 0.5, Corners: corners)],
    };

    private static readonly double[] KeystoneCorners = [0.45, 0.35, 0.9, 0.1, 0.9, 0.9, 0.45, 0.65];

    [Fact]
    public void HomographyFit_RecoversPerspectiveMapAndRejectsOutliers()
    {
        Assert.True(InkHomography.TryFit(PlanarSource.Zip(PlanarTarget).ToArray(), out InkHomography exact));
        var random = new Random(3);
        var pairs = new List<(Vector2 From, Vector2 To)>();
        for (int i = 0; i < 40; i++)
        {
            Vector2 point = new(20 + random.NextSingle() * 100, 20 + random.NextSingle() * 60);
            pairs.Add((point, exact.Apply(point)));
        }
        for (int i = 0; i < 8; i++)
            pairs.Add((new(20 + i * 10, 50), new(random.NextSingle() * 200, random.NextSingle() * 200)));
        Assert.True(InkObjectTracker.TryFitHomography(pairs, out InkHomography fitted));
        for (int i = 0; i < 4; i++)
            Assert.InRange(Vector2.Distance(fitted.Apply(PlanarSource[i]), PlanarTarget[i]), 0, 0.05f);
        Assert.False(InkObjectTracker.TryFitHomography(pairs.Take(3).ToArray(), out _));
        Assert.True(InkHomography.IsConvexQuad(PlanarTarget));
        Assert.False(InkHomography.IsConvexQuad([PlanarTarget[0], PlanarTarget[2], PlanarTarget[1], PlanarTarget[3]]));
    }

    [Fact]
    public void PlanarProjection_PinsReferenceBoxCornersToTrackedCorners()
    {
        InkObjectTracking tracking = PlanarTracking(KeystoneCorners);
        Assert.Null(InkObjectMotion.Projection(Tracking(), 1, 200, 100));
        Matrix4x4 projection = InkObjectMotion.Projection(tracking, 1, 200, 100)!.Value;
        Vector2[] reference = [new(20, 20), new(120, 20), new(120, 80), new(20, 80)];
        for (int i = 0; i < 4; i++)
        {
            Vector4 mapped = Vector4.Transform(new Vector4(reference[i], 0, 1), projection);
            Assert.InRange(mapped.X / mapped.W, KeystoneCorners[i * 2] * 200 - 0.01, KeystoneCorners[i * 2] * 200 + 0.01);
            Assert.InRange(mapped.Y / mapped.W, KeystoneCorners[i * 2 + 1] * 100 - 0.01, KeystoneCorners[i * 2 + 1] * 100 + 0.01);
        }
        InkObjectPose halfway = InkObjectMotion.Evaluate(tracking, 0.5);
        Assert.Equal(0.275, halfway.Corners![0], 8);
        tracking.Keys[1] = tracking.Keys[1] with { Corners = [0.45, 0.35, 0.9, 0.9, 0.9, 0.1, 0.45, 0.65] };
        Assert.Null(InkObjectMotion.Projection(tracking, 1, 200, 100));
    }

    [Fact]
    public void PlanarTracking_PersistsAndRejectsMissingOrUnexpectedCorners()
    {
        var project = new EditorProject();
        project.InkLayers[0].ObjectTracking = PlanarTracking(KeystoneCorners);
        EditorProject reopened = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        InkObjectTracking tracking = reopened.InkLayers[0].ObjectTracking!;
        Assert.True(tracking.Perspective);
        Assert.Equal(KeystoneCorners, tracking.Keys[1].Corners);
        InkObjectTracking clone = InkObjectMotion.Clone(tracking);
        Assert.True(clone.Perspective);
        tracking.Keys[1] = tracking.Keys[1] with { Corners = null };
        Assert.False(InkObjectMotion.IsValid(tracking));
        InkObjectTracking rigid = Tracking();
        rigid.Keys[0] = rigid.Keys[0] with { Corners = KeystoneCorners };
        Assert.False(InkObjectMotion.IsValid(rigid));
        string legacy = EditorProjectSerialiser.Serialise(new EditorProject { InkLayers = [new() { ObjectTracking = Tracking() }] })
            .Replace($"\"Version\": {EditorProject.CurrentVersion}", "\"Version\": 74");
        Assert.False(EditorProjectSerialiser.Deserialise(legacy).InkLayers[0].ObjectTracking!.Perspective);
    }

    [Fact]
    public void PlanarRendering_WarpsInkIntoTrackedQuadWithPerspectiveDivide()
    {
        using var device = new CanvasDevice();
        var project = new EditorProject();
        project.InkLayers[0].ObjectTracking = PlanarTracking(KeystoneCorners);
        // Ink at the reference box's top-right and centre.
        project.Frames[0].Strokes =
        [
            [new() { X = 0.58, Y = 0.24, BrushWidth = 6, BrushColour = 0xFFFF0000 }],
            [new() { X = 0.35, Y = 0.5, BrushWidth = 6, BrushColour = 0xFFFF0000 }],
        ];
        Matrix4x4 projection = InkObjectMotion.Projection(project.InkLayers[0].ObjectTracking, 1, 200, 100)!.Value;
        Vector2 Project(Vector2 point)
        {
            Vector4 mapped = Vector4.Transform(new Vector4(point, 0, 1), projection);
            return new(mapped.X / mapped.W, mapped.Y / mapped.W);
        }
        uint Sample(Vector2 point) => InkColourSampling.Sample(device, project, 0, 200, 100, point.X, point.Y, 1);
        Vector2 corner = Project(new(116, 24)), centre = Project(new(70, 50));
        Assert.Equal(0xFFFF0000, Sample(corner));
        Assert.Equal(0xFFFF0000, Sample(centre));
        // Without the homogeneous divide the centre would land at the affine average of the corners.
        Assert.True(Vector2.Distance(centre, new(135, 50)) > 6);
        Assert.Equal(0u, Sample(new(135, 50)));
        Assert.Equal(0u, Sample(new(70, 50)));
    }

    [Fact]
    public void PlanarRendering_PreservesModifierStrokesInsideWarpedLayer()
    {
        using var device = new CanvasDevice();
        var project = new EditorProject();
        double[] box = [0.05, 0.1, 0.95, 0.1, 0.95, 0.9, 0.05, 0.9];
        project.InkLayers[0].ObjectTracking = new()
        {
            SourceClipId = "video-1", StartSeconds = 0, EndSeconds = 2, Perspective = true,
            AnchorX = 0.5, AnchorY = 0.5, BoxWidth = 0.9, BoxHeight = 0.8,
            Keys = [new(0, 0.5, 0.5, Corners: box), new(1, 0.5, 0.5, Corners: box)],
        };
        project.Frames[0].Strokes =
        [
            InkStrokeEditing.CreateShape(InkShape.Rectangle, 0.1, 0.2, 0.5, 0.8, 1, 0xFFFF0000, filled: true),
            [
                new() { X = 0.45, Y = 0.5, BrushWidth = 12, Pressure = 1, BrushTexture = InkBrushTexture.Smudge },
                new() { X = 0.75, Y = 0.5, BrushWidth = 12, Pressure = 1, BrushTexture = InkBrushTexture.Smudge },
            ],
        ];
        uint smudged = InkColourSampling.Sample(device, project, 0, 100, 80, 54, 40, 0.5);
        Assert.True(smudged >> 24 > 0, "Smudged colour must survive the recorded planar warp.");
        Assert.True(((smudged >> 16) & 0xFF) > 0);
    }

    [Fact]
    public void Tracker_PinsCornersOfSurfaceTurningInPerspective()
    {
        if (!VideoExportTests.HasH264Encoder) return;
        string path = Path.Combine(Path.GetTempPath(), $"inkmotion-planar-{Guid.NewGuid():N}.mp4");
        const int width = 384, height = 216, count = 18, textureWidth = 120, textureHeight = 100;
        var random = new Random(29);
        byte[] texture = new byte[textureWidth * textureHeight];
        random.NextBytes(texture);
        Vector2[] textureCorners = [new(0, 0), new(textureWidth, 0), new(textureWidth, textureHeight), new(0, textureHeight)];
        Vector2[] Corners(int frame) =>
        [
            new(132, 58), new(252 - frame * 1.5f, 58 + frame * 1.2f),
            new(252 - frame * 1.5f, 158 - frame * 1.2f), new(132, 158),
        ];
        try
        {
            using (var writer = new MediaFoundationVideoWriter(path, width, height, 24,
                VideoExportCodec.H264, 4_000_000, 0, 0, false))
            {
                for (int frame = 0; frame < count; frame++)
                {
                    Assert.True(InkHomography.TryFit(Corners(frame).Zip(textureCorners).ToArray(), out InkHomography inverse));
                    var pixels = new byte[width * height * 4];
                    for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        byte value = 20;
                        if (inverse.TryApply(x, y, out double u, out double v) &&
                            u >= 0 && u < textureWidth && v >= 0 && v < textureHeight)
                            value = texture[(int)v * textureWidth + (int)u];
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
                new(192d / width, 108d / height, textureWidth / (double)width, textureHeight / (double)height),
                new(0, 192d / width, 108d / height), width / (double)height, null, CancellationToken.None,
                perspective: true);
            Assert.Null(result.LostSeconds);
            Assert.All(result.Keys, key => Assert.NotNull(key.Corners));
            double[] last = result.Keys[^1].Corners!;
            Vector2[] expected = Corners(count - 1);
            for (int i = 0; i < 4; i++)
            {
                Assert.InRange(last[i * 2] * width, expected[i].X - 4, expected[i].X + 4);
                Assert.InRange(last[i * 2 + 1] * height, expected[i].Y - 4, expected[i].Y + 4);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Tracking_PersistsIndependentlyAndDoesNotPropagateToNewOrDuplicatedLayers()
    {
        var project = new EditorProject();
        project.InkLayers[0].ObjectTracking = Tracking();
        project.InkLayers.Add(new() { Id = "ink-2", Name = "Other" });
        EditorProject reopened = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        Assert.Equal(EditorProject.CurrentVersion, reopened.Version);
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
    public void TextTracking_PersistsAndClonesWithoutSharingMotionKeys()
    {
        var tracking = Tracking();
        var overlay = new InkTextOverlay { Text = "ARM", ObjectTracking = tracking };
        var title = new TimelineClipProject { Text = "TITLE", ObjectTracking = Tracking() };
        var project = new EditorProject
        {
            Frames = [new() { TextOverlays = [overlay] }],
            VideoTracks = [new() { Kind = TimelineTrackKind.Text, Clips = [title] }],
        };

        EditorProject reopened = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        InkTextOverlay reopenedText = reopened.Frames[0].TextOverlays[0];
        TimelineClipProject reopenedTitle = reopened.VideoTracks[0].Clips[0];
        Assert.Equal(2, reopenedText.ObjectTracking!.Keys.Count);
        Assert.Equal(2, reopenedTitle.ObjectTracking!.Keys.Count);

        EditorFrame clonedFrame = InkEditing.Clone(project.Frames[0]);
        clonedFrame.TextOverlays[0].ObjectTracking!.Keys.Clear();
        Assert.Equal(2, overlay.ObjectTracking!.Keys.Count);
        TimelineClipProject clonedTitle = VideoTimelineEditing.Clone(title);
        clonedTitle.ObjectTracking!.Keys.Clear();
        Assert.Equal(2, title.ObjectTracking!.Keys.Count);
        Assert.Null(VideoTimelineEditing.CopyForPlacement(title, 10).ObjectTracking);
    }

    [Fact]
    public void TimelineTitleRendering_FollowsTrackedPositionAndHidesAfterTrackingStops()
    {
        using var device = new CanvasDevice(true);
        var title = new TimelineClipProject
        {
            Text = "ARM",
            X = 0.25,
            Y = 0.5,
            FontSize = 300,
            ObjectTracking = new()
            {
                SourceClipId = "video-1",
                StartSeconds = 0,
                EndSeconds = 2,
                ReferenceSeconds = 0,
                AnchorX = 0.25,
                AnchorY = 0.5,
                BoxWidth = 0.2,
                BoxHeight = 0.2,
                Keys = [new(0, 0.25, 0.5), new(1, 0.75, 0.5)],
            },
        };
        (int Left, int Right) BoundsAt(double seconds)
        {
            using var target = new CanvasRenderTarget(device, 400, 200, 96);
            using (CanvasDrawingSession session = target.CreateDrawingSession())
            {
                session.Clear(Microsoft.UI.Colors.Transparent);
                TimelineCompositor.DrawTitle(session, title, 400, 200, 1,
                    VideoTransformKeyframes.GetValueAtTime(title, seconds, 24), seconds, []);
            }
            byte[] pixels = target.GetPixelBytes();
            int left = 400, right = -1;
            for (int y = 0; y < 200; y++)
            for (int x = 0; x < 400; x++)
            {
                if (pixels[(y * 400 + x) * 4 + 3] == 0) continue;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
            }
            return (left, right);
        }

        (int firstLeft, int firstRight) = BoundsAt(0);
        (int laterLeft, int laterRight) = BoundsAt(1);
        Assert.InRange(firstLeft, 20, 130);
        Assert.InRange(laterLeft - firstLeft, 150, 260);
        Assert.True(laterRight > laterLeft);
        Assert.Equal((400, -1), BoundsAt(2));
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
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Tracker_FollowsMovingRotatingScalingTextureAndResumesAfterCorrection(bool disappear, bool fastMovement)
    {
        if (!VideoExportTests.HasH264Encoder) return;
        string path = Path.Combine(Path.GetTempPath(), $"inkmotion-object-{Guid.NewGuid():N}.mp4");
        const int width = 384, height = 216, count = 18;
        double initialCentre = fastMovement ? 80 : 150;
        double movementPerFrame = fastMovement ? 15 : 1;
        double expectedFinalCentre = initialCentre + (count - 1) * movementPerFrame;
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
                        Matrix3x2.CreateTranslation((float)(initialCentre + frame * movementPerFrame), 108);
                    Matrix3x2.Invert(transform, out Matrix3x2 inverse);
                    for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        Vector2 local = Vector2.Transform(new(x, y), inverse) + new Vector2(48, 40);
                        byte value = 20;
                        if ((!disappear || frame < 10 || frame >= 13) && local.X >= 0 && local.X < 96 && local.Y >= 0 && local.Y < 80)
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
                new(initialCentre / width, 0.5, 0.24, 0.35), new(0, initialCentre / width, 0.5),
                width / (double)height, null, CancellationToken.None);
            if (disappear)
            {
                Assert.NotNull(result.LostSeconds);
                Assert.InRange(result.LostSeconds!.Value, 9 / 24d, 12 / 24d);
                Assert.True(result.Keys.Count > 4);
                var tracking = new InkObjectTracking
                {
                    SourceClipId = clip.Id, StartSeconds = 0, EndSeconds = count / 24d,
                    CanvasAspectRatio = width / (double)height, AnchorX = initialCentre / width, AnchorY = 0.5,
                    BoxWidth = 0.24, BoxHeight = 0.35, LostSeconds = result.LostSeconds,
                    Keys = result.Keys.ToList(),
                };
                const int correctionFrame = 14;
                double correctedScale = 1 + correctionFrame * 0.004;
                double correctionSeconds = correctionFrame / 24d;
                InkTrackingResult resumed = InkObjectTracker.Analyse(project, clip.Id, correctionSeconds, count / 24d,
                    new(0.5 + ((150d + correctionFrame) / width - 0.5) / 2, 0.5,
                        0.12 * correctedScale, 0.35 * correctedScale),
                    new(correctionSeconds, 0, 0, Rotation: correctionFrame * 0.008 * 180 / Math.PI),
                    width * 2d / height, null, CancellationToken.None, tracking);
                Assert.Null(resumed.LostSeconds);
                Assert.InRange(resumed.Keys[^1].X * width, 162, 172);
                Assert.False(InkObjectMotion.IsVisible(tracking, 11 / 24d));
                tracking.Keys.RemoveAll(key => key.Seconds >= correctionSeconds);
                tracking.Keys.AddRange(resumed.Keys);
                tracking.LostSeconds = resumed.LostSeconds;
                Assert.True(InkObjectMotion.IsVisible(tracking, 11 / 24d));
                InkObjectPose gapPose = InkObjectMotion.Evaluate(tracking, 11 / 24d);
                InkObjectPose beforeGap = tracking.Keys.Last(key => key.Seconds < 11 / 24d);
                InkObjectPose afterGap = tracking.Keys.First(key => key.Seconds > 11 / 24d);
                double gapAmount = (11 / 24d - beforeGap.Seconds) / (afterGap.Seconds - beforeGap.Seconds);
                Assert.Equal(beforeGap.X + (afterGap.X - beforeGap.X) * gapAmount, gapPose.X, 8);
            }
            else
            {
                Assert.Null(result.LostSeconds);
                InkObjectPose last = result.Keys[^1];
                Assert.InRange(last.X * width, expectedFinalCentre - 5, expectedFinalCentre + 5);
                Assert.InRange(last.Scale, 1.03, 1.13);
                if (fastMovement)
                    Assert.InRange(last.Rotation, 0, 12);
                else
                    Assert.InRange(last.Rotation, 4, 12);
                var tracking = new InkObjectTracking
                {
                    SourceClipId = clip.Id, StartSeconds = 0, EndSeconds = count / 24d,
                    CanvasAspectRatio = width / (double)height, AnchorX = initialCentre / width, AnchorY = 0.5,
                    BoxWidth = 0.24, BoxHeight = 0.35, Keys = result.Keys.ToList(),
                };
                const int restart = 8;
                double correctedScale = 1 + restart * 0.004;
                double correctedCentre = initialCentre + restart * movementPerFrame;
                InkTrackingResult resumed = InkObjectTracker.Analyse(project, clip.Id, restart / 24d, count / 24d,
                    new(0.5 + (correctedCentre / width - 0.5) / 2, 0.5,
                        0.12 * correctedScale, 0.35 * correctedScale),
                    new(restart / 24d, 0, 0, Rotation: restart * 0.008 * 180 / Math.PI),
                    width * 2d / height, null, CancellationToken.None, tracking);
                Assert.Null(resumed.LostSeconds);
                Assert.InRange(resumed.Keys[^1].X * width, expectedFinalCentre - 5, expectedFinalCentre + 5);
                Assert.InRange(resumed.Keys[^1].Scale, 1.03, 1.13);
                project.InkLayers[0].IsVisible = true;
                project.InkLayers[0].ObjectTracking = tracking;
                project.Frames[0].Strokes = [[new() { X = initialCentre / width, Y = 0.5, BrushWidth = 8, BrushColour = 0xFFFF0000 }]];
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
