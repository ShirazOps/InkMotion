using NAudio.Wave;
using Xunit;

namespace InkMotion.Tests;

public sealed class AudioChannelLayoutTests
{
    private const float Tolerance = 0.001f;

    [Theory]
    [InlineData(AudioChannelLayout.Mono, 1)]
    [InlineData(AudioChannelLayout.Stereo, 2)]
    [InlineData(AudioChannelLayout.Quad, 4)]
    [InlineData(AudioChannelLayout.Surround51, 6)]
    [InlineData(AudioChannelLayout.Surround71, 8)]
    [InlineData(AudioChannelLayout.Surround714, 12)]
    [InlineData(AudioChannelLayout.Surround121, 13)]
    public void Layouts_HaveExpectedChannelCountsAndUniqueMaskBits(AudioChannelLayout layout, int channels)
    {
        AudioChannelLayoutInfo info = AudioChannelLayouts.Get(layout);

        Assert.Equal(channels, info.ChannelCount);
        Assert.Equal(channels, System.Numerics.BitOperations.PopCount((uint)info.ChannelMask));
        Assert.Equal(info.Speakers.Count, info.Speakers.Distinct().Count());
        Assert.Same(info, AudioChannelLayouts.InferFromChannelCount(channels));
    }

    [Fact]
    public void Surround121_HasTwelveFullRangeChannelsPlusLfe()
    {
        AudioChannelLayoutInfo info = AudioChannelLayouts.Get(AudioChannelLayout.Surround121);

        Assert.Equal(12, info.Speakers.Count(speaker => speaker != AudioSpeaker.LowFrequency));
        Assert.Single(info.Speakers, AudioSpeaker.LowFrequency);
    }

    [Fact]
    public void MixMatrix_SameLayoutIsIdentity()
    {
        foreach (AudioChannelLayoutInfo layout in AudioChannelLayouts.All)
        {
            float[,] matrix = AudioChannelLayouts.CreateMixMatrix(layout.Layout, layout.Layout);
            for (int output = 0; output < layout.ChannelCount; output++)
            {
                for (int input = 0; input < layout.ChannelCount; input++)
                {
                    Assert.Equal(output == input ? 1f : 0f, matrix[output, input]);
                }
            }
        }
    }

    [Fact]
    public void MixMatrix_MonoToStereoUsesConstantPowerPan()
    {
        float[,] matrix = AudioChannelLayouts.CreateMixMatrix(AudioChannelLayout.Mono, AudioChannelLayout.Stereo);

        Assert.Equal(0.7071f, matrix[0, 0], Tolerance);
        Assert.Equal(0.7071f, matrix[1, 0], Tolerance);
    }

    [Fact]
    public void MixMatrix_StereoToMonoAveragesWithConstantPower()
    {
        float[,] matrix = AudioChannelLayouts.CreateMixMatrix(AudioChannelLayout.Stereo, AudioChannelLayout.Mono);

        Assert.Equal(0.7071f, matrix[0, 0], Tolerance);
        Assert.Equal(0.7071f, matrix[0, 1], Tolerance);
    }

    [Fact]
    public void MixMatrix_FivePointOneToStereoFollowsItuDownmix()
    {
        float[,] matrix = AudioChannelLayouts.CreateMixMatrix(AudioChannelLayout.Surround51, AudioChannelLayout.Stereo);

        // Inputs: L R C LFE Lb Rb
        Assert.Equal(new[] { 1f, 0f, 0.7071f, 0.3536f, 0.7071f, 0f }, Row(matrix, 0), new ToleranceComparer());
        Assert.Equal(new[] { 0f, 1f, 0.7071f, 0.3536f, 0f, 0.7071f }, Row(matrix, 1), new ToleranceComparer());
    }

    [Fact]
    public void MixMatrix_StereoToFivePointOneKeepsFrontPairOnly()
    {
        float[,] matrix = AudioChannelLayouts.CreateMixMatrix(AudioChannelLayout.Stereo, AudioChannelLayout.Surround51);

        Assert.Equal(1f, matrix[0, 0]);
        Assert.Equal(1f, matrix[1, 1]);
        for (int output = 2; output < 6; output++)
        {
            Assert.Equal(0f, matrix[output, 0]);
            Assert.Equal(0f, matrix[output, 1]);
        }
    }

