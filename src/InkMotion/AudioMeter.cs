using NAudio.Wave;

namespace InkMotion;

public sealed record AudioMeterReading(float Peak, double Rms, bool Clipped)
{
    public double PeakDecibels => Peak > 0 ? 20 * Math.Log10(Peak) : double.NegativeInfinity;
    public double RmsDecibels => Rms > 0 ? 20 * Math.Log10(Rms) : double.NegativeInfinity;
}

public sealed class AudioMeterSampleProvider(ISampleProvider source) : ISampleProvider
{
    private readonly object _sync = new();
    private float _peak;
    private double _squares;
    private long _samples;
    private bool _clipped;

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
        }

        return read;
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
