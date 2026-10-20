namespace InkMotion;

internal sealed class AudioReverb
{
    private readonly Comb[] _combs;
    private readonly AllPass[] _diffusers;

    public AudioReverb(int sampleRate, AudioEffectSettings settings)
    {
        int Length(double seconds) => Math.Max(1, checked((int)Math.Round(seconds * settings.ReverbRoomScale * sampleRate)));
        _combs = new[] { 0.0297, 0.0371, 0.0411, 0.0437 }.Select(seconds =>
        {
            int length = Length(seconds);
            // A delay-line round trip loses 60 dB over the nominal decay time without damping.
            double feedback = Math.Pow(10, -3 * length / (sampleRate * settings.ReverbDecaySeconds));
            return new Comb(length, feedback, settings.ReverbDamping);
        }).ToArray();
        _diffusers = [new(Length(0.005)), new(Length(0.0017))];
    }

    public double Process(double sample)
    {
        double wet = 0;
        foreach (Comb comb in _combs)
        {
            wet += comb.Process(sample);
        }

        wet /= _combs.Length;
        foreach (AllPass diffuser in _diffusers)
        {
            wet = diffuser.Process(wet);
        }

        return wet;
    }

    private sealed class Comb(int length, double feedback, double damping)
    {
        private readonly double[] _buffer = new double[length];
        private int _position;
        private double _lowPass;

        public double Process(double sample)
        {
            double delayed = _buffer[_position];
            _lowPass = delayed * (1 - damping) + _lowPass * damping;
            _buffer[_position] = sample + _lowPass * feedback;
            _position = (_position + 1) % _buffer.Length;
            return delayed;
        }
    }

    private sealed class AllPass(int length)
    {
        private readonly double[] _buffer = new double[length];
        private int _position;

        public double Process(double sample)
        {
            const double feedback = 0.5;
            double delayed = _buffer[_position];
            double output = delayed - feedback * sample;
            _buffer[_position] = sample + feedback * output;
            _position = (_position + 1) % _buffer.Length;
            return output;
        }
    }
}
