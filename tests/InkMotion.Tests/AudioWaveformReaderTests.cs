using System.Text;
using Xunit;

namespace InkMotion.Tests;

public sealed class AudioWaveformReaderTests
{
    [Fact]
    public void ReadPeaks_GroupsSamplesAndCombinesStereoChannels()
    {
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.wav");
        try
        {
            WritePcmWave(path, channels: 2, [0, 0, 16384, 0, 0, -32768, 8192, 8192]);

            float[] peaks = AudioWaveformReader.ReadPeaks(path, peakCount: 4);

            Assert.Equal(4, peaks.Length);
            Assert.Equal(0, peaks[0]);
            Assert.InRange(peaks[1], 0.49f, 0.51f);
            Assert.Equal(1, peaks[2]);
            Assert.InRange(peaks[3], 0.24f, 0.26f);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void WritePcmWave(string path, short channels, short[] samples)
    {
        const int sampleRate = 8000;
        const short bitsPerSample = 16;
        short blockAlignment = (short)(channels * bitsPerSample / 8);
        int dataLength = samples.Length * sizeof(short);
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * blockAlignment);
        writer.Write(blockAlignment);
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);
        foreach (short sample in samples)
        {
            writer.Write(sample);
        }
    }
}
