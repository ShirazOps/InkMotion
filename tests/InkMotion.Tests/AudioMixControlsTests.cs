using NAudio.Wave;
using Xunit;

namespace InkMotion.Tests;

public sealed class AudioMixControlsTests
{
    [Theory]
    [InlineData(-1, 1, 0)]
    [InlineData(0, 1, 1)]
    [InlineData(0.5, 0.5, 1)]
    [InlineData(1, 0, 1)]
    public void StereoBalance_AttenuatesOnlyTheOppositeSide(double balance, float left, float right)
    {
        float[,] matrix = AudioMixControls.CreateMatrix(2, AudioChannelLayout.Stereo, balance);
        Assert.Equal(left, matrix[0, 0]);
        Assert.Equal(right, matrix[1, 1]);
        float[,] monitor = AudioClipPlayer.CreateMonitorMatrix(2, AudioChannelLayout.Stereo, balance);
        Assert.Equal(left, monitor[0, 0]);
        Assert.Equal(right, monitor[1, 1]);
    }

    [Fact]
    public void SurroundBalance_KeepsCentreAndLfeAndAttenuatesEveryLeftSpeaker()
    {
        var info = AudioChannelLayouts.Get(AudioChannelLayout.Surround714);
        float[,] matrix = AudioMixControls.CreateMatrix(info.ChannelCount, info.Layout, 1);
        for (int index = 0; index < info.ChannelCount; index++)
        {
            bool left = info.Speakers[index] is AudioSpeaker.FrontLeft or AudioSpeaker.BackLeft or
                AudioSpeaker.SideLeft or AudioSpeaker.TopFrontLeft or AudioSpeaker.TopBackLeft;
            Assert.Equal(left ? 0 : 1, matrix[index, index]);
        }
    }

    [Fact]
    public void VersionNineMigratesToNeutralBalanceAndNoSolo()
    {
        EditorProject project = EditorProjectSerialiser.Deserialise("""{"Version":9,"AudioTracks":[{"AudioPath":"voice.wav"}]}""");
        Assert.Equal(EditorProject.CurrentVersion, project.Version);
        Assert.False(project.AudioTracks[0].Solo);
        Assert.Equal(0, project.AudioTracks[0].Balance);
        project.AudioTracks[0].Solo = true;
        project.AudioTracks[0].Balance = -0.75;
        EditorProject reopened = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        Assert.True(reopened.AudioTracks[0].Solo);
        Assert.Equal(-0.75, reopened.AudioTracks[0].Balance);
        AudioTrackProject copy = AudioClipEditing.Clone(reopened.AudioTracks[0]);
        Assert.True(copy.Solo);
        Assert.Equal(-0.75, copy.Balance);
        copy.Balance = 2;
        Assert.False(AudioClipEditing.HasValidClipSettings(copy));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioMixControls.CreateMatrix(2, AudioChannelLayout.Stereo, double.NaN));
    }

    [Theory]
    [InlineData(false, 0.125f)]
    [InlineData(true, 0f)]
    public void SoloAndBalance_ApplyToRealExportAndMuteWins(bool muted, float expectedLeft)
    {
        string input = Path.Combine(Path.GetTempPath(), $"inkmotion-solo-{Guid.NewGuid():N}.wav");
        string output = Path.ChangeExtension(input, ".out.wav");
        try
        {
            using (var writer = new WaveFileWriter(input, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)))
            {
                writer.WriteSamples(Enumerable.Repeat(0.25f, 48000).ToArray(), 0, 48000);
            }

            var project = new EditorProject
            {
                AudioTracks = [
                    new() { AudioPath = input, Solo = true, Balance = 0.5, Muted = muted },
                    new() { AudioPath = input },
                ],
                VideoTracks = [new()
                {
                    Kind = TimelineTrackKind.Video,
                    Clips = [new() { SourcePath = input, SourceDurationSeconds = 0.5 }],
                }],
            };
            AudioExporter.Export(project, new(AudioExportCodec.FloatWave, 48000, 32), output, null, CancellationToken.None);
            using var reader = new AudioFileReader(output);
            float[] samples = new float[48000];
            Assert.Equal(samples.Length, reader.Read(samples, 0, samples.Length));
            Assert.InRange(Math.Abs(samples[24000] - expectedLeft), 0, 0.00001f);
            Assert.InRange(Math.Abs(samples[24001] - (muted ? 0 : 0.25f)), 0, 0.00001f);
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }
}
