using NAudio.Wave;

namespace InkMotion;

public enum AudioChannelLayout
{
    Mono,
    Stereo,
    Quad,
    Surround51,
    Surround71,
    Surround714,
    Surround121,
}

public enum AudioSpeaker
{
    FrontLeft,
    FrontRight,
    FrontCentre,
    LowFrequency,
    BackLeft,
    BackRight,
    BackCentre,
    SideLeft,
    SideRight,
    TopFrontLeft,
    TopFrontRight,
    TopBackLeft,
    TopBackRight,
}

public sealed record AudioChannelLayoutInfo(
    AudioChannelLayout Layout,
    string DisplayName,
    IReadOnlyList<AudioSpeaker> Speakers,
    int ChannelMask)
{
    public int ChannelCount => Speakers.Count;
}

public static class AudioChannelLayouts
{
    private static readonly Dictionary<AudioSpeaker, (double Azimuth, double Elevation, int MaskBit)> SpeakerPositions = new()
    {
        [AudioSpeaker.FrontLeft] = (-30, 0, 0x1),
        [AudioSpeaker.FrontRight] = (30, 0, 0x2),
        [AudioSpeaker.FrontCentre] = (0, 0, 0x4),
        [AudioSpeaker.LowFrequency] = (0, 0, 0x8),
        [AudioSpeaker.BackLeft] = (-135, 0, 0x10),
        [AudioSpeaker.BackRight] = (135, 0, 0x20),
        [AudioSpeaker.BackCentre] = (180, 0, 0x100),
        [AudioSpeaker.SideLeft] = (-90, 0, 0x200),
        [AudioSpeaker.SideRight] = (90, 0, 0x400),
        [AudioSpeaker.TopFrontLeft] = (-45, 45, 0x1000),
        [AudioSpeaker.TopFrontRight] = (45, 45, 0x4000),
        [AudioSpeaker.TopBackLeft] = (-135, 45, 0x8000),
        [AudioSpeaker.TopBackRight] = (135, 45, 0x20000),
    };

    private static readonly AudioChannelLayoutInfo[] Layouts =
    [
        Create(AudioChannelLayout.Mono, "Mono (1.0)", AudioSpeaker.FrontCentre),
        Create(AudioChannelLayout.Stereo, "Stereo (2.0)", AudioSpeaker.FrontLeft, AudioSpeaker.FrontRight),
        Create(
            AudioChannelLayout.Quad,
            "Quad (4.0)",
            AudioSpeaker.FrontLeft,
            AudioSpeaker.FrontRight,
            AudioSpeaker.BackLeft,
            AudioSpeaker.BackRight),
        Create(
            AudioChannelLayout.Surround51,
            "5.1 surround",
            AudioSpeaker.FrontLeft,
            AudioSpeaker.FrontRight,
            AudioSpeaker.FrontCentre,
            AudioSpeaker.LowFrequency,
            AudioSpeaker.BackLeft,
            AudioSpeaker.BackRight),
        Create(
            AudioChannelLayout.Surround71,
            "7.1 surround",
            AudioSpeaker.FrontLeft,
            AudioSpeaker.FrontRight,
            AudioSpeaker.FrontCentre,
            AudioSpeaker.LowFrequency,
            AudioSpeaker.BackLeft,
            AudioSpeaker.BackRight,
            AudioSpeaker.SideLeft,
            AudioSpeaker.SideRight),
        Create(
            AudioChannelLayout.Surround714,
            "7.1.4 immersive",
            AudioSpeaker.FrontLeft,
            AudioSpeaker.FrontRight,
            AudioSpeaker.FrontCentre,
            AudioSpeaker.LowFrequency,
            AudioSpeaker.BackLeft,
            AudioSpeaker.BackRight,
            AudioSpeaker.SideLeft,
            AudioSpeaker.SideRight,
            AudioSpeaker.TopFrontLeft,
            AudioSpeaker.TopFrontRight,
            AudioSpeaker.TopBackLeft,
            AudioSpeaker.TopBackRight),
        Create(
            AudioChannelLayout.Surround121,
            "12.1 immersive",
            AudioSpeaker.FrontLeft,
            AudioSpeaker.FrontRight,
            AudioSpeaker.FrontCentre,
            AudioSpeaker.LowFrequency,
            AudioSpeaker.BackLeft,
            AudioSpeaker.BackRight,
            AudioSpeaker.BackCentre,
            AudioSpeaker.SideLeft,
            AudioSpeaker.SideRight,
            AudioSpeaker.TopFrontLeft,
            AudioSpeaker.TopFrontRight,
            AudioSpeaker.TopBackLeft,
            AudioSpeaker.TopBackRight),
    ];

