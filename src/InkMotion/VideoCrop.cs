using Windows.Foundation;

namespace InkMotion;

public readonly record struct VideoPreviewTransform(
    double Scale,
    double RotationDegrees,
    double PivotX,
    double PivotY,
    double TranslateX,
    double TranslateY);

public static class VideoCrop
{
    public static string? Validate(TimelineClipProject clip, TimelineTrackKind kind, double frameRate = 24)
    {
        if (kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image) &&
            (clip.CropLeftPercent != 0 || clip.CropTopPercent != 0 ||
             clip.CropRightPercent != 0 || clip.CropBottomPercent != 0))
        {
            return "Only video and image clips can be cropped.";
        }

        if ((kind is TimelineTrackKind.Video or TimelineTrackKind.Image) &&
            (!double.IsFinite(clip.CropLeftPercent) || !double.IsFinite(clip.CropTopPercent) ||
            !double.IsFinite(clip.CropRightPercent) || !double.IsFinite(clip.CropBottomPercent) ||
            clip.CropLeftPercent is < 0 or > 49 || clip.CropTopPercent is < 0 or > 49 ||
            clip.CropRightPercent is < 0 or > 49 || clip.CropBottomPercent is < 0 or > 49 ||
            clip.CropLeftPercent + clip.CropRightPercent >= 100 ||
            clip.CropTopPercent + clip.CropBottomPercent >= 100))
        {
            return "Crop edges must be between 0 and 49 percent and leave a visible source area.";
        }

        return VideoCropKeyframes.Validate(clip, kind, frameRate);
    }

    public static Rect GetSourceRect(Rect source, TimelineClipProject clip) =>
        GetSourceRect(source, new VideoCropValue(
            clip.CropLeftPercent, clip.CropTopPercent, clip.CropRightPercent, clip.CropBottomPercent));

    public static Rect GetSourceRect(Rect source, VideoCropValue crop)
    {
        double left = crop.Left / 100;
        double top = crop.Top / 100;
        double width = 1 - left - (crop.Right / 100);
        double height = 1 - top - (crop.Bottom / 100);
        return new Rect(
            source.X + (source.Width * left),
            source.Y + (source.Height * top),
            source.Width * width,
            source.Height * height);
    }

    public static VideoPreviewTransform GetPreviewTransform(
        Size viewport,
        Size source,
        TimelineClipProject clip,
        VideoTransformValue transform,
        VideoCropValue? cropValue = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (viewport.Width <= 0 || viewport.Height <= 0 || source.Width <= 0 || source.Height <= 0)
        {
            return new VideoPreviewTransform(transform.Scale, transform.RotationDegrees,
                viewport.Width / 2, viewport.Height / 2, 0, 0);
        }

        VideoCropValue crop = cropValue ?? VideoCropKeyframes.GetValue(clip, 0);
        double left = crop.Left / 100;
        double top = crop.Top / 100;
        double cropWidth = 1 - left - (crop.Right / 100);
        double cropHeight = 1 - top - (crop.Bottom / 100);
        double fitWhole = Math.Min(viewport.Width / source.Width, viewport.Height / source.Height);
        double fitCrop = Math.Min(viewport.Width / (source.Width * cropWidth), viewport.Height / (source.Height * cropHeight));
        double scale = transform.Scale * fitCrop / fitWhole;
        double wholeWidth = source.Width * fitWhole;
        double wholeHeight = source.Height * fitWhole;
        double pivotX = ((viewport.Width - wholeWidth) / 2) + ((left + (cropWidth / 2)) * wholeWidth);
        double pivotY = ((viewport.Height - wholeHeight) / 2) + ((top + (cropHeight / 2)) * wholeHeight);
        double targetX = transform.X * viewport.Width;
        double targetY = transform.Y * viewport.Height;
        return new VideoPreviewTransform(
            scale,
            transform.RotationDegrees,
            pivotX,
            pivotY,
            targetX - pivotX,
            targetY - pivotY);
    }
}
