using NAudio.Wave;
using Xunit;

namespace InkMotion.Tests;

public sealed class AudioSourceSampleRangeTests
{
    [Fact]
    public void AppliesBothEndpointsPreservingTimelineTempoFadesEffectsAndTags()
    {
        var clip = new AudioTrackProject
        {
            AudioPath = "source.wav", StartFrame = 49, SourceInSeconds = 1, SourceOutSeconds = 3,
            TempoPercent = 150, PitchSemitones = 2, FadeInSeconds = 0.1, FadeOutSeconds = 0.2,
            GainDecibels = -3, Volume = 0.5, Balance = -0.2, Solo = true, Title = "Keep",
            Effects = new() { Enabled = true, ReverbEnabled = true },
        };
        Assert.True(AudioClipEditing.SetSourceSampleRange(clip, 96000, 240000, 480000, 48000));
        Assert.Equal(2, clip.SourceInSeconds);
        Assert.Equal(5, clip.SourceOutSeconds);
        Assert.Equal(49, clip.StartFrame);
        Assert.Equal(150, clip.TempoPercent);
        Assert.Equal(2, clip.PitchSemitones);
        Assert.Equal(0.1, clip.FadeInSeconds);
        Assert.Equal(0.2, clip.FadeOutSeconds);
        Assert.Equal(-3, clip.GainDecibels);
        Assert.Equal(0.5, clip.Volume);
        Assert.Equal(-0.2, clip.Balance);
        Assert.True(clip.Solo);
        Assert.True(clip.Effects.ReverbEnabled);
        Assert.Equal("Keep", clip.Title);
        var project = new EditorProject { AudioTracks = [clip] };
        AudioTrackProject reopened = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project)).AudioTracks[0];
        Assert.Equal(clip.SourceInSeconds, reopened.SourceInSeconds);
        Assert.Equal(clip.SourceOutSeconds, reopened.SourceOutSeconds);
    }

    [Theory]
    [InlineData(-1, 100, 1000, 48000)]
    [InlineData(100, 100, 1000, 48000)]
    [InlineData(101, 100, 1000, 48000)]
    [InlineData(0, 1001, 1000, 48000)]
    [InlineData(0, 1, 0, 48000)]
    [InlineData(0, 1, 1000, 0)]
    [InlineData(0, 1, 9007199254740992L, 48000)]
    public void InvalidRangesLeaveClipUnchanged(long first, long end, long sourceFrames, int rate)
    {
        var clip = new AudioTrackProject { SourceInSeconds = 1, SourceOutSeconds = 2 };
        string before = EditorProjectSerialiser.Serialise(new() { AudioTracks = [clip] });
        Assert.False(AudioClipEditing.SetSourceSampleRange(clip, first, end, sourceFrames, rate));
        Assert.Equal(before, EditorProjectSerialiser.Serialise(new() { AudioTracks = [clip] }));
    }

    [Fact]
    public void EnforcesTempoAdjustedFadeDurationAndRejectsUnchangedRanges()
    {
        var clip = new AudioTrackProject { TempoPercent = 200, FadeInSeconds = 0.2, FadeOutSeconds = 0.3 };
        Assert.False(AudioClipEditing.SetSourceSampleRange(clip, 0, 47999, 96000, 48000));
        Assert.Null(clip.SourceOutSeconds);
        Assert.True(AudioClipEditing.SetSourceSampleRange(clip, 0, 48000, 96000, 48000));
        Assert.False(AudioClipEditing.SetSourceSampleRange(clip, 0, 48000, 96000, 48000));
        Assert.False(AudioClipEditing.SetSourceSampleRange(new(), 0, 96000, 96000, 48000));
    }

    [Theory]
    [InlineData(44100, 1, 1, 1)]
    [InlineData(48000, 2, 137, 333)]
    [InlineData(384000, 2, 997, 1)]
    [InlineData(44100, 1, 2047, 1)]
    [InlineData(384000, 2, 2047, 1)]
    public void FloatWaveExportContainsExactlyTheSelectedSourceSamples(int rate, int channels, int first, int count)
    {
        string input = Path.Combine(Path.GetTempPath(), $"inkmotion-samples-{Guid.NewGuid():N}.wav");
        string output = Path.ChangeExtension(input, ".out.wav");
        try
        {
            float[] signal = Enumerable.Range(0, 2048 * channels).Select(index => (index % 997) / 2000f).ToArray();
            using (var writer = new WaveFileWriter(input, WaveFormat.CreateIeeeFloatWaveFormat(rate, channels)))
            {
                writer.WriteSamples(signal, 0, signal.Length);
            }

            var clip = new AudioTrackProject { AudioPath = input };
            Assert.True(AudioClipEditing.SetSourceSampleRange(clip, first, first + count, 2048, rate));
            var project = new EditorProject
            {
                AudioMixLayout = channels == 1 ? AudioChannelLayout.Mono : AudioChannelLayout.Stereo,
                AudioTracks = [clip],
            };
            AudioExporter.Export(project, new(AudioExportCodec.FloatWave, rate, 32), output, null, CancellationToken.None);
            using var reader = new AudioFileReader(output);
            float[] actual = new float[count * channels];
            Assert.Equal(actual.Length, reader.Read(actual, 0, actual.Length));
            Assert.Equal(0, reader.Read(new float[100], 0, 100));
            Assert.Equal(signal.Skip(first * channels).Take(count * channels), actual);
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }
}
