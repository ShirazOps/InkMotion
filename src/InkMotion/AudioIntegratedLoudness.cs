namespace InkMotion;

public sealed class AudioIntegratedLoudness
{
    private const double AbsoluteGateLufs = -70;
    private const double RelativeGateLufs = -10;
    private const double LufsOffset = -0.691;
    private const double SurroundWeight = 1.41;
    private readonly Biquad[] _shelfFilters;
    private readonly Biquad[] _highPassFilters;
    private readonly double[] _channelWeights;
    private readonly double[] _blockEnergy;
    private readonly List<double> _blocks = [];
    private readonly int _blockFrames;
    private readonly int _blockHopFrames;
    private double _blockSum;
    private long _framesProcessed;

    public AudioIntegratedLoudness(int sampleRate, IReadOnlyList<AudioSpeaker> speakers)
    {
        if (sampleRate != ExportPlanning.AudioSampleRate)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Integrated loudness currently uses the 48 kHz BS.1770 filter coefficients.");
        }
        ArgumentNullException.ThrowIfNull(speakers);
        if (speakers.Count == 0)
        {
            throw new ArgumentException("At least one channel speaker is required.", nameof(speakers));
        }

        _blockFrames = checked((int)Math.Round(sampleRate * 0.4));
        _blockHopFrames = Math.Max(1, (int)Math.Round(sampleRate * 0.1));
        _blockEnergy = new double[_blockFrames];
        _shelfFilters = Enumerable.Range(0, speakers.Count).Select(_ => Biquad.KWeightingShelf()).ToArray();
        _highPassFilters = Enumerable.Range(0, speakers.Count).Select(_ => Biquad.KWeightingHighPass()).ToArray();
        _channelWeights = speakers.Select(WeightForSpeaker).ToArray();
    }

    public void AddFrame(ReadOnlySpan<float> samples)
    {
        if (samples.Length != _channelWeights.Length)
        {
            throw new ArgumentException("The sample frame must contain exactly one sample per channel.", nameof(samples));
        }

        double energy = 0;
        for (int channel = 0; channel < samples.Length; channel++)
        {
            double filtered = _highPassFilters[channel].Process(_shelfFilters[channel].Process(samples[channel]));
            energy += filtered * filtered * _channelWeights[channel];
        }

        int ringIndex = (int)(_framesProcessed % _blockFrames);
        if (_framesProcessed >= _blockFrames)
        {
            _blockSum -= _blockEnergy[ringIndex];
        }
        _blockEnergy[ringIndex] = energy;
        _blockSum += energy;
        _framesProcessed++;

        if (_framesProcessed >= _blockFrames &&
            (_framesProcessed - _blockFrames) % _blockHopFrames == 0)
        {
            _blocks.Add(_blockSum / _blockFrames);
        }
    }

    public double? GetIntegratedLufs()
    {
        double absoluteThreshold = EnergyAtLufs(AbsoluteGateLufs);
        double[] absoluteGated = _blocks.Where(energy => energy > absoluteThreshold).ToArray();
        if (absoluteGated.Length == 0)
        {
            return null;
        }

        double relativeThreshold = Mean(absoluteGated) * EnergyAtLufs(RelativeGateLufs);
        double[] gated = absoluteGated.Where(energy => energy > relativeThreshold).ToArray();
        if (gated.Length == 0)
        {
            return null;
        }

        return LufsOffset + 10 * Math.Log10(Mean(gated));
    }

    private static double Mean(IReadOnlyList<double> values) => values.Average();

    private static double EnergyAtLufs(double lufs) => Math.Pow(10, (lufs - LufsOffset) / 10);

    private static double WeightForSpeaker(AudioSpeaker speaker) => speaker switch
    {
        AudioSpeaker.LowFrequency => 0,
        AudioSpeaker.BackLeft or AudioSpeaker.BackRight or AudioSpeaker.BackCentre or
            AudioSpeaker.SideLeft or AudioSpeaker.SideRight or AudioSpeaker.TopFrontLeft or
            AudioSpeaker.TopFrontRight or AudioSpeaker.TopBackLeft or AudioSpeaker.TopBackRight => SurroundWeight,
        _ => 1,
    };

    private sealed class Biquad(double b0, double b1, double b2, double a1, double a2)
    {
        private double _x1;
        private double _x2;
        private double _y1;
        private double _y2;

        public double Process(double sample)
        {
            double output = b0 * sample + b1 * _x1 + b2 * _x2 - a1 * _y1 - a2 * _y2;
            _x2 = _x1;
            _x1 = sample;
            _y2 = _y1;
            _y1 = output;
            return output;
        }

        public static Biquad KWeightingShelf() => new(
            1.53512485958697, -2.69169618940638, 1.19839281085285,
            -1.69065929318241, 0.73248077421585);

        public static Biquad KWeightingHighPass() => new(
            1, -2, 1, -1.99004745483398, 0.99007225036621);
    }
}
