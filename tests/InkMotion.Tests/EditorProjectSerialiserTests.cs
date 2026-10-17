using Xunit;

namespace InkMotion.Tests;

public sealed class EditorProjectSerialiserTests
{
    [Fact]
    public void Deserialise_ProjectWithoutAudioMetadata_UsesAudioDefaults()
    {
        EditorProject restored = EditorProjectSerialiser.Deserialise(
            """{"Version":1,"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""");

        Assert.Null(restored.AudioPath);
        Assert.Equal(1, restored.AudioVolume);
        Assert.False(restored.AudioMuted);
    }

    [Fact]
    public void Deserialise_VersionOneProject_MigratesToCurrentVersion()
    {
        EditorProject restored = EditorProjectSerialiser.Deserialise(
            """{"Version":1,"Frames":[{"Number":1,"Strokes":[[{"X":0.2,"Y":0.3}]]}],"SelectedFrameIndex":0}""");

        Assert.Equal(3, restored.Version);
        Assert.Equal(0.5, restored.Frames[0].Strokes[0][0].Pressure);
        Assert.Equal(4, restored.Frames[0].Strokes[0][0].BrushWidth);
        Assert.Equal(0xFFFFA500u, restored.Frames[0].Strokes[0][0].BrushColour);
    }

    [Fact]
    public void RoundTrip_PreservesVideoFramesInkAndKeyframes()
    {
        var original = new EditorProject
        {
            VideoPath = @"C:\Media\scene.mp4",
            AudioPath = @"C:\Media\music.wav",
            AudioVolume = 0.65,
            AudioMuted = true,
            SelectedFrameIndex = 1,
            Frames =
            [
                new EditorFrame
                {
                    Number = 1,
                    Strokes = [[new InkPoint { X = 0.15, Y = 0.25 }]],
                },
                new EditorFrame
                {
                    Number = 2,
                    IsKeyframe = true,
                    Strokes =
                    [
                        [
                            new InkPoint { X = 0.4, Y = 0.6 },
                            new InkPoint { X = 0.8, Y = 0.9 },
                        ],
                    ],
                },
            ],
            FrameRate = 30,
        };

        EditorProject restored = EditorProjectSerialiser.Deserialise(
            EditorProjectSerialiser.Serialise(original));

        Assert.Equal(3, restored.Version);
        Assert.Equal(original.VideoPath, restored.VideoPath);
        Assert.Equal(original.AudioPath, restored.AudioPath);
        Assert.Equal(original.AudioVolume, restored.AudioVolume);
        Assert.Equal(original.AudioMuted, restored.AudioMuted);
        Assert.Equal(original.SelectedFrameIndex, restored.SelectedFrameIndex);
        Assert.Equal(30, restored.FrameRate);
        Assert.Collection(
            restored.Frames,
            frame => Assert.Equal(1, frame.Number),
            frame => Assert.Equal(2, frame.Number));
        Assert.Single(restored.Frames[0].Strokes);
        Assert.Equal((0.15, 0.25), (restored.Frames[0].Strokes[0][0].X, restored.Frames[0].Strokes[0][0].Y));
        Assert.True(restored.Frames[1].IsKeyframe);
        Assert.Equal(2, restored.Frames[1].Strokes[0].Count);
    }

    [Fact]
    public void RoundTrip_PreservesInkPressure()
    {
        var original = new EditorProject
        {
            Frames =
            [
                new EditorFrame
                {
                    Number = 1,
                    Strokes =
                    [
                        [
                            new InkPoint { X = 0.2, Y = 0.3, Pressure = 0.15 },
                            new InkPoint { X = 0.8, Y = 0.9, Pressure = 0.95 },
                        ],
                    ],
                },
            ],
        };

        EditorProject restored = EditorProjectSerialiser.Deserialise(
            EditorProjectSerialiser.Serialise(original));

        Assert.Equal(0.15, restored.Frames[0].Strokes[0][0].Pressure);
        Assert.Equal(0.95, restored.Frames[0].Strokes[0][1].Pressure);
    }

