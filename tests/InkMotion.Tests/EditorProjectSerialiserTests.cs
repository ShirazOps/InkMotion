using Xunit;

namespace InkMotion.Tests;

public sealed class EditorProjectSerialiserTests
{
    [Fact]
    public void SupportedVideoExtensions_IncludeWmvAviAndAv1Containers()
    {
        Assert.Contains(".wmv", VideoFileTypes.SupportedExtensions);
        Assert.Contains(".avi", VideoFileTypes.SupportedExtensions);
        Assert.Contains(".av1", VideoFileTypes.SupportedExtensions);
        Assert.Contains(".mkv", VideoFileTypes.SupportedExtensions);
        Assert.Contains(".webm", VideoFileTypes.SupportedExtensions);
    }

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

        Assert.Equal(EditorProject.CurrentVersion, restored.Version);
        Assert.Equal(0.5, restored.Frames[0].Strokes[0][0].Pressure);
        Assert.Equal(4, restored.Frames[0].Strokes[0][0].BrushWidth);
        Assert.Equal(0xFFFFA500u, restored.Frames[0].Strokes[0][0].BrushColour);
    }

    [Fact]
    public void Deserialise_VersionEighteenProject_MigratesWithoutAudioVideoLinks()
    {
        EditorProject restored = EditorProjectSerialiser.Deserialise(
            """{"Version":18,"AudioTracks":[{"AudioPath":"C:\\Media\\music.wav"}],"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""");

        Assert.Equal(EditorProject.CurrentVersion, restored.Version);
        Assert.Null(Assert.Single(restored.AudioTracks).LinkedVideoClipId);
    }

    [Fact]
    public void Deserialise_VersionNineteenProject_MigratesWithNoTransitions()
    {
        EditorProject restored = EditorProjectSerialiser.Deserialise(
            """{"Version":19,"VideoTracks":[{"Id":"track","Kind":"Video","Name":"Video 1","Clips":[{"Id":"clip","SourcePath":"C:\\Media\\scene.mp4","SourceDurationSeconds":10}]}],"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""");

        Assert.Equal(EditorProject.CurrentVersion, restored.Version);
        Assert.Equal(0, Assert.Single(Assert.Single(restored.VideoTracks).Clips).TransitionInSeconds);
    }

    [Fact]
    public void Deserialise_VersionTwentyProject_MigratesWithNoTransformKeyframes()
    {
        EditorProject restored = EditorProjectSerialiser.Deserialise(
            """{"Version":20,"VideoTracks":[{"Id":"track","Kind":"Video","Name":"Video 1","Clips":[{"Id":"clip","SourcePath":"C:\\Media\\scene.mp4","SourceDurationSeconds":10}]}],"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""");

        Assert.Equal(EditorProject.CurrentVersion, restored.Version);
        Assert.Empty(Assert.Single(Assert.Single(restored.VideoTracks).Clips).TransformKeyframes);
    }

    [Fact]
    public void Deserialise_VersionTwentyOneProject_MigratesWithNoCrop()
    {
        EditorProject restored = EditorProjectSerialiser.Deserialise(
            """{"Version":21,"VideoTracks":[{"Id":"track","Kind":"Video","Name":"Video 1","Clips":[{"Id":"clip","SourcePath":"C:\\Media\\scene.mp4","SourceDurationSeconds":10}]}],"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""");

        TimelineClipProject clip = Assert.Single(Assert.Single(restored.VideoTracks).Clips);
        Assert.Equal(EditorProject.CurrentVersion, restored.Version);
        Assert.Equal((0, 0, 0, 0),
            (clip.CropLeftPercent, clip.CropTopPercent, clip.CropRightPercent, clip.CropBottomPercent));
    }

    [Fact]
    public void Serialise_RoundTripsCrossDissolveTransition()
    {
        TimelineTrackProject track = new()
        {
            Kind = TimelineTrackKind.Video,
            Clips =
            [
                new TimelineClipProject
                {
                    Id = "outgoing",
                    StartFrame = 1,
                    SourcePath = @"C:\Media\outgoing.mp4",
                    SourceOutSeconds = 5,
                    SourceDurationSeconds = 6,
                },
                new TimelineClipProject
                {
                    Id = "incoming",
                    StartFrame = 121,
                    SourcePath = @"C:\Media\incoming.mp4",
                    SourceInSeconds = 1,
                    SourceOutSeconds = 6,
                    SourceDurationSeconds = 6,
                    TransitionInSeconds = 1,
                },
            ],
        };
        EditorProject original = new() { VideoTracks = [track] };

        EditorProject restored = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(original));

        TimelineClipProject[] clips = Assert.Single(restored.VideoTracks).Clips.ToArray();
        Assert.Equal("outgoing", clips[0].Id);
        Assert.Equal("incoming", clips[1].Id);
        Assert.Equal(1, clips[1].TransitionInSeconds);
    }

    [Fact]
    public void RoundTrip_PreservesVideoFramesInkAndKeyframes()
    {
        var original = new EditorProject
        {
            VideoTracks =
            [
                new TimelineTrackProject
                {
                    Kind = TimelineTrackKind.Video,
                    Name = "Video 1",
                    Clips = [new TimelineClipProject { SourcePath = @"C:\Media\scene.mp4", SourceDurationSeconds = 12 }],
                },
            ],
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
                    ExposureFrames = 4,
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

        Assert.Equal(EditorProject.CurrentVersion, restored.Version);
        Assert.Null(restored.VideoPath);
        Assert.Equal(@"C:\Media\scene.mp4", Assert.Single(Assert.Single(restored.VideoTracks).Clips).SourcePath);
        Assert.Equal(original.AudioPath, restored.AudioPath);
        Assert.Equal(original.AudioVolume, restored.AudioVolume);
        Assert.Equal(original.AudioMuted, restored.AudioMuted);
        Assert.Equal(original.SelectedFrameIndex, restored.SelectedFrameIndex);
        Assert.Equal(30, restored.FrameRate);
        Assert.Equal(4, restored.Frames[1].ExposureFrames);
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
        Assert.Contains("\"InterpolationMode\": \"Linear\"", serialised);
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

        Assert.Equal(EditorProject.CurrentVersion, restored.Version);
        Assert.Equal("ink-1", restored.ActiveInkLayerId);
        Assert.Equal("Ink 1", Assert.Single(restored.InkLayers).Name);
        Assert.Single(restored.Frames[0].Strokes);
    }

    [Fact]
    public void Deserialise_VersionThreeProject_DefaultsToLinearTween()
    {
        EditorProject restored = EditorProjectSerialiser.Deserialise(
            """{"Version":3,"InkLayers":[{"Id":"ink-1","Name":"Ink 1"}],"Frames":[{"Number":1,"IsKeyframe":true,"Strokes":[]}],"SelectedFrameIndex":0}""");

        Assert.Equal(EditorProject.CurrentVersion, restored.Version);
        Assert.Equal(InkInterpolationMode.Linear, restored.Frames[0].InterpolationMode);
    }

    [Fact]
    public void Deserialise_VersionFourProject_MigratesWithDefaultCelExposure()
    {
        EditorProject restored = EditorProjectSerialiser.Deserialise(
            """{"Version":4,"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""");

        Assert.Equal(EditorProject.CurrentVersion, restored.Version);
        Assert.Equal(1, restored.Frames[0].ExposureFrames);
    }

    [Fact]
    public void Deserialise_VersionFiveProject_AddsTextOverlayCollection()
    {
        EditorProject restored = EditorProjectSerialiser.Deserialise(
            """{"Version":5,"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""");

        Assert.Equal(EditorProject.CurrentVersion, restored.Version);
        Assert.Empty(restored.Frames[0].TextOverlays);
    }

    [Fact]
    public void RoundTrip_PreservesStyledTextOverlay()
    {
        var original = new EditorProject
        {
            Frames =
            [
                new EditorFrame
                {
                    Number = 1,
                    TextOverlays =
                    [
                        new InkTextOverlay
                        {
                            Text = "InkMotion",
                            X = 0.25,
                            Y = 0.75,
                            FontFamily = "Georgia",
                            FontSize = 64,
                            Colour = 0xFFFF69B4,
                        },
                    ],
                },
            ],
        };

        EditorProject restored = EditorProjectSerialiser.Deserialise(
            EditorProjectSerialiser.Serialise(original));

        InkTextOverlay overlay = Assert.Single(restored.Frames[0].TextOverlays);
        Assert.Equal(("InkMotion", 0.25, 0.75, "Georgia", 64, 0xFFFF69B4u),
            (overlay.Text, overlay.X, overlay.Y, overlay.FontFamily, overlay.FontSize, overlay.Colour));
    }

    [Fact]
    public void RoundTrip_PreservesInkTweenModeAsReadableText()
    {
        var original = new EditorProject
        {
            Frames =
            [
                new EditorFrame
                {
                    Number = 1,
                    IsKeyframe = true,
                    InterpolationMode = InkInterpolationMode.EaseInOut,
                },
            ],
        };

        string serialised = EditorProjectSerialiser.Serialise(original);

        Assert.Contains("\"InterpolationMode\": \"EaseInOut\"", serialised);
        EditorProject restored = EditorProjectSerialiser.Deserialise(serialised);
        Assert.Equal(InkInterpolationMode.EaseInOut, restored.Frames[0].InterpolationMode);
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
    [InlineData("""{"Version":999,"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":6,"Frames":[{"Number":1,"ExposureFrames":0,"Strokes":[]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":6,"Frames":[{"Number":1,"ExposureFrames":1000,"Strokes":[]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":6,"Frames":[{"Number":1,"TextOverlays":[{"Text":"","FontFamily":"Segoe UI","FontSize":24}],"Strokes":[]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":6,"Frames":[{"Number":1,"TextOverlays":[{"Text":"Bad size","FontFamily":"Segoe UI","FontSize":513}],"Strokes":[]}],"SelectedFrameIndex":0}""")]
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
    [InlineData("""{"Version":4,"Frames":[{"Number":1,"InterpolationMode":99,"Strokes":[]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":7,"AudioTracks":[{"SourceInSeconds":-1}],"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":7,"AudioTracks":[{"SourceInSeconds":2,"SourceOutSeconds":2}],"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":7,"AudioTracks":[{"FadeInSeconds":-0.5}],"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":7,"AudioTracks":[{"FadeOutSeconds":601}],"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":7,"AudioTracks":[{"GainDecibels":25}],"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":8,"AudioTracks":[{"TempoPercent":49}],"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":8,"AudioTracks":[{"TempoPercent":201}],"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":8,"AudioTracks":[{"PitchSemitones":12.5}],"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":9,"VideoPath":"C:\\Media\\scene.mp4","Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":9,"VideoTracks":[{"Id":"t","Kind":"Video","Name":"Video 1","Clips":[{"Id":"a","SourcePath":"v.mp4","SourceDurationSeconds":10},{"Id":"b","StartFrame":24,"SourcePath":"v.mp4","SourceDurationSeconds":10}]}],"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":9,"VideoTracks":[{"Id":"t","Kind":"Video","Name":"Video 1","Clips":[{"Id":"a","SourcePath":"v.mp4","SourceInSeconds":4,"SourceOutSeconds":3}]}],"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":9,"VideoTracks":[{"Id":"t","Kind":"Text","Name":"Title 1","Clips":[{"Id":"a","Text":""}]}],"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":9,"VideoTracks":[{"Id":"t","Kind":"Image","Name":"Image 1","Clips":[{"Id":"a","SourcePath":"a.png","Scale":5}]}],"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":9,"VideoTracks":[{"Id":"t","Kind":"Text","Name":"Title 1","Clips":[{"Id":"a","Text":"Hi"}]},{"Id":"t","Kind":"Text","Name":"Title 2","Clips":[]}],"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":8,"AudioMixLayout":99,"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""")]
    public void Deserialise_RejectsInvalidProjectData(string json)
    {
        Assert.Throws<InvalidDataException>(() => EditorProjectSerialiser.Deserialise(json));
    }

    [Fact]
    public void Serialise_RoundTripsAudioClipEdits()
    {
        var project = new EditorProject
        {
            AudioTracks =
            [
                new AudioTrackProject
                {
                    AudioPath = @"C:\Media\voice.wav",
                    StartFrame = 12,
                    SourceInSeconds = 1.5,
                    SourceOutSeconds = 4.25,
                    FadeInSeconds = 0.5,
                    FadeOutSeconds = 0.75,
                    GainDecibels = 3.2,
                    TempoPercent = 125,
                    PitchSemitones = -3.5,
                },
            ],
            AudioMixLayout = AudioChannelLayout.Surround121,
        };

        EditorProject restored = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        AudioTrackProject track = Assert.Single(restored.AudioTracks);

        Assert.Equal(1.5, track.SourceInSeconds);
        Assert.Equal(4.25, track.SourceOutSeconds);
        Assert.Equal(0.5, track.FadeInSeconds);
        Assert.Equal(0.75, track.FadeOutSeconds);
        Assert.Equal(3.2, track.GainDecibels);
        Assert.Equal(125, track.TempoPercent);
        Assert.Equal(-3.5, track.PitchSemitones);
        Assert.Equal(AudioChannelLayout.Surround121, restored.AudioMixLayout);
    }

    [Fact]
    public void Serialise_RoundTripsLinkedAudioVideoClip()
    {
        var project = new EditorProject
        {
            VideoTracks =
            [
                new TimelineTrackProject
                {
                    Kind = TimelineTrackKind.Video,
                    Clips =
                    [
                        new TimelineClipProject
                        {
                            Id = "video-clip",
                            SourcePath = @"C:\Media\scene.mp4",
                            SourceDurationSeconds = 10,
                            SourceOutSeconds = 4,
                        },
                    ],
                },
            ],
            AudioTracks =
            [
                new AudioTrackProject
                {
                    AudioPath = @"C:\Media\dialog.wav",
                    LinkedVideoClipId = "video-clip",
                    StartFrame = 12,
                },
            ],
        };

        EditorProject restored = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));

        Assert.Equal("video-clip", Assert.Single(restored.AudioTracks).LinkedVideoClipId);
        Assert.Equal(12, Assert.Single(restored.AudioTracks).StartFrame);
    }

    [Fact]
    public void Deserialise_VersionSixAudioTrack_DefaultsToUntrimmedClip()
    {
        EditorProject restored = EditorProjectSerialiser.Deserialise(
            """{"Version":6,"AudioTracks":[{"AudioPath":"C:\\Media\\music.wav","StartFrame":3}],"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""");

        AudioTrackProject track = Assert.Single(restored.AudioTracks);
        Assert.Equal(EditorProject.CurrentVersion, restored.Version);
        Assert.Equal(0, track.SourceInSeconds);
        Assert.Null(track.SourceOutSeconds);
        Assert.Equal(0, track.GainDecibels);
    }

    [Fact]
    public void Deserialise_VersionSevenProject_DefaultsToStereoMixAndOriginalTempo()
    {
        EditorProject restored = EditorProjectSerialiser.Deserialise(
            """{"Version":7,"AudioTracks":[{"AudioPath":"C:\\Media\\music.wav"}],"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""");

        AudioTrackProject track = Assert.Single(restored.AudioTracks);
        Assert.Equal(EditorProject.CurrentVersion, restored.Version);
        Assert.Equal(AudioChannelLayout.Stereo, restored.AudioMixLayout);
        Assert.Equal(100, track.TempoPercent);
        Assert.Equal(0, track.PitchSemitones);
    }

    [Fact]
    public void Deserialise_VersionEightProject_MovesVideoPathToTimelineClip()
    {
        EditorProject restored = EditorProjectSerialiser.Deserialise(
            """{"Version":8,"VideoPath":"C:\\Media\\scene.mp4","Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""");

        TimelineTrackProject track = Assert.Single(restored.VideoTracks);
        TimelineClipProject clip = Assert.Single(track.Clips);
        Assert.Equal(EditorProject.CurrentVersion, restored.Version);
        Assert.Null(restored.VideoPath);
        Assert.Equal(TimelineTrackKind.Video, track.Kind);
        Assert.Equal(@"C:\Media\scene.mp4", clip.SourcePath);
        Assert.Equal(1, clip.StartFrame);
        Assert.Null(clip.SourceOutSeconds);
        Assert.Equal(0, clip.SourceDurationSeconds);
        Assert.DoesNotContain("VideoPath", EditorProjectSerialiser.Serialise(restored));
    }

    [Fact]
    public void Serialise_RoundTripsTimelineTracksAndClips()
    {
        var project = new EditorProject
        {
            VideoTracks =
            [
                new TimelineTrackProject
                {
                    Id = "titles",
                    Kind = TimelineTrackKind.Text,
                    Name = "Title 1",
                    IsLocked = true,
                    Clips = [new TimelineClipProject { Id = "t1", Text = "Opening", FontSize = 96, Colour = 0xFFFF0000, Y = 0.8, DurationSeconds = 3, FadeInSeconds = 0.5 }],
                },
                new TimelineTrackProject
                {
                    Id = "images",
                    Kind = TimelineTrackKind.Image,
                    Name = "Image 1",
                    IsVisible = false,
                    Clips = [new TimelineClipProject { Id = "i1", SourcePath = @"C:\Media\logo.png", Scale = 0.5, X = 0.9, StartFrame = 25 }],
                },
                new TimelineTrackProject
                {
                    Id = "video",
                    Kind = TimelineTrackKind.Video,
                    Name = "Video 1",
                    IsMuted = true,
                    Clips =
                    [
                        new TimelineClipProject { Id = "v1", SourcePath = @"C:\Media\a.mp4", SourceInSeconds = 1, SourceOutSeconds = 3, SourceDurationSeconds = 10, Volume = 0.4 },
                        new TimelineClipProject { Id = "v2", SourcePath = @"C:\Media\b.mp4", StartFrame = 49, SourceDurationSeconds = 4, Opacity = 0.75 },
                    ],
                },
            ],
        };

        EditorProject restored = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));

        Assert.Collection(
            restored.VideoTracks,
            track =>
            {
                Assert.Equal(("titles", TimelineTrackKind.Text, true), (track.Id, track.Kind, track.IsLocked));
                TimelineClipProject clip = Assert.Single(track.Clips);
                Assert.Equal(("Opening", 96d, 0xFFFF0000u, 0.8, 3d, 0.5), (clip.Text, clip.FontSize, clip.Colour, clip.Y, clip.DurationSeconds, clip.FadeInSeconds));
            },
            track =>
            {
                Assert.False(track.IsVisible);
                TimelineClipProject clip = Assert.Single(track.Clips);
                Assert.Equal((@"C:\Media\logo.png", 0.5, 0.9, 25), (clip.SourcePath, clip.Scale, clip.X, clip.StartFrame));
            },
            track =>
            {
                Assert.True(track.IsMuted);
                Assert.Equal(2, track.Clips.Count);
                Assert.Equal((1d, (double?)3, 10d, 0.4), (track.Clips[0].SourceInSeconds, track.Clips[0].SourceOutSeconds, track.Clips[0].SourceDurationSeconds, track.Clips[0].Volume));
                Assert.Equal((49, 0.75), (track.Clips[1].StartFrame, track.Clips[1].Opacity));
            });
    }
}