    [Fact]
    public void MixMatrix_TwelvePointOneHeightAndBackCentreFoldIntoSevenPointOne()
    {
        float[,] matrix = AudioChannelLayouts.CreateMixMatrix(AudioChannelLayout.Surround121, AudioChannelLayout.Surround71);
        IReadOnlyList<AudioSpeaker> source = AudioChannelLayouts.Get(AudioChannelLayout.Surround121).Speakers;
        IReadOnlyList<AudioSpeaker> target = AudioChannelLayouts.Get(AudioChannelLayout.Surround71).Speakers;

        int backCentre = IndexOf(source, AudioSpeaker.BackCentre);
        Assert.Equal(0.5f, matrix[IndexOf(target, AudioSpeaker.BackLeft), backCentre], Tolerance);
        Assert.Equal(0.5f, matrix[IndexOf(target, AudioSpeaker.BackRight), backCentre], Tolerance);

        int topFrontLeft = IndexOf(source, AudioSpeaker.TopFrontLeft);
        Assert.True(matrix[IndexOf(target, AudioSpeaker.FrontLeft), topFrontLeft] > 0.5f);
        Assert.Equal(0f, matrix[IndexOf(target, AudioSpeaker.FrontRight), topFrontLeft]);
        for (int input = 0; input < source.Count; input++)
        {
            double power = Enumerable.Range(0, target.Count).Sum(output => matrix[output, input] * matrix[output, input]);
            Assert.True(power > 0, $"{source[input]} is not routed.");
            Assert.True(power <= 1.0001, $"{source[input]} gains power.");
        }
    }

    [Fact]
    public void MixMatrix_UnknownChannelCountMapsDiscretely()
    {
        float[,] matrix = AudioChannelLayouts.CreateMixMatrix(3, AudioChannelLayout.Stereo);

        Assert.Equal(2, matrix.GetLength(0));
        Assert.Equal(3, matrix.GetLength(1));
        Assert.Equal(1f, matrix[0, 0]);
        Assert.Equal(1f, matrix[1, 1]);
        Assert.Equal(0f, matrix[0, 2]);
        Assert.Equal(0f, matrix[1, 2]);
    }

    [Fact]
    public void MonitorMatrix_RoutesSurroundSourceThroughProjectLayoutToStereo()
    {
        float[,] direct = AudioChannelLayouts.CreateMixMatrix(AudioChannelLayout.Surround51, AudioChannelLayout.Stereo);
        float[,] monitor = AudioClipPlayer.CreateMonitorMatrix(6, AudioChannelLayout.Surround51);
        float[,] monoProject = AudioClipPlayer.CreateMonitorMatrix(2, AudioChannelLayout.Mono);

        Assert.Equal(Row(direct, 0), Row(monitor, 0), new ToleranceComparer());
        Assert.Equal(Row(direct, 1), Row(monitor, 1), new ToleranceComparer());
        Assert.Equal(0.5f, monoProject[0, 0], Tolerance);
        Assert.Equal(0.5f, monoProject[1, 1], Tolerance);
    }

    [Fact]
    public void ChannelMixingSampleProvider_AppliesMatrixAndClamps()
    {
        var source = new BufferSource(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), [0.5f, 0.25f, 1f, 1f]);
        var mixer = new ChannelMixingSampleProvider(source, new float[,] { { 0.5f, 0.5f } });
        var output = new float[2];

        int read = mixer.Read(output, 0, output.Length);

        Assert.Equal(2, read);
        Assert.Equal(1, mixer.WaveFormat.Channels);
        Assert.Equal(new[] { 0.375f, 1f }, output);

        mixer.UpdateMatrix(new float[,] { { 2f, 2f } });
        source.Reset();
        mixer.Read(output, 0, output.Length);
        Assert.Equal(new[] { 1f, 1f }, output);
        Assert.Throws<ArgumentException>(() => mixer.UpdateMatrix(new float[,] { { 1f } }));
    }

    private static float[] Row(float[,] matrix, int row) =>
        Enumerable.Range(0, matrix.GetLength(1)).Select(column => matrix[row, column]).ToArray();

    private static int IndexOf(IReadOnlyList<AudioSpeaker> speakers, AudioSpeaker speaker) =>
        speakers.Select((value, index) => (value, index)).First(pair => pair.value == speaker).index;

    private sealed class ToleranceComparer : IEqualityComparer<float>
    {
        public bool Equals(float x, float y) => Math.Abs(x - y) <= Tolerance;

        public int GetHashCode(float obj) => 0;
    }

    private sealed class BufferSource(WaveFormat format, float[] samples) : ISampleProvider
    {
        private int _position;

        public WaveFormat WaveFormat { get; } = format;

        public void Reset() => _position = 0;

        public int Read(float[] buffer, int offset, int count)
        {
            int read = Math.Min(count, samples.Length - _position);
            Array.Copy(samples, _position, buffer, offset, read);
            _position += read;
            return read;
        }
    }
}
