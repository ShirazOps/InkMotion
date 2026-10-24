using System.Diagnostics;

namespace InkMotion;

public static class ExportJobWorker
{
    public static int Run(ExportJobStore store, string id)
    {
        try
        {
            using FileStream? lease = store.TryAcquireWorker(id);
            if (lease is null)
            {
                return 3;
            }

            ExportJobStatus status = store.ReadStatus(id);
            if (status.Phase is not (ExportJobPhase.Queued or ExportJobPhase.Starting))
            {
                return 3;
            }

            using Process process = Process.GetCurrentProcess();
            status = status with
            {
                Phase = ExportJobPhase.Running,
                ProcessId = process.Id,
                ProcessStartUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                Message = "Preparing export in a separate process.",
                UpdatedUtc = DateTimeOffset.UtcNow,
            };
            store.WriteStatus(status);
            using var cancellation = new CancellationTokenSource();
            using var stopPolling = new CancellationTokenSource();
            Task poll = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        if (store.IsCancellationRequested(id))
                        {
                            cancellation.Cancel();
                            return;
                        }

                        await Task.Delay(200, stopPolling.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (stopPolling.IsCancellationRequested)
                {
                }
            });
            try
            {
                ExportJobRequest request = store.ReadRequest(id);
                EditorProject project = EditorProjectSerialiser.Deserialise(request.ProjectJson);
                if (!StringComparer.OrdinalIgnoreCase.Equals(status.OutputPath, request.OutputPath))
                {
                    throw new InvalidDataException("The export output path does not match its job record.");
                }

                if (store.IsCancellationRequested(id))
                {
                    cancellation.Cancel();
                }

                cancellation.Token.ThrowIfCancellationRequested();
                var clock = Stopwatch.StartNew();
                void Report(double fraction, string message)
                {
                    if (clock.ElapsedMilliseconds < 500)
                    {
                        return;
                    }

                    status = status with { Fraction = Math.Clamp(fraction, 0, 1), Message = message, UpdatedUtc = DateTimeOffset.UtcNow };
                    store.WriteStatus(status);
                    clock.Restart();
                }

                string summary;
                IReadOnlyList<string> warnings;
                if (request.Audio is { } audio)
                {
                    AudioExportResult result = AudioExporter.Export(project, audio, request.OutputPath,
                        new InlineProgress<double>(fraction => Report(fraction, $"Rendering audio mix · {fraction:P0}")), cancellation.Token);
                    summary = $"Exported {result.SampleRate:N0} Hz · {result.Channels} channels";
                    warnings = result.Warnings;
                }
                else
                {
                    ExportVideoSettings video = request.Video!;
                    VideoExportResult result = VideoExporter.Export(new(project, video.Profile, video.Codec, request.OutputPath,
                        video.InkReferenceHeight, video.PreferHardware, video.Quality, video.AudioSampleRate, video.AudioBitRate,
                        video.FFmpeg, video.FFmpeg is null ? null :
                            Path.Combine(store.JobDirectory(video.CheckpointKey ?? id), "segments")),
                        new InlineProgress<VideoExportProgress>(update => Report(update.Fraction,
                            $"{update.Stage} · frame {update.Frame:N0} of {update.FrameCount:N0}")), cancellation.Token);
                    summary = $"Exported {result.DurationSeconds:0.0} s · " +
                        (result.EncoderName is not null ? $"encoder: {result.EncoderName}" : result.HardwareTransformsEnabled
                            ? "hardware transforms preferred (encoder hardware use unverified)"
                            : "hardware transforms not requested (encoder hardware use unverified)");
                    warnings = result.Warnings;
                }

                status = status with { Phase = ExportJobPhase.Completed, Fraction = 1,
                    Message = summary + (warnings.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, warnings)) };
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                status = status with { Phase = ExportJobPhase.Cancelled, Message = "Export cancelled; any partial output created by this export was removed." };
            }
            catch (Exception ex)
            {
                // This is the worker boundary: persist every managed failure for the next editor session.
                status = cancellation.IsCancellationRequested || store.IsCancellationRequested(id)
                    ? status with
                    {
                        Phase = ExportJobPhase.Cancelled,
                        Message = "Export cancelled; any partial output created by this export was removed.",
                    }
                    : status with { Phase = ExportJobPhase.Failed, Message = $"Export failed: {ex.Message}" };
            }
            finally
            {
                stopPolling.Cancel();
                poll.GetAwaiter().GetResult();
            }

            store.WriteStatus(status with { UpdatedUtc = DateTimeOffset.UtcNow });
            try
            {
                store.StartNextQueued(Environment.ProcessPath ??
                    throw new InvalidOperationException("Could not locate the InkMotion worker executable."));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
                System.ComponentModel.Win32Exception or InvalidOperationException or System.Text.Json.JsonException)
            {
                Trace.TraceError($"Could not start the next queued export after job {id}: {ex}");
            }
            return status.Phase == ExportJobPhase.Completed ? 0 : 1;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // If the job store itself is unavailable, retain a diagnostic without displaying a worker window.
            Trace.TraceError($"Export job {id}: {ex}");
            try
            {
                File.WriteAllText(Path.Combine(store.JobDirectory(id), "worker-error.txt"), ex.ToString());
            }
            catch (Exception diagnosticError) when (diagnosticError is IOException or UnauthorizedAccessException)
            {
                Trace.TraceError($"Could not save export diagnostic: {diagnosticError}");
            }
            return 2;
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