    public static IReadOnlyList<AudioChannelLayoutInfo> All { get; } = Array.AsReadOnly(Layouts);

    public static AudioChannelLayoutInfo Get(AudioChannelLayout layout) =>
        Layouts.FirstOrDefault(info => info.Layout == layout) ??
        throw new ArgumentOutOfRangeException(nameof(layout), layout, "Unknown channel layout.");

    public static bool IsDefined(AudioChannelLayout layout) => Layouts.Any(info => info.Layout == layout);

    public static AudioChannelLayoutInfo? InferFromChannelCount(int channelCount) =>
        Layouts.FirstOrDefault(info => info.ChannelCount == channelCount);

    public static string DescribeSource(int channelCount) =>
        InferFromChannelCount(channelCount) is AudioChannelLayoutInfo layout
            ? layout.DisplayName
            : $"{channelCount} discrete channels";

    /// <summary>
    /// Creates a mixing matrix indexed [output, input]. Speakers present in both layouts are copied directly;
    /// others are folded into the nearest output speakers with constant-power weights (ITU-style -3 dB for
    /// surround and height speakers). LFE folds into the front speakers at -6 dB when the output has no LFE.
    /// </summary>
    public static float[,] CreateMixMatrix(int sourceChannelCount, AudioChannelLayout target)
    {
        AudioChannelLayoutInfo targetLayout = Get(target);
        if (InferFromChannelCount(sourceChannelCount) is not AudioChannelLayoutInfo sourceLayout)
        {
            var discrete = new float[targetLayout.ChannelCount, sourceChannelCount];
            for (int channel = 0; channel < Math.Min(sourceChannelCount, targetLayout.ChannelCount); channel++)
            {
                discrete[channel, channel] = 1;
            }

            return discrete;
        }

        return CreateMixMatrix(sourceLayout.Speakers, targetLayout.Speakers);
    }

    public static float[,] CreateMixMatrix(AudioChannelLayout source, AudioChannelLayout target) =>
        CreateMixMatrix(Get(source).Speakers, Get(target).Speakers);

    /// <summary>Creates an equal-power mono panner across the horizontal speakers, leaving height and LFE channels silent.</summary>
    public static float[,] CreateSpatialPanMatrix(AudioChannelLayout target, double azimuthDegrees)
    {
        if (!double.IsFinite(azimuthDegrees) || azimuthDegrees is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(nameof(azimuthDegrees));
        }

        IReadOnlyList<AudioSpeaker> speakers = Get(target).Speakers;
        var positions = speakers
            .Select((speaker, index) => (Speaker: speaker, Index: index))
            .Where(position => position.Speaker != AudioSpeaker.LowFrequency &&
                SpeakerPositions[position.Speaker].Elevation == 0)
            .Select(position => (position.Index, Angle: NormalizeAzimuth(SpeakerPositions[position.Speaker].Azimuth)))
            .OrderBy(position => position.Angle)
            .ToArray();
        var matrix = new float[speakers.Count, 1];
        if (positions.Length == 0)
        {
            return matrix;
        }

        double angle = NormalizeAzimuth(azimuthDegrees);
        int next = Array.FindIndex(positions, position => position.Angle >= angle - 1e-10);
        if (next < 0)
        {
            next = 0;
        }

        if (Math.Abs(positions[next].Angle - angle) <= 1e-10)
        {
            matrix[positions[next].Index, 0] = 1;
            return matrix;
        }

        int previous = (next - 1 + positions.Length) % positions.Length;
        double previousAngle = positions[previous].Angle;
        double nextAngle = positions[next].Angle;
        if (previous > next)
        {
            nextAngle += 360;
        }

        if (angle < previousAngle)
        {
            angle += 360;
        }

        double proportion = Math.Clamp((angle - previousAngle) / (nextAngle - previousAngle), 0, 1);
        matrix[positions[previous].Index, 0] = (float)Math.Cos(proportion * Math.PI / 2);
        matrix[positions[next].Index, 0] = (float)Math.Sin(proportion * Math.PI / 2);
        return matrix;
    }

