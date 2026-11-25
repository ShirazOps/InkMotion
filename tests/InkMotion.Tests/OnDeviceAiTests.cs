using System.Numerics;
using System.Runtime.InteropServices;
using Xunit;

namespace InkMotion.Tests;

public sealed class OnDeviceAiTests
{
    [Fact]
    public void WindowsMl_IsOfferedOnlyOn64BitArchitectures()
    {
        Assert.Null(OnDeviceModels.UnsupportedReason(Architecture.X64));
        Assert.Null(OnDeviceModels.UnsupportedReason(Architecture.Arm64));
        Assert.Contains("x86", OnDeviceModels.UnsupportedReason(Architecture.X86));
    }

    [Fact]
    public async Task Import_RejectsNonOnnxFilesAndKeepsValidOnes()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"inkmotion-models-{Guid.NewGuid():N}");
        string source = Path.Combine(directory, "source.onnx");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new OnDeviceModelStore(Path.Combine(directory, "store"));
            await File.WriteAllBytesAsync(source, [0x50, 0x4B, 0x03, 0x04]);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.ImportAsync(OnDeviceModels.PoseEstimator, source, CancellationToken.None));
            Assert.False(store.IsInstalled(OnDeviceModels.PoseEstimator));

            await File.WriteAllBytesAsync(source, [0x08, 0x07, 0x12, 0x00]);
            string hash = await store.ImportAsync(OnDeviceModels.PoseEstimator, source, CancellationToken.None);
            Assert.Equal(64, hash.Length);
            Assert.True(store.IsInstalled(OnDeviceModels.PoseEstimator));
            store.Remove(OnDeviceModels.PoseEstimator);
            Assert.False(store.IsInstalled(OnDeviceModels.PoseEstimator));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Download_RequiresMatchingConsentAndAPublishedModel()
    {
        var store = new OnDeviceModelStore(Path.Combine(Path.GetTempPath(), $"inkmotion-models-{Guid.NewGuid():N}"));
        using var client = new HttpClient(new RefusingHandler());
        var wrongConsent = OnDeviceModelDownloadConsent.GrantedByUser(OnDeviceModels.SegmentationEncoder);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DownloadAsync(OnDeviceModels.PoseEstimator, wrongConsent, client, null, CancellationToken.None));
        var consent = OnDeviceModelDownloadConsent.GrantedByUser(OnDeviceModels.PoseEstimator);
        Assert.False(OnDeviceModels.PoseEstimator.IsPublished);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DownloadAsync(OnDeviceModels.PoseEstimator, consent, client, null, CancellationToken.None));
        Assert.False(Directory.Exists(store.Directory));
    }

    [Fact]
    public void SimccDecode_MapsPeaksBackToFramePixels()
    {
        int binsX = (int)(RtmPoseModel.InputWidth * RtmPoseModel.SplitRatio);
        int binsY = (int)(RtmPoseModel.InputHeight * RtmPoseModel.SplitRatio);
        var x = new float[RtmPoseModel.KeypointCount * binsX];
        var y = new float[RtmPoseModel.KeypointCount * binsY];
        // Keypoint 0 peaks at the crop centre; keypoint 1 at its top-left corner with a weaker y peak.
        x[binsX / 2] = 0.9f;
        y[binsY / 2] = 0.8f;
        x[binsX] = 0.7f;
        y[binsY] = 0.2f;
        PoseKeypoint[] points = RtmPoseModel.Decode(x, y, new(100, 200), new(96, 128));
        Assert.Equal(new Vector2(100, 200), points[0].Point);
        Assert.Equal(0.8f, points[0].Confidence);
        Assert.Equal(new Vector2(52, 136), points[1].Point);
        Assert.Equal(0.2f, points[1].Confidence);
        Assert.Throws<InvalidDataException>(() => RtmPoseModel.Decode(new float[3], y, Vector2.Zero, Vector2.One));
    }

    [Fact]
    public void PoseCrop_IsEnlargedToTheModelAspect()
    {
        (Vector2 centre, Vector2 size) = RtmPoseModel.CropFor(new(10, 20), new(100, 100));
        Assert.Equal(new Vector2(10, 20), centre);
        Assert.Equal(125, size.X, 3);
        Assert.Equal(125 / 0.75f, size.Y, 3);
        Assert.Equal(3 * RtmPoseModel.InputWidth * RtmPoseModel.InputHeight,
            RtmPoseModel.Preprocess(new byte[4 * 4 * 4], 4, 4, new(2, 2), size).Length);
    }

    [Fact]
    public void NearestBone_MatchesTheDrawnLineInItsDirection()
    {
        var keypoints = Enumerable.Range(0, RtmPoseModel.KeypointCount)
            .Select(_ => new PoseKeypoint(new(-1000, -1000), 0f)).ToArray();
        keypoints[5] = new(new(100, 100), 0.9f);
        keypoints[7] = new(new(100, 200), 0.9f);
        Assert.Equal((7, 5), RtmPoseModel.NearestBone(keypoints, new(102, 198), new(98, 104)));
        Assert.Equal((5, 7), RtmPoseModel.NearestBone(keypoints, new(98, 104), new(102, 198)));
        Assert.Null(RtmPoseModel.NearestBone(keypoints, new(400, 400), new(400, 500)));
    }

    [Fact]
    public void LimbPose_TurnsAndStretchesTheAnchorWithTheSegment()
    {
        InkObjectPose pose = InkObjectTracker.LimbPose(1, new(0, 0), new(10, 0), new(0, 0), new(0, 20),
            new(5, 0), 0, 100, 100);
        Assert.Equal(90, pose.Rotation, 3);
        Assert.Equal(2, pose.Scale, 3);
        Assert.Equal(0, pose.X, 3);
        Assert.Equal(0.1, pose.Y, 3);
        // Unwraps against the previous rotation instead of jumping by 360 degrees.
        InkObjectPose unwrapped = InkObjectTracker.LimbPose(1, new(0, 0), new(10, 0), new(0, 0), new(10, -1),
            new(5, 0), 350, 100, 100);
        Assert.InRange(unwrapped.Rotation, 350, 360);
    }

    [Fact]
    public void LimbBox_PadsAndClampsTheLine()
    {
        InkTrackingBox box = InkObjectTracker.LimbBox(0.5, 0.5, 0.5, 0.5);
        Assert.Equal(0.06, box.Width, 6);
        Assert.Equal(0.06, box.Height, 6);
        InkTrackingBox edge = InkObjectTracker.LimbBox(0, 0.2, 0.5, 0.2);
        Assert.Equal(0, edge.X - edge.Width / 2, 6);
        Assert.True(edge.Width > 0.5);
    }

    [Fact]
    public void MaskContours_TraceAndSimplifyARectangle()
    {
        const int width = 20, height = 12;
        var mask = new bool[width * height];
        for (int y = 2; y < 8; y++)
        for (int x = 3; x < 13; x++)
            mask[y * width + x] = true;
        Assert.Equal((3, 2, 12, 7, 60), MaskContours.Bounds(mask, width, height));
        List<Vector2[]> outlines = MaskContours.Trace(mask, width, height);
        Vector2[] outline = Assert.Single(outlines);
        Assert.Equal(4, outline.Length);
        Assert.Contains(new Vector2(3, 2), outline);
        Assert.Contains(new Vector2(13, 8), outline);
        Assert.Empty(MaskContours.Trace(mask, width, height, minimumArea: 100));
        Assert.Null(MaskContours.Bounds(new bool[width * height], width, height));
    }

    [Fact]
    public void ArticulatedTracking_ValidatesItsBoneAndJoints()
    {
        InkObjectTracking tracking = Tracking();
        Assert.True(InkObjectMotion.IsValid(tracking));
        tracking.Articulation = InkTrackingArticulation.TwoPoint;
        Assert.False(InkObjectMotion.IsValid(tracking));
        tracking.Bone = [0.2, 0.3, 0.4, 0.5];
        Assert.True(InkObjectMotion.IsValid(tracking));
        tracking.Perspective = true;
        Assert.False(InkObjectMotion.IsValid(tracking));
        tracking.Perspective = false;
        tracking.Articulation = InkTrackingArticulation.Pose;
        Assert.False(InkObjectMotion.IsValid(tracking));
        tracking.PoseJoints = [5, 7];
        Assert.True(InkObjectMotion.IsValid(tracking));
        tracking.PoseJoints = [5, 5];
        Assert.False(InkObjectMotion.IsValid(tracking));
    }

    [Fact]
    public void AutoMatte_HoldsKeysUntilTheObjectIsLostAndSurvivesSaving()
    {
        var clip = new TimelineClipProject { Id = "video-1", SourcePath = "source.mp4", SourceDurationSeconds = 4 };
        var project = new EditorProject { VideoTracks = [new() { Kind = TimelineTrackKind.Video, Clips = [clip] }] };
        InkAutoMatte matte = Matte();
        Assert.True(InkAutoMattes.IsValid(matte));
        Assert.Null(InkAutoMattes.Evaluate(matte, project.VideoTracks, 0.5));
        Assert.Equal(1, InkAutoMattes.Evaluate(matte, project.VideoTracks, 1.2)!.Seconds);
        Assert.Equal(1.5, InkAutoMattes.Evaluate(matte, project.VideoTracks, 1.9)!.Seconds);
        Assert.Null(InkAutoMattes.Evaluate(matte, project.VideoTracks, 2));

        project.InkLayers[0].IsHoldout = true;
        project.InkLayers[0].AutoMatte = matte;
        EditorProject reopened = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        InkAutoMatte restored = reopened.InkLayers[0].AutoMatte!;
        Assert.Equal(2, restored.Keys.Count);
        Assert.Equal(matte.Keys[1].Polygons[0], restored.Keys[1].Polygons[0]);
        Assert.True(InkAutoMattes.IsActive(reopened.InkLayers[0], reopened.VideoTracks, 1.2));

        matte.Keys[0].Polygons.Add([0, 0, 1, 1]);
        Assert.False(InkAutoMattes.IsValid(matte));
    }

    private static InkAutoMatte Matte() => new()
    {
        SourceClipId = "video-1", CanvasAspectRatio = 16 / 9.0, StartSeconds = 1, EndSeconds = 3, LostSeconds = 2,
        Keys =
        [
            new(1, 0.5, 0.5, 16 / 9.0, [[0.1, 0.1, 0.3, 0.1, 0.2, 0.4]]),
            new(1.5, 0.5, 0.5, 16 / 9.0, [[0.2, 0.1, 0.4, 0.1, 0.3, 0.4]]),
        ],
    };

    private static InkObjectTracking Tracking() => new()
    {
        SourceClipId = "video-1", StartSeconds = 0, EndSeconds = 2, ReferenceSeconds = 0,
        AnchorX = 0.3, AnchorY = 0.4, BoxWidth = 0.2, BoxHeight = 0.2, Keys = [new(0, 0.3, 0.4)],
    };

    private sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No network access is expected in tests.");
    }
}
