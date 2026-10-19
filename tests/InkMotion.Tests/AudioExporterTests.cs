using NAudio.Wave;
using Xunit;

namespace InkMotion.Tests;

public sealed class AudioExporterTests
{
    public static IEnumerable<object[]> WaveFormats =>
        from rate in AudioExportSettings.SampleRates
        from format in new[] { (16, false), (24, false), (32, false), (32, true) }
        select new object[] { rate, format.Item1, format.Item2 };

    [Theory]
    [MemberData(nameof(WaveFormats))]
    public void WaveExport_RoundTripsTheSelectedFormat(int rate, int depth, bool floatingPoint)
    {
        string input = Path.Combine(Path.GetTempPath(), $"inkmotion-input-{Guid.NewGuid():N}.wav");
        string output = Path.Combine(Path.GetTempPath(), $"inkmotion-output-{Guid.NewGuid():N}.wav");
        try
        {
            using (var writer = new WaveFileWriter(input, WaveFormat.CreateIeeeFloatWaveFormat(48000, 1)))
            {
                for (int i = 0; i < 4800; i++)
                {
                    writer.WriteSample((float)(0.25 * Math.Sin(i * 2 * Math.PI * 440 / 48000)));
                }
            }

            var project = new EditorProject
            {
                AudioMixLayout = AudioChannelLayout.Stereo,
                AudioTracks = [new() { AudioPath = input }],
            };
            var settings = new AudioExportSettings(floatingPoint ? AudioExportCodec.FloatWave : AudioExportCodec.PcmWave, rate, depth);
            AudioExportResult result = AudioExporter.Export(project, settings, output, null, CancellationToken.None);
            using var reader = new WaveFileReader(output);
            Assert.Equal(rate, reader.WaveFormat.SampleRate);
            Assert.Equal(depth, reader.WaveFormat.BitsPerSample);
            Assert.Equal(2, reader.WaveFormat.Channels);
            Assert.Equal(2, result.Channels);
            Assert.InRange(reader.TotalTime.TotalSeconds, 0.099, 0.101);
            reader.Dispose();
            using var decoded = new AudioFileReader(output);
            float[] samples = new float[rate / 10 * 2];
            int read = decoded.Read(samples, 0, samples.Length);
            Assert.InRange(samples.Take(read).Max(Math.Abs), 0.1f, 0.4f);
            Assert.True(samples.Take(read).All(float.IsFinite));
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    [Fact]
    public void Cancellation_RemovesPartialAudio()
    {
        string input = Path.Combine(Path.GetTempPath(), $"inkmotion-cancel-{Guid.NewGuid():N}.wav");
        string output = Path.ChangeExtension(input, ".out.wav");
        try
        {
            using (var writer = new WaveFileWriter(input, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)))
            {
                writer.WriteSamples(new float[96000], 0, 96000);
            }

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var project = new EditorProject { AudioTracks = [new() { AudioPath = input }] };
            Assert.Throws<OperationCanceledException>(() =>
                AudioExporter.Export(project, new(AudioExportCodec.PcmWave, 48000), output, null, cancellation.Token));
            Assert.False(File.Exists(output));
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    [Theory]
    [InlineData(AudioExportCodec.Mp3)]
    [InlineData(AudioExportCodec.Aac)]
    public void CompressedExport_RoundTripsWhenTheWindowsEncoderIsAvailable(AudioExportCodec codec)
    {
        if (!AudioEncoderCapabilities.Discover().Any(format => format == new AudioEncoderFormat(codec, 48000, 2, 192000)))
        {
            return;
        }

        string input = Path.Combine(Path.GetTempPath(), $"inkmotion-compressed-input-{Guid.NewGuid():N}.wav");
        var settings = new AudioExportSettings(codec, 48000);
        string output = Path.ChangeExtension(input, settings.Extension);
        try
        {
            using (var writer = new WaveFileWriter(input, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)))
            {
                for (int i = 0; i < 48000; i++)
                {
                    float sample = (float)(0.25 * Math.Sin(i * 2 * Math.PI * 440 / 48000));
                    writer.WriteSample(sample);
                    writer.WriteSample(sample);
                }
            }

            var project = new EditorProject { AudioTracks = [new() { AudioPath = input }] };
            AudioExporter.Export(project, settings, output, null, CancellationToken.None);
            using var reader = new MediaFoundationReader(output);
            Assert.Equal(48000, reader.WaveFormat.SampleRate);
            Assert.Equal(2, reader.WaveFormat.Channels);
            Assert.InRange(reader.TotalTime.TotalSeconds, 0.95, 1.15);
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    [Theory]
    [InlineData(AudioExportCodec.Mp3, 384000, 24)]
    [InlineData(AudioExportCodec.Aac, 96000, 24)]
    [InlineData(AudioExportCodec.FloatWave, 48000, 24)]
    [InlineData(AudioExportCodec.PcmWave, 48000, 20)]
    public void Settings_RejectUnsupportedCombinations(AudioExportCodec codec, int rate, int depth) =>
        Assert.Throws<ArgumentException>(() => new AudioExportSettings(codec, rate, depth).Validate());

    [Fact]
    public void WaveSettings_CoverAllRequestedRatesAndDepths()
    {
        foreach (int rate in AudioExportSettings.SampleRates)
        {
            foreach (int depth in new[] { 16, 24, 32 })
            {
                new AudioExportSettings(AudioExportCodec.PcmWave, rate, depth).Validate();
            }
        }

    }

    [Fact]
    public void HighRateExport_DoesNotDownsampleTheMixTo48Khz()
    {
        string input = Path.Combine(Path.GetTempPath(), $"inkmotion-highrate-{Guid.NewGuid():N}.wav");
        string output = Path.ChangeExtension(input, ".out.wav");
        try
        {
            using (var writer = new WaveFileWriter(input, WaveFormat.CreateIeeeFloatWaveFormat(192000, 1)))
            {
                for (int i = 0; i < 19200; i++)
                {
                    writer.WriteSample((float)(0.25 * Math.Sin(i * 2 * Math.PI * 40000 / 192000)));
                }
            }

            var project = new EditorProject { AudioMixLayout = AudioChannelLayout.Mono, AudioTracks = [new() { AudioPath = input }] };
            AudioExporter.Export(project, new(AudioExportCodec.FloatWave, 384000, 32), output, null, CancellationToken.None);
            using var reader = new AudioFileReader(output);
            var samples = new float[38400];
            int read = reader.Read(samples, 0, samples.Length);
            double rms = Math.Sqrt(samples.Take(read).Average(sample => sample * (double)sample));
            Assert.InRange(rms, 0.1, 0.25);
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    [Fact]
    public void Export_RejectsOverwritingAnAudioSource()
    {
        var project = new EditorProject { AudioTracks = [new() { AudioPath = @"C:\media\source.wav" }] };
        Assert.Throws<InvalidOperationException>(() => AudioExporter.Export(project,
            new(AudioExportCodec.PcmWave, 48000), @"C:\MEDIA\source.wav", null, CancellationToken.None));
    }
}
