using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace InkMotion;

public enum ScreenRecordingAudioFlow
{
    RenderLoopback,
    Microphone,
}

public sealed record ScreenRecordingAudioDevice(string Id, string Name, ScreenRecordingAudioFlow Flow);

public sealed record ScreenRecordingCameraDevice(string Id, string Name);

public sealed record ScreenRecordingAudioTrackResult(
    string Path,
    ScreenRecordingAudioFlow Flow,
    double StartOffsetSeconds,
    double DurationSeconds,
    long DataBytes,
    IReadOnlyList<string> Warnings);

public sealed record ScreenRecordingCameraTrackResult(
    string Path,
    double StartOffsetSeconds,
    double DurationSeconds,
    IReadOnlyList<string> Warnings);

public sealed record ScreenRecordingAudioTrackSnapshot(
    string Path,
    ScreenRecordingAudioFlow Flow,
    double StartOffsetSeconds,
    double ElapsedDurationSeconds,
    long DataBytes,
    bool IsActive);

public sealed record ScreenRecordingCameraTrackSnapshot(
    string Path,
    double StartOffsetSeconds,
    double ElapsedDurationSeconds,
    bool IsActive);

public sealed record ScreenRecordingInputsSnapshot(
    IReadOnlyList<ScreenRecordingAudioTrackSnapshot> AudioTracks,
    ScreenRecordingCameraTrackSnapshot? CameraTrack,
    double SessionElapsedSeconds);

public sealed record ScreenRecordingInputsResult(
    IReadOnlyList<ScreenRecordingAudioTrackResult> AudioTracks,
    ScreenRecordingCameraTrackResult? CameraTrack,
    double SessionDurationSeconds);

public sealed class ScreenRecordingInputException : Exception
{
    public ScreenRecordingInputException(
        string message,
        IEnumerable<string> partialFiles,
        IEnumerable<Exception> errors,
        ScreenRecordingInputsResult? finalisedTracks = null)
        : base(message, new AggregateException(errors))
    {
        PartialFiles = partialFiles.ToArray();
        FinalisedTracks = finalisedTracks;
    }

    public IReadOnlyList<string> PartialFiles { get; }
    public ScreenRecordingInputsResult? FinalisedTracks { get; }
}

/// <summary>
/// Captures optional independent system-audio, microphone, and video tracks against a caller-owned Stopwatch.
/// Call InitializeCameraAsync before starting the shared clock to keep device initialization out of the recording.
/// </summary>
public sealed class ScreenRecordingInputs : IAsyncDisposable
{
    private static readonly TimeSpan RecordingStoppedTimeout = TimeSpan.FromSeconds(10);
    private readonly string _outputFolder;
    private readonly Stopwatch _sessionClock;
    private readonly SemaphoreSlim _stopLock = new(1, 1);
    private readonly Dictionary<ScreenRecordingAudioFlow, AudioTrackCapture> _audioTracks = new();
    private readonly List<string> _outputFiles = new();
    private MediaCapture? _mediaCapture;
    private StorageFile? _cameraFile;
    private ScreenRecordingCameraTrackResult? _cameraResult;
    private double _cameraStartOffset;
    private double _cameraStopOffset;
    private bool _cameraStartAttempted;
    private bool _cameraFileFinalised;
    private Exception? _cameraError;
    private double? _sessionStopElapsed;
    private ScreenRecordingInputException? _stopFailure;
    private bool _stopped;
    private bool _disposed;

    public ScreenRecordingInputs(string durableOutputFolder, Stopwatch sessionClock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(durableOutputFolder);
        ArgumentNullException.ThrowIfNull(sessionClock);
        _outputFolder = Path.GetFullPath(durableOutputFolder);
        _sessionClock = sessionClock;
        Directory.CreateDirectory(_outputFolder);
    }

    /// <summary>Paths reserved for this session, including any partial files retained after a capture error.</summary>
    public IReadOnlyList<string> OutputFiles => _outputFiles.ToArray();

