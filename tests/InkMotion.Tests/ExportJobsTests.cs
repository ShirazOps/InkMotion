using System.Diagnostics;
using System.Text.Json.Nodes;
using NAudio.Wave;
using Xunit;

namespace InkMotion.Tests;

public sealed class ExportJobsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"InkMotion export jobs {Guid.NewGuid():N}");
    private readonly ExportJobStore _store;
    private static string WorkerExecutable => Path.Combine(AppContext.BaseDirectory, "InkMotion.exe");

    public ExportJobsTests()
    {
        Directory.CreateDirectory(_root);
        _store = new ExportJobStore(Path.Combine(_root, "jobs"));
    }

    [Fact]
    public void Requests_AreIndependentValidatedSnapshotsWithExactSettings()
    {
        var project = new EditorProject { Frames = [new() { Strokes = [[new() { X = 0.4 }]] }] };
        var video = new ExportVideoSettings(VideoExportProfile.Create(ExportResolution.P1080, 120),
            VideoExportCodec.Hevc, 900, false, 0.9, 44100, 160000);
        ExportJobStatus job = _store.Create(project, Path.Combine(_root, "video.mp4"), null, video);
        project.Frames[0].Strokes[0][0].X = 0.8;
        ExportJobRequest request = _store.ReadRequest(job.Id);
        Assert.Equal(video, request.Video);
        Assert.Null(request.Audio);
        Assert.Equal(0.4, EditorProjectSerialiser.Deserialise(request.ProjectJson).Frames[0].Strokes[0][0].X);
        Assert.Equal(ExportJobPhase.Queued, _store.ReadStatus(job.Id).Phase);
        Assert.Throws<ArgumentException>(() => _store.Create(project, Path.Combine(_root, "bad.wav"), null, null));
        Assert.Throws<ArgumentException>(() => _store.JobDirectory(@"..\outside"));
    }

    [Fact]
    public void Scan_UsesExclusiveWorkerLeaseNotStaleProcessIdsAndRecoversMissingWorkers()
    {
        ExportJobStatus job = CreateAudioJob(new());
        _store.WriteStatus(job with { Phase = ExportJobPhase.Running, ProcessId = Environment.ProcessId });
        using (FileStream? lease = _store.TryAcquireWorker(job.Id))
        {
            Assert.NotNull(lease);
            Assert.Null(_store.TryAcquireWorker(job.Id));
            Assert.Equal(ExportJobPhase.Running, Assert.Single(new ExportJobStore(Path.Combine(_root, "jobs")).Scan().Jobs).Phase);
        }

        ExportJobStatus interrupted = Assert.Single(_store.Scan().Jobs);
        Assert.Equal(ExportJobPhase.Interrupted, interrupted.Phase);
        Assert.Contains("incomplete", interrupted.Message);
        Assert.Equal(3, ExportJobWorker.Run(_store, job.Id));
    }

    [Fact]
    public void Scan_PreservesQueuedJobsAndRecoversAbandonedWorkerStarts()
    {
        ExportJobStatus job = CreateAudioJob(new());
        Assert.Equal(ExportJobPhase.Queued, Assert.Single(_store.Scan().Jobs).Phase);
        _store.WriteStatus(job with { UpdatedUtc = DateTimeOffset.UtcNow.AddMinutes(-1) });
        Assert.Equal(ExportJobPhase.Queued, Assert.Single(_store.Scan().Jobs).Phase);
        _store.WriteStatus(job with
        {
            Phase = ExportJobPhase.Starting,
            UpdatedUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
        });
        Assert.Equal(ExportJobPhase.Interrupted, Assert.Single(_store.Scan().Jobs).Phase);
    }

    [Fact]
    public void CorruptStatus_IsReportedWithoutHidingOtherJobs()
    {
        ExportJobStatus first = CreateAudioJob(new());
        ExportJobStatus second = _store.Create(new(), Path.Combine(_root, "second.wav"), new(AudioExportCodec.PcmWave, 48000), null);
        File.WriteAllText(Path.Combine(_store.JobDirectory(first.Id), "status.json"), """{"OutputPath":null}""");
        ExportJobScan scan = _store.Scan();
        Assert.Equal(second.Id, Assert.Single(scan.Jobs).Id);
        Assert.Contains(first.Id, Assert.Single(scan.Errors));
    }

    [Fact]
    public void CancelBeforeStart_IsPersistedAndDoesNotTouchExistingOutput()
    {
        ExportJobStatus job = CreateAudioJob(new());
        File.WriteAllText(job.OutputPath, "existing output");
        _store.Cancel(job.Id);
        Assert.Equal(3, ExportJobWorker.Run(_store, job.Id));
        Assert.Equal(ExportJobPhase.Cancelled, _store.ReadStatus(job.Id).Phase);
        Assert.Equal("existing output", File.ReadAllText(job.OutputPath));
        Assert.Equal(3, ExportJobWorker.Run(_store, job.Id));
    }

    [Fact]
    public void UnsupportedRequestVersion_PersistsExplicitFailure()
    {
        ExportJobStatus job = CreateAudioJob(new());
        _store.WriteStatus(job with { AutomaticRetriesRemaining = 0 });
        string path = Path.Combine(_store.JobDirectory(job.Id), "request.json");
        JsonObject json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["Version"] = 999;
        File.WriteAllText(path, json.ToJsonString());
        Assert.Equal(1, ExportJobWorker.Run(_store, job.Id));
        ExportJobStatus failed = _store.ReadStatus(job.Id);
        Assert.Equal(ExportJobPhase.Failed, failed.Phase);
        Assert.Contains("request", failed.Message);
        Assert.False(File.Exists(job.OutputPath));
    }

    [Fact]
    public async Task WorkerFailure_IsRetriedAutomaticallyOnce()
    {
        ExportJobStatus job = CreateAudioJob(new());
        ExportJobStatus starting = Assert.IsType<ExportJobStatus>(
            _store.StartNextQueued(WorkerExecutable));
        Assert.Equal(job.Id, starting.Id);

        var clock = Stopwatch.StartNew();
        ExportJobStatus status;
        do
        {
            status = _store.ReadStatus(job.Id);
            if (status.AutomaticRetriesRemaining == 0)
            {
                break;
            }
            await Task.Delay(20);
        } while (clock.Elapsed < TimeSpan.FromSeconds(30));

        Assert.Equal(0, status.AutomaticRetriesRemaining);
        status = await WaitForTerminal(job.Id);
        Assert.Equal(ExportJobPhase.Failed, status.Phase);
        Assert.Contains("Nothing to export", status.Message);
        Assert.Null(_store.StartNextQueued(WorkerExecutable));
    }

    [Fact]
    public async Task AudioExport_RunsInRealWorkerAndIsRediscoveredByNewStore()
    {
        string input = WriteTone();
        var project = new EditorProject { AudioTracks = [new() { AudioPath = input }] };
        ExportJobStatus job = _store.Launch(project,
            Path.Combine(_root, "audio output.wav"), new(AudioExportCodec.PcmWave, 44100, 24), null, WorkerExecutable);
        Assert.Equal(input, EditorProjectSerialiser.Deserialise(_store.ReadRequest(job.Id).ProjectJson).AudioTracks[0].AudioPath);
        ExportJobStatus result = await WaitForTerminal(job.Id);
        Assert.Equal(ExportJobPhase.Completed, result.Phase);
        Assert.NotEqual(Environment.ProcessId, result.ProcessId);
        Assert.True(result.ProcessStartUtcTicks > 0);
        Assert.Equal(1, result.Fraction);
        using var reader = new AudioFileReader(job.OutputPath);
        Assert.Equal(44100, reader.WaveFormat.SampleRate);
        float[] samples = new float[4410];
        Assert.True(reader.Read(samples, 0, samples.Length) > 0);
        Assert.InRange(samples.Max(Math.Abs), 0.1f, 0.3f);
        ExportJobStatus recovered = Assert.Single(new ExportJobStore(Path.Combine(_root, "jobs")).Scan().Jobs);
        Assert.Equal(result, recovered);
    }

    [Fact]
    public async Task ExportQueue_SubmitsWhileBusyAndAdvancesInOrder()
    {
        string input = WriteTone();
        var project = new EditorProject { AudioTracks = [new() { AudioPath = input }] };
        ExportJobStatus active = CreateAudioJob(project);
        using FileStream? lease = _store.TryAcquireWorker(active.Id);
        Assert.NotNull(lease);
        _store.WriteStatus(active with { Phase = ExportJobPhase.Running, ProcessId = Environment.ProcessId });

        ExportJobStatus first = _store.Launch(project, Path.Combine(_root, "first queued.wav"),
            new(AudioExportCodec.PcmWave, 44100, 24), null, WorkerExecutable);
        ExportJobStatus second = _store.Launch(project, Path.Combine(_root, "second queued.wav"),
            new(AudioExportCodec.PcmWave, 44100, 24), null, WorkerExecutable);
        Assert.Equal(ExportJobPhase.Queued, first.Phase);
        Assert.Equal(ExportJobPhase.Queued, second.Phase);

        _store.WriteStatus(active with { Phase = ExportJobPhase.Completed, Fraction = 1 });
        lease.Dispose();
        ExportJobStatus starting = Assert.IsType<ExportJobStatus>(_store.StartNextQueued(WorkerExecutable));
        Assert.Equal(first.Id, starting.Id);
        ExportJobStatus firstResult = await WaitForTerminal(first.Id);
        ExportJobStatus secondResult = await WaitForTerminal(second.Id);
        Assert.True(firstResult.Phase == ExportJobPhase.Completed, firstResult.Message);
        Assert.True(secondResult.Phase == ExportJobPhase.Completed, secondResult.Message);
    }

    [Fact]
    public async Task InterruptedExport_IsRetriedAutomaticallyOnce()
    {
        string input = WriteTone();
        var project = new EditorProject { AudioTracks = [new() { AudioPath = input }] };
        ExportJobStatus job = CreateAudioJob(project);
        _store.WriteStatus(job with
        {
            Phase = ExportJobPhase.Interrupted,
            Message = "The previous worker stopped unexpectedly.",
        });

        ExportJobStatus starting = Assert.IsType<ExportJobStatus>(_store.StartNextQueued(WorkerExecutable));
        Assert.Equal(job.Id, starting.Id);
        Assert.Equal(0, starting.AutomaticRetriesRemaining);
        Assert.Contains("automatically retrying", starting.Message, StringComparison.OrdinalIgnoreCase);
        ExportJobStatus completed = await WaitForTerminal(job.Id);
        Assert.True(completed.Phase == ExportJobPhase.Completed, completed.Message);
        Assert.Equal(0, completed.AutomaticRetriesRemaining);
    }

    [Fact]
    public async Task VideoExport_SurvivesTerminatedLaunchingProcessAndProducesPlayableFile()
    {
        if (!VideoExportTests.HasH264Encoder)
        {
            return;
        }

        ExportJobStatus job = CreateVideoJob();
        ProcessStartInfo workerInfo = _store.CreateWorkerStartInfo(WorkerExecutable, job.Id);
        // A disposable parent models the editor's lifetime, using the same independent Process.Start semantics.
        string quote(string value) => "'" + value.Replace("'", "''") + "'";
        string script = "$p = Start-Process -FilePath " + quote(workerInfo.FileName) +
            " -WorkingDirectory " + quote(workerInfo.WorkingDirectory) +
            " -ArgumentList " + quote(string.Join(" ", workerInfo.ArgumentList.Select(arg => "\"" + arg + "\""))) +
            " -PassThru; $p.WaitForExit()";
        var parentStart = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        parentStart.ArgumentList.Add("-NoProfile");
        parentStart.ArgumentList.Add("-EncodedCommand");
        parentStart.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)));
        using Process parent = Process.Start(parentStart)!;
        try
        {
            ExportJobStatus running = await WaitForRunning(job.Id);
            Assert.Equal(ExportJobPhase.Running, running.Phase);
            parent.Kill();
            await parent.WaitForExitAsync();
            ExportJobStatus rediscovered = Assert.Single(new ExportJobStore(Path.Combine(_root, "jobs")).Scan().Jobs);
            Assert.True(rediscovered.Phase is ExportJobPhase.Running or ExportJobPhase.Completed);
            ExportJobStatus result = await WaitForTerminal(job.Id);
            Assert.True(result.Phase == ExportJobPhase.Completed, result.Message);
            using var reader = new MediaFoundationVideoReader(job.OutputPath);
            Assert.True(reader.TryGetFrame(0));
            Assert.Equal(128, reader.Width);
            Assert.Equal(80, reader.Height);
        }
        finally
        {
            if (!parent.HasExited)
            {
                parent.Kill();
                await parent.WaitForExitAsync();
            }
            await StopOwnedWorker(job.Id);
        }
    }

    [Fact]
    public async Task DuplicateWorkerCannotClaimJobAndCrossProcessCancellationRemovesPartialOutput()
    {
        if (!VideoExportTests.HasH264Encoder)
        {
            return;
        }

        ExportJobStatus job = CreateVideoJob();
        using Process worker = Process.Start(_store.CreateWorkerStartInfo(WorkerExecutable, job.Id))!;
        try
        {
            await WaitForRunning(job.Id);
            Assert.Equal(3, ExportJobWorker.Run(_store, job.Id));
            _store.Cancel(job.Id);
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(ExportJobPhase.Cancelled, _store.ReadStatus(job.Id).Phase);
            Assert.False(File.Exists(job.OutputPath));
        }
        finally
        {
            if (!worker.HasExited)
            {
                worker.Kill();
                await worker.WaitForExitAsync();
            }
        }
    }

    [Fact]
    public async Task KilledWorkerIsReportedAsInterruptedWithoutDeletingUnknownOutput()
    {
        if (!VideoExportTests.HasH264Encoder)
        {
            return;
        }

        ExportJobStatus job = CreateVideoJob();
        using Process worker = Process.Start(_store.CreateWorkerStartInfo(WorkerExecutable, job.Id))!;
        try
        {
            await WaitForRunning(job.Id);
            worker.Kill();
            await worker.WaitForExitAsync();
            ExportJobStatus recovered = Assert.Single(_store.Scan().Jobs);
            var clock = Stopwatch.StartNew();
            while (recovered.IsActive && clock.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(50);
                recovered = Assert.Single(_store.Scan().Jobs);
            }
            Assert.Equal(ExportJobPhase.Interrupted, recovered.Phase);
            Assert.Equal(3, ExportJobWorker.Run(_store, job.Id));
        }
        finally
        {
            if (!worker.HasExited)
            {
                worker.Kill();
                await worker.WaitForExitAsync();
            }
        }
    }

    [Fact]
    public void SubmissionFailure_PersistsReasonAndProtectsReservedOutputAndStem()
    {
        ExportJobStatus job = CreateVideoJob();
        string missingExe = Path.Combine(_root, "missing.exe");
        Assert.Throws<InvalidOperationException>(() => _store.Launch(new(), job.OutputPath, new(AudioExportCodec.PcmWave, 48000), null, missingExe));
        Assert.Throws<InvalidOperationException>(() => _store.Launch(new(), ExportPlanning.GetSurroundStemPath(job.OutputPath),
            new(AudioExportCodec.PcmWave, 48000), null, missingExe));
        Assert.Throws<IOException>(() => _store.Launch(new(), Path.Combine(_root, "failed.wav"), new(AudioExportCodec.PcmWave, 48000), null, missingExe));
        Assert.Contains(_store.Scan().Jobs, status => status.Phase == ExportJobPhase.Failed && status.Message.Contains("start worker"));
    }

    [Fact]
    public void SnapshotNormalisesRelativeMediaPathsWithoutMutatingTheEditor()
    {
        var project = new EditorProject
        {
            AudioTracks = [new() { AudioPath = "source.wav" }],
            VideoTracks = [new() { Clips = [new() { SourcePath = "source.mp4", SourceDurationSeconds = 1 }] }],
        };
        ExportJobStatus job = CreateAudioJob(project);
        EditorProject snapshot = EditorProjectSerialiser.Deserialise(_store.ReadRequest(job.Id).ProjectJson);
        Assert.Equal(Path.GetFullPath("source.wav"), snapshot.AudioTracks[0].AudioPath);
        Assert.Equal(Path.GetFullPath("source.mp4"), snapshot.VideoTracks[0].Clips[0].SourcePath);
        Assert.Equal("source.wav", project.AudioTracks[0].AudioPath);
        Assert.Equal("source.mp4", project.VideoTracks[0].Clips[0].SourcePath);
    }

    [Fact]
    public async Task RetryUsesSavedSnapshotAndSettingsWithNewIndependentJobId()
    {
        string input = WriteTone();
        var project = new EditorProject { AudioTracks = [new() { AudioPath = input }] };
        ExportJobStatus original = CreateAudioJob(project);
        project.AudioTracks.Clear();
        _store.Cancel(original.Id);
        Assert.Equal(3, ExportJobWorker.Run(_store, original.Id));
        ExportJobStatus retry = _store.Retry(original.Id, WorkerExecutable);
        Assert.NotEqual(original.Id, retry.Id);
        Assert.Equal(original.OutputPath, retry.OutputPath);
        ExportJobStatus result = await WaitForTerminal(retry.Id);
        Assert.True(result.Phase == ExportJobPhase.Completed, result.Message);
        Assert.Equal(ExportJobPhase.Cancelled, _store.ReadStatus(original.Id).Phase);
        Assert.Throws<InvalidOperationException>(() => _store.Retry(retry.Id, WorkerExecutable));
        using var reader = new AudioFileReader(retry.OutputPath);
        Assert.Equal(48000, reader.WaveFormat.SampleRate);
        Assert.InRange(reader.TotalTime.TotalSeconds, 0.099, 0.101);
    }

    [Fact]
    public async Task AtomicProgressWritesRemainReadableDuringConcurrentDiscovery()
    {
        ExportJobStatus job = CreateAudioJob(new());
        _store.WriteStatus(job with { Phase = ExportJobPhase.Running });
        using FileStream? lease = _store.TryAcquireWorker(job.Id);
        Assert.NotNull(lease);
        Task writes = Task.Run(() =>
        {
            for (int index = 0; index < 40; index++)
            {
                _store.WriteStatus(job with { Phase = ExportJobPhase.Running, Fraction = index / 40d, Message = $"Frame {index}" });
            }
        });
        try
        {
            for (int index = 0; index < 40; index++)
            {
                ExportJobScan scan = _store.Scan();
                Assert.Empty(scan.Errors);
                Assert.Equal(ExportJobPhase.Running, Assert.Single(scan.Jobs).Phase);
                await Task.Yield();
            }
        }
        finally
        {
            await writes;
        }
        Assert.Equal(39 / 40d, _store.ReadStatus(job.Id).Fraction);
    }

    [Fact]
    public async Task InvalidWorkerIdentifierExitsWithoutOpeningEditorOrTouchingJobs()
    {
        var start = new ProcessStartInfo(WorkerExecutable) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--export-job");
        start.ArgumentList.Add(@"..\outside");
        start.ArgumentList.Add(_root);
        using Process process = Process.Start(start)!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(2, process.ExitCode);
        Assert.Empty(_store.Scan().Jobs);
    }

    private ExportJobStatus CreateAudioJob(EditorProject project) =>
        _store.Create(project, Path.Combine(_root, "output.wav"), new(AudioExportCodec.PcmWave, 48000), null);

    private ExportJobStatus CreateVideoJob() => _store.Create(new()
    {
        Frames = [new() { ExposureFrames = 999, Strokes = [[new() { X = 0.5, Y = 0.5, BrushWidth = 8 }]] }],
    }, Path.Combine(_root, "output.mp4"), null,
        new(new(ExportResolution.P720, 128, 80, 24), VideoExportCodec.H264, 80, false, 0.5, 48000, 192000));

    private string WriteTone()
    {
        string input = Path.Combine(_root, "source.wav");
        using var writer = new WaveFileWriter(input, WaveFormat.CreateIeeeFloatWaveFormat(48000, 1));
        for (int index = 0; index < 4800; index++)
        {
            writer.WriteSample((float)(0.2 * Math.Sin(index * 2 * Math.PI * 440 / 48000)));
        }
        return input;
    }

    private async Task<ExportJobStatus> WaitForRunning(string id)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(30))
        {
            ExportJobStatus status = _store.ReadStatus(id);
            if (status.Phase == ExportJobPhase.Running)
            {
                return status;
            }
            Assert.True(status.IsActive, status.Message);
            await Task.Delay(20);
        }
        Assert.Fail("Worker did not start within 30 seconds.");
        throw new TimeoutException();
    }

    private async Task<ExportJobStatus> WaitForTerminal(string id)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(90))
        {
            ExportJobScan scan = _store.Scan();
            Assert.Empty(scan.Errors);
            ExportJobStatus status = scan.Jobs.Single(job => job.Id == id);
            if (!status.IsActive)
            {
                // Terminal state is durable before the worker releases its lease and exits.
                await StopOwnedWorker(id, kill: false);
                return status;
            }
            await Task.Delay(50);
        }
        await StopOwnedWorker(id);
        Assert.Fail("Export did not finish within 90 seconds.");
        throw new TimeoutException();
    }

    private async Task StopOwnedWorker(string id, bool kill = true)
    {
        ExportJobStatus status = _store.ReadStatus(id);
        if (status.ProcessId == 0)
        {
            return;
        }
        Process process;
        try
        {
            process = Process.GetProcessById(status.ProcessId);
        }
        catch (ArgumentException)
        {
            return;
        }
        using (process)
        {
            if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == status.ProcessStartUtcTicks)
            {
                if (kill)
                {
                    process.Kill();
                }
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    public void Dispose() => Directory.Delete(_root, true);
}