    [Fact]
    public void RoundTrip_PreservesInkBrushSettings()
    {
        var original = new EditorProject
        {
            Frames =
            [
                new EditorFrame
                {
                    Number = 1,
                    Strokes = [[new InkPoint
                    {
                        X = 0.2,
                        Y = 0.3,
                        BrushWidth = 8.5,
                        BrushColour = 0xFF00BFFF,
                    }]],
                },
            ],
        };

        string serialised = EditorProjectSerialiser.Serialise(original);
        Assert.Contains("\"BrushColor\":", serialised);
        EditorProject restored = EditorProjectSerialiser.Deserialise(serialised);

        InkPoint point = Assert.Single(Assert.Single(restored.Frames[0].Strokes));
        Assert.Equal(8.5, point.BrushWidth);
        Assert.Equal(0xFF00BFFFu, point.BrushColour);
    }

    [Fact]
    public void Deserialise_VersionTwoProject_MigratesToBaseInkLayer()
    {
        EditorProject restored = EditorProjectSerialiser.Deserialise(
            """{"Version":2,"Frames":[{"Number":1,"Strokes":[[{"X":0.2,"Y":0.3}]]}],"SelectedFrameIndex":0}""");

        Assert.Equal(3, restored.Version);
        Assert.Equal("ink-1", restored.ActiveInkLayerId);
        Assert.Equal("Ink 1", Assert.Single(restored.InkLayers).Name);
        Assert.Single(restored.Frames[0].Strokes);
    }

    [Fact]
    public void RoundTrip_PreservesIndependentInkLayersAndFrameStrokes()
    {
        var original = new EditorProject
        {
            InkLayers =
            [
                new InkLayerProject { Id = "ink-1", Name = "Sketch", Opacity = 0.8 },
                new InkLayerProject { Id = "ink-colour", Name = "Colour", IsVisible = false, Opacity = 0.45 },
            ],
            ActiveInkLayerId = "ink-colour",
            Frames =
            [
                new EditorFrame
                {
                    Number = 1,
                    Strokes = [[new InkPoint { X = 0.1, Y = 0.2 }]],
                    LayerStrokes = new Dictionary<string, List<List<InkPoint>>>
                    {
                        ["ink-colour"] = [[new InkPoint { X = 0.7, Y = 0.8 }]],
                    },
                },
            ],
        };

        EditorProject restored = EditorProjectSerialiser.Deserialise(
            EditorProjectSerialiser.Serialise(original));

        Assert.Equal("ink-colour", restored.ActiveInkLayerId);
        Assert.Collection(restored.InkLayers,
            layer => Assert.Equal(("Sketch", true, 0.8), (layer.Name, layer.IsVisible, layer.Opacity)),
            layer => Assert.Equal(("Colour", false, 0.45), (layer.Name, layer.IsVisible, layer.Opacity)));
        Assert.Equal(0.1, restored.Frames[0].Strokes[0][0].X);
        Assert.Equal(0.7, restored.Frames[0].LayerStrokes["ink-colour"][0][0].X);
    }

    [Fact]
    public void Deserialise_LegacyInkWithoutPressureUsesNeutralPressure()
    {
        EditorProject restored = EditorProjectSerialiser.Deserialise(
            """{"Version":1,"Frames":[{"Number":1,"Strokes":[[{"X":0.2,"Y":0.3}]]}],"SelectedFrameIndex":0}""");

        Assert.Equal(0.5, restored.Frames[0].Strokes[0][0].Pressure);
    }

    [Fact]
    public void RoundTrip_PreservesMultipleAudioTracks()
    {
        var original = new EditorProject
        {
            AudioTracks =
            [
                new AudioTrackProject
                {
                    AudioPath = @"C:\Media\music.wav",
                    StartFrame = 25,
                    Volume = 0.7,
                    Title = "Theme",
                    Artist = "InkMotion",
                    Album = "Drafts",
                    Genre = "Ambient",
                    Comments = "Project-only comment",
                    Year = 2026,
                    TagsInitialised = true,
                },
                new AudioTrackProject
                {
                    AudioPath = @"C:\Media\voice.mp3",
                    StartFrame = 73,
                    Volume = 0.4,
                    Muted = true,
                },
            ],
        };

        EditorProject restored = EditorProjectSerialiser.Deserialise(
            EditorProjectSerialiser.Serialise(original));

        Assert.Collection(
            restored.AudioTracks,
            track =>
            {
                Assert.Equal(@"C:\Media\music.wav", track.AudioPath);
                Assert.Equal(25, track.StartFrame);
                Assert.Equal(0.7, track.Volume);
                Assert.False(track.Muted);
                Assert.Equal("Theme", track.Title);
                Assert.Equal("InkMotion", track.Artist);
                Assert.Equal("Drafts", track.Album);
                Assert.Equal("Ambient", track.Genre);
                Assert.Equal("Project-only comment", track.Comments);
                Assert.Equal(2026u, track.Year);
                Assert.True(track.TagsInitialised);
            },
            track =>
            {
                Assert.Equal(@"C:\Media\voice.mp3", track.AudioPath);
                Assert.Equal(73, track.StartFrame);
                Assert.Equal(0.4, track.Volume);
                Assert.True(track.Muted);
            });
    }

