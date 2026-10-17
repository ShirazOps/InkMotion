using Xunit;

namespace InkMotion.Tests;

public sealed class EditorProjectSerializerTests
{
    [Fact]
    public void RoundTrip_PreservesVideoFramesInkAndKeyframes()
    {
        var original = new EditorProject
        {
            VideoPath = @"C:\Media\scene.mp4",
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

    [Theory]
    [InlineData("""{"Version":2,"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":1,"Frames":[],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":1,"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":1}""")]
    [InlineData("""{"Version":1,"Frames":[{"Number":1,"Strokes":[[{"X":1.5,"Y":0.5}]]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":1,"Frames":[{"Number":1,"Strokes":[]},{"Number":3,"Strokes":[]}],"SelectedFrameIndex":0}""")]
    [InlineData("""{"Version":1,"FrameRate":25,"Frames":[{"Number":1,"Strokes":[]}],"SelectedFrameIndex":0}""")]
    public void Deserialize_RejectsInvalidProjectData(string json)
    {
        Assert.Throws<InvalidDataException>(() => EditorProjectSerializer.Deserialize(json));
    }
}
