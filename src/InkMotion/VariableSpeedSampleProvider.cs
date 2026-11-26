using NAudio.Wave;

namespace InkMotion;

/// <summary>
/// Varispeed playback that follows a time map from output seconds to source seconds, so pitch follows speed as it
/// does for constant-speed clips. The source is read sequentially; samples are linearly interpolated when slowing
/// down and box-averaged when a step skips source samples, which limits aliasing at higher speeds.
/// </summary>
public sealed class VariableSpeedSampleProvider : ISampleProvider
{
    private const int MapBlockFrames = 64;
    private readonly ISampleProvider _source;
    private readonly Func<double, double> _sourceSecondsAt;
    private readonly int _channels;
    private readonly double _sampleRate;
    private float[] _buffer;
    private int _bufferFrames;
    private long _bufferStartFrame;
    private bool _sourceEnded;
    private long _outputFrame;
    private double _previousPosition;
    private long _blockStartFrame = -1;
    private double _blockStartPosition;
    private double _blockEndPosition;

    public VariableSpeedSampleProvider(ISampleProvider source, Func<double, double> sourceSecondsAtOutputSeconds)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceSecondsAtOutputSeconds);
        _source = source;
        _sourceSecondsAt = sourceSecondsAtOutputSeconds;
        WaveFormat = source.WaveFormat;
        _channels = WaveFormat.Channels;
        _sampleRate = WaveFormat.SampleRate;
        _buffer = new float[_channels * 4096];
        _previousPosition = Math.Max(0, _sourceSecondsAt(0) * _sampleRate);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        int frames = count / _channels;
        int written = 0;
        while (written < frames)
        {
            double position = Math.Max(_previousPosition, GetPosition(_outputFrame));
            long lastNeeded = (long)Math.Floor(position) + 1;
            if (!EnsureBuffered(lastNeeded) && Math.Floor(position) >= _bufferStartFrame + _bufferFrames)
            {
                break;
            }

            int target = offset + (written * _channels);
            if (position - _previousPosition > 1.5)
            {
                Average(_previousPosition, position, buffer, target);
            }
            else
            {
                Interpolate(position, buffer, target);
            }

            _previousPosition = position;
            _outputFrame++;
            written++;
            Discard((long)Math.Floor(_previousPosition) - 1);
        }

        return written * _channels;
    }

    private double GetPosition(long outputFrame)
    {
        long blockStart = outputFrame - (outputFrame % MapBlockFrames);
        if (blockStart != _blockStartFrame)
        {
            _blockStartFrame = blockStart;
            _blockStartPosition = _sourceSecondsAt(blockStart / _sampleRate) * _sampleRate;
            _blockEndPosition = _sourceSecondsAt((blockStart + MapBlockFrames) / _sampleRate) * _sampleRate;
        }

        double fraction = (outputFrame - blockStart) / (double)MapBlockFrames;
        return _blockStartPosition + ((_blockEndPosition - _blockStartPosition) * fraction);
    }

    private void Interpolate(double position, float[] destination, int offset)
    {
        long index = (long)Math.Floor(position);
        float weight = (float)(position - index);
        for (int channel = 0; channel < _channels; channel++)
        {
            float first = Sample(index, channel);
            float second = Sample(index + 1, channel);
            destination[offset + channel] = first + ((second - first) * weight);
        }
    }

    private void Average(double fromPosition, double toPosition, float[] destination, int offset)
    {
        long first = (long)Math.Floor(fromPosition) + 1;
        long last = (long)Math.Floor(toPosition);
        int samples = (int)Math.Max(1, last - first + 1);
        for (int channel = 0; channel < _channels; channel++)
        {
            double sum = 0;
            for (long index = first; index <= last; index++)
            {
                sum += Sample(index, channel);
            }

            destination[offset + channel] = (float)(sum / samples);
        }
    }

    private float Sample(long frame, int channel)
    {
        long local = frame - _bufferStartFrame;
        return local < 0 || local >= _bufferFrames ? 0 : _buffer[(local * _channels) + channel];
    }

    private bool EnsureBuffered(long lastFrame)
    {
        while (!_sourceEnded && _bufferStartFrame + _bufferFrames <= lastFrame)
        {
            int requiredFrames = _bufferFrames + 4096;
            if (requiredFrames * _channels > _buffer.Length)
            {
                Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, requiredFrames * _channels));
            }

            int read = _source.Read(_buffer, _bufferFrames * _channels, 4096 * _channels);
            if (read <= 0)
            {
                _sourceEnded = true;
                break;
            }

            _bufferFrames += read / _channels;
        }

        return _bufferStartFrame + _bufferFrames > lastFrame;
    }

    private void Discard(long beforeFrame)
    {
        int drop = (int)Math.Clamp(beforeFrame - _bufferStartFrame, 0, _bufferFrames);
        if (drop < 4096)
        {
            return;
        }

        Array.Copy(_buffer, drop * _channels, _buffer, 0, (_bufferFrames - drop) * _channels);
        _bufferFrames -= drop;
        _bufferStartFrame += drop;
    }
}
