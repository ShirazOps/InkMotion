using NAudio.Wave;
using Xunit;

namespace InkMotion.Tests;

public sealed class AudioEncoderCapabilitiesTests
{
    public static IEnumerable<object[]> Formats =>
        from codec in new[] { AudioExportCodec.Mp3, AudioExportCodec.Aac }
        from rate in new[] { 44100, 48000 }
        from bitRate in new[] { 128000, 160000, 192000, 256000, 320000 }
        select new object[] { codec, rate, bitRate };

    [Fact]
    public void Discovery_IsDeterministicDistinctAndOnlyListsOfferedSettings()
    {
        IReadOnlyList<AudioEncoderFormat> formats = AudioEncoderCapabilities.Discover();
        Assert.Equal(formats, AudioEncoderCapabilities.Discover());
        Assert.Equal(formats.Count, formats.Distinct().Count());
        Assert.All(formats, format =>
        {
            Assert.Contains(format.Channels, new[] { 1, 2 });
            new AudioExportSettings(format.Codec, format.SampleRate, BitRate: format.BitRate).Validate();
        });
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void EveryAvailableStereoFormat_EncodesWithTheRequestedRate(AudioExportCodec codec, int rate, int bitRate)
    {
        if (!AudioEncoderCapabilities.Discover().Contains(new(codec, rate, 2, bitRate)))
        {
            return;
        }

        var settings = new AudioExportSettings(codec, rate, BitRate: bitRate);
        string path = Path.Combine(Path.GetTempPath(), $"inkmotion-encoder-{Guid.NewGuid():N}{settings.Extension}");
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            for (int frame = 0; frame < rate / 5; frame++)
            {
                short sample = (short)(Math.Sin(frame * 2 * Math.PI * 440 / rate) * 8000);
                writer.Write(sample);
                writer.Write(sample);
            }
        }

        bytes.Position = 0;
        try
        {
            using var source = new RawSourceWaveStream(bytes, new WaveFormat(rate, 16, 2));
            bool started = false;
            AudioEncoderCapabilities.EncodeExact(source, path, settings, () => started = true);
            Assert.True(started);
            using var reader = new MediaFoundationReader(path);
            Assert.Equal(rate, reader.WaveFormat.SampleRate);
            Assert.Equal(2, reader.WaveFormat.Channels);
            Assert.InRange(reader.TotalTime.TotalSeconds, 0.18, 0.35);
            var decoded = reader.ToSampleProvider();
            float[] samples = new float[rate / 5 * 2];
            int read = decoded.Read(samples, 0, samples.Length);
            Assert.True(read > 0);
            Assert.True(samples.Take(read).Max(Math.Abs) > 0.1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UnsupportedFormat_IsRejectedBeforeTouchingTheExistingFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"inkmotion-unavailable-{Guid.NewGuid():N}.m4a");
        try
        {
            File.WriteAllText(path, "Previous export");
            using var bytes = new MemoryStream(new byte[4800]);
            using var source = new RawSourceWaveStream(bytes, new WaveFormat(48000, 16, 3));
            bool started = false;
            Assert.Throws<InvalidOperationException>(() => AudioEncoderCapabilities.EncodeExact(
                source, path, new(AudioExportCodec.Aac, 48000), () => started = true));
            Assert.False(started);
            Assert.Equal("Previous export", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
