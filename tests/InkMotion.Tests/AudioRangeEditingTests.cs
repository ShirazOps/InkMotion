using NAudio.Wave;
using Xunit;

namespace InkMotion.Tests;

public sealed class AudioRangeEditingTests
{
    [Fact]
    public void Partition_UsesTempoAndPreservesOuterFadesAndTags()
    {
        var source = new AudioTrackProject
        {
            StartFrame = 25,
            SourceInSeconds = 2,
            SourceOutSeconds = 14,
            TempoPercent = 200,
            PitchSemitones = 3,
            FadeInSeconds = 0.25,
            FadeOutSeconds = 0.5,
            Title = "Speech",
        };
        AudioRangeParts parts = AudioRangeEditing.Partition(source, 49, 97, 24, 20);
        Assert.NotNull(parts.Before);
        Assert.NotNull(parts.After);
        Assert.Equal(2, parts.Before.SourceInSeconds);
        Assert.Equal(4, parts.Before.SourceOutSeconds);
        Assert.Equal(4, parts.Range.SourceInSeconds);
        Assert.Equal(8, parts.Range.SourceOutSeconds);
        Assert.Equal(8, parts.After.SourceInSeconds);
        Assert.Equal(14, parts.After.SourceOutSeconds);
        Assert.Equal(49, parts.Range.StartFrame);
        Assert.Equal(97, parts.After.StartFrame);
        Assert.Equal(0.25, parts.Before.FadeInSeconds);
        Assert.Equal(0, parts.Range.FadeInSeconds);
        Assert.Equal(0, parts.Range.FadeOutSeconds);
        Assert.Equal(0.5, parts.After.FadeOutSeconds);
        Assert.Equal(3, parts.Range.PitchSemitones);
        Assert.Equal("Speech", parts.Range.Title);
        Assert.Equal(14, source.SourceOutSeconds);
        Assert.Equal(25, source.StartFrame);
    }

    [Fact]
    public void WholeRange_IncludesFinalPartialFrameWithoutExtendingSource()
    {
        var source = new AudioTrackProject { SourceOutSeconds = 1.01 };
        AudioRangeParts parts = AudioRangeEditing.Partition(source, 1, 26, 24, 2);
        Assert.Null(parts.Before);
        Assert.Null(parts.After);
        Assert.Equal(1.01, parts.Range.SourceOutSeconds);
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioRangeEditing.Partition(source, 1, 27, 24, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioRangeEditing.Partition(source, 2, 2, 24, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioRangeEditing.Partition(source, 0, 2, 24, 2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RangeRemovalOrSilence_ExportsAGapWithoutMovingSurroundingAudio(bool silence)
    {
        string input = Path.Combine(Path.GetTempPath(), $"inkmotion-range-{Guid.NewGuid():N}.wav");
        string output = Path.ChangeExtension(input, ".out.wav");
        try
        {
            using (var writer = new WaveFileWriter(input, WaveFormat.CreateIeeeFloatWaveFormat(48000, 1)))
            {
                writer.WriteSamples(Enumerable.Repeat(0.25f, 48000 * 3).ToArray(), 0, 48000 * 3);
            }

            AudioRangeParts parts = AudioRangeEditing.Partition(new() { AudioPath = input }, 25, 49, 24, 3);
            var project = new EditorProject { FrameRate = 24, AudioMixLayout = AudioChannelLayout.Mono };
            project.AudioTracks = [parts.Before!, parts.After!];
            if (silence)
            {
                parts.Range.Muted = true;
                project.AudioTracks.Add(parts.Range);
            }

            AudioExporter.Export(project, new(AudioExportCodec.FloatWave, 48000, 32), output, null, CancellationToken.None);
            using var reader = new AudioFileReader(output);
            float[] samples = new float[48000 * 3];
            int read = reader.Read(samples, 0, samples.Length);
            Assert.Equal(samples.Length, read);
            Assert.InRange(samples[24000], 0.24f, 0.26f);
            Assert.InRange(Math.Abs(samples[72000]), 0, 0.000001f);
            Assert.InRange(samples[120000], 0.24f, 0.26f);
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    [Fact]
    public void AllMutedAudio_StillExportsTheFullSilentDuration()
    {
        string input = Path.Combine(Path.GetTempPath(), $"inkmotion-silent-{Guid.NewGuid():N}.wav");
        string output = Path.ChangeExtension(input, ".out.wav");
        try
        {
            using (var writer = new WaveFileWriter(input, WaveFormat.CreateIeeeFloatWaveFormat(48000, 1)))
            {
                writer.WriteSamples(new float[48000], 0, 48000);
            }

            var project = new EditorProject { AudioTracks = [new() { AudioPath = input, Muted = true }] };
            AudioExporter.Export(project, new(AudioExportCodec.FloatWave, 48000, 32), output, null, CancellationToken.None);
            using var reader = new AudioFileReader(output);
            Assert.Equal(1, reader.TotalTime.TotalSeconds, 6);
            float[] samples = new float[96000];
            Assert.Equal(samples.Length, reader.Read(samples, 0, samples.Length));
            Assert.All(samples, sample => Assert.Equal(0, sample));
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }
}
