using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace InkMotion;

/// <summary>A bounded, local-only PCM queue. Underruns produce silence; overruns stop monitoring rather than accumulating latency.</summary>
internal sealed class AudioMonitorBuffer : IWaveProvider
{
    private readonly BufferedWaveProvider _buffer;
    private readonly object _sync = new();

    public AudioMonitorBuffer(WaveFormat format)
    {
        _buffer = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromMilliseconds(250),
            DiscardOnBufferOverflow = false,
            ReadFully = true,
        };
    }

    public WaveFormat WaveFormat => _buffer.WaveFormat;

    public void Add(byte[] samples, int count)
    {
        lock (_sync)
        {
            if (count % WaveFormat.BlockAlign != 0)
                throw new InvalidDataException("The audio input returned an incomplete sample frame.");
            if (count > _buffer.BufferLength - _buffer.BufferedBytes)
                throw new InvalidOperationException("The monitoring buffer overran. Stop other audio work and restart monitoring.");
            _buffer.AddSamples(samples, 0, count);
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        lock (_sync)
        {
            return _buffer.Read(buffer, offset, count);
        }
    }
}

internal sealed class AudioInputMonitor : IDisposable
{
    private readonly WaveInEvent _capture;
    private readonly WaveOutEvent _output;
    private readonly AudioMonitorBuffer _buffer;
    private int _stopped;

    public AudioInputMonitor(int inputDevice, int outputDevice, int sampleRate, int channels, double gainDecibels)
    {
        if (!double.IsFinite(gainDecibels) || gainDecibels is < -60 or > 0)
            throw new ArgumentOutOfRangeException(nameof(gainDecibels));
        _capture = new WaveInEvent
        {
            DeviceNumber = inputDevice,
            WaveFormat = new WaveFormat(sampleRate, 16, Math.Clamp(channels, 1, 2)),
            BufferMilliseconds = 20,
            NumberOfBuffers = 3,
        };
        _output = new WaveOutEvent { DeviceNumber = outputDevice, DesiredLatency = 60, NumberOfBuffers = 3 };
        _buffer = new AudioMonitorBuffer(_capture.WaveFormat);
        _capture.DataAvailable += DataAvailable;
        _capture.RecordingStopped += CaptureStopped;
        _output.PlaybackStopped += OutputStopped;
        GainDecibels = gainDecibels;
    }

    public double GainDecibels { get; }
    public event Action<AudioInputMonitor, string>? Failed;

    public void Start()
    {
        var gain = new VolumeSampleProvider(_buffer.ToSampleProvider())
        {
            Volume = (float)AudioClipEditing.DecibelsToLinear(GainDecibels),
        };
        _output.Init(gain);
        _output.Play();
        _capture.StartRecording();
    }

    private void DataAvailable(object? sender, WaveInEventArgs e)
    {
        if (Volatile.Read(ref _stopped) != 0) return;
        try
        {
            _buffer.Add(e.Buffer, e.BytesRecorded);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
        {
            Fail(ex.Message);
        }
    }

    private void CaptureStopped(object? sender, StoppedEventArgs e) =>
        Fail(e.Exception?.Message ?? "The input device stopped.");

    private void OutputStopped(object? sender, StoppedEventArgs e) =>
        Fail(e.Exception?.Message ?? "The output device stopped.");

    private void Fail(string message)
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 0) Failed?.Invoke(this, message);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _stopped, 1);
        _capture.DataAvailable -= DataAvailable;
        _capture.RecordingStopped -= CaptureStopped;
        _output.PlaybackStopped -= OutputStopped;
        try
        {
            _capture.Dispose();
        }
        finally
        {
            _output.Dispose();
        }
    }
}
