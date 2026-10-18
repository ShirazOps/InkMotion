using NAudio.Wave;

namespace InkMotion;

/// <summary>
/// Changes tempo without changing pitch (WSOLA) and shifts pitch without changing tempo
/// (WSOLA followed by cubic resampling). Unity settings pass audio through unchanged.
/// </summary>
public sealed class TimeStretchSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly Action<double>? _seekSource;
    private readonly object _sync = new();
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly int _hop;
    private readonly int _windowLength;
    private readonly int _searchRadius;
    private readonly float[] _window;
    private readonly float[] _tail;
    private float[] _readBuffer = [];

    private float[] _input = [];
    private long _inputStartFrame;
    private int _inputFrames;
    private double _idealPosition;
    private long _previousSegment;
    private bool _hasPrevious;

    private float[] _stretched = [];
    private int _stretchedFrames;
    private double _resamplePosition;

    private double _tempoRatio = 1;
    private double _pitchRatio = 1;
    private double _seekSeconds;
    private long _outputFrames;

    public TimeStretchSampleProvider(ISampleProvider source, Action<double>? seekSource = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _seekSource = seekSource;
        _channels = source.WaveFormat.Channels;
        _sampleRate = source.WaveFormat.SampleRate;
        _hop = Math.Max(64, _sampleRate / 50);
        _windowLength = _hop * 2;
        _searchRadius = Math.Max(16, _sampleRate * 12 / 1000);
        _window = new float[_windowLength];
        for (int n = 0; n < _windowLength; n++)
        {
            _window[n] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * n / _windowLength));
        }

        _tail = new float[_hop * _channels];
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public double TempoRatio
    {
        get
        {
            lock (_sync)
            {
                return _tempoRatio;
            }
        }
    }

    public double PositionSeconds
    {
        get
        {
            lock (_sync)
            {
                return _seekSeconds + _outputFrames * _tempoRatio / _sampleRate;
            }
        }
    }

    public static double SemitonesToRatio(double semitones) => Math.Pow(2, semitones / 12);

    public void SetParameters(double tempoRatio, double pitchSemitones)
    {
        if (!double.IsFinite(tempoRatio) || tempoRatio <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tempoRatio));
        }

        if (!double.IsFinite(pitchSemitones))
        {
            throw new ArgumentOutOfRangeException(nameof(pitchSemitones));
        }

        lock (_sync)
        {
            double pitchRatio = SemitonesToRatio(pitchSemitones);
            if (Math.Abs(tempoRatio - _tempoRatio) < 1e-9 && Math.Abs(pitchRatio - _pitchRatio) < 1e-9)
            {
                return;
            }

            double position = _seekSeconds + _outputFrames * _tempoRatio / _sampleRate;
            _tempoRatio = tempoRatio;
            _pitchRatio = pitchRatio;
            SeekCore(position);
        }
    }

    public void Seek(double sourceSeconds)
    {
        lock (_sync)
        {
            SeekCore(Math.Max(0, sourceSeconds));
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        lock (_sync)
        {
            int frames = count / _channels;
            if (IsBypassed)
            {
                int read = ReadSource(buffer, offset, frames * _channels);
                _outputFrames += read / _channels;
                return read;
            }

            for (int frame = 0; frame < frames; frame++)
            {
                WriteResampledFrame(buffer, offset + frame * _channels);
            }

            _outputFrames += frames;
            return frames * _channels;
        }
    }

    private bool IsBypassed => Math.Abs(_tempoRatio - 1) < 1e-9 && Math.Abs(_pitchRatio - 1) < 1e-9;

    private void SeekCore(double sourceSeconds)
    {
        _seekSource?.Invoke(sourceSeconds);
        _seekSeconds = sourceSeconds;
        _outputFrames = 0;
        _inputStartFrame = 0;
        _inputFrames = 0;
        _idealPosition = 0;
        _previousSegment = 0;
        _hasPrevious = false;
        _stretchedFrames = 0;
        _resamplePosition = 0;
        Array.Clear(_tail);
    }

    private int ReadSource(float[] buffer, int offset, int count)
    {
        int read = 0;
        while (read < count)
        {
            int chunk = _source.Read(buffer, offset + read, count - read);
            if (chunk <= 0)
            {
                break;
            }

            read += chunk;
        }

        Array.Clear(buffer, offset + read, count - read);
        return count;
    }

    private void WriteResampledFrame(float[] buffer, int offset)
    {
        int index = (int)Math.Floor(_resamplePosition);
        if (Math.Abs(_pitchRatio - 1) < 1e-9)
        {
            EnsureStretched(index + 1);
            Array.Copy(_stretched, index * _channels, buffer, offset, _channels);
            _resamplePosition += 1;
        }
        else
        {
            EnsureStretched(index + 3);
            float t = (float)(_resamplePosition - index);
            int previous = Math.Max(0, index - 1);
            for (int channel = 0; channel < _channels; channel++)
            {
                float y0 = _stretched[previous * _channels + channel];
                float y1 = _stretched[index * _channels + channel];
                float y2 = _stretched[(index + 1) * _channels + channel];
                float y3 = _stretched[(index + 2) * _channels + channel];
                float c1 = 0.5f * (y2 - y0);
                float c2 = y0 - 2.5f * y1 + 2f * y2 - 0.5f * y3;
                float c3 = 0.5f * (y3 - y0) + 1.5f * (y1 - y2);
                buffer[offset + channel] = ((c3 * t + c2) * t + c1) * t + y1;
            }

            _resamplePosition += _pitchRatio;
        }

        int consumable = (int)Math.Floor(_resamplePosition) - 1;
        if (consumable > _hop)
        {
            ConsumeStretched(consumable);
            _resamplePosition -= consumable;
        }
    }

    private void EnsureStretched(int frames)
    {
        while (_stretchedFrames < frames)
        {
            ProduceSegment();
        }
    }

    private void ConsumeStretched(int frames)
    {
        int remaining = _stretchedFrames - frames;
        Array.Copy(_stretched, frames * _channels, _stretched, 0, remaining * _channels);
        _stretchedFrames = remaining;
    }

    private void ProduceSegment()
    {
        double analysisHop = _hop * _tempoRatio / _pitchRatio;
        long ideal = (long)Math.Round(_idealPosition);
        long start = ideal;
        if (_hasPrevious)
        {
            long natural = _previousSegment + _hop;
            long low = Math.Max(_inputStartFrame, ideal - _searchRadius);
            long high = Math.Max(low, ideal + _searchRadius);
            EnsureInput(Math.Max(high, natural) + _windowLength);
            start = FindBestMatch(low, high, natural);
        }
        else
        {
            EnsureInput(ideal + _windowLength);
        }

        EnsureStretchedCapacity(_stretchedFrames + _hop);
        int segment = (int)(start - _inputStartFrame) * _channels;
        int destination = _stretchedFrames * _channels;
        for (int n = 0; n < _hop; n++)
        {
            float weight = _hasPrevious ? _window[n] : 1;
            for (int channel = 0; channel < _channels; channel++)
            {
                int sample = n * _channels + channel;
                _stretched[destination + sample] = _tail[sample] + weight * _input[segment + sample];
            }
        }

        for (int n = 0; n < _hop; n++)
        {
            float weight = _window[_hop + n];
            for (int channel = 0; channel < _channels; channel++)
            {
                int sample = n * _channels + channel;
                _tail[sample] = weight * _input[segment + (_hop * _channels) + sample];
            }
        }

        _stretchedFrames += _hop;
        _previousSegment = start;
        _hasPrevious = true;
        _idealPosition += analysisHop;
        long keepFrom = Math.Min((long)Math.Round(_idealPosition) - _searchRadius, _previousSegment + _hop);
        DiscardInput(keepFrom);
    }

    private long FindBestMatch(long low, long high, long natural)
    {
        long best = low;
        double bestScore = double.NegativeInfinity;
        for (long candidate = low; candidate <= high; candidate += 2)
        {
            double score = Correlate(candidate, natural, 4);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        long refinedBest = best;
        double refinedScore = double.NegativeInfinity;
        for (long candidate = Math.Max(low, best - 1); candidate <= Math.Min(high, best + 1); candidate++)
        {
            double score = Correlate(candidate, natural, 2);
            if (score > refinedScore)
            {
                refinedScore = score;
                refinedBest = candidate;
            }
        }

        return refinedBest;
    }

    private double Correlate(long candidate, long reference, int stride)
    {
        int candidateOffset = (int)(candidate - _inputStartFrame) * _channels;
        int referenceOffset = (int)(reference - _inputStartFrame) * _channels;
        double cross = 0;
        double energy = 1e-9;
        for (int n = 0; n < _hop; n += stride)
        {
            int sample = n * _channels;
            for (int channel = 0; channel < _channels; channel++)
            {
                float value = _input[candidateOffset + sample + channel];
                cross += value * _input[referenceOffset + sample + channel];
                energy += value * value;
            }
        }

        return cross / Math.Sqrt(energy);
    }

    private void EnsureInput(long endFrameExclusive)
    {
        long available = _inputStartFrame + _inputFrames;
        if (available >= endFrameExclusive)
        {
            return;
        }

        int needed = (int)(endFrameExclusive - available);
        int capacity = (_inputFrames + needed) * _channels;
        if (_input.Length < capacity)
        {
            Array.Resize(ref _input, Math.Max(capacity, _input.Length * 2));
        }

        int samples = needed * _channels;
        if (_readBuffer.Length < samples)
        {
            _readBuffer = new float[samples];
        }

        ReadSource(_readBuffer, 0, samples);
        Array.Copy(_readBuffer, 0, _input, _inputFrames * _channels, samples);
        _inputFrames += needed;
    }

    private void DiscardInput(long keepFromFrame)
    {
        int discard = (int)Math.Clamp(keepFromFrame - _inputStartFrame, 0, _inputFrames);
        if (discard == 0)
        {
            return;
        }

        int remaining = _inputFrames - discard;
        Array.Copy(_input, discard * _channels, _input, 0, remaining * _channels);
        _inputFrames = remaining;
        _inputStartFrame += discard;
    }

    private void EnsureStretchedCapacity(int frames)
    {
        int capacity = frames * _channels;
        if (_stretched.Length < capacity)
        {
            Array.Resize(ref _stretched, Math.Max(capacity, _stretched.Length * 2));
        }
    }
}
