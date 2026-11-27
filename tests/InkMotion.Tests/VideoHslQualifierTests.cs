using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;
using Xunit;

namespace InkMotion.Tests;

public sealed class VideoHslQualifierTests
{
    [Fact]
    public void WrapsRedAndLeavesUnselectedGreenAndGreysUnchanged()
    {
        var q = new VideoHslQualifier { Enabled = true, Hue = 355, HueWidth = 20, Softness = 0, Saturation = 0 };
        Assert.Equal((0.5, 0.5, 0.5), q.Apply(1, 0, 0));
        Assert.Equal((0d, 1d, 0d), q.Apply(0, 1, 0));
        Assert.Equal((0.4, 0.4, 0.4), q.Apply(0.4, 0.4, 0.4));
        Assert.Equal((1d, 0d, 0d), (q with { Enabled = false }).Apply(1, 0, 0));
    }

    [Fact]
    public void BoundsAndFeatherLimitTheCorrection()
    {
        var q = new VideoHslQualifier { Enabled = true, HueWidth = 10, Softness = 0.1, Saturation = 0 };
        var edge = q.Apply(1, 0.3, 0);
        Assert.InRange(edge.Red, 0.5, 0.999);
        Assert.Equal((1d, 0d, 0d), (q with { MinimumLightness = 0.6, Softness = 0 }).Apply(1, 0, 0));
        Assert.Equal((1d, 0d, 0d), (q with { MaximumSaturation = 0.8, Softness = 0 }).Apply(1, 0, 0));
        Assert.False((q with { MinimumLightness = 0.8, MaximumLightness = 0.2 }).IsValid);
        Assert.False((q with { Hue = double.NaN }).IsValid);
    }

    [Fact]
    public void PersistsClonesAndRejectsInvalidOrTitleSettings()
    {
        var clip = new TimelineClipProject { SourcePath = "image.png",
            HslQualifier = new() { Enabled = true, HueShift = 30 } };
        var project = new EditorProject { VideoTracks = [new() { Kind = TimelineTrackKind.Image, Clips = [clip] }] };
        var saved = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        Assert.Equal(clip.HslQualifier, saved.VideoTracks[0].Clips[0].HslQualifier);
        Assert.Equal(clip.HslQualifier, VideoTimelineEditing.Clone(clip).HslQualifier);
        Assert.NotNull(VideoColourGrading.Validate(clip, TimelineTrackKind.Text));
        clip.HslQualifier = clip.HslQualifier with { Hue = double.PositiveInfinity };
        Assert.NotNull(VideoColourGrading.Validate(clip, TimelineTrackKind.Image));
    }

    [Fact]
    public void SharedEffectDesaturatesOnlyQualifiedPixelsAndPreservesAlpha()
    {
        using var device = new CanvasDevice(true);
        using var bitmap = CanvasBitmap.CreateFromBytes(device, [0, 0, 128, 128, 0, 255, 0, 255],
            2, 1, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        using var effect = new VideoColourGradingEffect();
        using var target = new CanvasRenderTarget(device, 2, 1, 96);
        var clip = new TimelineClipProject { HslQualifier = new() { Enabled = true, Saturation = 0 } };
        using (var drawing = target.CreateDrawingSession())
        {
            drawing.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            drawing.DrawImage(effect.GetOutput(bitmap, clip));
        }
        byte[] pixels = target.GetPixelBytes();
        Assert.InRange(pixels[0], 60, 68);
        Assert.InRange(pixels[2], 60, 68);
        Assert.Equal(128, pixels[3]);
        Assert.InRange(pixels[5], 250, 255);
        Assert.InRange(pixels[6], 0, 4);
    }
}
