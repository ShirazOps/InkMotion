using System.Text.Json.Nodes;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;
using Xunit;

namespace InkMotion.Tests;

public sealed class VideoShotMatchingTests
{
    [Fact]
    public void MatchesMeanAndContrastWithFlatChannelFallback()
    {
        var source = VideoShotStatistics.Analyse([40, 100, 50, 255, 80, 100, 150, 255]);
        var reference = VideoShotStatistics.Analyse([80, 120, 100, 255, 100, 120, 200, 255]);
        VideoShotMatch match = source.MatchTo(reference);
        Assert.Equal(1, match.RedGain, 5);
        Assert.Equal(0.5, match.BlueGain, 5);
        Assert.Equal(1, match.GreenGain);
        Assert.Equal(20 / 255d, match.GreenOffset, 5);
        Assert.True(match.IsValid);
        Assert.True(source.MatchTo(source).IsNeutral);
    }

    [Fact]
    public void IgnoresTransparentPixelsAndUnpremultipliesVisibleSamples()
    {
        var opaque = VideoShotStatistics.Analyse([0, 0, 255, 255]);
        var translucent = VideoShotStatistics.Analyse([0, 0, 128, 128, 0, 0, 0, 0]);
        Assert.Equal(opaque.Mean, translucent.Mean);
        Assert.Throws<InvalidOperationException>(() => VideoShotStatistics.Analyse([0, 0, 0, 0]));
    }

    [Fact]
    public void SavesClonesAndMigratesLegacyDefaults()
    {
        var clip = new TimelineClipProject { SourcePath = "image.png", ShotMatch = new() { RedOffset = 0.1 } };
        var project = new EditorProject { VideoTracks = [new() { Kind = TimelineTrackKind.Image, Clips = [clip] }] };
        string json = EditorProjectSerialiser.Serialise(project);
        Assert.Equal(clip.ShotMatch, EditorProjectSerialiser.Deserialise(json).VideoTracks[0].Clips[0].ShotMatch);
        Assert.Equal(clip.ShotMatch, VideoTimelineEditing.Clone(clip).ShotMatch);
        JsonObject legacy = JsonNode.Parse(json)!.AsObject();
        legacy["Version"] = 93;
        legacy["VideoTracks"]![0]!["Clips"]![0]!.AsObject().Remove("ShotMatch");
        legacy["VideoTracks"]![0]!["Clips"]![0]!.AsObject().Remove("HslQualifier");
        var restored = EditorProjectSerialiser.Deserialise(legacy.ToJsonString());
        Assert.True(restored.VideoTracks[0].Clips[0].ShotMatch.IsNeutral);
        Assert.False(restored.VideoTracks[0].Clips[0].HslQualifier.Enabled);
        project.Colour = new();
        Assert.Throws<InvalidDataException>(() => EditorProjectSerialiser.Serialise(project));
    }

    [Fact]
    public void SharedCompositorAppliesMatchAfterGradingAndPreservesAlpha()
    {
        using var device = new CanvasDevice(true);
        using var bitmap = CanvasBitmap.CreateFromBytes(device,
            Enumerable.Repeat(new byte[] { 25, 50, 75, 128 }, 16).SelectMany(p => p).ToArray(),
            4, 4, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        using var target = new CanvasRenderTarget(device, 4, 4, 96);
        var clip = new TimelineClipProject { ShotMatch = new() { RedOffset = 0.2, BlueGain = 2 } };
        using (var drawing = target.CreateDrawingSession())
        {
            drawing.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            TimelineCompositor.DrawFitted(drawing, bitmap, bitmap.Bounds, 4, 4, 1, 0.5, 0.5, 1, colourGrade: clip);
        }
        byte[] pixels = target.GetPixelBytes();
        int i = (2 * 4 + 2) * 4;
        Assert.InRange(pixels[i], 47, 53);
        Assert.InRange(pixels[i + 1], 47, 53);
        Assert.InRange(pixels[i + 2], 97, 104);
        Assert.Equal(128, pixels[i + 3]);
    }
}
