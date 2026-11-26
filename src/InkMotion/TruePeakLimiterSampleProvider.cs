using NAudio.Wave;

namespace InkMotion;

/// <summary>Linked-channel 4×, 12-tap windowed-sinc peak detection with seven-frame look-ahead and a 100 ms release.</summary>
public sealed class TruePeakLimiterSampleProvider : ISampleProvider
{
    private const int OversamplingFactor = 4;
    private const int TapCount = 12;
    private const int FirstTapOffset = -5;
    private const int LookAheadFrames = 7;
    private const double ReleaseMilliseconds = 100;
    private static readonly double[,] InterpolationCoefficients = CreateInterpolationCoefficients();
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly double _ceiling;
    private readonly double _release;
    private readonly float[] _readBuffer;
    private readonly float[] _history;
    private readonly List<float> _pending = [];
    private double _gain = 1;
    private bool _sourceEnded;

    public TruePeakLimiterSampleProvider(ISampleProvider source, double ceilingDecibels = -1)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (!double.IsFinite(ceilingDecibels) || ceilingDecibels is < -24 or > 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ceilingDecibels));
        }

        _channels = source.WaveFormat.Channels;
        if (_channels < 1)
        {
            throw new ArgumentException("The source must contain at least one channel.", nameof(source));
        }

        _ceiling = Math.Pow(10, ceilingDecibels / 20);
        _release = Math.Exp(-1 / (ReleaseMilliseconds * 0.001 * source.WaveFormat.SampleRate));
        _readBuffer = new float[4096 * _channels];
        _history = new float[6 * _channels];
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        int requestedFrames = count / _channels;
        if (requestedFrames <= 0)
        {
            return 0;
        }

        BufferFrames(checked(requestedFrames + LookAheadFrames));
        int availableFrames = _pending.Count / _channels;
        int outputFrames = _sourceEnded
            ? Math.Min(requestedFrames, availableFrames)
            : Math.Min(requestedFrames, Math.Max(0, availableFrames - LookAheadFrames));
        for (int frame = 0; frame < outputFrames; frame++)
        {
            double peak = EstimatePeak(frame, availableFrames);
            double target = peak > _ceiling ? _ceiling / peak : 1;
            _gain = Math.Min(target, _release * _gain + (1 - _release));

            int sampleIndex = frame * _channels;
            Array.Copy(_history, _channels, _history, 0, _history.Length - _channels);
            for (int channel = 0; channel < _channels; channel++)
            {
                float sample = _pending[sampleIndex + channel];
                buffer[offset + sampleIndex + channel] = (float)(sample * _gain);
                _history[_history.Length - _channels + channel] = sample;
            }
        }

        int samplesRead = outputFrames * _channels;
        if (samplesRead > 0)
        {
            _pending.RemoveRange(0, samplesRead);
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
        for (int intervalOffset = -1; intervalOffset <= 1; intervalOffset++)
        {
            int interval = frame + intervalOffset;
            for (int channel = 0; channel < _channels; channel++)
            {
                double first = GetSample(interval, channel, availableFrames);
                double second = GetSample(interval + 1, channel, availableFrames);
                peak = Math.Max(peak, Math.Max(Math.Abs(first), Math.Abs(second)));
                for (int phase = 1; phase < OversamplingFactor; phase++)
                {
                    double interpolated = 0;
                    for (int tap = 0; tap < TapCount; tap++)
                    {
                        interpolated += GetSample(interval + FirstTapOffset + tap, channel, availableFrames) *
                            InterpolationCoefficients[phase, tap];
                    }

                    peak = Math.Max(peak, Math.Abs(interpolated));
                }
            }
        }

        return peak;
    }

    private double GetSample(int frame, int channel, int availableFrames)
    {
        if (frame < 0)
        {
            int historyFrame = Math.Max(0, 6 + frame);
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
                double window = absoluteDistance >= 6
                    ? 0
                    : 0.42 + 0.5 * Math.Cos(Math.PI * distance / 6) + 0.08 * Math.Cos(Math.PI * distance / 3);
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
