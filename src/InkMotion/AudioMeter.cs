using NAudio.Wave;

namespace InkMotion;

public sealed record AudioMeterReading(float Peak, double Rms, bool Clipped)
{
    public double PeakDecibels => Peak > 0 ? 20 * Math.Log10(Peak) : double.NegativeInfinity;
    public double RmsDecibels => Rms > 0 ? 20 * Math.Log10(Rms) : double.NegativeInfinity;
}

public sealed class AudioMeterSampleProvider(ISampleProvider source) : ISampleProvider
{
    private const int RecentFrameCapacity = 4096;
    private readonly object _sync = new();
    private readonly int _channels = source.WaveFormat.Channels;
    private readonly float[] _recentSamples = new float[RecentFrameCapacity * source.WaveFormat.Channels];
    private float _peak;
    private double _squares;
    private long _samples;
    private bool _clipped;
    private int _recentWriteFrame;
    private int _recentFrameCount;

    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = source.Read(buffer, offset, count);
        float peak = 0;
        double squares = 0;
        for (int index = offset; index < offset + read; index++)
        {
            float sample = buffer[index];
            peak = Math.Max(peak, Math.Abs(sample));
            squares += (double)sample * sample;
        }

        lock (_sync)
        {
            _peak = Math.Max(_peak, peak);
            _squares += squares;
            _samples += read;
            _clipped |= peak >= 1;
            int frames = read / _channels;
            int framesToStore = Math.Min(frames, RecentFrameCapacity);
            int sourceFrame = frames - framesToStore;
            int firstCount = Math.Min(framesToStore, RecentFrameCapacity - _recentWriteFrame);
            Array.Copy(
                buffer,
                offset + sourceFrame * _channels,
                _recentSamples,
                _recentWriteFrame * _channels,
                firstCount * _channels);
            if (firstCount < framesToStore)
            {
                Array.Copy(
                    buffer,
                    offset + (sourceFrame + firstCount) * _channels,
                    _recentSamples,
                    0,
                    (framesToStore - firstCount) * _channels);
            }

            _recentWriteFrame = (_recentWriteFrame + framesToStore) % RecentFrameCapacity;
            _recentFrameCount = Math.Min(RecentFrameCapacity, _recentFrameCount + frames);
        }

        return read;
    }

    public int CopyRecentSamples(float[] destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        int destinationCapacity = destination.Length / _channels;
        lock (_sync)
        {
            int frames = Math.Min(_recentFrameCount, destinationCapacity);
            int firstFrame = (_recentWriteFrame - frames + RecentFrameCapacity) % RecentFrameCapacity;
            int firstCount = Math.Min(frames, RecentFrameCapacity - firstFrame);
            Array.Copy(_recentSamples, firstFrame * _channels, destination, 0, firstCount * _channels);
            if (firstCount < frames)
            {
                Array.Copy(_recentSamples, 0, destination, firstCount * _channels, (frames - firstCount) * _channels);
            }

            return frames * _channels;
        }
    }

    public AudioMeterReading TakeReading(bool resetClip = false)
    {
        lock (_sync)
        {
            var reading = new AudioMeterReading(_peak, _samples == 0 ? 0 : Math.Sqrt(_squares / _samples), _clipped);
            _peak = 0;
            _squares = 0;
            _samples = 0;
            if (resetClip)
            {
                _clipped = false;
            }

            return reading;
        }
    }
}
