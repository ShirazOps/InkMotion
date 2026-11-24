using NAudio.Wave;
using Xunit;

namespace InkMotion.Tests;

public sealed class AudioBusTests
{
    [Fact]
    public void VersionSeventyNineMigratesWithNoBusesAndMasterRouting()
    {
        EditorProject project = EditorProjectSerialiser.Deserialise("""{"Version":79,"AudioTracks":[{}]}""");
        Assert.Equal(EditorProject.CurrentVersion, project.Version);
        Assert.Empty(project.AudioBuses);
        Assert.Null(project.AudioTracks[0].BusId);
    }

    [Fact]
    public void BusesAndRoutesRoundTrip()
    {
        var bus = new AudioBusProject { Name = "Dialogue", GainDecibels = -3, Solo = true };
        var project = new EditorProject
        {
            AudioBuses = [bus],
            AudioTracks = [new() { BusId = bus.Id }],
            VideoTracks = [new() { Kind = TimelineTrackKind.Video, AudioBusId = bus.Id }],
        };

        EditorProject reopened = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        AudioBusProject restored = Assert.Single(reopened.AudioBuses);
        Assert.Equal(bus.Id, restored.Id);
        Assert.Equal("Dialogue", restored.Name);
        Assert.Equal(-3, restored.GainDecibels);
        Assert.True(restored.Solo);
        Assert.Equal(bus.Id, reopened.AudioTracks[0].BusId);
        Assert.Equal(bus.Id, reopened.VideoTracks[0].AudioBusId);
    }

    [Fact]
    public void ValidationRejectsInvalidBusesAndRoutes()
    {
        var bus = new AudioBusProject();
        Assert.NotNull(AudioBuses.Validate(new EditorProject { AudioTracks = [new() { BusId = "missing" }] }));
        Assert.NotNull(AudioBuses.Validate(new EditorProject { AudioBuses = [bus, AudioBuses.Clone(bus)] }));
        Assert.NotNull(AudioBuses.Validate(new EditorProject { AudioBuses = [new() { GainDecibels = 20 }] }));
        Assert.NotNull(AudioBuses.Validate(new EditorProject
        {
            AudioBuses = [new() { Effects = new AudioEffectSettings { NoiseReductionEnabled = true } }],
        }));
        Assert.NotNull(AudioBuses.Validate(new EditorProject
        {
            AudioBuses = [bus],
            VideoTracks = [new() { Kind = TimelineTrackKind.Text, AudioBusId = bus.Id }],
        }));
        Assert.Null(AudioBuses.Validate(new EditorProject
        {
            AudioBuses = [bus],
            AudioTracks = [new() { BusId = bus.Id }],
            VideoTracks = [new() { Kind = TimelineTrackKind.Video, AudioBusId = bus.Id }],
        }));
        Assert.Throws<InvalidDataException>(() => EditorProjectSerialiser.Deserialise(
            """{"Version":80,"AudioTracks":[{"BusId":"missing"}]}"""));
    }

    [Fact]
    public void MuteAndSoloDecideWhichRoutesAreAudible()
    {
        var dialogue = new AudioBusProject { Name = "Dialogue" };
        var music = new AudioBusProject { Name = "Music" };
        List<AudioBusProject> buses = [dialogue, music];

        Assert.True(AudioBuses.IsAudible(buses, null));
        Assert.True(AudioBuses.IsAudible(buses, dialogue.Id));
        Assert.True(AudioBuses.IsAudible(null, "missing"));

        music.Muted = true;
        Assert.False(AudioBuses.IsAudible(buses, music.Id));

        music.Muted = false;
        dialogue.Solo = true;
        Assert.True(AudioBuses.IsAudible(buses, dialogue.Id));
        Assert.False(AudioBuses.IsAudible(buses, music.Id));
        Assert.False(AudioBuses.IsAudible(buses, null));

        dialogue.Muted = true;
        Assert.False(AudioBuses.IsAudible(buses, dialogue.Id));
    }

