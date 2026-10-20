using NAudio.Dsp;
using NAudio.Wave;

namespace InkMotion;

public sealed record AudioEffectSettings
{
    public bool Enabled { get; init; }
    public double HighPassHz { get; init; }
    public double LowPassHz { get; init; }
    public double BassDecibels { get; init; }
    public double MidDecibels { get; init; }
    public double TrebleDecibels { get; init; }
    public double CompressorThresholdDecibels { get; init; }
    public double CompressorRatio { get; init; } = 1;
    public double AttackMilliseconds { get; init; } = 10;
    public double ReleaseMilliseconds { get; init; } = 100;
    public bool GateEnabled { get; init; }
    public double GateThresholdDecibels { get; init; } = -40;
    public double GateAttackMilliseconds { get; init; } = 5;
    public double GateReleaseMilliseconds { get; init; } = 100;
    public double GateHoldMilliseconds { get; init; } = 20;
    public bool LimiterEnabled { get; init; }
    public double LimiterCeilingDecibels { get; init; } = -1;
    public double LimiterReleaseMilliseconds { get; init; } = 100;
    public bool DelayEnabled { get; init; }
    public double DelayMilliseconds { get; init; } = 250;
    public double DelayFeedback { get; init; } = 0.3;
    public double DelayWetGain { get; init; } = 0.25;
    public bool ReverbEnabled { get; init; }
    public double ReverbRoomScale { get; init; } = 1;
    public double ReverbDecaySeconds { get; init; } = 1.5;
    public double ReverbDamping { get; init; } = 0.3;
    public double ReverbWetGain { get; init; } = 0.25;

    public bool IsValid =>
        double.IsFinite(HighPassHz) && (HighPassHz == 0 || HighPassHz is >= 20 and <= 20000) &&
        double.IsFinite(LowPassHz) && (LowPassHz == 0 || LowPassHz is >= 20 and <= 20000) &&
        (HighPassHz == 0 || LowPassHz == 0 || HighPassHz < LowPassHz) &&
        new[] { BassDecibels, MidDecibels, TrebleDecibels }.All(value => double.IsFinite(value) && value is >= -24 and <= 24) &&
        double.IsFinite(CompressorThresholdDecibels) && CompressorThresholdDecibels is >= -60 and <= 0 &&
        double.IsFinite(CompressorRatio) && CompressorRatio is >= 1 and <= 20 &&
        double.IsFinite(AttackMilliseconds) && AttackMilliseconds is >= 0.1 and <= 200 &&
        double.IsFinite(ReleaseMilliseconds) && ReleaseMilliseconds is >= 5 and <= 2000 &&
        double.IsFinite(GateThresholdDecibels) && GateThresholdDecibels is >= -90 and <= 0 &&
        double.IsFinite(GateAttackMilliseconds) && GateAttackMilliseconds is >= 0.1 and <= 200 &&
        double.IsFinite(GateReleaseMilliseconds) && GateReleaseMilliseconds is >= 5 and <= 2000 &&
        double.IsFinite(GateHoldMilliseconds) && GateHoldMilliseconds is >= 0 and <= 1000 &&
        double.IsFinite(LimiterCeilingDecibels) && LimiterCeilingDecibels is >= -24 and <= 0 &&
        double.IsFinite(LimiterReleaseMilliseconds) && LimiterReleaseMilliseconds is >= 5 and <= 2000 &&
        double.IsFinite(DelayMilliseconds) && DelayMilliseconds is >= 1 and <= 2000 &&
        double.IsFinite(DelayFeedback) && DelayFeedback is >= 0 and <= 0.95 &&
        double.IsFinite(DelayWetGain) && DelayWetGain is >= 0 and <= 1 &&
        double.IsFinite(ReverbRoomScale) && ReverbRoomScale is >= 0.5 and <= 2 &&
        double.IsFinite(ReverbDecaySeconds) && ReverbDecaySeconds is >= 0.1 and <= 10 &&
        double.IsFinite(ReverbDamping) && ReverbDamping is >= 0 and <= 0.95 &&
        double.IsFinite(ReverbWetGain) && ReverbWetGain is >= 0 and <= 1;
}