    public static float[,] CreateMixMatrix(IReadOnlyList<AudioSpeaker> source, IReadOnlyList<AudioSpeaker> target)
    {
        var matrix = new float[target.Count, source.Count];
        int[] targetFullRange = Enumerable.Range(0, target.Count)
            .Where(index => target[index] != AudioSpeaker.LowFrequency)
            .ToArray();
        int sourceFullRangeCount = source.Count(speaker => speaker != AudioSpeaker.LowFrequency);
        bool isMonoTarget = targetFullRange.Length == 1;

        for (int input = 0; input < source.Count; input++)
        {
            AudioSpeaker speaker = source[input];
            if (isMonoTarget && speaker != AudioSpeaker.LowFrequency && sourceFullRangeCount > 1)
            {
                matrix[targetFullRange[0], input] = (float)(1 / Math.Sqrt(sourceFullRangeCount));
                continue;
            }

            int direct = IndexOf(target, speaker);
            if (direct >= 0)
            {
                matrix[direct, input] = 1;
                continue;
            }

            if (speaker == AudioSpeaker.LowFrequency)
            {
                int[] fronts = targetFullRange.Where(index => Math.Abs(SpeakerPositions[target[index]].Azimuth) <= 30).ToArray();
                if (fronts.Length == 0)
                {
                    fronts = targetFullRange;
                }

                float weight = (float)(0.5 / Math.Sqrt(fronts.Length));
                foreach (int output in fronts)
                {
                    matrix[output, input] = weight;
                }

                continue;
            }

            FoldSpeaker(matrix, input, speaker, target, targetFullRange);
        }

        return matrix;
    }

