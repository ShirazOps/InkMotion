using NAudio.Wave;
using Xunit;

namespace InkMotion.Tests;

public sealed class AafInterchangeTests
{
    [Fact]
    public void AudioProject_PreservesSourceTrimAndRecordOffset()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"inkmotion-aaf-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "voice.wav");
            WriteWave(path, channels: 1);
            var project = new EditorProject
            {
                AudioTracks = [new() { AudioPath = path, StartFrame = 49, SourceInSeconds = 1, SourceOutSeconds = 3 }],
            };
            AafAudioTimeline timeline = AafInterchange.FromProject(project);
            Assert.Equal(new AafAudioCut(path, 48, 24, 48), Assert.Single(timeline.Clips));
            var restored = AafInterchange.ToProject(timeline);
            Assert.Equal(49, restored.AudioTracks[0].StartFrame);
            Assert.Equal(1, restored.AudioTracks[0].SourceInSeconds);
            Assert.Equal(3, restored.AudioTracks[0].SourceOutSeconds);
            project.AudioTracks[0].FadeInSeconds = 0.1;
            Assert.Contains("FadeInSeconds", Assert.Throws<InvalidDataException>(() => AafInterchange.FromProject(project)).Message);
            project.AudioTracks[0].FadeInSeconds = 0;
            WriteWave(path, channels: 2);
            Assert.Throws<InvalidDataException>(() => AafInterchange.FromProject(project));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void MalformedBridgeOutput_CannotCreateInvalidProject()
    {
        string path = Path.Combine(Path.GetTempPath(), "source.wav");
        Assert.Throws<InvalidDataException>(() => AafInterchange.ToProject(new(24, [new(path, -1, 0, 24)])));
        Assert.Throws<InvalidDataException>(() => AafInterchange.ToProject(new(24, [new(path, 0, int.MaxValue, 24)])));
        Assert.Throws<InvalidDataException>(() => AafInterchange.ToProject(new(24, [new("relative.wav", 0, 0, 24)])));
        Assert.Throws<InvalidDataException>(() => AafInterchange.ToProject(new(25, [new(path, 0, 0, 24)])));
        Assert.Throws<InvalidDataException>(() => AafInterchange.ToProject(new(24, [])));
    }

    [Fact]
    public void RealBinaryAaf_ExportsAndImportsWhenToolIsSelected()
    {
        string? python = Environment.GetEnvironmentVariable("INKMOTION_TEST_AAF_PYTHON");
        string? directory = Environment.GetEnvironmentVariable("INKMOTION_INTERCHANGE_FIXTURES");
        if (python is null || directory is null) return;
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "linked mono.wav");
        WriteWave(path, channels: 1);
        var timeline = new AafAudioTimeline(24,
            [new(path, 48, 24, 48), new(path, 144, 48, 24)]);
        string output = Path.Combine(directory, "inkmotion-audio.aaf");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        AafInterchange.Export(timeline, output, python, timeout.Token);
        EditorProject imported = AafInterchange.Import(output, python, timeout.Token);
        Assert.Equal(timeline.Clips, AafInterchange.FromProject(imported).Clips);
        string external = Path.Combine(directory, "external-audio.aaf");
        if (File.Exists(external))
        {
            var fromExternal = AafInterchange.Import(external, python, timeout.Token);
            var clip = Assert.Single(fromExternal.AudioTracks);
            Assert.Equal(97, clip.StartFrame);
            Assert.Equal(3, clip.SourceInSeconds);
            Assert.Equal(4, clip.SourceOutSeconds);
        }
        foreach (string name in new[] { "unsupported-picture.aaf", "unsupported-stereo.aaf", "unsupported-cycle.aaf" })
        {
            string invalid = Path.Combine(directory, name);
            if (File.Exists(invalid))
                Assert.Throws<InvalidOperationException>(() => AafInterchange.Import(invalid, python, timeout.Token));
        }
    }

    private static void WriteWave(string path, int channels)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(48000, 16, channels));
        writer.Write(new byte[48000 * 2 * channels * 10], 0, 48000 * 2 * channels * 10);
    }
}
