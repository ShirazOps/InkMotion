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
            !double.IsFinite(clip.ChromaKeyTolerance) || clip.ChromaKeyTolerance is < 0 or > 1 ||
            !double.IsFinite(clip.ChromaKeySpillSuppression) || clip.ChromaKeySpillSuppression is < 0 or > 1)
        {
            return "Chroma-key colour is not supported, or tolerance/spill suppression is outside 0-1.";
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
    private readonly UnPremultiplyEffect _straight = new();
    private readonly ColorMatrixEffect _opaque = new() { AlphaMode = CanvasAlphaMode.Straight };
    private readonly ColorMatrixEffect _first = new() { AlphaMode = CanvasAlphaMode.Straight };
    private readonly ColorMatrixEffect _second = new() { AlphaMode = CanvasAlphaMode.Straight };
    private readonly BlendEffect _maximum = new() { Mode = BlendEffectMode.Lighten };
    private readonly BlendEffect _minimum = new() { Mode = BlendEffectMode.Darken };
    private readonly ArithmeticCompositeEffect _amount = new() { MultiplyAmount = 0 };
    private readonly CompositeEffect _masked = new() { Mode = CanvasComposite.DestinationIn };

    public ICanvasImage GetOutput(ICanvasImage source, TimelineClipProject clip, double? animatedTolerance = null)
    {
        _effect.Source = source;
        _effect.Color = Color.FromArgb(
            (byte)(clip.ChromaKeyColour >> 24),
            (byte)(clip.ChromaKeyColour >> 16),
            (byte)(clip.ChromaKeyColour >> 8),
            (byte)clip.ChromaKeyColour);
        _effect.Tolerance = (float)(animatedTolerance ?? clip.ChromaKeyTolerance);
        _effect.Feather = clip.ChromaKeyFeather;
        if (clip.ChromaKeySpillSuppression <= 0)
        {
            return _effect;
        }

        // Opaque, straight-colour intermediates avoid blending alpha into the channel cap.
        Matrix5x4 identity = new() { M11 = 1, M22 = 1, M33 = 1, M54 = 1 };
        Matrix5x4 first = identity;
        Matrix5x4 second = identity;
        if (clip.ChromaKeyColour == 0xFF00FF00)
        {
            first.M22 = second.M22 = 0;
            first.M12 = 1;
            second.M32 = 1;
        }
        else if (clip.ChromaKeyColour == 0xFF0000FF)
        {
            first.M33 = second.M33 = 0;
            first.M13 = 1;
            second.M23 = 1;
        }
        else
        {
            first.M11 = second.M11 = first.M33 = second.M33 = 0;
            first.M21 = second.M21 = first.M23 = second.M23 = 1;
        }
        _straight.Source = source;
        _opaque.Source = _first.Source = _second.Source = _straight;
        _opaque.ColorMatrix = identity;
        _first.ColorMatrix = first;
        _second.ColorMatrix = second;
        _maximum.Background = _first;
        _maximum.Foreground = _second;
        _minimum.Background = _opaque;
        _minimum.Foreground = _maximum;
        _amount.Source1 = _opaque;
        _amount.Source2 = _minimum;
        _amount.Source1Amount = (float)(1 - clip.ChromaKeySpillSuppression);
        _amount.Source2Amount = (float)clip.ChromaKeySpillSuppression;
        _masked.Sources.Clear();
        _masked.Sources.Add(_amount);
        _masked.Sources.Add(_effect);
        return _masked;
    }

    public void Dispose()
    {
        _masked.Dispose();
        _amount.Dispose();
        _minimum.Dispose();
        _maximum.Dispose();
        _second.Dispose();
        _first.Dispose();
        _opaque.Dispose();
        _straight.Dispose();
        _effect.Dispose();
    }
}
