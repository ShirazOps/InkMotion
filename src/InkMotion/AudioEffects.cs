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

    public bool IsValid =>
        double.IsFinite(HighPassHz) && (HighPassHz == 0 || HighPassHz is >= 20 and <= 20000) &&
        double.IsFinite(LowPassHz) && (LowPassHz == 0 || LowPassHz is >= 20 and <= 20000) &&
        (HighPassHz == 0 || LowPassHz == 0 || HighPassHz < LowPassHz) &&
        new[] { BassDecibels, MidDecibels, TrebleDecibels }.All(value => double.IsFinite(value) && value is >= -24 and <= 24) &&
        double.IsFinite(CompressorThresholdDecibels) && CompressorThresholdDecibels is >= -60 and <= 0 &&
        double.IsFinite(CompressorRatio) && CompressorRatio is >= 1 and <= 20 &&
        double.IsFinite(AttackMilliseconds) && AttackMilliseconds is >= 0.1 and <= 200 &&
        double.IsFinite(ReleaseMilliseconds) && ReleaseMilliseconds is >= 5 and <= 2000;
}

public sealed class AudioEffectsSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly object _sync = new();
    private AudioEffectSettings _settings;
    private BiQuadFilter[][] _filters = [];
    private double _gainReductionDecibels;
    private bool _suppressed;

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
            throw new ArgumentException("Invalid audio filter, EQ or compressor settings.", nameof(settings));
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

                double over = Math.Max(0, 20 * Math.Log10(Math.Max(peak, 1e-12)) - _settings.CompressorThresholdDecibels);
                double target = over * (1 - 1 / _settings.CompressorRatio);
                double coefficient = target > _gainReductionDecibels ? attack : release;
                _gainReductionDecibels = coefficient * _gainReductionDecibels + (1 - coefficient) * target;
                float gain = (float)AudioClipEditing.DecibelsToLinear(-_gainReductionDecibels);
                for (int channel = 0; channel < channels && frame + channel < read; channel++)
                {
                    buffer[offset + frame + channel] *= gain;
                }
            }

            return read;
        }
    }
}
