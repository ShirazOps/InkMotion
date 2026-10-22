using NAudio.Dsp;
using NAudio.Wave;

namespace InkMotion;

public sealed record AudioNoiseProfile(
    string SourcePath,
    int SampleRate,
    int Channels,
    int FftSize,
    double[] Magnitudes)
{
    public const int DefaultFftSize = 1024;
    public const int HopSize = DefaultFftSize / 2;
    public int BinCount => FftSize / 2 + 1;

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(SourcePath) &&
        SampleRate is >= 8000 and <= 384000 &&
        Channels is >= 1 and <= 32 &&
        FftSize == DefaultFftSize &&
        Magnitudes is not null &&
        Magnitudes.Length == Channels * BinCount &&
        Magnitudes.All(value => double.IsFinite(value) && value >= 0);

    public bool MatchesSource(string sourcePath, int sampleRate, int channels) =>
        IsValid &&
        string.Equals(Path.GetFullPath(SourcePath), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase) &&
        SampleRate == sampleRate &&
        Channels == channels;

    public static AudioNoiseProfile Capture(
        string sourcePath,
        long firstSampleFrame,
        long endSampleFrame,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (firstSampleFrame < 0 || endSampleFrame <= firstSampleFrame)
        {
            throw new ArgumentOutOfRangeException(nameof(firstSampleFrame), "Choose a non-empty source sample range.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var reader = new AudioFileReader(sourcePath);
        int sampleRate = reader.WaveFormat.SampleRate;
        int channels = reader.WaveFormat.Channels;
        long sourceFrames = reader.Length / reader.WaveFormat.BlockAlign;
        if (endSampleFrame > sourceFrames || endSampleFrame - firstSampleFrame < HopSize)
        {
            throw new ArgumentOutOfRangeException(nameof(endSampleFrame),
                $"Choose at least {HopSize} sample frames within the source.");
        }

        reader.Position = checked(firstSampleFrame * reader.WaveFormat.BlockAlign);
        var totals = new double[channels * (DefaultFftSize / 2 + 1)];
        var previousHop = new float[HopSize * channels];
        var currentHop = new float[HopSize * channels];
        var frame = new Complex[DefaultFftSize];
        double windowSum = Enumerable.Range(0, DefaultFftSize).Sum(index => GetWindow(index));
        long remaining = endSampleFrame - firstSampleFrame;
        int windows = 0;

        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int hopFrames = (int)Math.Min(HopSize, remaining);
            Array.Clear(currentHop);
            ReadExactly(reader, currentHop, hopFrames * channels, cancellationToken);

            for (int channel = 0; channel < channels; channel++)
            {
                for (int index = 0; index < DefaultFftSize; index++)
                {
                    float sample = index < HopSize
                        ? previousHop[index * channels + channel]
                        : currentHop[(index - HopSize) * channels + channel];
                    frame[index] = new Complex { X = (float)(sample * GetWindow(index)) };
                }

                FastFourierTransform.FFT(true, 10, frame);
                for (int bin = 0; bin <= DefaultFftSize / 2; bin++)
                {
                    totals[channel * (DefaultFftSize / 2 + 1) + bin] +=
                        Math.Sqrt((frame[bin].X * (double)frame[bin].X) + (frame[bin].Y * (double)frame[bin].Y)) /
                        windowSum;
                }
            }

            Array.Copy(currentHop, previousHop, currentHop.Length);
            remaining -= hopFrames;
            windows++;
        }

        var profile = new AudioNoiseProfile(
            Path.GetFullPath(sourcePath),
            sampleRate,
            channels,
            DefaultFftSize,
            totals.Select(value => value / windows).ToArray());
        if (!profile.IsValid)
        {
            throw new InvalidDataException("The selected source range did not produce a valid noise profile.");
        }

        return profile;
    }

    internal static double GetWindow(int index) =>
        0.5 - (0.5 * Math.Cos(2 * Math.PI * index / (DefaultFftSize - 1)));

    private static void ReadExactly(AudioFileReader reader, float[] buffer, int count, CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int next = reader.Read(buffer, read, count - read);
            if (next == 0)
            {
                throw new InvalidDataException("Source audio ended before the selected noise range could be analysed.");
            }

            read += next;
        }
    }
}

internal sealed class AudioNoiseReductionSampleProvider : ISampleProvider
{
    private const int FftSize = AudioNoiseProfile.DefaultFftSize;
    private const int HopSize = AudioNoiseProfile.HopSize;
    private readonly ISampleProvider _source;
    private AudioNoiseProfile _profile;
    private double _subtraction;
    private double _floor;
    private readonly int _channels;
    private readonly float[] _previousHop;
    private readonly float[] _currentHop;
    private readonly float[] _overlap;
    private readonly float[] _pending;
    private readonly Complex[] _fft = new Complex[FftSize];
    private readonly double[] _gains = new double[FftSize / 2 + 1];
    private readonly double[] _smoothedGains = new double[FftSize / 2 + 1];
    private int _pendingPosition;
    private int _pendingFrames;
    private int _currentHopFrames;
    private int _previousHopFrames;
    private bool _sourceEnded;
    private bool _tailFlushed;
    private int _processedHops;
    private static readonly double WindowSum = Enumerable.Range(0, FftSize)
        .Sum(AudioNoiseProfile.GetWindow);

    public AudioNoiseReductionSampleProvider(ISampleProvider source, AudioNoiseProfile profile, double strengthDecibels)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(profile);
        _source = source;
        ValidateSettings(profile, strengthDecibels);
        _profile = profile;
        _subtraction = 1 - Math.Pow(10, -strengthDecibels / 20);
        _floor = Math.Pow(10, -strengthDecibels / 20);
        _channels = source.WaveFormat.Channels;
        _previousHop = new float[HopSize * _channels];
        _currentHop = new float[HopSize * _channels];
        _overlap = new float[HopSize * _channels];
        _pending = new float[HopSize * _channels];
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public AudioNoiseProfile Profile => _profile;

