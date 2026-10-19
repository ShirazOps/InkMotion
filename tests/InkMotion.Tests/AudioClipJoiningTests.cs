using NAudio.Wave;
using Xunit;

namespace InkMotion.Tests;

public sealed class AudioClipJoiningTests
{
    private static (AudioTrackProject Left, AudioTrackProject Right) Split()
    {
        var left = new AudioTrackProject
        {
            AudioPath = Path.Combine(Path.GetTempPath(), "join-source.wav"), StartFrame = 25,
            SourceInSeconds = 1, SourceOutSeconds = 5, TempoPercent = 200,
            FadeInSeconds = 0.2, FadeOutSeconds = 0.3, Title = "Voice", Artist = "Artist",
            Effects = new() { Enabled = true, HighPassHz = 100, GateEnabled = true },
        };
        AudioTrackProject right = AudioClipEditing.Split(left, 49, 24, 6)!;
        return (left, right);
    }

    [Fact]
    public void Join_ReversesSplitWithTrimTempoOuterFadesAndIndependentSettings()
    {
        var (left, right) = Split();
        AudioTrackProject joined = AudioClipJoining.Join(left, right, 24, 6);
        Assert.Equal(25, joined.StartFrame);
        Assert.Equal(1, joined.SourceInSeconds);
        Assert.Equal(5, joined.SourceOutSeconds);
        Assert.Equal(2, AudioClipEditing.GetTimelineDuration(joined, 6));
        Assert.Equal(0.2, joined.FadeInSeconds);
        Assert.Equal(0.3, joined.FadeOutSeconds);
        Assert.Equal(left.Effects, joined.Effects);
        Assert.Equal("Voice", joined.Title);
        joined.Title = "Changed";
        joined.SourceInSeconds = 0;
        Assert.Equal(1, left.SourceInSeconds);
        Assert.Equal("Voice", left.Title);
        Assert.Equal(3, left.SourceOutSeconds);
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("overlap")]
    [InlineData("source")]
    [InlineData("path")]
    [InlineData("fade")]
    [InlineData("gain")]
    [InlineData("tags")]
    [InlineData("effects")]
    [InlineData("solo")]
    public void Join_RejectsIncompatibleClipsWithoutChangingEitherInput(string change)
    {
        var (left, right) = Split();
        switch (change)
        {
            case "gap": right.StartFrame++; break;
            case "overlap": right.StartFrame--; break;
            case "source": right.SourceInSeconds += 0.1; break;
            case "path": right.AudioPath += ".other.wav"; break;
            case "fade": right.FadeInSeconds = 0.01; break;
            case "gain": right.GainDecibels = -3; break;
            case "tags": right.Title = "Different"; break;
            case "effects": right.Effects = new(); break;
            case "solo": right.Solo = true; break;
        }

        double? beforeLeftEnd = left.SourceOutSeconds;
        double beforeRightStart = right.SourceInSeconds;
        Assert.Throws<InvalidOperationException>(() => AudioClipJoining.Join(left, right, 24, 6));
        Assert.Equal(beforeLeftEnd, left.SourceOutSeconds);
        Assert.Equal(beforeRightStart, right.SourceInSeconds);
    }

    [Fact]
    public void JoinedClip_SaveLoadAndExportPreserveTheUnsplitAudioExactly()
    {
        string path = Path.Combine(Path.GetTempPath(), $"inkmotion-join-{Guid.NewGuid():N}.wav");
        string output = Path.ChangeExtension(path, ".out.wav");
        try
        {
            float[] samples = Enumerable.Range(0, 48000).Select(index => (float)(0.25 * Math.Sin(index * 2 * Math.PI * 440 / 48000))).ToArray();
            using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(48000, 1)))
            {
                writer.WriteSamples(samples, 0, samples.Length);
            }

            var left = new AudioTrackProject { AudioPath = path };
            AudioTrackProject right = AudioClipEditing.Split(left, 13, 24, 1)!;
            var project = new EditorProject
            {
                AudioMixLayout = AudioChannelLayout.Mono,
                AudioTracks = [AudioClipJoining.Join(left, right, 24, 1)],
            };
            project = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
            AudioExporter.Export(project, new(AudioExportCodec.FloatWave, 48000, 32), output, null, CancellationToken.None);
            using var reader = new AudioFileReader(output);
            float[] decoded = new float[samples.Length];
            Assert.Equal(samples.Length, reader.Read(decoded, 0, decoded.Length));
            Assert.Equal(samples, decoded);
        }
        finally
        {
            File.Delete(path);
            File.Delete(output);
        }
    }
}