    /// <summary>Poll from the UI timer so a failed input can promptly stop the remaining capture services.</summary>
    public void ThrowIfFailed()
    {
        if (_stopFailure is not null)
        {
            throw _stopFailure;
        }

        Exception[] errors = GetCaptureErrors();
        if (errors.Length != 0)
        {
            throw new ScreenRecordingInputException(
                "One or more screen-recording inputs have failed.",
                _outputFiles,
                errors,
                GetFinalisedTracks());
        }
    }

    /// <summary>
    /// Returns only closed, successfully finalized files, including healthy tracks after another input fails.
    /// Call after awaiting StopAsync (also when it throws) before importing files.
    /// </summary>
    public ScreenRecordingInputsResult GetFinalisedTracks() => CreateResult();

    /// <summary>
    /// Returns current timing and file metadata for crash-save checkpoints. Durations are elapsed-clock
    /// estimates until StopAsync completes; final audio durations are derived from flushed WAV data.
    /// </summary>
    public ScreenRecordingInputsSnapshot GetSnapshot()
    {
        double elapsed = _sessionStopElapsed ?? _sessionClock.Elapsed.TotalSeconds;
        ScreenRecordingAudioTrackSnapshot[] audio = _audioTracks.Values
            .Select(track => track.CreateSnapshot(elapsed))
            .ToArray();
        ScreenRecordingCameraTrackSnapshot? camera = _cameraStartAttempted && _cameraFile is not null
            ? new ScreenRecordingCameraTrackSnapshot(
                _cameraFile.Path,
                _cameraStartOffset,
                Math.Max(0, elapsed - _cameraStartOffset),
                !_stopped)
            : null;
        return new ScreenRecordingInputsSnapshot(audio, camera, elapsed);
    }

