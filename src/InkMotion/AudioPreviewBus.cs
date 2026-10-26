using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace InkMotion;

public sealed class AudioPreviewBus : ISampleProvider, IDisposable
{
    public const int SampleRate = 48000;
    public const int ChannelCount = 2;
    private readonly MixingSampleProvider _mixer;
    private readonly AudioMeterSampleProvider _meter;
    private readonly List<AudioPreviewInput> _inputs = [];
    private readonly object _sync = new();
    private WaveOutEvent? _output;
    private bool _disposed;

    public AudioPreviewBus()
    {
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, ChannelCount);
        _mixer = new MixingSampleProvider(WaveFormat) { ReadFully = true };
        _meter = new AudioMeterSampleProvider(_mixer);
    }

    public WaveFormat WaveFormat { get; }

    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;

    public event EventHandler<StoppedEventArgs>? PlaybackStopped;

    public AudioPreviewInput AddInput(ISampleProvider source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (source.WaveFormat.Channels != WaveFormat.Channels)
        {
            throw new ArgumentException("Preview bus inputs must be stereo.", nameof(source));
        }

        var input = new AudioPreviewInput(this, source, WaveFormat);
        lock (_sync)
        {
            _mixer.AddMixerInput(input);
            _inputs.Add(input);
        }

        return input;
    }

    public int Read(float[] buffer, int offset, int count) => _meter.Read(buffer, offset, count);

    public AudioMeterReading TakeMasterReading(bool resetClip = false) => _meter.TakeReading(resetClip);

    public int CopyRecentSamples(float[] destination) => _meter.CopyRecentSamples(destination);

    public void Play()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_output is null)
        {
            var output = new WaveOutEvent { DesiredLatency = 100, NumberOfBuffers = 2 };
            output.PlaybackStopped += Output_PlaybackStopped;
            try
            {
                output.Init(this);
            }
            catch
            {
                output.PlaybackStopped -= Output_PlaybackStopped;
                output.Dispose();
                throw;
            }

            _output = output;
        }

        if (_output.PlaybackState != PlaybackState.Playing)
        {
            try
            {
                _output.Play();
            }
            catch
            {
                _output.PlaybackStopped -= Output_PlaybackStopped;
                _output.Dispose();
                _output = null;
                throw;
            }
        }
    }

    public void Pause()
    {
        if (_output?.PlaybackState == PlaybackState.Playing)
        {
            _output.Pause();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _output?.Stop();
        if (_output is not null)
        {
            _output.PlaybackStopped -= Output_PlaybackStopped;
            _output.Dispose();
            _output = null;
        }

        lock (_sync)
        {
            foreach (AudioPreviewInput input in _inputs.ToArray())
            {
                input.Disable();
                _mixer.RemoveMixerInput(input);
                input.Detach();
            }

            _inputs.Clear();
        }
    }

    internal void RemoveInput(AudioPreviewInput input)
    {
        lock (_sync)
        {
            if (_inputs.Remove(input))
            {
                _mixer.RemoveMixerInput(input);
                input.Detach();
            }
        }
    }

    private void Output_PlaybackStopped(object? sender, StoppedEventArgs e) => PlaybackStopped?.Invoke(this, e);
}

public sealed class AudioPreviewInput : ISampleProvider, IDisposable
{
    private readonly ISampleProvider _source;
    private readonly AudioPreviewBus _bus;
    private readonly object _sync = new();
    private ISampleProvider _resampled;
    private int _enabled;
    private bool _detached;

    internal AudioPreviewInput(AudioPreviewBus bus, ISampleProvider source, WaveFormat waveFormat)
    {
        _bus = bus;
        _source = source;
        _resampled = CreateResampler(source, waveFormat.SampleRate);
        WaveFormat = waveFormat;
    }

    public WaveFormat WaveFormat { get; }

    public bool IsEnabled => Volatile.Read(ref _enabled) != 0;

    public void SetEnabled(bool enabled)
    {
        lock (_sync)
        {
            if (!_detached)
            {
                Volatile.Write(ref _enabled, enabled ? 1 : 0);
            }
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            if (!_detached)
            {
                _resampled = CreateResampler(_source, WaveFormat.SampleRate);
            }
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int requested = count - count % WaveFormat.Channels;
        lock (_sync)
        {
            if (_detached || !IsEnabled)
            {
                Array.Clear(buffer, offset, requested);
                return requested;
            }

            int read = _resampled.Read(buffer, offset, requested);
            if (read < requested)
            {
                Array.Clear(buffer, offset + Math.Max(0, read), requested - Math.Max(0, read));
            }

            return requested;
        }
    }

    public void Dispose() => _bus.RemoveInput(this);

    internal void Disable()
    {
        lock (_sync)
        {
            Volatile.Write(ref _enabled, 0);
        }
    }

    internal void Detach()
    {
        lock (_sync)
        {
            _detached = true;
            Volatile.Write(ref _enabled, 0);
        }
    }

    private static ISampleProvider CreateResampler(ISampleProvider source, int sampleRate) =>
        source.WaveFormat.SampleRate == sampleRate
            ? source
            : new WdlResamplingSampleProvider(source, sampleRate);
}
