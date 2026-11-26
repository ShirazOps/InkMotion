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

        var input = new AudioPreviewInput(RemoveInput, source, WaveFormat);
        lock (_sync)
        {
            _mixer.AddMixerInput(input);
            _inputs.Add(input);
        }

        return input;
    }

    public int Read(float[] buffer, int offset, int count) => _meter.Read(buffer, offset, count);

    /// <summary>
    /// Creates a submix that sums inputs in <paramref name="layout"/>, applies bus effects and gain, then routes to its
    /// parent or folds the result to stereo for the master mix.
    /// </summary>
    public AudioPreviewSubmix CreateSubmix(AudioChannelLayout layout, AudioEffectSettings effects, double gainDecibels,
        AudioPreviewSubmix? parent = null, IReadOnlyList<AudioVst3PluginProject>? vst3Plugins = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new AudioPreviewSubmix(this, layout, effects, gainDecibels, parent, vst3Plugins);
    }

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
    private readonly Action<AudioPreviewInput> _remove;
    private readonly object _sync = new();
    private ISampleProvider _resampled;
    private int _enabled;
    private bool _detached;

    internal AudioPreviewInput(Action<AudioPreviewInput> remove, ISampleProvider source, WaveFormat waveFormat)
    {
        _remove = remove;
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

    public void Dispose() => _remove(this);

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

public sealed class AudioPreviewSubmix : IDisposable
{
    private readonly MixingSampleProvider _mixer;
    private readonly AudioEffectsSampleProvider _effects;
    private readonly Vst3InsertChainSampleProvider _vst3;
    private readonly VolumeSampleProvider _gain;
    private readonly AudioPreviewInput _outputInput;
    private readonly List<AudioPreviewInput> _inputs = [];
    private readonly object _sync = new();
    private bool _disposed;

    internal AudioPreviewSubmix(AudioPreviewBus bus, AudioChannelLayout layout, AudioEffectSettings effects,
        double gainDecibels, AudioPreviewSubmix? parent, IReadOnlyList<AudioVst3PluginProject>? vst3Plugins)
    {
        Layout = layout;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(AudioPreviewBus.SampleRate, AudioChannelLayouts.Get(layout).ChannelCount);
        _mixer = new MixingSampleProvider(WaveFormat) { ReadFully = true };
        _effects = new AudioEffectsSampleProvider(_mixer, effects);
        _vst3 = new Vst3InsertChainSampleProvider(_effects, vst3Plugins);
        _gain = new VolumeSampleProvider(_vst3);
        SetGain(gainDecibels);
        var fold = new ChannelMixingSampleProvider(_gain, AudioChannelLayouts.CreateMixMatrix(layout, AudioChannelLayout.Stereo));
        _outputInput = parent?.AddInput(_gain) ?? bus.AddInput(fold);
        _outputInput.SetEnabled(true);
    }

    public AudioChannelLayout Layout { get; }

    public WaveFormat WaveFormat { get; }

    public string? Vst3FailureMessage => _vst3.FailureMessage;

    public IReadOnlyList<Vst3Parameter> GetVst3Parameters(string pluginId) =>
        _vst3.GetParameters(pluginId);

    public AudioPreviewInput AddInput(ISampleProvider source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (source.WaveFormat.Channels != WaveFormat.Channels)
        {
            throw new ArgumentException("Submix inputs must use the submix channel layout.", nameof(source));
        }

        var input = new AudioPreviewInput(RemoveInput, source, WaveFormat);
        lock (_sync)
        {
            _mixer.AddMixerInput(input);
            _inputs.Add(input);
        }

        return input;
    }

    public void Update(AudioEffectSettings effects, double gainDecibels)
    {
        _effects.Update(effects);
        SetGain(gainDecibels);
    }

    public void Update(
        AudioEffectSettings effects,
        double gainDecibels,
        IReadOnlyList<AudioVst3PluginProject> vst3Plugins)
    {
        _effects.Update(effects);
        _vst3.Update(vst3Plugins);
        SetGain(gainDecibels);
    }

    /// <summary>Clears delay and reverb state, for example after seeking.</summary>
    public void Reset() => _effects.Reset();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _outputInput.Dispose();
        _vst3.Dispose();
        lock (_sync)
        {
            foreach (AudioPreviewInput input in _inputs.ToArray())
            {
                _mixer.RemoveMixerInput(input);
                input.Detach();
            }

            _inputs.Clear();
        }
    }

    private void SetGain(double gainDecibels) => _gain.Volume = (float)Math.Pow(10, gainDecibels / 20);

    private void RemoveInput(AudioPreviewInput input)
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
}