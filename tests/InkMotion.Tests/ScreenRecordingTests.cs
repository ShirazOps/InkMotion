using System.Diagnostics;
using Xunit;

namespace InkMotion.Tests;

public sealed class ScreenRecordingTests
{
    [Fact]
    public void SelectionResize_TransformsFreeformPointsAndKeepsCroppedBounds()
    {
        ScreenRegion original = ScreenRegion.Freeform([new(-100, 20), new(100, 20), new(0, 180)]);
        ScreenRegion resized = original.ResizeTo(new(-300, -200, 400, 80));
        Assert.Equal(new ScreenPoint[] { new(-300, -200), new(100, -200), new(-100, -120) }, resized.Polygon);
        Assert.Equal(new ScreenPoint(-100, 20), original.Polygon![0]);
        ScreenCapturePlan plan = ScreenCapturePlan.Create([resized], [new(-400, -300, 800, 600)], 1920, 30);
        Assert.Equal(new(-300, -200, 400, 80), plan.Bounds);
        Assert.Equal(400, plan.Width);
        Assert.Equal(80, plan.Height);
        Assert.Null(new ScreenRegion(0, 0, 100, 100).ResizeTo(new(10, 20, 200, 300)).Polygon);
        Assert.Throws<ArgumentException>(() => original.ResizeTo(new(0, 0, 1, 100)));
    }

