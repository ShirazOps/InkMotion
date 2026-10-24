using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using System.Numerics;
using Windows.Foundation;

namespace InkMotion;

public enum VideoMaskShape
{
    None,
    Rectangle,
    Ellipse,
}

public readonly record struct VideoMaskValue(double X, double Y, double Width, double Height, double RotationDegrees = 0);

public sealed record VideoMaskKeyframe
{
    public int FrameOffset { get; set; }
    public double X { get; set; } = 0.5;
    public double Y { get; set; } = 0.5;
    public double Width { get; set; } = 1;
    public double Height { get; set; } = 1;
    public double RotationDegrees { get; set; }
}

public static class VideoClipMask
{
    public static string? Validate(TimelineClipProject clip, TimelineTrackKind kind, double frameRate = 24)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!Enum.IsDefined(clip.MaskShape) ||
            !IsValid(clip.MaskX, clip.MaskY, clip.MaskWidth, clip.MaskHeight, clip.MaskRotationDegrees) ||
            !double.IsFinite(clip.MaskFeather) || clip.MaskFeather is < 0 or > 0.25 ||
            clip.MaskKeyframes is null || clip.MaskKeyframes.Any(key => key is null ||
                key.FrameOffset < 0 ||
                !IsValid(key.X, key.Y, key.Width, key.Height, key.RotationDegrees) ||
                !IsInClip(clip, kind, key.FrameOffset, frameRate)))
        {
            return "Video mask geometry or keyframes are not valid.";
        }

        if ((clip.MaskShape != VideoMaskShape.None || clip.MaskKeyframes.Count > 0) &&
            kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image))
        {
            return "Masks are supported only for video and image clips.";
        }

        if (clip.MaskKeyframes.Select(key => key.FrameOffset).Distinct().Count() != clip.MaskKeyframes.Count)
        {
            return "Video mask keyframes must use unique clip-frame positions.";
        }

        return null;
    }

    public static VideoMaskValue GetValue(TimelineClipProject clip, double frameOffset)
    {
        VideoMaskKeyframe[] keys = clip.MaskKeyframes.OrderBy(key => key.FrameOffset).ToArray();
        if (keys.Length == 0)
        {
            return new(clip.MaskX, clip.MaskY, clip.MaskWidth, clip.MaskHeight, clip.MaskRotationDegrees);
        }

        if (frameOffset <= keys[0].FrameOffset)
        {
            return ToValue(keys[0]);
        }

        for (int index = 1; index < keys.Length; index++)
        {
            VideoMaskKeyframe right = keys[index];
            if (frameOffset > right.FrameOffset)
            {
                continue;
            }

            VideoMaskKeyframe left = keys[index - 1];
            double amount = (frameOffset - left.FrameOffset) / (right.FrameOffset - left.FrameOffset);
            VideoMaskValue start = ToValue(left);
            VideoMaskValue end = ToValue(right);
            return new(
                Lerp(start.X, end.X, amount),
                Lerp(start.Y, end.Y, amount),
                Lerp(start.Width, end.Width, amount),
                Lerp(start.Height, end.Height, amount),
                Lerp(start.RotationDegrees, end.RotationDegrees, amount));
        }

        return ToValue(keys[^1]);
    }

    public static bool SetKeyframe(
        TimelineClipProject clip,
        TimelineTrackKind kind,
        int frameOffset,
        double frameRate,
        VideoMaskValue? requestedValue = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image) || !double.IsFinite(frameRate) || frameRate <= 0 ||
            !IsInClip(clip, kind, frameOffset, frameRate) ||
            VideoMaskShapeKeyframes.GetValue(clip, frameOffset) == VideoMaskShape.None)
        {
            return false;
        }

        VideoMaskValue value = requestedValue ?? GetValue(clip, frameOffset);
        if (!IsValid(value.X, value.Y, value.Width, value.Height, value.RotationDegrees))
        {
            return false;
        }
        VideoMaskKeyframe? existing = clip.MaskKeyframes.FirstOrDefault(key => key.FrameOffset == frameOffset);
        if (existing is null)
        {
            clip.MaskKeyframes.Add(new VideoMaskKeyframe { FrameOffset = frameOffset });
            existing = clip.MaskKeyframes[^1];
        }

        existing.X = value.X;
        existing.Y = value.Y;
        existing.Width = value.Width;
        existing.Height = value.Height;
        existing.RotationDegrees = value.RotationDegrees;
        clip.MaskKeyframes.Sort((left, right) => left.FrameOffset.CompareTo(right.FrameOffset));
        return true;
    }

    public static bool CanSetKeyframe(TimelineClipProject clip, TimelineTrackKind kind, int frameOffset, double frameRate) =>
        (kind is TimelineTrackKind.Video or TimelineTrackKind.Image) &&
        double.IsFinite(frameRate) && frameRate > 0 &&
        VideoMaskShapeKeyframes.GetValue(clip, frameOffset) != VideoMaskShape.None &&
        IsInClip(clip, kind, frameOffset, frameRate);

    public static bool IsKeyframeAt(TimelineClipProject clip, int frameOffset) =>
        clip.MaskKeyframes.Any(key => key.FrameOffset == frameOffset);

    public static bool TryRemoveKeyframe(TimelineClipProject clip, int frameOffset)
    {
        ArgumentNullException.ThrowIfNull(clip);
        int index = clip.MaskKeyframes.FindIndex(key => key.FrameOffset == frameOffset);
        if (index < 0)
        {
            return false;
        }

        if (clip.MaskKeyframes.Count == 1)
        {
            VideoMaskValue value = GetValue(clip, frameOffset);
            clip.MaskX = value.X;
            clip.MaskY = value.Y;
            clip.MaskWidth = value.Width;
            clip.MaskHeight = value.Height;
            clip.MaskRotationDegrees = value.RotationDegrees;
        }

        clip.MaskKeyframes.RemoveAt(index);
        return true;
    }

    public static void Split(TimelineClipProject left, TimelineClipProject right, int splitFrameOffset)
    {
        if (left.MaskKeyframes.Count == 0)
        {
            return;
        }

        VideoMaskValue splitValue = GetValue(left, splitFrameOffset);
        VideoMaskValue startValue = GetValue(left, 0);
        int leftEndFrame = Math.Max(0, splitFrameOffset - 1);
        VideoMaskValue leftEndValue = GetValue(left, leftEndFrame);
        List<VideoMaskKeyframe> original = left.MaskKeyframes;
        left.MaskKeyframes = original.Where(key => key.FrameOffset < splitFrameOffset)
            .Select(key => CloneKey(key)).ToList();
        if (!left.MaskKeyframes.Any(key => key.FrameOffset == 0))
        {
            left.MaskKeyframes.Add(ToKeyframe(0, startValue));
        }
        if (!left.MaskKeyframes.Any(key => key.FrameOffset == leftEndFrame))
        {
            left.MaskKeyframes.Add(ToKeyframe(leftEndFrame, leftEndValue));
        }
        left.MaskKeyframes.Sort((first, second) => first.FrameOffset.CompareTo(second.FrameOffset));
        right.MaskKeyframes = original.Where(key => key.FrameOffset >= splitFrameOffset)
            .Select(key => CloneKey(key, key.FrameOffset - splitFrameOffset)).ToList();
        if (right.MaskKeyframes.Count == 0 || right.MaskKeyframes[0].FrameOffset != 0)
        {
            right.MaskKeyframes.Insert(0, ToKeyframe(0, splitValue));
        }
    }

    public static void TrimStart(TimelineClipProject clip, int removedFrames)
    {
        if (clip.MaskKeyframes.Count == 0 || removedFrames <= 0)
        {
            return;
        }

        VideoMaskValue startValue = GetValue(clip, removedFrames);
        clip.MaskKeyframes = clip.MaskKeyframes.Where(key => key.FrameOffset > removedFrames)
            .Select(key => CloneKey(key, key.FrameOffset - removedFrames)).ToList();
        clip.MaskKeyframes.Insert(0, ToKeyframe(0, startValue));
    }

    public static void TrimEnd(TimelineClipProject clip, int retainedFrames)
    {
        if (clip.MaskKeyframes.Count == 0)
        {
            return;
        }

        if (retainedFrames <= 0)
        {
            clip.MaskKeyframes.Clear();
            return;
        }

        VideoMaskValue startValue = GetValue(clip, 0);
        int endFrame = retainedFrames - 1;
        VideoMaskValue endValue = GetValue(clip, endFrame);
        clip.MaskKeyframes = clip.MaskKeyframes.Where(key => key.FrameOffset < retainedFrames)
            .Select(key => CloneKey(key)).ToList();
        if (clip.MaskKeyframes.Count == 0 || clip.MaskKeyframes[0].FrameOffset != 0)
        {
            clip.MaskKeyframes.Insert(0, ToKeyframe(0, startValue));
        }
        if (!clip.MaskKeyframes.Any(key => key.FrameOffset == endFrame))
        {
            clip.MaskKeyframes.Add(ToKeyframe(endFrame, endValue));
            clip.MaskKeyframes.Sort((first, second) => first.FrameOffset.CompareTo(second.FrameOffset));
        }
    }

    public static void RescaleFrameRate(TimelineClipProject clip, double oldFrameRate, double newFrameRate)
    {
        foreach (VideoMaskKeyframe keyframe in clip.MaskKeyframes)
        {
            keyframe.FrameOffset = (int)Math.Round(keyframe.FrameOffset * newFrameRate / oldFrameRate);
        }
        clip.MaskKeyframes = clip.MaskKeyframes.OrderBy(key => key.FrameOffset)
            .GroupBy(key => key.FrameOffset).Select(group => group.Last()).ToList();
    }

    public static void ClearInvalid(IReadOnlyList<TimelineTrackProject> tracks, double frameRate)
    {
        foreach (TimelineTrackProject track in tracks)
        {
            foreach (TimelineClipProject clip in track.Clips)
            {
                clip.MaskKeyframes ??= [];
                if (track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image))
                {
                    clip.MaskKeyframes.Clear();
                    continue;
                }

                clip.MaskKeyframes.RemoveAll(key => key.FrameOffset < 0 ||
                    !IsInClip(clip, track.Kind, key.FrameOffset, frameRate) ||
                    !IsValid(key.X, key.Y, key.Width, key.Height, key.RotationDegrees));
                clip.MaskKeyframes = clip.MaskKeyframes.OrderBy(key => key.FrameOffset)
                    .GroupBy(key => key.FrameOffset).Select(group => group.Last()).ToList();
            }
        }
    }

    public static CanvasGeometry CreateGeometry(
        CanvasDrawingSession session,
        VideoMaskShape shape,
        VideoMaskValue mask,
        float width,
        float height)
    {
        float centreX = (float)(mask.X * width);
        float centreY = (float)(mask.Y * height);
        float halfWidth = (float)(mask.Width * width / 2);
        float halfHeight = (float)(mask.Height * height / 2);
        CanvasGeometry geometry = shape switch
        {
            VideoMaskShape.Rectangle => CanvasGeometry.CreateRectangle(
                session, new Rect(centreX - halfWidth, centreY - halfHeight, halfWidth * 2, halfHeight * 2)),
            VideoMaskShape.Ellipse => CanvasGeometry.CreateEllipse(session, centreX, centreY, halfWidth, halfHeight),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        if (mask.RotationDegrees == 0)
        {
            return geometry;
        }

        using (geometry)
        {
            return geometry.Transform(Matrix3x2.CreateRotation(
                (float)(mask.RotationDegrees * Math.PI / 180), new Vector2(centreX, centreY)));
        }
    }

    public static void DrawMaskedImage(
        CanvasDrawingSession session,
        ICanvasImage image,
        Rect destination,
        Rect sourceRect,
        float opacity,
        TimelineClipProject clip,
        int clipFrameOffset,
        float width,
        float height)
    {
        VideoMaskValue mask = GetValue(clip, clipFrameOffset);
        VideoMaskShape shape = VideoMaskShapeKeyframes.GetValue(clip, clipFrameOffset);
        if (shape == VideoMaskShape.None)
        {
            session.DrawImage(image, destination, sourceRect, opacity);
            return;
        }

        double feather = VideoMaskFeatherKeyframes.GetValue(clip, clipFrameOffset);
        bool inverted = VideoMaskInversionKeyframes.GetValue(clip, clipFrameOffset);
        if (feather <= 0)
        {
            using CanvasGeometry geometry = CreateGeometry(session, shape, mask, width, height);
            if (inverted)
            {
                using CanvasGeometry bounds = CanvasGeometry.CreateRectangle(session, new Rect(0, 0, width, height));
                using CanvasGeometry outside = bounds.CombineWith(
                    geometry, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
                DrawInLayer(session, outside, image, destination, sourceRect, opacity);
            }
            else
            {
                DrawInLayer(session, geometry, image, destination, sourceRect, opacity);
            }

            return;
        }

        const int featherSteps = 8;
        double featherX = Math.Min(feather * Math.Min(width, height) / width, mask.Width * 0.49);
        double featherY = Math.Min(feather * Math.Min(width, height) / height, mask.Height * 0.49);
        VideoMaskValue previousMask = mask;
        using CanvasGeometry outerGeometry = CreateGeometry(session, shape, mask, width, height);
        if (inverted)
        {
            using CanvasGeometry bounds = CanvasGeometry.CreateRectangle(session, new Rect(0, 0, width, height));
            using CanvasGeometry outside = bounds.CombineWith(
                outerGeometry, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
            DrawInLayer(session, outside, image, destination, sourceRect, opacity);
        }

        for (int step = 0; step < featherSteps; step++)
        {
            double inset = (step + 1d) / featherSteps;
            VideoMaskValue innerMask = new(
                mask.X,
                mask.Y,
                mask.Width - (2 * featherX * inset),
                mask.Height - (2 * featherY * inset),
                mask.RotationDegrees);
            using CanvasGeometry innerGeometry = CreateGeometry(session, shape, innerMask, width, height);
            using CanvasGeometry previousGeometry = CreateGeometry(session, shape, previousMask, width, height);
            using CanvasGeometry band = previousGeometry.CombineWith(
                innerGeometry, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
            double bandAmount = (step + 0.5) / featherSteps;
            float bandOpacity = opacity * (float)(inverted ? 1 - bandAmount : bandAmount);
            DrawInLayer(session, band, image, destination, sourceRect, bandOpacity);
            previousMask = innerMask;
        }

        if (!inverted)
        {
            using CanvasGeometry centre = CreateGeometry(session, shape, previousMask, width, height);
            DrawInLayer(session, centre, image, destination, sourceRect, opacity);
        }
    }

    private static void DrawInLayer(
        CanvasDrawingSession session,
        CanvasGeometry geometry,
        ICanvasImage image,
        Rect destination,
        Rect sourceRect,
        float opacity)
    {
        using (session.CreateLayer(1, geometry))
        {
            session.DrawImage(image, destination, sourceRect, opacity);
        }
    }

    private static VideoMaskValue ToValue(VideoMaskKeyframe key) =>
        new(key.X, key.Y, key.Width, key.Height, key.RotationDegrees);

    private static bool IsInClip(TimelineClipProject clip, TimelineTrackKind kind, int frameOffset, double frameRate)
    {
        if (frameOffset < 0 || !double.IsFinite(frameRate) || frameRate <= 0)
        {
            return false;
        }

        double duration = VideoTimelineEditing.GetDurationSeconds(clip, kind);
        return double.IsFinite(duration) && duration > 0 &&
            frameOffset < Math.Ceiling(duration * frameRate - 1e-6);
    }

    private static VideoMaskKeyframe ToKeyframe(int frameOffset, VideoMaskValue value) => new()
    {
        FrameOffset = frameOffset,
        X = value.X,
        Y = value.Y,
        Width = value.Width,
        Height = value.Height,
        RotationDegrees = value.RotationDegrees,
    };

    private static VideoMaskKeyframe CloneKey(VideoMaskKeyframe key, int? frameOffset = null) => new()
    {
        FrameOffset = frameOffset ?? key.FrameOffset,
        X = key.X,
        Y = key.Y,
        Width = key.Width,
        Height = key.Height,
        RotationDegrees = key.RotationDegrees,
    };

    private static bool IsValid(double x, double y, double width, double height, double rotationDegrees) =>
        double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(width) && double.IsFinite(height) &&
        double.IsFinite(rotationDegrees) &&
        x is >= 0 and <= 1 && y is >= 0 and <= 1 && width is > 0 and <= 1 && height is > 0 and <= 1 &&
        rotationDegrees is >= -180 and <= 180;

    private static double Lerp(double start, double end, double amount) => start + ((end - start) * amount);
}
