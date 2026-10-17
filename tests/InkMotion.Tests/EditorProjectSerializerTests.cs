using Xunit;

namespace InkMotion.Tests;

public sealed class EditorProjectSerializerTests
{
    [Fact]
    public void Deserialize_ProjectWithoutAudioMetadata_UsesAudioDefaults()
    {
        EditorProject restored = EditorProjectSerializer.Deserialize(
            """{"Version":1,"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""");

        Assert.Null(restored.AudioPath);
        Assert.Equal(1, restored.AudioVolume);
        Assert.False(restored.AudioMuted);
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

        EditorProject restored = EditorProjectSerializer.Deserialize(
            EditorProjectSerializer.Serialize(original));

        Assert.Equal(original.Version, restored.Version);
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

        EditorProject restored = EditorProjectSerializer.Deserialize(
            EditorProjectSerializer.Serialize(original));

        Assert.Collection(
            restored.AudioTracks,
            track =>
            {
                Assert.Equal(@"C:\Media\music.wav", track.AudioPath);
                Assert.Equal(25, track.StartFrame);
                Assert.Equal(0.7, track.Volume);
                Assert.False(track.Muted);
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
    public void Deserialize_LegacyAudioPath_MigratesToFirstAudioTrack()
    {
        EditorProject restored = EditorProjectSerializer.Deserialize(
            """{"Version":1,"AudioPath":"C:\\Media\\music.wav","AudioVolume":0.5,"AudioMuted":true,"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""");

        var track = Assert.Single(restored.AudioTracks);
        Assert.Equal(@"C:\Media\music.wav", track.AudioPath);
        Assert.Equal(1, track.StartFrame);
        Assert.Equal(0.5, track.Volume);
        Assert.True(track.Muted);
    }

    [Theory]
    [InlineData("""{"Version":2,"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":1,"Frames":[],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":1,"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":1}""")]
    [InlineData("""{"Version":1,"Frames":[{"Number":1,"Strokes":[[{"X":1.5,"Y":0.5}]]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":1,"Frames":[{"Number":1,"Strokes":[]},{"Number":3,"Strokes":[]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":1,"FrameRate":25,"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":1,"AudioVolume":1.5,"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":1,"AudioTracks":[{"StartFrame":0}],"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":1,"AudioTracks":[{"Volume":-0.1}],"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""")]
    public void Deserialize_RejectsInvalidProjectData(string json)
    {
        Assert.Throws<InvalidDataException>(() => EditorProjectSerializer.Deserialize(json));
    }
}
