using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace InkMotion;

/// <summary>Linked-channel 4×, 48-tap windowed-sinc detection, 48-frame look-ahead and configurable release.</summary>
public sealed class TruePeakLimiterSampleProvider : ISampleProvider
{
    private const int OversamplingFactor = 4;
    private const int TapCount = 48;
    private const int FirstTapOffset = -23;
    private const int GainLookAheadFrames = 24;
    private const int LookAheadFrames = 48;
    private const int HistoryFrames = 24;
    private static readonly double[,] InterpolationCoefficients = CreateInterpolationCoefficients();
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private double _ceiling;
    private double _release;
    private bool _enabled = true;
    private readonly float[] _readBuffer;
    private readonly float[] _history;
    private readonly List<float> _pending = [];
    private readonly List<double> _peaks = [];
    private double _gain = 1;
    private bool _sourceEnded;

    public TruePeakLimiterSampleProvider(ISampleProvider source, double ceilingDecibels = -1, double releaseMilliseconds = 100)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _channels = source.WaveFormat.Channels;
        if (_channels < 1)
        {
            throw new ArgumentException("The source must contain at least one channel.", nameof(source));
        }

        Configure(true, ceilingDecibels, releaseMilliseconds);
        _readBuffer = new float[4096 * _channels];
        _history = new float[HistoryFrames * _channels];
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    internal void Configure(bool enabled, double ceilingDecibels, double releaseMilliseconds)
    {
        if (!double.IsFinite(ceilingDecibels) || ceilingDecibels is < -24 or > 0)
            throw new ArgumentOutOfRangeException(nameof(ceilingDecibels));
        if (!double.IsFinite(releaseMilliseconds) || releaseMilliseconds is < 5 or > 2000)
            throw new ArgumentOutOfRangeException(nameof(releaseMilliseconds));
        _enabled = enabled;
        // Reserve 0.8 dB for peaks between the 4x grid points and gain-envelope reconstruction.
        _ceiling = Math.Pow(10, (ceilingDecibels - 0.8) / 20);
        _release = Math.Exp(-1 / (releaseMilliseconds * 0.001 * WaveFormat.SampleRate));
        if (!enabled) _gain = 1;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        int requestedFrames = count / _channels;
        if (requestedFrames <= 0)
        {
            return 0;
        }

        int lookAhead = _enabled ? LookAheadFrames : 0;
        BufferFrames(checked(requestedFrames + lookAhead));
        int availableFrames = _pending.Count / _channels;
        int outputFrames = _sourceEnded
            ? Math.Min(requestedFrames, availableFrames)
            : Math.Min(requestedFrames, Math.Max(0, availableFrames - lookAhead));
        if (_enabled)
        {
            for (int frame = _peaks.Count; frame < outputFrames + GainLookAheadFrames; frame++)
                _peaks.Add(EstimatePeak(frame, availableFrames));
        }
        for (int frame = 0; frame < outputFrames; frame++)
        {
            double peak = 0;
            if (_enabled)
            {
                for (int future = frame; future <= frame + GainLookAheadFrames; future++)
                    peak = Math.Max(peak, _peaks[future]);
            }
            double target = peak > _ceiling ? _ceiling / peak : 1;
            _gain = Math.Min(target, _release * _gain + (1 - _release));

            int sampleIndex = frame * _channels;
            for (int channel = 0; channel < _channels; channel++)
            {
                float sample = _pending[sampleIndex + channel];
                buffer[offset + sampleIndex + channel] = (float)(sample * _gain);
            }
        }

        int samplesRead = outputFrames * _channels;
        if (samplesRead > 0)
        {
            int retained = Math.Min(samplesRead, _history.Length);
            Array.Copy(_history, retained, _history, 0, _history.Length - retained);
            _pending.CopyTo(samplesRead - retained, _history, _history.Length - retained, retained);
            _pending.RemoveRange(0, samplesRead);
            if (_enabled) _peaks.RemoveRange(0, outputFrames);
            else _peaks.Clear();
        }

        return samplesRead;
    }

    private void BufferFrames(int targetFrames)
    {
        int targetSamples = checked(targetFrames * _channels);
        while (_pending.Count < targetSamples && !_sourceEnded)
        {
            int samplesToRead = Math.Min(_readBuffer.Length, targetSamples - _pending.Count);
            samplesToRead -= samplesToRead % _channels;
            if (samplesToRead == 0)
            {
                samplesToRead = _channels;
            }

            int read = _source.Read(_readBuffer, 0, samplesToRead);
            if (read <= 0)
            {
                _sourceEnded = true;
                break;
            }
            if (read % _channels != 0)
            {
                throw new InvalidDataException("The audio source returned an incomplete sample frame.");
            }

            for (int index = 0; index < read; index++)
            {
                _pending.Add(_readBuffer[index]);
            }
        }
    }