    public static float[,] Multiply(float[,] outer, float[,] inner)
    {
        int rows = outer.GetLength(0);
        int shared = outer.GetLength(1);
        if (inner.GetLength(0) != shared)
        {
            throw new ArgumentException("Matrix dimensions do not match.", nameof(inner));
        }

        int columns = inner.GetLength(1);
        var result = new float[rows, columns];
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                float sum = 0;
                for (int k = 0; k < shared; k++)
                {
                    sum += outer[row, k] * inner[k, column];
                }

                result[row, column] = sum;
            }
        }

        return result;
    }

    private static void FoldSpeaker(
        float[,] matrix,
        int input,
        AudioSpeaker speaker,
        IReadOnlyList<AudioSpeaker> target,
        int[] targetFullRange)
    {
        (double azimuth, double elevation, _) = SpeakerPositions[speaker];
        (int Index, double Weight, double Distance)[] candidates = targetFullRange
            .Select(index =>
            {
                double distance = AngularDistance(azimuth, SpeakerPositions[target[index]].Azimuth);
                return (Index: index, Weight: Math.Max(0, Math.Cos(distance * Math.PI / 180)), Distance: distance);
            })
            .OrderByDescending(candidate => candidate.Weight)
            .ThenBy(candidate => candidate.Distance)
            .Take(2)
            .ToArray();
        if (candidates.Length == 0)
        {
            return;
        }

        if (candidates.All(candidate => candidate.Weight <= 0))
        {
            // No speaker within 90 degrees: keep the source on its own side when possible.
            (int Index, double Weight, double Distance)[] nearest = targetFullRange
                .Select(index => (Index: index, Weight: 1d, Distance: AngularDistance(azimuth, SpeakerPositions[target[index]].Azimuth)))
                .OrderBy(candidate => candidate.Distance)
                .ToArray();
            (int Index, double Weight, double Distance)[] sameSide = nearest
                .Where(candidate => Math.Sign(azimuth) != 0 && Math.Sign(SpeakerPositions[target[candidate.Index]].Azimuth) == Math.Sign(azimuth))
                .Take(1)
                .ToArray();
            candidates = sameSide.Length > 0 ? sameSide : nearest.Take(2).ToArray();
        }

        double power = Math.Sqrt(candidates.Sum(candidate => candidate.Weight * candidate.Weight));
        double attenuation = Math.Abs(azimuth) > 60 || elevation > 0 ? Math.Sqrt(0.5) : 1;
        foreach ((int index, double weight, _) in candidates)
        {
            matrix[index, input] += (float)(weight / power * attenuation);
        }
    }

    private static double AngularDistance(double first, double second)
    {
        double difference = Math.Abs(first - second) % 360;
        return difference > 180 ? 360 - difference : difference;
    }

    private static double NormalizeAzimuth(double angle)
    {
        double normalised = angle % 360;
        return normalised < 0 ? normalised + 360 : normalised;
    }

    private static int IndexOf(IReadOnlyList<AudioSpeaker> speakers, AudioSpeaker speaker)
    {
        for (int index = 0; index < speakers.Count; index++)
        {
            if (speakers[index] == speaker)
            {
                return index;
            }
        }

        return -1;
    }

    private static AudioChannelLayoutInfo Create(AudioChannelLayout layout, string name, params AudioSpeaker[] speakers) =>
        new(layout, name, Array.AsReadOnly(speakers), speakers.Sum(speaker => SpeakerPositions[speaker].MaskBit));
}

public sealed class ChannelMixingSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _inputChannels;
    private float[,] _matrix;
    private float[] _inputBuffer = [];

    public ChannelMixingSampleProvider(ISampleProvider source, float[,] matrix)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(matrix);
        _source = source;
        _inputChannels = source.WaveFormat.Channels;
        ValidateMatrix(matrix, matrix.GetLength(0));
        _matrix = matrix;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, matrix.GetLength(0));
    }

    public WaveFormat WaveFormat { get; }

    public void UpdateMatrix(float[,] matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        ValidateMatrix(matrix, WaveFormat.Channels);
        Volatile.Write(ref _matrix, matrix);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int outputChannels = WaveFormat.Channels;
        int frames = count / outputChannels;
        int inputSamples = frames * _inputChannels;
        if (_inputBuffer.Length < inputSamples)
        {
            _inputBuffer = new float[inputSamples];
        }

        int read = _source.Read(_inputBuffer, 0, inputSamples);
        int framesRead = read / _inputChannels;
        float[,] matrix = Volatile.Read(ref _matrix);
        for (int frame = 0; frame < framesRead; frame++)
        {
            int inputIndex = frame * _inputChannels;
            int outputIndex = offset + frame * outputChannels;
            for (int output = 0; output < outputChannels; output++)
            {
                float sum = 0;
                for (int input = 0; input < _inputChannels; input++)
                {
                    sum += matrix[output, input] * _inputBuffer[inputIndex + input];
                }

                buffer[outputIndex + output] = Math.Clamp(sum, -1f, 1f);
            }
        }

        return framesRead * outputChannels;
    }

    private void ValidateMatrix(float[,] matrix, int outputChannels)
    {
        if (matrix.GetLength(0) != outputChannels || matrix.GetLength(1) != _inputChannels || outputChannels <= 0)
        {
            throw new ArgumentException("The mixing matrix does not match the channel counts.", nameof(matrix));
        }
    }
}