    public static IReadOnlyList<ScreenRecordingAudioDevice> ListAudioDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = new List<ScreenRecordingAudioDevice>();
        foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                devices.Add(new ScreenRecordingAudioDevice(device.ID, device.FriendlyName, ScreenRecordingAudioFlow.RenderLoopback));
            }
        }

        foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (device)
            {
                devices.Add(new ScreenRecordingAudioDevice(device.ID, device.FriendlyName, ScreenRecordingAudioFlow.Microphone));
            }
        }

        return devices;
    }

    public static async Task<IReadOnlyList<ScreenRecordingCameraDevice>> ListCamerasAsync()
    {
        IReadOnlyList<DeviceInformation> devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        return devices.Select(device => new ScreenRecordingCameraDevice(device.Id, device.Name)).ToArray();
    }

    /// <summary>
    /// Opens and initializes the explicitly selected camera without starting a recording.
    /// Invoke this before starting the shared Stopwatch.
    /// </summary>
    public async Task InitializeCameraAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        if (_mediaCapture is not null)
        {
            throw new InvalidOperationException("A camera has already been initialized for this session.");
        }

        var capture = new MediaCapture();
        _mediaCapture = capture;
        capture.Failed += Camera_Failed;
        capture.RecordLimitationExceeded += Camera_RecordLimitationExceeded;
        try
        {
            var settings = new MediaCaptureInitializationSettings
            {
                VideoDeviceId = deviceId,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                PhotoCaptureSource = PhotoCaptureSource.VideoPreview,
            };
            await capture.InitializeAsync(settings).AsTask(cancellationToken);
        }
        catch (Exception ex)
        {
            _cameraError = ex;
            throw CreateCaptureException("The selected camera could not be initialized.", ex);
        }
    }

    /// <summary>Starts capture from the explicit audio endpoint ID, writing a separate PCM/float WAV track.</summary>
    public Task StartAudioAsync(
        ScreenRecordingAudioDevice device,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        ArgumentNullException.ThrowIfNull(device);
        EnsureClockRunning();
        cancellationToken.ThrowIfCancellationRequested();
        if (_audioTracks.ContainsKey(device.Flow))
        {
            throw new InvalidOperationException($"A {device.Flow} track has already been started.");
        }

        var track = new AudioTrackCapture(this, device);
        _audioTracks.Add(device.Flow, track);
        _outputFiles.Add(track.Path);
        try
        {
            track.Start();
        }
        catch (Exception ex)
        {
            track.RecordError(ex);
            Exception reported = ex;
            try
            {
                track.CloseAfterStartFailure();
            }
            catch (Exception cleanupError)
            {
                reported = new AggregateException(ex, cleanupError);
            }

            throw CreateCaptureException($"The selected {device.Flow} device could not start.", reported);
        }

        return Task.CompletedTask;
    }

    /// <summary>Starts MP4 video-only capture. Initialize the camera before starting the shared Stopwatch.</summary>
    public async Task StartCameraAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        EnsureClockRunning();
        cancellationToken.ThrowIfCancellationRequested();
        if (_mediaCapture is null)
        {
            throw new InvalidOperationException("InitializeCameraAsync must complete before starting camera capture.");
        }

        if (_cameraStartAttempted)
        {
            throw new InvalidOperationException("Camera capture has already been started.");
        }

        string path = NewOutputPath("Camera", ".mp4");
        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(_outputFolder).AsTask(cancellationToken);
        _cameraFile = await folder.CreateFileAsync(
            Path.GetFileName(path),
            CreationCollisionOption.FailIfExists).AsTask(cancellationToken);
        path = _cameraFile.Path;
        _outputFiles.Add(path);
        _cameraStartOffset = _sessionClock.Elapsed.TotalSeconds;
        _cameraStartAttempted = true;
        try
        {
            MediaEncodingProfile profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
            profile.Audio = null;
            await _mediaCapture.StartRecordToStorageFileAsync(profile, _cameraFile)
                .AsTask(cancellationToken);
        }
        catch (Exception ex)
        {
            _cameraError = ex;
            throw CreateCaptureException("The selected camera could not start recording.", ex);
        }
    }

    /// <summary>
    /// Stops all started tracks, awaits capture callbacks, and closes every writer before returning.
    /// A capture or finalization failure throws ScreenRecordingInputException and leaves files in place.
    /// </summary>
    public async Task<ScreenRecordingInputsResult> StopAsync()
    {
        await _stopLock.WaitAsync();
        try
        {
            if (_stopped)
            {
                if (_stopFailure is not null)
                {
                    throw _stopFailure;
                }

                return CreateResult();
            }

            _stopped = true;
            double stopElapsed = _sessionClock.Elapsed.TotalSeconds;
            _sessionStopElapsed = stopElapsed;
            var errors = new List<Exception>();
            // Issue all stop requests together rather than leaving another input running during a timeout.
            Task cameraStop = StopCameraAsync();
            Task[] audioStops = _audioTracks.Values.Select(track => track.StopAsync(stopElapsed)).ToArray();
            foreach (Task stop in audioStops.Append(cameraStop))
            {
                try
                {
                    await stop;
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }

            try
            {
                await DisposeCameraAsync();
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }

            Exception[] captureErrors = GetCaptureErrors()
                .Concat(errors)
                .Distinct()
                .ToArray();
            if (captureErrors.Length > 0)
            {
                _stopFailure = new ScreenRecordingInputException(
                    "One or more screen-recording input tracks did not finish cleanly.",
                    _outputFiles,
                    captureErrors,
                    GetFinalisedTracks());
                throw _stopFailure;
            }

            return CreateResult();
        }
        finally
        {
            _stopLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        Exception? stopError = null;
        try
        {
            await StopAsync();
        }
        catch (Exception ex)
        {
            stopError = ex;
        }

        _disposed = true;
        _stopLock.Dispose();
        if (stopError is not null)
        {
            throw stopError;
        }
    }

    private void Camera_Failed(MediaCapture sender, MediaCaptureFailedEventArgs error)
    {
        Interlocked.CompareExchange(
            ref _cameraError,
            new InvalidOperationException($"Camera capture failed ({error.Code}): {error.Message}"),
            null);
    }

    private void Camera_RecordLimitationExceeded(MediaCapture sender)
    {
        Interlocked.CompareExchange(ref _cameraError,
            new InvalidOperationException("The camera reached its Windows recording limit. The take has been stopped; start a new recording."), null);
    }

    private async Task StopCameraAsync()
    {
        if (!_cameraStartAttempted || _mediaCapture is null)
        {
            return;
        }

        _cameraStopOffset = _sessionClock.Elapsed.TotalSeconds;
        try
        {
            await _mediaCapture.StopRecordAsync();
            _cameraFileFinalised = true;
        }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref _cameraError, ex, null);
            throw;
        }
    }

    private async Task DisposeCameraAsync()
    {
        if (_mediaCapture is null)
        {
            return;
        }

        MediaCapture capture = _mediaCapture;
        _mediaCapture = null;
        capture.Failed -= Camera_Failed;
        capture.RecordLimitationExceeded -= Camera_RecordLimitationExceeded;
        capture.Dispose();
        if (_cameraFile is not null && _cameraFileFinalised)
        {
            var properties = await _cameraFile.Properties.GetVideoPropertiesAsync();
            double duration = properties.Duration.TotalSeconds;
            if (!double.IsFinite(duration) || duration <= 0)
            {
                throw new InvalidDataException("The finalized camera MP4 does not report a valid video duration.");
            }

            double estimatedStart = _cameraStopOffset - duration;
            double startOffset = _cameraStartOffset;
            var warnings = new List<string>
            {
                "Camera timing is estimated from the common-clock stop request and finalized container duration; device timestamps are not shared with desktop capture. The offset remains independently editable.",
            };
            if (_cameraError is not null)
            {
                warnings.Add($"Camera capture reported an error; the finalized MP4 may be a partial track: {_cameraError.Message}");
            }
            if (estimatedStart >= _cameraStartOffset && estimatedStart <= _cameraStopOffset)
            {
                startOffset = estimatedStart;
            }
            else
            {
                warnings.Add("Container duration did not fit inside the measured start/stop interval; the start-call offset was retained.");
            }

            _cameraResult = new ScreenRecordingCameraTrackResult(
                _cameraFile.Path,
                startOffset,
                duration,
                warnings);
        }
    }

    private ScreenRecordingInputsResult CreateResult()
    {
        double elapsed = _sessionStopElapsed ?? _sessionClock.Elapsed.TotalSeconds;
        ScreenRecordingAudioTrackResult[] audio = _audioTracks.Values
            .Where(track => track.IsFinalised)
            .Select(track => track.CreateResult())
            .ToArray();
        return new ScreenRecordingInputsResult(audio, _cameraResult, Math.Max(0, elapsed));
    }

    private string NewOutputPath(string prefix, string extension) =>
        Path.Combine(_outputFolder, $"{prefix}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}{extension}");

    private void EnsureClockRunning()
    {
        if (!_sessionClock.IsRunning)
        {
            throw new InvalidOperationException("Start the shared Stopwatch before starting capture.");
        }
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stopped)
        {
            throw new InvalidOperationException("The recording inputs have already been stopped.");
        }
    }

    private ScreenRecordingInputException CreateCaptureException(string message, Exception error) =>
        new(message, _outputFiles, new[] { error });

    private Exception[] GetCaptureErrors() => _audioTracks.Values
        .Select(track => track.Error)
        .Append(Volatile.Read(ref _cameraError))
        .Where(error => error is not null)
        .Cast<Exception>()
        .Distinct()
        .ToArray();

    private sealed class AudioTrackCapture
    {
        private readonly ScreenRecordingInputs _owner;
        private readonly ScreenRecordingAudioDevice _device;
        private readonly object _sync = new();
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IWaveIn? _capture;
        private WaveFileWriter? _writer;
        private FileStream? _fileStream;
        private WaveFormat? _format;
        private readonly CancellationTokenSource _checkpointCancellation = new();
        private Task? _checkpointTask;
        private long _maximumDataBytes;
        private long _writtenBytes;
        private Exception? _error;
        private bool _started;
        private bool _stopRequested;
        private bool _closing;
        private bool _finalised;
        private bool _resourcesClosed;
        private double _startOffset;
        private double _stopOffset;

        public AudioTrackCapture(ScreenRecordingInputs owner, ScreenRecordingAudioDevice device)
        {
            _owner = owner;
            _device = device;
            Path = owner.NewOutputPath(device.Flow == ScreenRecordingAudioFlow.RenderLoopback ? "SystemAudio" : "Microphone", ".wav");
        }

        public string Path { get; }
        public bool IsFinalised
        {
            get
            {
                lock (_sync)
                {
                    return _finalised;
                }
            }
        }

        public Exception? Error
        {
            get
            {
                lock (_sync)
                {
                    return _error;
                }
            }
        }

        public void Start()
        {
            using var enumerator = new MMDeviceEnumerator();
            using MMDevice endpoint = enumerator.GetDevice(_device.Id);
            _capture = _device.Flow switch
            {
                ScreenRecordingAudioFlow.RenderLoopback => new WasapiLoopbackCapture(endpoint),
                ScreenRecordingAudioFlow.Microphone => new WasapiCapture(endpoint),
                _ => throw new ArgumentOutOfRangeException(nameof(_device.Flow)),
            };

            WaveFormat format = _capture.WaveFormat;
            if (format.Encoding is not (WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Extensible))
            {
                throw new NotSupportedException($"The selected endpoint uses unsupported WAV encoding {format.Encoding}.");
            }

            _fileStream = new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            _writer = new WaveFileWriter(_fileStream, format);
            // The extensible fmt and fact chunks make the header larger than a classic 44-byte PCM header.
            _maximumDataBytes = uint.MaxValue - _fileStream.Position;
            _maximumDataBytes -= _maximumDataBytes % format.BlockAlign;
            _format = format;
            _capture.DataAvailable += Capture_DataAvailable;
            _capture.RecordingStopped += Capture_RecordingStopped;
            _startOffset = _owner._sessionClock.Elapsed.TotalSeconds;
            _stopOffset = _startOffset;
            _capture.StartRecording();
            _started = true;
            _checkpointTask = RunCheckpointsAsync();
        }

        public void RecordError(Exception error)
        {
            lock (_sync)
            {
                _error ??= error;
            }
        }

        public void CloseAfterStartFailure()
        {
            Exception? cleanupError = null;
            if (_capture is not null)
            {
                _capture.DataAvailable -= Capture_DataAvailable;
                _capture.RecordingStopped -= Capture_RecordingStopped;
                try
                {
                    _capture.Dispose();
                }
                catch (Exception ex)
                {
                    cleanupError = ex;
                }

                _capture = null;
            }

            try
            {
                _writer?.Dispose();
            }
            catch (Exception ex)
            {
                cleanupError ??= ex;
            }
            finally
            {
                _writer = null;
                try
                {
                    _fileStream?.Dispose();
                }
                catch (Exception ex)
                {
                    cleanupError ??= ex;
                }
                _fileStream = null;
                _checkpointCancellation.Dispose();
                _resourcesClosed = true;
                _stopped.TrySetResult();
            }

            if (cleanupError is not null)
            {
                throw cleanupError;
            }
        }

        public async Task StopAsync(double stopOffset)
        {
            if (_resourcesClosed)
            {
                if (Error is not null)
                {
                    throw Error;
                }

                return;
            }

            _stopOffset = Math.Max(_startOffset, stopOffset);
            lock (_sync)
            {
                _stopRequested = true;
            }
            _checkpointCancellation.Cancel();
            Exception? stopError = null;
            if (_started && _capture is not null)
            {
                try
                {
                    _capture.StopRecording();
                    await _stopped.Task.WaitAsync(RecordingStoppedTimeout);
                }
                catch (Exception ex)
                {
                    stopError = ex;
                    RecordError(ex);
                }
            }

            if (_checkpointTask is not null)
            {
                await _checkpointTask;
            }

            lock (_sync)
            {
                _closing = true;
            }

            if (stopError is null)
            {
                lock (_sync)
                {
                    if (_writer is not null)
                    {
                        try
                        {
                            long desiredBytes = FramesToBytes(_stopOffset - _startOffset);
                            WriteSilence(Math.Max(0, desiredBytes - _writtenBytes));
                        }
                        catch (Exception ex)
                        {
                            _error ??= ex;
                        }
                    }
                }
            }

            Exception? disposeError = null;
            if (_capture is not null)
            {
                _capture.DataAvailable -= Capture_DataAvailable;
                _capture.RecordingStopped -= Capture_RecordingStopped;
                try
                {
                    _capture.Dispose();
                }
                catch (Exception ex)
                {
                    disposeError = ex;
                }
                _capture = null;
            }

            if (_writer is not null)
            {
                try
                {
                    _writer.Flush();
                    _fileStream?.Flush(flushToDisk: true);
                }
                catch (Exception ex)
                {
                    disposeError ??= ex;
                }

                try
                {
                    _writer.Dispose();
                    lock (_sync)
                    {
                        _finalised = true;
                    }
                }
                catch (Exception ex)
                {
                    disposeError ??= ex;
                }
                _writer = null;
            }

            try
            {
                _fileStream?.Dispose();
            }
            catch (Exception ex)
            {
                disposeError ??= ex;
            }
            _fileStream = null;
            if (_started)
            {
                _checkpointCancellation.Dispose();
            }
            _resourcesClosed = true;

            if (disposeError is not null)
            {
                RecordError(disposeError);
            }

            if (Error is not null)
            {
                throw Error;
            }
        }

        public ScreenRecordingAudioTrackResult CreateResult()
        {
            WaveFormat format = _format ?? throw new InvalidOperationException("The audio track format was not initialized.");
            long bytes = _writtenBytes;
            var warnings = new List<string>
            {
                "Audio start offset is measured immediately before the capture start call; device startup latency may add an unreported offset.",
                "Audio block timestamps are estimated from callback delivery time; inserted silence aligns observed gaps and trailing duration to the shared Stopwatch.",
            };
            if (Error is not null)
            {
                warnings.Add($"Capture ended with an error; this finalized WAV may be a partial track: {Error.Message}");
            }
            if (bytes >= _maximumDataBytes)
            {
                warnings.Add("The WAV RIFF size limit was reached; capture was stopped before the file could overflow.");
            }

            return new ScreenRecordingAudioTrackResult(
                Path,
                _device.Flow,
                _startOffset,
                format.AverageBytesPerSecond > 0 ? bytes / (double)format.AverageBytesPerSecond : 0,
                bytes,
                warnings);
        }

        public ScreenRecordingAudioTrackSnapshot CreateSnapshot(double elapsed)
        {
            long bytes;
            lock (_sync)
            {
                bytes = _writtenBytes;
            }

            return new ScreenRecordingAudioTrackSnapshot(
                Path,
                _device.Flow,
                _startOffset,
                Math.Max(0, elapsed - _startOffset),
                bytes,
                !_stopped.Task.IsCompleted);
        }

        private void Capture_DataAvailable(object? sender, WaveInEventArgs args)
        {
            bool stopCapture = false;
            lock (_sync)
            {
                if (_writer is null || _error is not null || _closing)
                {
                    return;
                }

                try
                {
                    int blockAlign = _writer.WaveFormat.BlockAlign;
                    if (args.BytesRecorded % blockAlign != 0)
                    {
                        throw new InvalidDataException("The audio endpoint returned a partial sample frame.");
                    }
                    int incomingBytes = args.BytesRecorded - (args.BytesRecorded % blockAlign);
                    long expectedEndBytes = FramesToBytes(Math.Max(0, _owner._sessionClock.Elapsed.TotalSeconds - _startOffset));
                    long gap = Math.Max(0, expectedEndBytes - _writtenBytes - incomingBytes);
                    WriteSilence(gap);
                    WriteCaptureBytes(args.Buffer, incomingBytes);
                }
                catch (Exception ex)
                {
                    _error ??= ex;
                    stopCapture = true;
                }
            }

            if (stopCapture)
            {
                RequestFailureStop();
            }
        }

        private async Task RunCheckpointsAsync()
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                while (await timer.WaitForNextTickAsync(_checkpointCancellation.Token))
                {
                    lock (_sync)
                    {
                        if (_writer is null || _closing || _error is not null)
                        {
                            return;
                        }

                        // NAudio 2.2.1 Flush updates RIFF/data/fact headers. Flush the underlying
                        // stream afterwards too, because the last header writes are otherwise buffered.
                        _writer.Flush();
                        _fileStream!.Flush(flushToDisk: true);
                    }
                }
            }
            catch (OperationCanceledException) when (_checkpointCancellation.IsCancellationRequested)
            {
                // Expected when StopAsync closes this track.
            }
            catch (Exception ex)
            {
                RecordError(ex);
                RequestFailureStop();
            }
        }

        private void RequestFailureStop()
        {
            IWaveIn? capture = _capture;
            if (capture is null)
            {
                return;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    capture.StopRecording();
                }
                catch (Exception ex)
                {
                    RecordError(ex);
                }
            });
        }

        private void Capture_RecordingStopped(object? sender, StoppedEventArgs args)
        {
            lock (_sync)
            {
                if (args.Exception is not null)
                {
                    _error ??= args.Exception;
                }
                else if (!_stopRequested && _error is null)
                {
                    _error = new InvalidOperationException("The audio endpoint stopped unexpectedly.");
                }
            }

            _stopped.TrySetResult();
        }

        private long FramesToBytes(double seconds)
        {
            WaveFormat format = _writer?.WaveFormat ?? _capture?.WaveFormat
                ?? throw new InvalidOperationException("The audio track has no active format.");
            double frames = Math.Max(0, seconds) * format.SampleRate;
            double bytes = Math.Floor(frames) * format.BlockAlign;
            if (bytes > _maximumDataBytes)
            {
                throw new IOException("The WAV RIFF size limit was reached while aligning track duration.");
            }

            return (long)bytes;
        }

        private void WriteCaptureBytes(byte[] buffer, int count)
        {
            if (count <= 0)
            {
                return;
            }

            long available = _maximumDataBytes - _writtenBytes;
            int writable = (int)Math.Min(count, Math.Max(0, available));
            writable -= writable % _writer!.WaveFormat.BlockAlign;
            if (writable > 0)
            {
                _writer.Write(buffer, 0, writable);
                _writtenBytes += writable;
            }

            if (writable != count)
            {
                throw new IOException("The WAV RIFF size limit was reached; capture was stopped to prevent file overflow.");
            }
        }

        private void WriteSilence(long count)
        {
            if (count <= 0)
            {
                return;
            }

            int blockAlign = _writer!.WaveFormat.BlockAlign;
            count -= count % blockAlign;
            long available = _maximumDataBytes - _writtenBytes;
            if (count > available)
            {
                count = Math.Max(0, available - available % blockAlign);
                throw new IOException("The WAV RIFF size limit was reached while inserting timeline silence.");
            }

            const int chunkSize = 64 * 1024;
            byte[] silence = new byte[Math.Min(chunkSize, (int)Math.Min(count, int.MaxValue))];
            Array.Resize(ref silence, silence.Length - silence.Length % blockAlign);
            if (_writer.WaveFormat.Encoding == WaveFormatEncoding.Pcm && _writer.WaveFormat.BitsPerSample == 8)
            {
                Array.Fill(silence, (byte)128);
            }

            while (count > 0)
            {
                int chunk = (int)Math.Min(count, silence.Length);
                _writer.Write(silence, 0, chunk);
                _writtenBytes += chunk;
                count -= chunk;
            }
        }
    }
}