    public void Update(AudioNoiseProfile profile, double strengthDecibels)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ValidateSettings(profile, strengthDecibels);
        _profile = profile;
        _subtraction = 1 - Math.Pow(10, -strengthDecibels / 20);
        _floor = Math.Pow(10, -strengthDecibels / 20);
    }

    public void Reset()
    {
        Array.Clear(_previousHop);
        Array.Clear(_currentHop);
        Array.Clear(_overlap);
        Array.Clear(_pending);
        _pendingPosition = 0;
        _pendingFrames = 0;
        _currentHopFrames = 0;
        _previousHopFrames = 0;
        _sourceEnded = false;
        _tailFlushed = false;
        _processedHops = 0;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int requestedFrames = count / _channels;
        int writtenFrames = 0;
        while (writtenFrames < requestedFrames)
        {
            if (_pendingFrames == 0 && !GenerateHop())
            {
                break;
            }

            int frames = Math.Min(requestedFrames - writtenFrames, _pendingFrames);
            Array.Copy(_pending, _pendingPosition * _channels, buffer, offset + writtenFrames * _channels,
                frames * _channels);
            _pendingPosition += frames;
            _pendingFrames -= frames;
            writtenFrames += frames;
        }

        return writtenFrames * _channels;
    }

    private bool GenerateHop()
    {
        if (!ReadNextHop())
        {
            if (!_sourceEnded || _tailFlushed)
            {
                return false;
            }

            _tailFlushed = true;
            _currentHopFrames = 0;
            Array.Clear(_currentHop);
        }

        for (int channel = 0; channel < _channels; channel++)
        {
            for (int index = 0; index < FftSize; index++)
            {
                float sample = index < HopSize
                    ? _previousHop[index * _channels + channel]
                    : _currentHop[(index - HopSize) * _channels + channel];
                _fft[index] = new Complex { X = (float)(sample * AudioNoiseProfile.GetWindow(index)) };
            }

            FastFourierTransform.FFT(true, 10, _fft);
            for (int bin = 0; bin <= FftSize / 2; bin++)
            {
                double magnitude =
                    Math.Sqrt((_fft[bin].X * (double)_fft[bin].X) + (_fft[bin].Y * (double)_fft[bin].Y)) /
                    WindowSum;
                double noise = _profile.Magnitudes[channel * (FftSize / 2 + 1) + bin];
                double reduced = Math.Max(magnitude - noise * _subtraction, magnitude * _floor);
                _gains[bin] = magnitude <= 1e-15 ? 1 : reduced / magnitude;
            }

            for (int bin = 0; bin <= FftSize / 2; bin++)
            {
                double previous = _gains[Math.Max(0, bin - 1)];
                double current = _gains[bin];
                double next = _gains[Math.Min(FftSize / 2, bin + 1)];
                _smoothedGains[bin] = (previous + (2 * current) + next) * 0.25;
            }

            for (int bin = 0; bin < FftSize; bin++)
            {
                int mirrored = bin <= FftSize / 2 ? bin : FftSize - bin;
                double gain = _smoothedGains[mirrored];
                _fft[bin].X = (float)(_fft[bin].X * gain);
                _fft[bin].Y = (float)(_fft[bin].Y * gain);
            }

            FastFourierTransform.FFT(false, 10, _fft);
            for (int index = 0; index < HopSize; index++)
            {
                double firstWindow = AudioNoiseProfile.GetWindow(index);
                double secondWindow = AudioNoiseProfile.GetWindow(index + HopSize);
                double normalisation = (firstWindow * firstWindow) + (secondWindow * secondWindow);
                int outputIndex = index * _channels + channel;
                _pending[outputIndex] = normalisation <= 1e-12
                    ? 0
                    : (float)((_overlap[outputIndex] + (_fft[index].X * firstWindow)) / normalisation);
                _overlap[outputIndex] = (float)(_fft[index + HopSize].X * secondWindow);
            }
        }

        int processedHop = _processedHops++;
        Array.Copy(_currentHop, _previousHop, _currentHop.Length);
        if (processedHop == 0)
        {
            _previousHopFrames = _currentHopFrames;
            _pendingFrames = 0;
            return true;
        }

        _pendingPosition = 0;
        _pendingFrames = _previousHopFrames;
        _previousHopFrames = _currentHopFrames;
        return _pendingFrames > 0;
    }

    private bool ReadNextHop()
    {
        if (_sourceEnded)
        {
            return false;
        }

        Array.Clear(_currentHop);
        _currentHopFrames = 0;
        int requested = _currentHop.Length;
        int read = 0;
        while (read < requested)
        {
            int next = _source.Read(_currentHop, read, requested - read);
            if (next == 0)
            {
                _sourceEnded = true;
                break;
            }

            read += next;
        }

        if (read % _channels != 0)
        {
            throw new InvalidDataException("Audio stream ended with an incomplete sample frame.");
        }

        _currentHopFrames = read / _channels;
        return read > 0;
    }

    private void ValidateSettings(AudioNoiseProfile profile, double strengthDecibels)
    {
        if (!profile.IsValid || profile.SampleRate != WaveFormat.SampleRate ||
            profile.Channels != WaveFormat.Channels)
        {
            throw new ArgumentException("The noise profile does not match the audio source format.", nameof(profile));
        }

        if (!double.IsFinite(strengthDecibels) || strengthDecibels is < 0 or > 24)
        {
            throw new ArgumentOutOfRangeException(nameof(strengthDecibels));
        }
    }
}
