using NAudio.Wave;

namespace InkMotion;

public sealed class AudioClipSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly Action<double>? _seekSource;
    private readonly double _sourceDurationSeconds;
    private readonly object _sync = new();
    private AudioClipGainSettings _settings;
    private Func<double, double>? _gainEnvelope;
    private long _framePosition;

    public AudioClipSampleProvider(
        ISampleProvider source,
        double sourceDurationSeconds,
        AudioClipGainSettings settings,
        Action<double>? seekSource = null,
        Func<double, double>? gainEnvelope = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);
        _source = source;
        _sourceDurationSeconds = sourceDurationSeconds;
        _settings = settings;
        _seekSource = seekSource;
        _gainEnvelope = gainEnvelope;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public double PositionSeconds
    {
        get
        {
            lock (_sync)
            {
                return (double)_framePosition / WaveFormat.SampleRate;
            }
        }
    }

    public void UpdateSettings(AudioClipGainSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Volatile.Write(ref _settings, settings);
    }

    public void UpdateGainEnvelope(Func<double, double>? gainEnvelope) =>
        Volatile.Write(ref _gainEnvelope, gainEnvelope);

    public void Seek(double sourceSeconds)
    {
        lock (_sync)
        {
            double clamped = Math.Max(0, sourceSeconds);
            _seekSource?.Invoke(clamped);
            _framePosition = (long)Math.Round(clamped * WaveFormat.SampleRate);
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        lock (_sync)
        {
            int channels = WaveFormat.Channels;
            int sampleRate = WaveFormat.SampleRate;
            int requested = count - count % channels;
            int read = 0;
            while (read < requested)
            {
                int chunk = _source.Read(buffer, offset + read, requested - read);
                if (chunk <= 0)
                {
                    break;
                }

                read += chunk;
            }

            Array.Clear(buffer, offset + read, requested - read);
            AudioClipGainSettings settings = Volatile.Read(ref _settings);
            Func<double, double>? gainEnvelope = Volatile.Read(ref _gainEnvelope);
            for (int sample = 0; sample < requested; sample += channels, _framePosition++)
            {
                double sourceSeconds = (double)_framePosition / sampleRate;
                float gain = (float)(settings.GetGain(sourceSeconds, _sourceDurationSeconds) *
                    (gainEnvelope?.Invoke(sourceSeconds) ?? 1));
                for (int channel = 0; channel < channels; channel++)
                {
                    int index = offset + sample + channel;
                    buffer[index] = Math.Clamp(buffer[index] * gain, -1f, 1f);
                }
            }

            return requested;
        }
    }
}

public sealed class AudioClipPlayer : IDisposable
{
    private const int OutputLatencyMilliseconds = 100;
    private readonly AudioFileReader _reader;
    private readonly string _audioPath;
    private readonly AudioClipSampleProvider _clip;
    private readonly TimeStretchSampleProvider _stretch;
    private readonly AudioEffectsSampleProvider _effects;
    private readonly ChannelMixingSampleProvider _monitorMix;
    private readonly AudioMeterSampleProvider _meter;
    private readonly AudioPreviewBus _previewBus;
    private readonly AudioPreviewInput _previewInput;
    private bool _disposed;
    private bool _soloSuppressed;
    private AudioClipGainSettings _gainSettings;
    private double _tempoRatio;
    private double _pitchSemitones;

    public AudioClipPlayer(
        string audioPath,
        AudioTrackProject project,
        AudioChannelLayout mixLayout,
        AudioPreviewBus previewBus,
        Func<double, double>? gainEnvelope = null)
    {
        ArgumentNullException.ThrowIfNull(previewBus);
        _reader = new AudioFileReader(audioPath);
        _audioPath = Path.GetFullPath(audioPath);
        _previewBus = previewBus;
        _gainSettings = AudioClipEditing.CreateGainSettings(project);
        try
        {
            DurationSeconds = AudioSourcePosition.DurationSeconds(_reader);
            ChannelCount = _reader.WaveFormat.Channels;
            _clip = new AudioClipSampleProvider(
                _reader,
                DurationSeconds,
                _gainSettings,
                seconds => AudioSourcePosition.Seek(_reader, seconds),
                gainEnvelope);
            _stretch = new TimeStretchSampleProvider(_clip, _clip.Seek);
            _tempoRatio = AudioClipEditing.GetTempoRatio(project);
            _pitchSemitones = project.PitchSemitones;
            _stretch.SetParameters(_tempoRatio, _pitchSemitones);
            _effects = new AudioEffectsSampleProvider(_stretch, GetCompatibleEffects(project.Effects));
            _effects.SetSuppressed(_gainSettings.LinearGain == 0);
            _monitorMix = new ChannelMixingSampleProvider(_effects, CreateMonitorMatrix(ChannelCount, mixLayout,
                project.Balance, project.SpatialPanEnabled, project.SpatialPanAzimuthDegrees));
            _meter = new AudioMeterSampleProvider(_monitorMix);
            _previewInput = _previewBus.AddInput(_meter);
        }
        catch
        {
            _reader.Dispose();
            throw;
        }
    }

