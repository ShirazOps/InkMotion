using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InkMotion;

public sealed record ExportVideoSettings(VideoExportProfile Profile, VideoExportCodec Codec, double InkReferenceHeight,
    bool PreferHardware, double Quality, int AudioSampleRate, int AudioBitRate);

public sealed record ExportJobRequest(string Id, string ProjectJson, string OutputPath,
    AudioExportSettings? Audio, ExportVideoSettings? Video, int Version = 1);

public enum ExportJobPhase { Queued, Starting, Running, Completed, Cancelled, Failed, Interrupted }

public sealed record ExportJobStatus(string Id, string OutputPath, ExportJobPhase Phase, double Fraction,
    string Message, DateTimeOffset UpdatedUtc, int ProcessId = 0, long ProcessStartUtcTicks = 0,
    int AutomaticRetriesRemaining = 1)
{
    public bool IsActive => Phase is ExportJobPhase.Queued or ExportJobPhase.Starting or ExportJobPhase.Running;
    public string DisplayText => $"{Path.GetFileName(OutputPath)} | {Phase} | {Fraction:P0}\n{Message}";
}

public sealed record ExportJobScan(IReadOnlyList<ExportJobStatus> Jobs, IReadOnlyList<string> Errors);

public sealed class ExportJobStore
{
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InkMotion", "ExportJobs");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly string _root;

    public ExportJobStore(string? root = null) => _root = Path.GetFullPath(root ?? DefaultRoot);

    public static bool IsJobId(string id) => Guid.TryParseExact(id, "N", out _);

    public string JobDirectory(string id)
    {
        if (!IsJobId(id))
        {
            throw new ArgumentException("Invalid export job identifier.", nameof(id));
        }

        return Path.Combine(_root, id);
    }

    public ExportJobRequest ReadRequest(string id)
    {
        ExportJobRequest request = ReadJson<ExportJobRequest>(Path.Combine(JobDirectory(id), "request.json"));
        if (request.Version != 1 || request.Id != id || string.IsNullOrWhiteSpace(request.ProjectJson) ||
            (request.Audio is null) == (request.Video is null) ||
            string.IsNullOrWhiteSpace(request.OutputPath) || !Path.IsPathFullyQualified(request.OutputPath))
        {
            throw new InvalidDataException("Unsupported or invalid export job request.");
        }

        return request;
    }

    public ExportJobStatus ReadStatus(string id)
    {
        using FileStream access = AcquireStatusAccess(id);
        ExportJobStatus status = ReadJson<ExportJobStatus>(Path.Combine(JobDirectory(id), "status.json"));
        if (status.Id != id || !Enum.IsDefined(status.Phase) || !double.IsFinite(status.Fraction) ||
            status.Fraction is < 0 or > 1 || status.Message is null ||
            string.IsNullOrWhiteSpace(status.OutputPath) || !Path.IsPathFullyQualified(status.OutputPath) ||
            status.AutomaticRetriesRemaining is < 0 or > 1)
        {
            throw new InvalidDataException("Invalid export job status.");
        }

        return status;
    }

    public void WriteStatus(ExportJobStatus status)
    {
        using FileStream access = AcquireStatusAccess(status.Id);
        WriteJson(Path.Combine(JobDirectory(status.Id), "status.json"), status);
    }