    [Fact]
    public void RoundTrip_PreservesExplicitlyClearedAudioTags()
    {
        var original = new EditorProject
        {
            AudioTracks =
            [
                new AudioTrackProject
                {
                    AudioPath = @"C:\Media\music.wav",
                    TagsInitialised = true,
                },
            ],
        };

        string serialised = EditorProjectSerialiser.Serialise(original);
        Assert.Contains("\"TagsInitialized\": true", serialised);
        EditorProject restored = EditorProjectSerialiser.Deserialise(serialised);

        var track = Assert.Single(restored.AudioTracks);
        Assert.True(track.TagsInitialised);
        Assert.Empty(track.Title);
        Assert.Empty(track.Artist);
    }

    [Fact]
    public void Deserialise_LegacyAudioPath_MigratesToFirstAudioTrack()
    {
        EditorProject restored = EditorProjectSerialiser.Deserialise(
            """{"Version":1,"AudioPath":"C:\\Media\\music.wav","AudioVolume":0.5,"AudioMuted":true,"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""");

        var track = Assert.Single(restored.AudioTracks);
        Assert.Equal(@"C:\Media\music.wav", track.AudioPath);
        Assert.Equal(1, track.StartFrame);
        Assert.Equal(0.5, track.Volume);
        Assert.True(track.Muted);
    }

    [Theory]
    [InlineData("""{"Version":4,"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":1,"Frames":[],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":1,"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":1}""")]
    [InlineData("""{"Version":1,"Frames":[{"Number":1,"Strokes":[[{"X":1.5,"Y":0.5}]]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":1,"Frames":[{"Number":1,"Strokes":[]},{"Number":3,"Strokes":[]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":1,"FrameRate":25,"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":1,"AudioVolume":1.5,"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":1,"AudioTracks":[{"StartFrame":0}],"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":1,"AudioTracks":[{"Volume":-0.1}],"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":1,"Frames":[{"Number":1,"Strokes":[[{"X":0.5,"Y":0.5,"Pressure":1.5}]]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":1,"Frames":[{"Number":1,"Strokes":[[{"X":0.5,"Y":0.5,"Pressure":-0.1}]]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":1,"Frames":[{"Number":1,"Strokes":[[{"X":0.5,"Y":0.5,"BrushWidth":0.1}]]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":1,"AudioTracks":[{"Year":10000}],"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":3,"InkLayers":[{"Id":"ink-1","Name":"Ink 1"},{"Id":"ink-1","Name":"Duplicate"}],"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":3,"InkLayers":[{"Id":"other","Name":"No base layer"}],"ActiveInkLayerId":"other","Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":3,"InkLayers":[{"Id":"ink-1","Name":"Ink 1"},{"Id":"ink-2","Name":"Bad opacity","Opacity":1.1}],"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":3,"InkLayers":[{"Id":"ink-1","Name":"Ink 1"}],"Frames":[{"Number":1,"Strokes":[],"LayerStrokes":{"unknown":[]}}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":3,"InkLayers":[{"Id":"ink-1","Name":"Ink 1"},{"Id":"ink-2","Name":"Ink 2"}],"Frames":[{"Number":1,"Strokes":[],"LayerStrokes":{"ink-2":null}}],"SelectedFrameIndex":0}""")]
    public void Deserialise_RejectsInvalidProjectData(string json)
    {
        Assert.Throws<InvalidDataException>(() => EditorProjectSerialiser.Deserialise(json));
    }
}
