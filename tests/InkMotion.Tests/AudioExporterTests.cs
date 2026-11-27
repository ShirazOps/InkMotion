using System.IO.Compression;
using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StemExport_WritesAlignedDirectNestedBusAndSendWaves(bool additive)
    {
        string directInput = Path.Combine(Path.GetTempPath(), $"inkmotion-stem-direct-{Guid.NewGuid():N}.wav");
        string busInput = Path.Combine(Path.GetTempPath(), $"inkmotion-stem-bus-{Guid.NewGuid():N}.wav");
        string output = Path.Combine(Path.GetTempPath(), $"inkmotion-stems-{Guid.NewGuid():N}.zip");
        try
        {
            WriteConstant(directInput, 0.1f);
            WriteConstant(busInput, 0.2f);
            var parent = new AudioBusProject { Name = "Parent", GainDecibels = -6 };
            var child = new AudioBusProject { Name = "Child", ParentBusId = parent.Id };
            var auxiliary = new AudioBusProject { Name = "Auxiliary" };
            var project = new EditorProject
            {
                AudioMixLayout = AudioChannelLayout.Mono,
                AudioBuses = [parent, child, auxiliary],
                AudioTracks =
                [
                    new() { AudioPath = directInput },
                    new()
                    {
                        AudioPath = busInput,
                        BusId = child.Id,
                        AuxSends = [new AudioAuxSendProject { BusId = auxiliary.Id, GainDecibels = -6 }],
                    },
                ],
            };

            AudioExportResult result = AudioExporter.Export(project,
                new(AudioExportCodec.PcmWave, 48000, 16, ExportStems: true, AdditiveStems: additive), output, null, CancellationToken.None);

            Assert.Equal(4, result.StemCount);
            using var archive = ZipFile.OpenRead(output);
            Assert.Equal(4, archive.Entries.Count);
            AssertStem(archive.GetEntry("Master (direct).wav")!, 0.1f);
            AssertStem(archive.Entries.Single(entry => entry.Name.StartsWith("Parent [", StringComparison.Ordinal)), additive ? 0 : 0.1f);
            AssertStem(archive.Entries.Single(entry => entry.Name.StartsWith("Child [", StringComparison.Ordinal)), 0.1f);
            AssertStem(archive.Entries.Single(entry => entry.Name.StartsWith("Auxiliary [", StringComparison.Ordinal)), 0.1f);
        }
        finally
        {
            File.Delete(directInput);
            File.Delete(busInput);
            File.Delete(output);
        }
    }

    [Fact]
    public void StemExport_CancellationRemovesPartialArchive()
    {
        string input = Path.Combine(Path.GetTempPath(), $"inkmotion-stem-cancel-{Guid.NewGuid():N}.wav");
        string output = Path.ChangeExtension(input, ".zip");
        try
        {
            WriteConstant(input, 0.1f, 48000);
            using var cancellation = new CancellationTokenSource();
            var project = new EditorProject { AudioTracks = [new() { AudioPath = input }] };

            Assert.Throws<OperationCanceledException>(() => AudioExporter.Export(project,
                new(AudioExportCodec.PcmWave, 48000, 16, ExportStems: true), output,
                new CancelOnProgress(cancellation), cancellation.Token));
            Assert.False(File.Exists(output));
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    [Theory]
    [InlineData(AudioChannelLayout.Mono, false, false)]
    [InlineData(AudioChannelLayout.Stereo, true, false)]
    [InlineData(AudioChannelLayout.Surround51, true, false)]
    [InlineData(AudioChannelLayout.Mono, true, true)]
    public void AdditiveStemsSumToFullMixWithNestedNonlinearProcessingAndEverySend(
        AudioChannelLayout layout, bool masterLimiter, bool trackSolo)
    {
        string input = Path.Combine(Path.GetTempPath(), $"inkmotion-additive-{Guid.NewGuid():N}.wav");
        string zip = Path.ChangeExtension(input, ".zip");
        string mix = Path.ChangeExtension(input, ".mix.wav");
        try
        {
            WriteConstant(input, 0.3f);
            var parent = new AudioBusProject
            {
                Name = "Parent",
                GainDecibels = 3,
                Effects = new()
                {
                    Enabled = true, CompressorThresholdDecibels = -18, CompressorRatio = 4,
                    LimiterEnabled = true, LimiterTruePeak = true, LimiterCeilingDecibels = -9,
                    DelayEnabled = true, DelayMilliseconds = 10, DelayFeedback = 0.1, DelayWetGain = 0.5,
                },
            };
            var child = new AudioBusProject { Name = "Child", ParentBusId = parent.Id };
            var leaf = new AudioBusProject { Name = "Leaf", ParentBusId = child.Id };
            var muted = new AudioBusProject { Name = "Muted", Muted = true };
            var nested = new EditorProject { AudioTracks = [new() { AudioPath = input, Volume = 0.5 }] };
            var project = new EditorProject
            {
                AudioMixLayout = layout,
                AudioBuses = [parent, child, leaf, muted],
                AudioTracks =
                [
                    new() { AudioPath = input, Volume = 0.25, Solo = trackSolo,
                        AuxSends = [new() { BusId = leaf.Id, GainDecibels = -12 }] },
                    new() { AudioPath = input, BusId = leaf.Id, Solo = trackSolo,
                        AuxSends = [new() { BusId = parent.Id, GainDecibels = -3 },
                            new() { BusId = child.Id, GainDecibels = -6 }] },
                    new() { AudioPath = input, BusId = parent.Id },
                    new() { AudioPath = input, BusId = muted.Id },
                ],
                VideoTracks = [new()
                {
                    Kind = TimelineTrackKind.Compound, AudioBusId = child.Id,
                    Clips = [new()
                    {
                        NestedProject = nested, NestedName = "Nested", SourceDurationSeconds = 0.1,
                        DurationSeconds = 0.1,
                    }],
                }],
            };
            var settings = new AudioExportSettings(AudioExportCodec.FloatWave, 44100, 32,
                TruePeakLimiterEnabled: masterLimiter, TruePeakCeilingDecibels: -12);
            AudioExporter.Export(project, settings, mix, null, CancellationToken.None);
            // Bus solos are ignored for both legacy and additive stem exports.
            parent.Solo = true;
            AudioExportResult result = AudioExporter.Export(project, settings with
            {
                ExportStems = true, AdditiveStems = true,
            }, zip, null, CancellationToken.None);
            Assert.Contains(result.Warnings, warning => warning.Contains("ordered residuals", StringComparison.Ordinal));
            Assert.True(parent.Solo);
            using var fullReader = new WaveFileReader(mix);
            float[] expected = new float[(int)(fullReader.Length / sizeof(float))];
            fullReader.ReadExactly(MemoryMarshal.AsBytes(expected.AsSpan()));
            double[] sum = new double[expected.Length];
            using var archive = ZipFile.OpenRead(zip);
            Assert.Equal(5, archive.Entries.Count);
            Assert.StartsWith("Master", archive.Entries[0].Name);
            Assert.StartsWith("Leaf", archive.Entries[1].Name);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                using var data = new MemoryStream();
                using (Stream stream = entry.Open()) stream.CopyTo(data);
                data.Position = 0;
                using var reader = new WaveFileReader(data);
                Assert.Equal(fullReader.WaveFormat.Channels, reader.WaveFormat.Channels);
                Assert.Equal(fullReader.WaveFormat.SampleRate, reader.WaveFormat.SampleRate);
                float[] samples = new float[expected.Length];
                Assert.Equal(fullReader.Length, reader.Length);
                Assert.Equal(32, reader.WaveFormat.BitsPerSample);
                reader.ReadExactly(MemoryMarshal.AsBytes(samples.AsSpan()));
                for (int sample = 0; sample < samples.Length; sample++) sum[sample] += samples[sample];
                if (entry.Name.StartsWith("Muted", StringComparison.Ordinal))
                    Assert.All(samples, value => Assert.Equal(0, value));
            }
            Assert.True(expected.Any(value => Math.Abs(value) > 0.01));
            for (int sample = 0; sample < expected.Length; sample++)
                Assert.InRange(Math.Abs(sum[sample] - expected[sample]), 0, 0.000001);
        }
        finally
        {
            File.Delete(input);
            File.Delete(zip);
            File.Delete(mix);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdditiveStemsCancelOrRejectPcmClippingWithoutLeavingAnArchive(bool cancel)
    {
        string input = Path.Combine(Path.GetTempPath(), $"inkmotion-additive-failure-{Guid.NewGuid():N}.wav");
        string output = Path.ChangeExtension(input, ".zip");
        try
        {
            WriteConstant(input, 0.8f);
            var project = new EditorProject
            {
                AudioTracks = [new() { AudioPath = input }, new() { AudioPath = input }],
            };
            using var cancellation = new CancellationTokenSource();
            var settings = new AudioExportSettings(cancel ? AudioExportCodec.FloatWave : AudioExportCodec.PcmWave,
                48000, cancel ? 32 : 16, ExportStems: true, AdditiveStems: true);
            if (cancel)
                Assert.Throws<OperationCanceledException>(() => AudioExporter.Export(project, settings, output,
                    new CancelOnProgress(cancellation), cancellation.Token));
            else
                Assert.Contains("float", Assert.Throws<InvalidOperationException>(() =>
                    AudioExporter.Export(project, settings, output, null, CancellationToken.None)).Message);
            Assert.False(File.Exists(output));
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    [Fact]
    public void StemSettings_RejectCompressedCodecs()
    {
        Assert.Throws<ArgumentException>(() =>
            new AudioExportSettings(AudioExportCodec.Mp3, 48000, ExportStems: true).Validate());
    }

    private static void AssertStem(ZipArchiveEntry entry, float expectedSample)
    {
        using Stream source = entry.Open();
        using var data = new MemoryStream();
        source.CopyTo(data);
        data.Position = 0;
        using var reader = new WaveFileReader(data);
        Assert.Equal(48000, reader.WaveFormat.SampleRate);
        Assert.Equal(1, reader.WaveFormat.Channels);
        Assert.Equal(16, reader.WaveFormat.BitsPerSample);
        Assert.InRange(reader.TotalTime.TotalSeconds, 0.099, 0.101);
        float[] samples = new float[4800];
        int read = reader.ToSampleProvider().Read(samples, 0, samples.Length);
        Assert.Equal(samples.Length, read);
        Assert.InRange(Math.Abs(samples[1000] - expectedSample), 0, 0.002f);
    }

    private static void WriteConstant(string path, float sample, int frames = 4800)
    {
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(48000, 1));
        for (int index = 0; index < frames; index++)
        {
            writer.WriteSample(sample);
        }
    }

    private sealed class CancelOnProgress(CancellationTokenSource cancellation) : IProgress<double>
    {
        public void Report(double value)
        {
            if (value > 0)
            {
                cancellation.Cancel();
            }
        }
    }
}