public sealed class AudioEffectsSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly object _sync = new();
    private AudioEffectSettings _settings;
    private BiQuadFilter[][] _filters = [];
    private double _gainReductionDecibels;
    private bool _suppressed;
    private double _gateGain;
    private bool _gateOpen;
    private int _gateHoldFrames;
    private double _limiterGain = 1;
    private float[] _delayBuffer = [];
    private int _delayPosition;
    private AudioReverb[] _reverb = [];

    public AudioEffectsSampleProvider(ISampleProvider source, AudioEffectSettings settings)
    {
        _source = source;
        _settings = settings;
        Update(settings);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public void Update(AudioEffectSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.IsValid)
        {
            throw new ArgumentException("Invalid audio filter, EQ, compressor, gate, reverb, delay or limiter settings.", nameof(settings));
        }

        lock (_sync)
        {
            if (_settings == settings && _filters.Length == WaveFormat.Channels)
            {
                return;
            }

            _settings = settings;
            ResetCore();
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            ResetCore();
        }
    }

    public void SetSuppressed(bool suppressed)
    {
        lock (_sync)
        {
            if (_suppressed == suppressed)
            {
                return;
            }

            _suppressed = suppressed;
            ResetCore();
        }
    }

    private void ResetCore()
    {
        _gainReductionDecibels = 0;
        _gateGain = 0;
        _gateOpen = false;
        _gateHoldFrames = 0;
        _limiterGain = 1;
        _delayPosition = 0;
        _delayBuffer = _settings.Enabled && _settings.DelayEnabled
            ? new float[checked(Math.Max(1, (int)Math.Round(_settings.DelayMilliseconds * 0.001 * WaveFormat.SampleRate)) * WaveFormat.Channels)]
            : [];
        _reverb = _settings.Enabled && _settings.ReverbEnabled
            ? Enumerable.Range(0, WaveFormat.Channels).Select(_ => new AudioReverb(WaveFormat.SampleRate, _settings)).ToArray()
            : [];
        _filters = Enumerable.Range(0, WaveFormat.Channels).Select(_ => CreateFilters()).ToArray();
    }

    private BiQuadFilter[] CreateFilters()
    {
        var filters = new List<BiQuadFilter>();
        float rate = WaveFormat.SampleRate;
        float Frequency(double frequency) => (float)Math.Min(frequency, rate * 0.45);
        if (_settings.HighPassHz > 0)
        {
            filters.Add(BiQuadFilter.HighPassFilter(rate, Frequency(_settings.HighPassHz), 0.7071f));
        }

        if (_settings.LowPassHz > 0)
        {
            filters.Add(BiQuadFilter.LowPassFilter(rate, Frequency(_settings.LowPassHz), 0.7071f));
        }

        if (_settings.BassDecibels != 0)
        {
            filters.Add(BiQuadFilter.LowShelf(rate, Frequency(120), 1, (float)_settings.BassDecibels));
        }

        if (_settings.MidDecibels != 0)
        {
            filters.Add(BiQuadFilter.PeakingEQ(rate, Frequency(1000), 0.8f, (float)_settings.MidDecibels));
        }

        if (_settings.TrebleDecibels != 0)
        {
            filters.Add(BiQuadFilter.HighShelf(rate, Frequency(6000), 1, (float)_settings.TrebleDecibels));
        }

        return filters.ToArray();
    }

    public int Read(float[] buffer, int offset, int count)
    {
        lock (_sync)
        {
            int read = _source.Read(buffer, offset, count);
            if (_suppressed)
            {
                Array.Clear(buffer, offset, read);
                return read;
            }

            if (!_settings.Enabled)
            {
                return read;
            }

            int channels = WaveFormat.Channels;
            double attack = Math.Exp(-1 / (_settings.AttackMilliseconds * 0.001 * WaveFormat.SampleRate));
            double release = Math.Exp(-1 / (_settings.ReleaseMilliseconds * 0.001 * WaveFormat.SampleRate));
            double gateAttack = TimeCoefficient(_settings.GateAttackMilliseconds);
            double gateRelease = TimeCoefficient(_settings.GateReleaseMilliseconds);
            double gateThreshold = AudioClipEditing.DecibelsToLinear(_settings.GateThresholdDecibels);
            double gateCloseThreshold = gateThreshold * AudioClipEditing.DecibelsToLinear(-3);
            int gateHold = (int)Math.Round(_settings.GateHoldMilliseconds * 0.001 * WaveFormat.SampleRate);
            double limiterCeiling = AudioClipEditing.DecibelsToLinear(_settings.LimiterCeilingDecibels);
            double limiterRelease = TimeCoefficient(_settings.LimiterReleaseMilliseconds);
            for (int frame = 0; frame < read; frame += channels)
            {
                double peak = 0;
                for (int channel = 0; channel < channels && frame + channel < read; channel++)
                {
                    int index = offset + frame + channel;
                    foreach (BiQuadFilter filter in _filters[channel])
                    {
                        buffer[index] = filter.Transform(buffer[index]);
                    }

                    peak = Math.Max(peak, Math.Abs(buffer[index]));
                }

                double gateGain = 1;
                if (_settings.GateEnabled)
                {
                    if (peak >= gateThreshold || (_gateOpen && peak >= gateCloseThreshold))
                    {
                        _gateOpen = true;
                        _gateHoldFrames = gateHold;
                    }
                    else if (_gateHoldFrames > 0)
                    {
                        _gateHoldFrames--;
                    }
                    else
                    {
                        _gateOpen = false;
                    }

                    double gateTarget = _gateOpen ? 1 : 0;
                    double smoothing = _gateOpen ? gateAttack : gateRelease;
                    _gateGain = smoothing * _gateGain + (1 - smoothing) * gateTarget;
                    gateGain = _gateGain;
                }

                double over = Math.Max(0, 20 * Math.Log10(Math.Max(peak * gateGain, 1e-12)) - _settings.CompressorThresholdDecibels);
                double target = over * (1 - 1 / _settings.CompressorRatio);
                double coefficient = target > _gainReductionDecibels ? attack : release;
                _gainReductionDecibels = coefficient * _gainReductionDecibels + (1 - coefficient) * target;
                double gain = gateGain * AudioClipEditing.DecibelsToLinear(-_gainReductionDecibels);
                peak = 0;
                for (int channel = 0; channel < channels && frame + channel < read; channel++)
                {
                    int index = offset + frame + channel;
                    double sample = buffer[index] * gain;
                    if (_reverb.Length > 0)
                    {
                        sample += _reverb[channel].Process(sample) * _settings.ReverbWetGain;
                    }

                    if (_delayBuffer.Length > 0)
                    {
                        float delayed = _delayBuffer[_delayPosition];
                        _delayBuffer[_delayPosition] = (float)(sample + delayed * _settings.DelayFeedback);
                        sample += delayed * _settings.DelayWetGain;
                        _delayPosition = (_delayPosition + 1) % _delayBuffer.Length;
                    }

                    buffer[index] = (float)sample;
                    peak = Math.Max(peak, Math.Abs(buffer[index]));
                }

                double outputGain = 1;
                if (_settings.LimiterEnabled)
                {
                    double limit = peak > limiterCeiling ? limiterCeiling / peak : 1;
                    _limiterGain = Math.Min(limit, limiterRelease * _limiterGain + (1 - limiterRelease));
                    outputGain = _limiterGain;
                }

                for (int channel = 0; channel < channels && frame + channel < read; channel++)
                {
                    buffer[offset + frame + channel] = (float)(buffer[offset + frame + channel] * outputGain);
                }

            }

            return read;
        }
    }

    private double TimeCoefficient(double milliseconds) =>
        Math.Exp(-1 / (milliseconds * 0.001 * WaveFormat.SampleRate));
}