    public double DurationSeconds { get; }

    public int ChannelCount { get; }

    public int SourceSampleRate => _reader.WaveFormat.SampleRate;

    public long SourceSampleFrames => _reader.Length / _reader.WaveFormat.BlockAlign;

    public bool IsPlaying => !_disposed && _previewInput.IsEnabled && _previewBus.IsPlaying;

    public AudioMeterReading TakeMeterReading(bool resetClip = false) => _meter.TakeReading(resetClip);

    public double AudiblePositionSeconds =>
        Math.Max(0, _stretch.PositionSeconds - (IsPlaying ? OutputLatencyMilliseconds / 2000d * _stretch.TempoRatio : 0));

    /// <summary>
    /// Builds the monitoring matrix: the source is mapped into the project mix layout and then folded down
    /// to the stereo output device, so surround mixes can be auditioned on headphones or stereo speakers.
    /// </summary>
    public static float[,] CreateMonitorMatrix(
        int sourceChannelCount, AudioChannelLayout mixLayout, double balance = 0,
        bool spatialPanEnabled = false, double spatialPanAzimuthDegrees = 0) =>
        AudioChannelLayouts.Multiply(
            AudioChannelLayouts.CreateMixMatrix(mixLayout, AudioChannelLayout.Stereo),
            AudioMixControls.CreateMatrix(sourceChannelCount, mixLayout, balance,
                spatialPanEnabled, spatialPanAzimuthDegrees));

    public void Update(AudioTrackProject project, AudioChannelLayout mixLayout)
    {
        _gainSettings = AudioClipEditing.CreateGainSettings(project);
        _clip.UpdateSettings(_soloSuppressed ? _gainSettings with { LinearGain = 0 } : _gainSettings);
        double tempoRatio = AudioClipEditing.GetTempoRatio(project);
        if (_tempoRatio != tempoRatio || _pitchSemitones != project.PitchSemitones)
        {
            _tempoRatio = tempoRatio;
            _pitchSemitones = project.PitchSemitones;
            _stretch.SetParameters(tempoRatio, _pitchSemitones);
            _previewInput.Reset();
        }
        _effects.Update(GetCompatibleEffects(project.Effects));
        _effects.SetSuppressed(_soloSuppressed || _gainSettings.LinearGain == 0);
        _monitorMix.UpdateMatrix(CreateMonitorMatrix(ChannelCount, mixLayout, project.Balance,
            project.SpatialPanEnabled, project.SpatialPanAzimuthDegrees));
    }

    public void UpdateGainEnvelope(Func<double, double>? gainEnvelope) =>
        _clip.UpdateGainEnvelope(gainEnvelope);

    public void SetSoloSuppressed(bool suppressed)
    {
        if (_soloSuppressed == suppressed)
        {
            return;
        }

        _soloSuppressed = suppressed;
        _clip.UpdateSettings(suppressed ? _gainSettings with { LinearGain = 0 } : _gainSettings);
        _effects.SetSuppressed(suppressed || _gainSettings.LinearGain == 0);
    }

    public void Seek(double sourceSeconds)
    {
        _stretch.Seek(sourceSeconds);
        _effects.Reset();
        _previewInput.Reset();
    }

    public void Play()
    {
        if (!_disposed)
        {
            _previewInput.SetEnabled(true);
        }
    }

    public void Pause()
    {
        if (!_disposed)
        {
            _previewInput.SetEnabled(false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _disposed = true;
        _previewInput.Dispose();
        _reader.Dispose();
    }

    private AudioEffectSettings GetCompatibleEffects(AudioEffectSettings settings) =>
        settings.NoiseReductionEnabled &&
        settings.NoiseProfile is AudioNoiseProfile profile &&
        !profile.MatchesSource(_audioPath, _reader.WaveFormat.SampleRate, _reader.WaveFormat.Channels)
            ? settings with { NoiseReductionEnabled = false }
            : settings;
}