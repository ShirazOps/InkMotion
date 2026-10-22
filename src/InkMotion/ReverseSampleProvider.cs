using NAudio.Wave;

namespace InkMotion;

public sealed class ReverseSampleProvider : ISampleProvider
{
    private const int BlockFrames = 4096;
    private readonly ISampleProvider _source;
    private readonly Action<double> _seek;
    private readonly int _channels;
    private readonly long _startFrame;
    private long _nextEndFrame;
    private float[] _block = [];
    private int _nextBlockFrame;
    private bool _ended;

    public ReverseSampleProvider(ISampleProvider source, Action<double> seek, double startSeconds, double endSeconds)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(seek);
        if (!double.IsFinite(startSeconds) || startSeconds < 0 ||
            !double.IsFinite(endSeconds) || endSeconds < startSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(startSeconds), "The source range must be finite and non-negative.");
        }

        _source = source;
        _seek = seek;
        _channels = source.WaveFormat.Channels;
        _startFrame = (long)Math.Ceiling(startSeconds * source.WaveFormat.SampleRate);
        _nextEndFrame = (long)Math.Floor(endSeconds * source.WaveFormat.SampleRate);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset + count > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        int requestedFrames = count / _channels;
        int writtenFrames = 0;
        while (writtenFrames < requestedFrames)
        {
            if (_nextBlockFrame == 0 && !LoadPreviousBlock())
            {
                break;
            }

            int frames = Math.Min(requestedFrames - writtenFrames, _nextBlockFrame);
            for (int frame = 0; frame < frames; frame++)
            {
                int sourceOffset = (--_nextBlockFrame) * _channels;
                Array.Copy(_block, sourceOffset, buffer, offset + ((writtenFrames + frame) * _channels), _channels);
            }

            writtenFrames += frames;
        }

        return writtenFrames * _channels;
    }

    private bool LoadPreviousBlock()
    {
        if (_ended || _nextEndFrame <= _startFrame)
        {
            return false;
        }

        int requestedFrames = (int)Math.Min(BlockFrames, _nextEndFrame - _startFrame);
        long blockStartFrame = _nextEndFrame - requestedFrames;
        _seek(blockStartFrame / (double)WaveFormat.SampleRate);
        int requestedSamples = requestedFrames * _channels;
        if (_block.Length < requestedSamples)
        {
            _block = new float[requestedSamples];
        }

        int readSamples = 0;
        while (readSamples < requestedSamples)
        {
            int read = _source.Read(_block, readSamples, requestedSamples - readSamples);
            if (read == 0)
            {
                break;
            }

            readSamples += read;
        }

        _nextBlockFrame = readSamples / _channels;
        _nextEndFrame = blockStartFrame;
        if (_nextBlockFrame == 0)
        {
            _ended = true;
            return false;
        }

        return true;
    }
}