    [Fact]
    public void Freeform_NativeCaptureMaskExcludesOutsidePixelsWithoutCapturingDesktop()
    {
        var display = DesktopRecordingFrames.Displays()[0].Bounds;
        int x = display.X + 20, y = display.Y + 20;
        ScreenRegion shape = ScreenRegion.Freeform([new(x, y), new(x + 100, y), new(x, y + 100)]);
        ScreenCapturePlan plan = ScreenCapturePlan.Create([shape], [display], 1920, 30);
        using var frames = new DesktopRecordingFrames(plan, false);
        nint mask = (nint)typeof(DesktopRecordingFrames).GetField("_clipRegion",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(frames)!;
        Assert.True(PtInRegion(mask, 10, 10));
        Assert.False(PtInRegion(mask, 90, 90));
        Assert.False(PtInRegion(mask, -1, 10));
        Assert.False(PtInRegion(mask, 100, 10));
        Assert.Equal(100, plan.Width);
        Assert.Equal(100, plan.Height);
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool PtInRegion(nint region, int x, int y);

    [Fact]
    public void Freeform_CropsToShapeBoundsAndPreservesDesktopPointsAcrossDisplaysAndPersistence()
    {
        ScreenPoint[] points = [new(-100, 20), new(100, 20), new(0, 180)];
        ScreenRegion shape = ScreenRegion.Freeform(points);
        Assert.Equal(-100, shape.X);
        Assert.Equal(20, shape.Y);
        Assert.Equal(200, shape.Width);
        Assert.Equal(160, shape.Height);
        ScreenRegion[] displays = [new(-200, 0, 200, 200), new(0, 0, 200, 200)];
        ScreenCapturePlan plan = ScreenCapturePlan.Create([shape], displays, 1920, 30);
        Assert.Equal(new(-100, 20, 200, 160), plan.Bounds);
        Assert.Equal(200, plan.Width);
        Assert.Equal(160, plan.Height);
        Assert.Equal(2, plan.Regions.Count);
        Assert.All(plan.Regions, piece => Assert.Equal(points, piece.Polygon));
        string json = System.Text.Json.JsonSerializer.Serialize(plan);
        var restored = System.Text.Json.JsonSerializer.Deserialize<ScreenCapturePlan>(json)!;
        Assert.Equal(points, restored.Regions[0].Polygon);
        Assert.Throws<ArgumentException>(() => ScreenRegion.Freeform([new(0, 0), new(1, 1)]));
        Assert.Throws<ArgumentException>(() => ScreenCapturePlan.Create(
            [new(0, 0, 10, 10) { Polygon = points }], displays, 1920, 30));
    }

    [Fact]
    public void Plan_PreservesNegativeMonitorPositionsAndClipsRegionsToConnectedDisplays()
    {
        ScreenRegion[] displays = [new(-1920, 200, 1920, 1080), new(0, 0, 2560, 1440)];
        ScreenCapturePlan plan = ScreenCapturePlan.Create(displays, displays, 3840, 30);
        Assert.Equal(new(-1920, 0, 4480, 1440), plan.Bounds);
        Assert.Equal(3840, plan.Width);
        Assert.Equal(1234, plan.Height);
        Assert.Equal(displays, plan.Regions);
        ScreenCapturePlan clipped = ScreenCapturePlan.Create([new(-100, 0, 200, 400)], displays, 1920, 24);
        Assert.Equal(new(-100, 200, 100, 200), clipped.Regions[0]);
        Assert.Equal(new(0, 0, 100, 400), clipped.Regions[1]);
        Assert.Throws<ArgumentException>(() => ScreenCapturePlan.Create([new(6000, 0, 100, 100)], displays, 1920, 30));
        Assert.Throws<ArgumentException>(() => ScreenCapturePlan.Create([], displays, 1920, 30));
        Assert.Throws<ArgumentException>(() => ScreenCapturePlan.Create([new(0, 0, -5, 10)], displays, 1920, 30));
        Assert.Throws<ArgumentException>(() => ScreenCapturePlan.Create([new(0, 0, 20000, 20000)],
            [new(0, 0, 20000, 20000)], 3840, 30));
    }

    [Fact]
    public void Recovery_KeepsFinalisedSourcesSeparateAndDoesNotImportUnfinishedFiles()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"inkmotion-capture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var manifest = new ScreenRecordingManifest
            {
                Plan = ScreenCapturePlan.Create([new(0, 0, 1920, 1080)], [new(0, 0, 1920, 1080)], 1920, 15),
                Status = "Interrupted",
                Media =
                [
                    new() { FileName = "Screen-00000.mp4", DurationSeconds = 5, Finalised = true },
                    new() { FileName = "Screen-00001.mp4", OffsetSeconds = 5, DurationSeconds = 2, Finalised = true },
                    new() { FileName = "Screen-00002.mp4", OffsetSeconds = 7 },
                    new() { FileName = "Webcam.mp4", Kind = "Webcam", OffsetSeconds = 0.2, DurationSeconds = 6.8, Finalised = true },
                    new() { FileName = "System.wav", Kind = "System", DurationSeconds = 7, Finalised = true },
                    new() { FileName = "Microphone.wav", Kind = "Microphone", DurationSeconds = 7, Finalised = true },
                ],
            };
            foreach (ScreenRecordingMedia media in manifest.Media) File.WriteAllBytes(Path.Combine(directory, media.FileName), [1, 2, 3]);
            ScreenRecordingStore.Save(directory, manifest);
            ScreenRecordingManifest restored = ScreenRecordingStore.Read(directory);
            EditorProject project = ScreenRecordingStore.BuildProject(directory, restored);
            Assert.Equal(30, project.FrameRate);
            Assert.Equal(2, project.VideoTracks.Count);
            Assert.Equal(2, project.VideoTracks[0].Clips.Count);
            Assert.Equal(151, project.VideoTracks[0].Clips[1].StartFrame);
            Assert.Equal(7, project.VideoTracks[1].Clips[0].StartFrame);
            Assert.Equal(0.25, project.VideoTracks[1].Clips[0].Scale);
            Assert.Equal(2, project.AudioTracks.Count);
            Assert.All(project.AudioTracks, track => Assert.Equal(1, track.StartFrame));
            string path = ScreenRecordingStore.SaveProject(directory, restored);
            Assert.Equal(2, EditorProjectSerialiser.Deserialise(File.ReadAllText(path)).VideoTracks.Count);
            Assert.True(File.Exists(Path.Combine(directory, "Screen-00002.mp4")));
            restored.Media[0].FileName = "..\\private.mp4";
            ScreenRecordingStore.Save(directory, restored);
            Assert.Throws<InvalidDataException>(() => ScreenRecordingStore.Read(directory));
            Assert.Throws<InvalidOperationException>(() => ScreenRecordingStore.Delete(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Recovery_ImportsCheckpointedWavPrefixWithoutDeletingIncompleteSources()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"inkmotion-wav-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "Microphone.wav");
            using (var writer = new NAudio.Wave.WaveFileWriter(path, new NAudio.Wave.WaveFormat(48000, 16, 1)))
            {
                writer.Write(new byte[96000], 0, 96000);
                writer.Flush();
            }
            var manifest = new ScreenRecordingManifest
            {
                Media = [new() { FileName = "Microphone.wav", Kind = "Microphone", OffsetSeconds = 0.1 },
                    new() { FileName = "Missing.wav", Kind = "System" }],
            };
            await ScreenRecordingStore.RecoverAsync(directory, manifest);
            Assert.True(manifest.Media[0].Finalised);
            Assert.Equal(1, manifest.Media[0].DurationSeconds);
            Assert.False(manifest.Media[1].Finalised);
            Assert.Equal(2, manifest.Warnings.Count);
            Assert.True(File.Exists(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task OptionalInputs_OffMeansNoDevicesOrMediaAndStopIsIdempotent()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"inkmotion-inputs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using var inputs = new ScreenRecordingInputs(directory, Stopwatch.StartNew());
            inputs.ThrowIfFailed();
            Assert.Empty(inputs.GetSnapshot().AudioTracks);
            Assert.Null(inputs.GetSnapshot().CameraTrack);
            ScreenRecordingInputsResult first = await inputs.StopAsync();
            ScreenRecordingInputsResult second = await inputs.StopAsync();
            Assert.Empty(first.AudioTracks);
            Assert.Null(second.CameraTrack);
            Assert.Empty(Directory.EnumerateFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Encoder_SavesRecoverableSegmentsWithWallClockTimingWithoutCapturingDesktop()
    {
        if (!VideoExportTests.HasH264Encoder) return;
        string directory = Path.Combine(Path.GetTempPath(), $"inkmotion-encoder-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var manifest = new ScreenRecordingManifest
            {
                Plan = ScreenCapturePlan.Create([new(0, 0, 64, 64)], [new(0, 0, 64, 64)], 1920, 15),
            };
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1.4));
            var clock = Stopwatch.StartNew();
            await Task.Run(() => ScreenRecordingEncoder.Record(directory, manifest, clock,
                () => new SyntheticFrames(), () => ScreenRecordingStore.Save(directory, manifest), stop.Token));
            ScreenRecordingManifest restored = ScreenRecordingStore.Read(directory);
            ScreenRecordingMedia media = Assert.Single(restored.Media, media => media.Finalised);
            Assert.InRange(media.DurationSeconds + media.OffsetSeconds, 1, 1.7);
            using var reader = new MediaFoundationVideoReader(Path.Combine(directory, media.FileName));
            Assert.True(reader.TryGetFrame(0));
            Assert.Equal(64, reader.Width);
            Assert.True(reader.Pixels[2] > 180);
            Assert.True(restored.DroppedFrames > 0);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class SyntheticFrames : IScreenRecordingFrames
    {
        public byte[] Capture()
        {
            Thread.Sleep(100);
            var pixels = new byte[64 * 64 * 4];
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i + 2] = 255; pixels[i + 3] = 255; }
            return pixels;
        }
        public void Dispose() { }
    }

    [Fact]
    public async Task Encoder_FailureLeavesPriorSegmentPlayableAndRecoverable()
    {
        if (!VideoExportTests.HasH264Encoder) return;
        string directory = Path.Combine(Path.GetTempPath(), $"inkmotion-interrupted-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var manifest = new ScreenRecordingManifest
            {
                Plan = ScreenCapturePlan.Create([new(0, 0, 64, 64)], [new(0, 0, 64, 64)], 1920, 15),
            };
            var clock = Stopwatch.StartNew();
            await Assert.ThrowsAsync<IOException>(() => Task.Run(() => ScreenRecordingEncoder.Record(directory, manifest, clock,
                () => new FailingFrames(), () => ScreenRecordingStore.Save(directory, manifest), CancellationToken.None)));
            ScreenRecordingManifest restored = ScreenRecordingStore.Read(directory);
            ScreenRecordingMedia first = Assert.Single(restored.Media, media => media.Finalised);
            Assert.Equal(5, first.DurationSeconds);
            Assert.Equal(2, restored.Media.Count);
            Assert.True(File.Exists(Path.Combine(directory, restored.Media[1].FileName)));
            using var reader = new MediaFoundationVideoReader(Path.Combine(directory, first.FileName));
            Assert.True(reader.TryGetFrame(4));
            string projectPath = ScreenRecordingStore.SaveProject(directory, restored);
            Assert.Single(EditorProjectSerialiser.Deserialise(File.ReadAllText(projectPath)).VideoTracks[0].Clips);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class FailingFrames : IScreenRecordingFrames
    {
        private int _count;
        public byte[] Capture()
        {
            if (++_count > 78) throw new IOException("Simulated capture interruption.");
            return new byte[64 * 64 * 4];
        }
        public void Dispose() { }
    }
}
