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
        var display = new ScreenRegion(-200, 0, 400, 200);
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
            await using var inputs = new ScreenRecordingInputs(directory, ScreenRecordingClock.StartNew());
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
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var clock = ScreenRecordingClock.StartNew();
            int captured = 0;
            await Task.Run(() => ScreenRecordingEncoder.Record(directory, manifest, clock,
                () => new SyntheticFrames(() => { if (++captured == 8) stop.Cancel(); }),
                () => ScreenRecordingStore.Save(directory, manifest), stop.Token));
            clock.Stop();
            Assert.Equal(8, captured);
            ScreenRecordingManifest restored = ScreenRecordingStore.Read(directory);
            ScreenRecordingMedia media = Assert.Single(restored.Media, media => media.Finalised);
            Assert.InRange(media.DurationSeconds + media.OffsetSeconds, 0.8, clock.Elapsed.TotalSeconds + 0.2);
            Assert.InRange(clock.Elapsed.TotalSeconds - media.DurationSeconds - media.OffsetSeconds, -0.2, 1);
            using var reader = new MediaFoundationVideoReader(Path.Combine(directory, media.FileName));
            Assert.True(reader.TryGetFrame(0));
            Assert.Equal(64, reader.Width);
            Assert.True(reader.Pixels[2] > 180);
            Assert.True(restored.DroppedFrames > 0);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class SyntheticFrames(Action? captured = null) : IScreenRecordingFrames
    {
        public byte[] Capture()
        {
            Thread.Sleep(100);
            captured?.Invoke();
            var pixels = new byte[64 * 64 * 4];
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i + 2] = 255; pixels[i + 3] = 255; }
            return pixels;
        }
        public void Dispose() { }
    }

    [Fact]
    public void Clock_ExcludesPausedTimeAndCountsRepeatedPauses()
    {
        var clock = ScreenRecordingClock.StartNew();
        Thread.Sleep(50);
        Assert.True(clock.Pause());
        Assert.False(clock.Pause());
        TimeSpan paused = clock.Elapsed;
        Thread.Sleep(150);
        Assert.Equal(paused, clock.Elapsed);
        Assert.False(clock.IsRunning);
        Assert.True(clock.Resume());
        Assert.False(clock.Resume());
        Assert.True(clock.Pause());
        Assert.True(clock.Resume());
        Assert.Equal(2, clock.PauseCount);
        Assert.True(clock.IsRunning);
        clock.Stop();
        Assert.False(clock.Resume());
        Assert.Throws<InvalidOperationException>(clock.Start);
    }

    [Fact]
    public async Task Clock_StopOrCancellationReleasesPausedWaiters()
    {
        var clock = ScreenRecordingClock.StartNew();
        clock.Pause();
        Task<bool> waiting = Task.Run(() => clock.WaitWhilePaused(CancellationToken.None));
        await Task.Delay(100);
        Assert.False(waiting.IsCompleted);
        clock.Stop();
        Assert.False(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        var second = ScreenRecordingClock.StartNew();
        second.Pause();
        using var cancel = new CancellationTokenSource(100);
        Assert.False(await Task.Run(() => second.WaitWhilePaused(cancel.Token)).WaitAsync(TimeSpan.FromSeconds(5)));
        second.Resume();
        Assert.True(second.WaitWhilePaused(CancellationToken.None));
    }

    [Fact]
    public async Task OptionalInputs_PauseAndResumeWithoutDevicesAndStopWhilePaused()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"inkmotion-inputs-pause-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var clock = ScreenRecordingClock.StartNew();
            await using var inputs = new ScreenRecordingInputs(directory, clock);
            clock.Pause();
            await inputs.PauseAsync();
            await inputs.ResumeAsync();
            await inputs.PauseAsync();
            ScreenRecordingInputsResult result = await inputs.StopAsync();
            Assert.Empty(result.AudioTracks);
            await Assert.ThrowsAsync<InvalidOperationException>(inputs.PauseAsync);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task LoopbackAudio_PauseResumeKeepsTrackAlignedToRecordedTime()
    {
        // Opt-in: captures the default output device's loopback (no microphone) for about two seconds.
        if (Environment.GetEnvironmentVariable("INKMOTION_TEST_LOOPBACK_AUDIO") != "1") return;
        ScreenRecordingAudioDevice device = ScreenRecordingInputs.ListAudioDevices()
            .First(d => d.Flow == ScreenRecordingAudioFlow.RenderLoopback);
        string directory = Path.Combine(Path.GetTempPath(), $"inkmotion-loopback-pause-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var clock = ScreenRecordingClock.StartNew();
            var wall = Stopwatch.StartNew();
            await using var inputs = new ScreenRecordingInputs(directory, clock);
            await inputs.StartAudioAsync(device, CancellationToken.None);
            await Task.Delay(1000);
            clock.Pause();
            await inputs.PauseAsync();
            await Task.Delay(1500);
            await inputs.ResumeAsync();
            clock.Resume();
            await Task.Delay(1000);
            inputs.ThrowIfFailed();
            clock.Stop();
            ScreenRecordingAudioTrackResult track = Assert.Single((await inputs.StopAsync()).AudioTracks);
            using var reader = new NAudio.Wave.WaveFileReader(track.Path);
            Assert.InRange(reader.TotalTime.TotalSeconds, clock.Elapsed.TotalSeconds - 0.15, clock.Elapsed.TotalSeconds + 0.15);
            Assert.True(wall.Elapsed.TotalSeconds - reader.TotalTime.TotalSeconds >= 1.3);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Encoder_RepeatedPausesCaptureNothingAndExcludePausedTime()
    {
        if (!VideoExportTests.HasH264Encoder) return;
        string directory = Path.Combine(Path.GetTempPath(), $"inkmotion-encoder-pause-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var manifest = new ScreenRecordingManifest
            {
                Plan = ScreenCapturePlan.Create([new(0, 0, 64, 64)], [new(0, 0, 64, 64)], 1920, 15),
            };
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var clock = ScreenRecordingClock.StartNew();
            var wall = Stopwatch.StartNew();
            int captured = 0, pausedCaptures = 0;
            var resumes = new List<Task>();
            await Task.Run(() => ScreenRecordingEncoder.Record(directory, manifest, clock,
                () => new SyntheticFrames(() =>
                {
                    if (clock.IsPaused) pausedCaptures++;
                    captured++;
                    if (captured is 4 or 8)
                    {
                        clock.Pause();
                        resumes.Add(Task.Delay(700).ContinueWith(_ => clock.Resume()));
                    }
                    if (captured == 12) stop.Cancel();
                }),
                () => ScreenRecordingStore.Save(directory, manifest), stop.Token));
            await Task.WhenAll(resumes);
            clock.Stop();
            Assert.Equal(0, pausedCaptures);
            Assert.Equal(2, clock.PauseCount);
            ScreenRecordingMedia media = Assert.Single(ScreenRecordingStore.Read(directory).Media, media => media.Finalised);
            Assert.InRange(media.OffsetSeconds + media.DurationSeconds, 0.8, clock.Elapsed.TotalSeconds + 0.2);
            Assert.True(wall.Elapsed.TotalSeconds - clock.Elapsed.TotalSeconds >= 1.3);
            using var reader = new MediaFoundationVideoReader(Path.Combine(directory, media.FileName));
            Assert.True(reader.TryGetFrame(0));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Encoder_StopWhilePausedFinalisesRecordedSegment()
    {
        if (!VideoExportTests.HasH264Encoder) return;
        string directory = Path.Combine(Path.GetTempPath(), $"inkmotion-encoder-pause-stop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var manifest = new ScreenRecordingManifest
            {
                Plan = ScreenCapturePlan.Create([new(0, 0, 64, 64)], [new(0, 0, 64, 64)], 1920, 15),
            };
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var clock = ScreenRecordingClock.StartNew();
            int captured = 0;
            await Task.Run(() => ScreenRecordingEncoder.Record(directory, manifest, clock,
                () => new SyntheticFrames(() =>
                {
                    if (++captured == 6)
                    {
                        clock.Pause();
                        stop.CancelAfter(500);
                    }
                }),
                () => ScreenRecordingStore.Save(directory, manifest), stop.Token));
            Assert.True(clock.IsPaused);
            Assert.Equal(6, captured);
            ScreenRecordingMedia media = Assert.Single(ScreenRecordingStore.Read(directory).Media, media => media.Finalised);
            Assert.InRange(media.DurationSeconds, 0.3, clock.Elapsed.TotalSeconds + 0.2);
        }
        finally { Directory.Delete(directory, true); }
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
            var clock = ScreenRecordingClock.StartNew();
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