    private FileStream AcquireStatusAccess(string id)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(Path.Combine(JobDirectory(id), "status.access"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 && clock.Elapsed < TimeSpan.FromSeconds(2))
            {
                Thread.Sleep(10);
            }
        }
    }

    public ExportJobStatus Create(EditorProject project, string outputPath, AudioExportSettings? audio,
        ExportVideoSettings? video)
    {
        if ((audio is null) == (video is null))
        {
            throw new ArgumentException("Choose exactly one export type.");
        }

        outputPath = Path.GetFullPath(outputPath);
        EditorProject snapshot = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        foreach (AudioTrackProject track in snapshot.AudioTracks.Where(track => !string.IsNullOrWhiteSpace(track.AudioPath)))
        {
            track.AudioPath = Path.GetFullPath(track.AudioPath!);
        }
        foreach (TimelineClipProject clip in snapshot.VideoTracks.SelectMany(track => track.Clips)
            .Where(clip => !string.IsNullOrWhiteSpace(clip.SourcePath)))
        {
            clip.SourcePath = Path.GetFullPath(clip.SourcePath!);
        }
        ExportPlanning.ValidateOutputPath(snapshot, outputPath);
        audio?.Validate();
        string id = Guid.NewGuid().ToString("N");
        var request = new ExportJobRequest(id, EditorProjectSerialiser.Serialise(snapshot), outputPath, audio, video);
        var status = new ExportJobStatus(id, outputPath, ExportJobPhase.Queued, 0, "Waiting for export worker.",
            DateTimeOffset.UtcNow);
        Directory.CreateDirectory(JobDirectory(id));
        WriteJson(Path.Combine(JobDirectory(id), "request.json"), request);
        WriteStatus(status);
        return status;
    }

    public ExportJobStatus Launch(EditorProject project, string outputPath, AudioExportSettings? audio,
        ExportVideoSettings? video, string executable)
    {
        // Serialise submissions across editor instances, including reservation of surround stem paths.
        using var submission = new Mutex(false, @"Local\InkMotion.ExportJobSubmission");
        bool acquired;
        try
        {
            acquired = submission.WaitOne(TimeSpan.FromSeconds(10));
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }

        if (!acquired)
        {
            throw new IOException("Another editor is submitting an export. Try again.");
        }

        try
        {
            ExportJobScan scan = Scan();
            if (scan.Errors.Count > 0)
            {
                throw new IOException(string.Join(Environment.NewLine, scan.Errors));
            }

            string[] outputs = ReservedPaths(Path.GetFullPath(outputPath), video is not null);
            foreach (ExportJobStatus active in scan.Jobs.Where(job =>
                job.IsActive || job.Phase == ExportJobPhase.Interrupted && job.AutomaticRetriesRemaining > 0))
            {
                ExportJobRequest existing = ReadRequest(active.Id);
                if (ReservedPaths(existing.OutputPath, existing.Video is not null)
                    .Intersect(outputs, StringComparer.OrdinalIgnoreCase).Any())
                {
                    throw new InvalidOperationException("An active export already uses this output or its surround stem. Choose another file.");
                }
            }

            ExportJobStatus status = Create(project, outputPath, audio, video);
            _ = StartNextQueued(executable);
            ExportJobStatus submitted = ReadStatus(status.Id);
            if (submitted.Phase == ExportJobPhase.Failed &&
                submitted.Message.StartsWith("Could not start worker:", StringComparison.Ordinal))
            {
                throw new IOException("Could not start the export worker.");
            }

            return submitted;
        }
        finally
        {
            submission.ReleaseMutex();
        }
    }

    public ExportJobStatus? StartNextQueued(string executable)
    {
        using var queue = new Mutex(false, @"Local\InkMotion.ExportJobQueue");
        bool acquired;
        try
        {
            acquired = queue.WaitOne(TimeSpan.FromSeconds(10));
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }

        if (!acquired)
        {
            return null;
        }

        try
        {
            ExportJobScan scan = Scan();
            if (scan.Errors.Count > 0)
            {
                throw new IOException(string.Join(Environment.NewLine, scan.Errors));
            }
            if (scan.Jobs.Any(job => job.Phase is ExportJobPhase.Starting or ExportJobPhase.Running))
            {
                return null;
            }

            foreach (ExportJobStatus queued in scan.Jobs.Where(job =>
                job.Phase == ExportJobPhase.Queued ||
                job.Phase == ExportJobPhase.Interrupted && job.AutomaticRetriesRemaining > 0)
                .OrderBy(job => job.UpdatedUtc).ThenBy(job => job.Id, StringComparer.Ordinal))
            {
                bool retryingInterrupted = queued.Phase == ExportJobPhase.Interrupted;
                string cancelPath = Path.Combine(JobDirectory(queued.Id), "cancel");
                if (retryingInterrupted && File.Exists(cancelPath))
                {
                    File.Delete(cancelPath);
                }

                ExportJobStatus starting = queued with
                {
                    Phase = ExportJobPhase.Starting,
                    Message = retryingInterrupted
                        ? "Automatically retrying an interrupted export (one attempt maximum)."
                        : "Starting queued export worker.",
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    AutomaticRetriesRemaining = retryingInterrupted
                        ? queued.AutomaticRetriesRemaining - 1
                        : queued.AutomaticRetriesRemaining,
                };
                WriteStatus(starting);
                try
                {
                    using Process worker = Process.Start(CreateWorkerStartInfo(executable, starting.Id))
                        ?? throw new IOException("Windows did not start the export worker.");
                    return starting;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
                {
                    WriteStatus(starting with
                    {
                        Phase = ExportJobPhase.Failed,
                        Message = $"Could not start worker: {ex.Message}",
                        UpdatedUtc = DateTimeOffset.UtcNow,
                    });
                }
            }

            return null;
        }
        finally
        {
            queue.ReleaseMutex();
        }
    }

    public FileStream? TryAcquireWorker(string id)
    {
        try
        {
            return new FileStream(Path.Combine(JobDirectory(id), "worker.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
        {
            return null;
        }
    }

    public ProcessStartInfo CreateWorkerStartInfo(string executable, string id)
    {
        _ = JobDirectory(id);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!,
        };
        start.ArgumentList.Add("--export-job");
        start.ArgumentList.Add(id);
        start.ArgumentList.Add(_root);
        return start;
    }

    public ExportJobStatus Retry(string id, string executable)
    {
        ExportJobStatus status = ReadStatus(id);
        if (status.Phase is not (ExportJobPhase.Failed or ExportJobPhase.Cancelled or ExportJobPhase.Interrupted))
        {
            throw new InvalidOperationException("Only failed, cancelled or interrupted exports can be retried.");
        }

        ExportJobRequest request = ReadRequest(id);
        return Launch(EditorProjectSerialiser.Deserialise(request.ProjectJson), request.OutputPath,
            request.Audio, request.Video, executable);
    }

    public bool IsCancellationRequested(string id) => File.Exists(Path.Combine(JobDirectory(id), "cancel"));

    public void Cancel(string id)
    {
        using var queue = new Mutex(false, @"Local\InkMotion.ExportJobQueue");
        bool acquired;
        try
        {
            acquired = queue.WaitOne(TimeSpan.FromSeconds(10));
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }

        if (!acquired)
        {
            throw new IOException("The export queue is busy. Try cancelling again.");
        }

        try
        {
            ExportJobStatus status = ReadStatus(id);
            if (!status.IsActive)
            {
                return;
            }
            if (status.Phase == ExportJobPhase.Queued)
            {
                WriteStatus(status with
                {
                    Phase = ExportJobPhase.Cancelled,
                    Message = "Queued export cancelled before it started.",
                    UpdatedUtc = DateTimeOffset.UtcNow,
                });
            }
            else
            {
                using FileStream signal = new(Path.Combine(JobDirectory(id), "cancel"), FileMode.OpenOrCreate,
                    FileAccess.Write, FileShare.ReadWrite);
                signal.Flush(true);
            }
        }
        finally
        {
            queue.ReleaseMutex();
        }
    }

    public ExportJobScan Scan()
    {
        var jobs = new List<ExportJobStatus>();
        var errors = new List<string>();
        if (!Directory.Exists(_root))
        {
            return new(jobs, errors);
        }

        foreach (string directory in Directory.EnumerateDirectories(_root).Where(path => IsJobId(Path.GetFileName(path))))
        {
            string id = Path.GetFileName(directory);
            try
            {
                ExportJobStatus status = ReadStatus(id);
                if (status.Phase == ExportJobPhase.Running ||
                    status.Phase == ExportJobPhase.Starting &&
                    DateTimeOffset.UtcNow - status.UpdatedUtc > TimeSpan.FromSeconds(30))
                {
                    using FileStream? lease = TryAcquireWorker(id);
                    if (lease is not null)
                    {
                        // Re-read under the lease: the worker may have completed since the initial read.
                        status = ReadStatus(id);
                        if (status.IsActive)
                        {
                            string diagnostic = Path.Combine(directory, "worker-error.txt");
                            string? detail = File.Exists(diagnostic) ? File.ReadLines(diagnostic).FirstOrDefault() : null;
                            bool cancellationRequested = IsCancellationRequested(id);
                            status = status with
                            {
                                Phase = cancellationRequested ? ExportJobPhase.Cancelled : ExportJobPhase.Interrupted,
                                Message = cancellationRequested
                                    ? "The worker stopped after cancellation was requested. Partial output may remain."
                                    : "The export worker stopped unexpectedly. Output may be incomplete; export again to retry." +
                                        (detail is null ? "" : Environment.NewLine + detail),
                                UpdatedUtc = DateTimeOffset.UtcNow,
                            };
                            WriteStatus(status);
                        }
                    }
                }

                jobs.Add(status);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            {
                errors.Add($"Export job {id}: {ex.Message}");
            }
        }

        return new(jobs.OrderByDescending(job => job.IsActive).ThenByDescending(job => job.UpdatedUtc).ToArray(), errors);
    }

    private static string[] ReservedPaths(string output, bool video) =>
        video ? [output, ExportPlanning.GetSurroundStemPath(output)] : [output];

    private static T ReadJson<T>(string path)
    {
        using FileStream stream = RetryFileAccess(() => new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete));
        return JsonSerializer.Deserialize<T>(stream, JsonOptions) ?? throw new InvalidDataException("Empty export job record.");
    }

    private static T RetryFileAccess<T>(Func<T> operation)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return operation();
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) &&
                (ex.HResult & 0xffff) is 5 or 32 or 33 && clock.Elapsed < TimeSpan.FromSeconds(2))
            {
                // Antivirus/indexing handles can briefly prevent replacement even with our own readers serialised.
                Thread.Sleep(20);
            }
        }
    }

    private static void WriteJson<T>(string path, T value)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, JsonOptions);
                stream.Flush(true);
            }

            RetryFileAccess(() =>
            {
                File.Move(temporary, path, true);
                return true;
            });
        }
        finally
        {
            RetryFileAccess(() => { File.Delete(temporary); return true; });
        }
    }
}