    [Fact]
    public void ClearRoutesReturnsSourcesToMasterAndNamesSkipTakenNumbers()
    {
        var bus = new AudioBusProject { Name = "Bus 2" };
        var project = new EditorProject
        {
            AudioBuses = [bus],
            AudioTracks = [new() { BusId = bus.Id }, new() { BusId = "other" }],
            VideoTracks = [new() { Kind = TimelineTrackKind.Video, AudioBusId = bus.Id }],
        };

        AudioBuses.ClearRoutes(project, bus.Id);
        Assert.Null(project.AudioTracks[0].BusId);
        Assert.Equal("other", project.AudioTracks[1].BusId);
        Assert.Null(project.VideoTracks[0].AudioBusId);

        Assert.Equal("Bus 1", AudioBuses.CreateName([]));
        Assert.Equal("Bus 3", AudioBuses.CreateName([bus, new AudioBusProject { Name = "Bus 1" }]));
    }

    [Fact]
    public void VideoPreviewHonoursBusRoutingMuteAndSolo()
    {
        var bus = new AudioBusProject();
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Video, AudioBusId = bus.Id };
        var active = new TimelineClipLocation(track, new TimelineClipProject { SourcePath = "source.mp4" });

        AudioTrackProject routed = VideoTimelineEditing.CreatePreviewAudioProject(active, false, false, [bus]);
        Assert.Equal(bus.Id, routed.BusId);
        Assert.False(routed.Muted);

        bus.Muted = true;
        Assert.True(VideoTimelineEditing.CreatePreviewAudioProject(active, false, false, [bus]).Muted);

        AudioTrackProject original = VideoTimelineEditing.CreatePreviewAudioProject(active, true, false, [bus]);
        Assert.Null(original.BusId);
        Assert.False(original.Muted);

        bus.Muted = false;
        var other = new AudioBusProject { Solo = true };
        Assert.True(VideoTimelineEditing.CreatePreviewAudioProject(active, false, false, [bus, other]).Muted);
    }

    [Theory]
    [InlineData(-6.0206, false, false, 0.125f)]
    [InlineData(0, true, false, 0f)]
    [InlineData(0, false, true, 0.25f)]
    public void ExportAppliesBusGainMuteAndSolo(double gainDecibels, bool muted, bool soloOtherBus, float expected)
    {
        string input = Path.Combine(Path.GetTempPath(), $"inkmotion-bus-{Guid.NewGuid():N}.wav");
        try
        {
            WriteConstant(input, 0.25f);
            var bus = new AudioBusProject { GainDecibels = gainDecibels, Muted = muted };
            var other = new AudioBusProject { Name = "Other", Solo = soloOtherBus };
            var project = new EditorProject
            {
                AudioBuses = [bus, other],
                AudioTracks = [new() { AudioPath = input, BusId = soloOtherBus ? other.Id : bus.Id }],
            };
            if (soloOtherBus)
            {
                project.AudioTracks.Add(new() { AudioPath = input });
            }

            using var mixer = new ExportAudioMixer(project, 48000);
            float[] samples = new float[mixer.Channels * 1024];
            Assert.Equal(samples.Length, mixer.Read(samples, 0, samples.Length));
            Assert.InRange(Math.Abs(samples[1000] - expected), 0, 0.0005f);
        }
        finally
        {
            File.Delete(input);
        }
    }

    [Fact]
    public void ExportAppliesBusEffectsToTheSummedSources()
    {
        string input = Path.Combine(Path.GetTempPath(), $"inkmotion-bus-fx-{Guid.NewGuid():N}.wav");
        try
        {
            WriteConstant(input, 0.25f);
            var bus = new AudioBusProject
            {
                Effects = new AudioEffectSettings { Enabled = true, HighPassHz = 1000 },
            };
            Assert.True(AudioBuses.HasValidSettings(bus));
            var project = new EditorProject
            {
                AudioBuses = [bus],
                AudioTracks = [new() { AudioPath = input, BusId = bus.Id }, new() { AudioPath = input, BusId = bus.Id }],
            };

            using var mixer = new ExportAudioMixer(project, 48000);
            float[] samples = new float[mixer.Channels * 24000];
            Assert.Equal(samples.Length, mixer.Read(samples, 0, samples.Length));
            Assert.InRange(Math.Abs(samples[^2]), 0, 0.01f);
        }
        finally
        {
            File.Delete(input);
        }
    }

    private static void WriteConstant(string path, float value)
    {
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        writer.WriteSamples(Enumerable.Repeat(value, 96000).ToArray(), 0, 96000);
    }
}
