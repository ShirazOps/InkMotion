using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Windows.UI;

namespace InkMotion;

public static class VideoChromaKey
{
    public static bool IsSupportedColour(uint colour) =>
        colour is 0xFF00FF00 or 0xFF0000FF or 0xFFFF00FF;

    public static string? Validate(TimelineClipProject clip, TimelineTrackKind kind)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!IsSupportedColour(clip.ChromaKeyColour) ||
            !double.IsFinite(clip.ChromaKeyTolerance) || clip.ChromaKeyTolerance is < 0 or > 1)
        {
            return "Chroma-key colour or tolerance is not supported.";
        }

        if (clip.ChromaKeyEnabled && kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image))
        {
            return "Chroma key is supported only for video and image clips.";
        }

        return null;
    }
}

public sealed class VideoChromaKeyEffect : IDisposable
{
    private readonly ChromaKeyEffect _effect = new();

    public ICanvasImage GetOutput(ICanvasImage source, TimelineClipProject clip)
    {
        _effect.Source = source;
        _effect.Color = Color.FromArgb(
            (byte)(clip.ChromaKeyColour >> 24),
            (byte)(clip.ChromaKeyColour >> 16),
            (byte)(clip.ChromaKeyColour >> 8),
            (byte)clip.ChromaKeyColour);
        _effect.Tolerance = (float)clip.ChromaKeyTolerance;
        _effect.Feather = clip.ChromaKeyFeather;
        return _effect;
    }

    public void Dispose() => _effect.Dispose();
}
