using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Xunit;

namespace InkMotion.Tests;

public sealed class TitleEditingTests
{
    [Fact]
    public void TitlePosesAnimatePositionScaleAndRotationAndSurviveSaving()
    {
        var title = new TimelineClipProject { Text = "Moving title", StartFrame = 25, DurationSeconds = 4 };
        var first = new VideoTransformValue(0.2, 0.3, 0.5, -60);
        var last = new VideoTransformValue(0.8, 0.7, 1.5, 60);
        Assert.True(VideoTransformKeyframes.SetValueAtTime(title, TimelineTrackKind.Text, 1, 24, first));
        Assert.Null(VideoTimelineEditing.Validate(
            [new() { Kind = TimelineTrackKind.Text, Clips = [title] }], 24));
        Assert.True(VideoTransformKeyframes.TrySetAtFrame(title, TimelineTrackKind.Text, 0, 24));
        Assert.True(VideoTransformKeyframes.SetValueAtTime(title, TimelineTrackKind.Text, 3, 24, last));
        Assert.True(VideoTransformKeyframes.TrySetAtFrame(title, TimelineTrackKind.Text, 48, 24));
        var project = new EditorProject
        {
            VideoTracks = [new() { Kind = TimelineTrackKind.Text, Clips = [title] }],
        };
        EditorProject restored = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        TimelineClipProject saved = Assert.Single(Assert.Single(restored.VideoTracks).Clips);
        Assert.Equal(2, saved.TransformKeyframes.Count);
        VideoTransformValue middle = VideoTransformKeyframes.GetValueAtTime(saved, 2, 24);
        Assert.Equal(0.5, middle.X, 6);
        Assert.Equal(0.5, middle.Y, 6);
        Assert.Equal(1, middle.Scale, 6);
        Assert.Equal(0, middle.RotationDegrees, 6);
        Assert.Equal(first, VideoTransformKeyframes.GetValueAtTime(saved, 1, 24));
        Assert.Equal(last, VideoTransformKeyframes.GetValueAtTime(saved, 4, 24));
    }

    [Fact]
    public void TitleBackgroundSurvivesPlacementAndProjectRoundTrip()
    {
        var title = new TimelineClipProject { Text = "A title", TextBackgroundColour = 0x804080C0 };
        TimelineClipProject copied = VideoTimelineEditing.CopyForPlacement(title, 25);
        var project = new EditorProject
        {
            VideoTracks = [new() { Kind = TimelineTrackKind.Text, Clips = [copied] }],
        };
        EditorProject restored = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        TimelineClipProject saved = Assert.Single(Assert.Single(restored.VideoTracks).Clips);
        Assert.Equal(title.TextBackgroundColour, saved.TextBackgroundColour);
        Assert.Equal(25, saved.StartFrame);
    }

    [Fact]
    public void Version64TitlesRetainTransparentBackground()
    {
        EditorProject restored = EditorProjectSerialiser.Deserialise(
            """{"Version":64,"VideoTracks":[{"Kind":"Text","Clips":[{"Text":"Existing title"}]}]}""");
        Assert.Equal(EditorProject.CurrentVersion, restored.Version);
        Assert.Equal(0u, Assert.Single(Assert.Single(restored.VideoTracks).Clips).TextBackgroundColour);
    }

    [Fact]
    public void TitleBackgroundUsesSharedBoundsAndMultipliesClipOpacity()
    {
        using var device = new CanvasDevice(true);
        using var target = new CanvasRenderTarget(device, 400, 200, 96);
        var title = new TimelineClipProject
        {
            Text = "Title", FontSize = 200, Colour = 0, TextBackgroundColour = 0x80FF0000,
        };
        var transform = new VideoTransformValue(0.5, 0.5, 1, 0);
        Rect bounds = TimelineCompositor.GetTitleBounds(device, title, 400, 200, transform);
        using (CanvasDrawingSession session = target.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Transparent);
            TimelineCompositor.DrawTitle(session, title, 400, 200, 0.5f, transform);
        }
        byte[] pixels = target.GetPixelBytes();
        int centre = ((int)(bounds.Top + bounds.Height / 2) * 400 + (int)(bounds.Left + bounds.Width / 2)) * 4;
        Assert.Equal(64, pixels[centre + 3]);
        Assert.Equal(64, pixels[centre + 2]);
        Assert.Equal(0, pixels[3]);
        Rect scaled = TimelineCompositor.GetTitleBounds(device, title, 800, 400, transform);
        Assert.InRange(Math.Abs(scaled.Width - 2 * bounds.Width), 0, 0.01);
        Assert.InRange(Math.Abs(scaled.Height - 2 * bounds.Height), 0, 0.01);
    }
}
