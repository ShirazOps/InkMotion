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
    public void VersionEightyOneMigratesBusRoutesToMaster()
    {
        EditorProject project = EditorProjectSerialiser.Deserialise(
            """{"Version":81,"AudioBuses":[{"Id":"bus-parent","Name":"Parent"}],"AudioTracks":[{"BusId":"bus-parent"}]}""");

        Assert.Equal(EditorProject.CurrentVersion, project.Version);
        Assert.Null(Assert.Single(project.AudioBuses).ParentBusId);
        Assert.Equal("bus-parent", project.AudioTracks[0].BusId);
    }

    [Fact]
    public void VersionEightyTwoMigratesWithNoAuxiliarySends()
    {
        EditorProject project = EditorProjectSerialiser.Deserialise(
            """{"Version":82,"AudioTracks":[{}]}""");

        Assert.Equal(EditorProject.CurrentVersion, project.Version);
        Assert.Empty(project.AudioTracks[0].AuxSends);
    }

    [Fact]
    public void BusesAndRoutesRoundTrip()
    {
        var parent = new AudioBusProject { Name = "Dialogue", GainDecibels = -3, Solo = true };
        var bus = new AudioBusProject
        {
            Name = "Close mic",
            ParentBusId = parent.Id,
            Vst3Plugins = [new AudioVst3PluginProject
            {
                ModulePath = @"C:\Plugins\room.vst3",
                ClassId = "room-reverb",
                Name = "Room Reverb",
                ParameterValues = new Dictionary<uint, double> { [42] = 0.75 },
            }],
        };
        var project = new EditorProject
        {
            AudioBuses = [parent, bus],
            AudioTracks = [new()
            {
                BusId = bus.Id,
                AuxSends = [new AudioAuxSendProject { BusId = parent.Id, GainDecibels = -9 }],
                Vst3Plugins = [new AudioVst3PluginProject
                {
                    ModulePath = @"C:\Plugins\compressor.vst3",
                    ClassId = "compressor",
                    Name = "Compressor",
                    ParameterValues = new Dictionary<uint, double> { [9] = 0.25 },
                }],
            }],
            VideoTracks = [new() { Kind = TimelineTrackKind.Video, AudioBusId = parent.Id }],
        };

        EditorProject reopened = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        Assert.Equal(2, reopened.AudioBuses.Count);
        AudioBusProject restored = reopened.AudioBuses[1];
        Assert.Equal(bus.Id, restored.Id);
        Assert.Equal("Close mic", restored.Name);
        Assert.Equal(parent.Id, restored.ParentBusId);
        Assert.Equal(-3, reopened.AudioBuses[0].GainDecibels);
        Assert.True(reopened.AudioBuses[0].Solo);
        Assert.Equal(bus.Id, reopened.AudioTracks[0].BusId);
        Assert.Equal(parent.Id, reopened.VideoTracks[0].AudioBusId);
        Assert.Equal(parent.Id, Assert.Single(reopened.AudioTracks[0].AuxSends).BusId);
        Assert.Equal(-9, reopened.AudioTracks[0].AuxSends[0].GainDecibels);
        Assert.Equal(0.75, Assert.Single(restored.Vst3Plugins).ParameterValues[42]);
        Assert.Equal(0.25, Assert.Single(reopened.AudioTracks[0].Vst3Plugins).ParameterValues[9]);
        AudioBusProject busClone = AudioBuses.Clone(bus);
        AudioVst3PluginProject clonedBusPlugin = Assert.Single(busClone.Vst3Plugins);
        Assert.NotSame(bus.Vst3Plugins[0], clonedBusPlugin);
        Assert.NotSame(bus.Vst3Plugins[0].ParameterValues, clonedBusPlugin.ParameterValues);
        AudioTrackProject clone = AudioClipEditing.Clone(project.AudioTracks[0]);
        Assert.Equal(parent.Id, Assert.Single(clone.AuxSends).BusId);
        Assert.NotSame(project.AudioTracks[0].AuxSends[0], clone.AuxSends[0]);
        AudioVst3PluginProject clonedTrackPlugin = Assert.Single(clone.Vst3Plugins);
        Assert.NotSame(project.AudioTracks[0].Vst3Plugins[0], clonedTrackPlugin);
        Assert.NotSame(project.AudioTracks[0].Vst3Plugins[0].ParameterValues, clonedTrackPlugin.ParameterValues);
    }

    [Fact]
    public void ValidationRejectsInvalidVst3InsertSettings()
    {
        var invalidPlugin = new AudioVst3PluginProject
        {
            ModulePath = @"C:\Plugins\compressor.vst3",
            ClassId = "compressor",
            Name = "Compressor",
            ParameterValues = new Dictionary<uint, double> { [9] = 1.5 },
        };

        Assert.NotNull(AudioBuses.Validate(new EditorProject
        {
            AudioTracks = [new AudioTrackProject { Vst3Plugins = [invalidPlugin] }],
        }));
        Assert.NotNull(AudioBuses.Validate(new EditorProject
        {
            AudioBuses = [new AudioBusProject { Vst3Plugins = [invalidPlugin] }],
        }));
    }

    [Fact]
    public void UnapprovedSavedVst3InsertIsBypassedWithoutLoadingItsModule()
    {
        var source = new ConstantSampleProvider(0.25f, 48000, 2);
        var plugin = new AudioVst3PluginProject
        {
            Id = Guid.NewGuid().ToString("N"),
            ModulePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.vst3"),
            ClassId = Guid.NewGuid().ToString("N"),
            Name = "Unapproved test effect",
            Enabled = true,
        };
        using var chain = new Vst3InsertChainSampleProvider(source, [plugin]);
        var samples = new float[16];

        Assert.Contains("not been approved", chain.FailureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(samples.Length, chain.Read(samples, 0, samples.Length));
        Assert.All(samples, sample => Assert.Equal(0.25f, sample));
    }

    [Fact]
    public void InsertWithUnreadableApprovalIdentityIsBypassed()
    {
        var source = new ConstantSampleProvider(0.25f, 48000, 2);
        var plugin = new AudioVst3PluginProject
        {
            Id = Guid.NewGuid().ToString("N"),
            ModulePath = "\0invalid",
            ClassId = "test-effect",
            Name = "Invalid approval identity",
            Enabled = true,
        };
        using var chain = new Vst3InsertChainSampleProvider(source, [plugin]);
        var samples = new float[16];

        Assert.Contains("approval could not be checked", chain.FailureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(samples.Length, chain.Read(samples, 0, samples.Length));
        Assert.All(samples, sample => Assert.Equal(0.25f, sample));
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
        AudioBusProject parent = new();
        Assert.NotNull(AudioBuses.Validate(new EditorProject
        {
            AudioBuses = [parent, new AudioBusProject { ParentBusId = "missing" }],
        }));
        AudioBusProject first = new();
        AudioBusProject second = new() { ParentBusId = first.Id };
        first.ParentBusId = second.Id;
        Assert.NotNull(AudioBuses.Validate(new EditorProject
        {
            AudioBuses = [first, second],
        }));
        Assert.NotNull(AudioBuses.Validate(new EditorProject
        {
            AudioBuses = [parent],
            AudioTracks = [new() { AuxSends = [new AudioAuxSendProject { BusId = "missing" }] }],
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

    private sealed class ConstantSampleProvider(float value, int sampleRate, int channels) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

        public int Read(float[] buffer, int offset, int count)
        {
            Array.Fill(buffer, value, offset, count);
            return count;
        }
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
    public void ParentMuteAndSoloApplyToDescendantRoutes()
    {
        var parent = new AudioBusProject();
        var child = new AudioBusProject { ParentBusId = parent.Id };
        var other = new AudioBusProject();
        List<AudioBusProject> buses = [parent, child, other];

        parent.Muted = true;
        Assert.False(AudioBuses.IsAudible(buses, child.Id));
        parent.Muted = false;
        parent.Solo = true;
        Assert.True(AudioBuses.IsAudible(buses, child.Id));
        Assert.False(AudioBuses.IsAudible(buses, other.Id));
        Assert.False(AudioBuses.IsAudible(buses, null));
    }

    [Fact]
    public void ClearRoutesReturnsSourcesToMasterAndNamesSkipTakenNumbers()
    {
        var parent = new AudioBusProject { Name = "Parent" };
        var bus = new AudioBusProject { Name = "Bus 2", ParentBusId = parent.Id };
        var child = new AudioBusProject { Name = "Child", ParentBusId = bus.Id };
        var project = new EditorProject
        {
            AudioBuses = [parent, bus, child],
            AudioTracks =
            [
                new()
                {
                    BusId = bus.Id,
                    AuxSends = [new AudioAuxSendProject { BusId = bus.Id }],
                },
                new() { BusId = "other" },
            ],
            VideoTracks = [new() { Kind = TimelineTrackKind.Video, AudioBusId = bus.Id }],
        };

        AudioBuses.ClearRoutes(project, bus.Id);
        Assert.Null(project.AudioTracks[0].BusId);
        Assert.Equal("other", project.AudioTracks[1].BusId);
        Assert.Null(project.VideoTracks[0].AudioBusId);
        Assert.Equal(parent.Id, child.ParentBusId);
        Assert.Equal(parent.Id, Assert.Single(project.AudioTracks[0].AuxSends).BusId);

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

    [Fact]
    public void ExportRoutesNestedBusesThroughEachGainStage()
    {
        string input = Path.Combine(Path.GetTempPath(), $"inkmotion-nested-bus-{Guid.NewGuid():N}.wav");
        try
        {
            WriteConstant(input, 0.25f);
            var parent = new AudioBusProject { GainDecibels = -6.0206 };
            var child = new AudioBusProject { ParentBusId = parent.Id, GainDecibels = -6.0206 };
            var project = new EditorProject
            {
                AudioBuses = [parent, child],
                AudioTracks = [new() { AudioPath = input, BusId = child.Id }],
            };

            using var mixer = new ExportAudioMixer(project, 48000);
            float[] samples = new float[mixer.Channels * 1024];
            Assert.Equal(samples.Length, mixer.Read(samples, 0, samples.Length));
            Assert.InRange(Math.Abs(samples[1000] - 0.0625f), 0, 0.0005f);
        }
        finally
        {
            File.Delete(input);
        }
    }

    [Fact]
    public void ExportIncludesPostFaderTrackSendsInTheirTargetBus()
    {
        string input = Path.Combine(Path.GetTempPath(), $"inkmotion-aux-send-{Guid.NewGuid():N}.wav");
        try
        {
            WriteConstant(input, 0.25f);
            var bus = new AudioBusProject { GainDecibels = -6.0206 };
            var project = new EditorProject
            {
                AudioBuses = [bus],
                AudioTracks =
                [
                    new()
                    {
                        AudioPath = input,
                        AuxSends = [new AudioAuxSendProject { BusId = bus.Id, GainDecibels = -6.0206 }],
                    },
                ],
            };

            using var mixer = new ExportAudioMixer(project, 48000);
            float[] samples = new float[mixer.Channels * 1024];
            Assert.Equal(samples.Length, mixer.Read(samples, 0, samples.Length));
            Assert.Equal(2, mixer.SourceCount);
            Assert.InRange(Math.Abs(samples[1000] - 0.3125f), 0, 0.0005f);
        }
        finally
        {
            File.Delete(input);
        }
    }

    [Fact]
    public void SoloedBusCanBeHeardThroughATrackAuxiliarySend()
    {
        string input = Path.Combine(Path.GetTempPath(), $"inkmotion-aux-solo-{Guid.NewGuid():N}.wav");
        try
        {
            WriteConstant(input, 0.25f);
            var bus = new AudioBusProject { Solo = true };
            var project = new EditorProject
            {
                AudioBuses = [bus],
                AudioTracks =
                [
                    new()
                    {
                        AudioPath = input,
                        Volume = 0.5,
                        AuxSends = [new AudioAuxSendProject { BusId = bus.Id, GainDecibels = 0 }],
                    },
                ],
            };

            using var mixer = new ExportAudioMixer(project, 48000);
            float[] samples = new float[mixer.Channels * 1024];
            Assert.Equal(samples.Length, mixer.Read(samples, 0, samples.Length));
            Assert.InRange(Math.Abs(samples[1000] - 0.125f), 0, 0.0005f);
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