    private double EstimatePeak(int frame, int availableFrames)
    {
        double peak = 0;
        for (int channel = 0; channel < _channels; channel++)
        {
            double first = GetSample(frame, channel, availableFrames);
            double second = GetSample(frame + 1, channel, availableFrames);
            peak = Math.Max(peak, Math.Max(Math.Abs(first), Math.Abs(second)));
            for (int phase = 1; phase < OversamplingFactor; phase++)
            {
                double interpolated = 0;
                for (int tap = 0; tap < TapCount; tap++)
                {
                    interpolated += GetSample(frame + FirstTapOffset + tap, channel, availableFrames) *
                        InterpolationCoefficients[phase, tap];
                }

                peak = Math.Max(peak, Math.Abs(interpolated));
            }
        }

        return peak;
    }

    private double GetSample(int frame, int channel, int availableFrames)
    {
        if (frame < 0)
        {
            int historyFrame = Math.Max(0, HistoryFrames + frame);
            return _history[historyFrame * _channels + channel];
        }

        return frame < availableFrames ? _pending[frame * _channels + channel] : 0;
    }

    private static double[,] CreateInterpolationCoefficients()
    {
        var coefficients = new double[OversamplingFactor, TapCount];
        for (int phase = 1; phase < OversamplingFactor; phase++)
        {
            double fraction = phase / (double)OversamplingFactor;
            double sum = 0;
            for (int tap = 0; tap < TapCount; tap++)
            {
                double distance = FirstTapOffset + tap - fraction;
                double absoluteDistance = Math.Abs(distance);
                double window = absoluteDistance >= 24
                    ? 0
                    : 0.42 + 0.5 * Math.Cos(Math.PI * distance / 24) + 0.08 * Math.Cos(Math.PI * distance / 12);
                double sinc = absoluteDistance < 1e-12 ? 1 : Math.Sin(Math.PI * distance) / (Math.PI * distance);
                coefficients[phase, tap] = sinc * window;
                sum += coefficients[phase, tap];
            }

            for (int tap = 0; tap < TapCount; tap++)
            {
                coefficients[phase, tap] /= sum;
            }
        }

        return coefficients;
    }
}

/// <summary>Post-insert limiter stage shared by track/bus preview and export. Legacy effects keep their sample limiter.</summary>
internal sealed class AudioTruePeakStage : ISampleProvider
{
    private TruePeakLimiterSampleProvider _limiter;
    private readonly ISampleProvider _source;
    private readonly int _sampleRate;
    private AudioEffectSettings _settings;
    private readonly object _sync = new();
    private bool _suppressed;

    public AudioTruePeakStage(ISampleProvider source, AudioEffectSettings settings, int sampleRate = 0)
    {
        _source = source;
        _sampleRate = sampleRate == 0 ? source.WaveFormat.SampleRate : sampleRate;
        _settings = settings;
        _limiter = CreateLimiter();
        Update(settings);
    }

    public WaveFormat WaveFormat => _limiter.WaveFormat;

    public void Update(AudioEffectSettings settings)
    {
        lock (_sync)
        {
            _settings = settings;
            _limiter.Configure(settings.Enabled && settings.LimiterEnabled && settings.LimiterTruePeak,
                settings.LimiterCeilingDecibels, settings.LimiterReleaseMilliseconds);
        }
    }

    public void Reset()
    {
        lock (_sync) ResetCore();
    }

    private TruePeakLimiterSampleProvider CreateLimiter() => new(
        _source.WaveFormat.SampleRate == _sampleRate ? _source : new WdlResamplingSampleProvider(_source, _sampleRate));

    private void ResetCore()
    {
        _limiter = CreateLimiter();
        _limiter.Configure(_settings.Enabled && _settings.LimiterEnabled && _settings.LimiterTruePeak,
            _settings.LimiterCeilingDecibels, _settings.LimiterReleaseMilliseconds);
    }

    public void SetSuppressed(bool suppressed)
    {
        lock (_sync)
        {
            if (_suppressed == suppressed) return;
            _suppressed = suppressed;
            ResetCore();
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        lock (_sync)
        {
            int read = _limiter.Read(buffer, offset, count);
            if (_suppressed) Array.Clear(buffer, offset, read);
            return read;
        }
    }
}
