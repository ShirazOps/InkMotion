namespace InkMotion;

public readonly record struct VideoCropValue(double Left, double Top, double Right, double Bottom);

public sealed class VideoCropKeyframe
{
    public int FrameOffset { get; set; }
    public double Left { get; set; }
    public double Top { get; set; }
    public double Right { get; set; }
    public double Bottom { get; set; }
}

public static class VideoCropKeyframes
{
    private const double Tolerance = 1e-6;

    public static VideoCropValue GetValue(TimelineClipProject clip, double frameOffset)
    {
        ArgumentNullException.ThrowIfNull(clip);
        VideoCropKeyframe[] keys = clip.CropKeyframes.OrderBy(key => key.FrameOffset).ToArray();
        if (keys.Length == 0)
        {
            return GetStaticValue(clip);
        }

        if (frameOffset <= keys[0].FrameOffset)
        {
            return GetValue(keys[0]);
        }

        for (int index = 1; index < keys.Length; index++)
        {
            VideoCropKeyframe right = keys[index];
            if (frameOffset > right.FrameOffset)
            {
                continue;
            }

            VideoCropKeyframe left = keys[index - 1];
            double amount = (frameOffset - left.FrameOffset) / (right.FrameOffset - left.FrameOffset);
            return Lerp(GetValue(left), GetValue(right), amount);
        }

        return GetValue(keys[^1]);
    }

    public static VideoCropValue GetValueAtTime(TimelineClipProject clip, double seconds, double frameRate) =>
        GetValue(clip, (seconds * frameRate) - (clip.StartFrame - 1));

    public static bool TrySetAtFrame(TimelineClipProject clip, TimelineTrackKind kind, int frameOffset, double frameRate)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!CanSetAtFrame(clip, kind, frameOffset, frameRate))
        {
            return false;
        }

        VideoCropValue value = GetValue(clip, frameOffset);
        int index = clip.CropKeyframes.FindIndex(key => key.FrameOffset == frameOffset);
        VideoCropKeyframe keyframe = ToKeyframe(frameOffset, value);
        if (index >= 0)
        {
            clip.CropKeyframes[index] = keyframe;
        }
        else
        {
            clip.CropKeyframes.Add(keyframe);
            clip.CropKeyframes.Sort((left, right) => left.FrameOffset.CompareTo(right.FrameOffset));
        }

        return true;
    }

    public static bool CanSetAtFrame(TimelineClipProject clip, TimelineTrackKind kind, int frameOffset, double frameRate) =>
        IsSupportedKind(kind) && IsInClip(clip, kind, frameOffset, frameRate);

    public static bool SetValueAtTime(
        TimelineClipProject clip,
        TimelineTrackKind kind,
        double seconds,
        double frameRate,
        VideoCropValue value)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!IsSupportedKind(kind) || !IsValidValue(value))
        {
            return false;
        }

        if (clip.CropKeyframes.Count == 0)
        {
            SetStaticValue(clip, value);
            return true;
        }

        int frameOffset = VideoTransformKeyframes.GetFrameOffset(clip, seconds, frameRate);
        if (!IsInClip(clip, kind, frameOffset, frameRate))
        {
            return false;
        }

        int index = clip.CropKeyframes.FindIndex(key => key.FrameOffset == frameOffset);
        VideoCropKeyframe keyframe = ToKeyframe(frameOffset, value);
        if (index >= 0)
        {
            clip.CropKeyframes[index] = keyframe;
        }
        else
        {
            clip.CropKeyframes.Add(keyframe);
            clip.CropKeyframes.Sort((left, right) => left.FrameOffset.CompareTo(right.FrameOffset));
        }

        return true;
    }

    public static bool TryRemoveAtFrame(TimelineClipProject clip, int frameOffset)
    {
        ArgumentNullException.ThrowIfNull(clip);
        int index = clip.CropKeyframes.FindIndex(key => key.FrameOffset == frameOffset);
        if (index < 0)
        {
            return false;
        }

        if (clip.CropKeyframes.Count == 1)
        {
            SetStaticValue(clip, GetValue(clip, frameOffset));
        }

        clip.CropKeyframes.RemoveAt(index);
        return true;
    }

    public static bool IsKeyframeAt(TimelineClipProject clip, int frameOffset) =>
        clip.CropKeyframes.Any(key => key.FrameOffset == frameOffset);

    public static void Split(TimelineClipProject left, TimelineClipProject right, int splitFrameOffset)
    {
        if (left.CropKeyframes.Count == 0)
        {
            return;
        }

        VideoCropValue splitValue = GetValue(left, splitFrameOffset);
        VideoCropValue startValue = GetValue(left, 0);
        int leftEndFrame = Math.Max(0, splitFrameOffset - 1);
        VideoCropValue leftEndValue = GetValue(left, leftEndFrame);
        List<VideoCropKeyframe> original = left.CropKeyframes;
        left.CropKeyframes = original.Where(key => key.FrameOffset < splitFrameOffset)
            .Select(key => Clone(key)).ToList();
        if (!left.CropKeyframes.Any(key => key.FrameOffset == 0))
        {
            left.CropKeyframes.Add(ToKeyframe(0, startValue));
        }
        if (!left.CropKeyframes.Any(key => key.FrameOffset == leftEndFrame))
        {
            left.CropKeyframes.Add(ToKeyframe(leftEndFrame, leftEndValue));
        }
        left.CropKeyframes.Sort((first, second) => first.FrameOffset.CompareTo(second.FrameOffset));
        right.CropKeyframes = original.Where(key => key.FrameOffset >= splitFrameOffset)
            .Select(key => Clone(key, key.FrameOffset - splitFrameOffset)).ToList();
        if (right.CropKeyframes.Count == 0 || right.CropKeyframes[0].FrameOffset != 0)
        {
            right.CropKeyframes.Insert(0, ToKeyframe(0, splitValue));
        }
    }

    public static void TrimStart(TimelineClipProject clip, int removedFrames)
    {
        if (clip.CropKeyframes.Count == 0 || removedFrames <= 0)
        {
            return;
        }

        VideoCropValue startValue = GetValue(clip, removedFrames);
        clip.CropKeyframes = clip.CropKeyframes.Where(key => key.FrameOffset > removedFrames)
            .Select(key => Clone(key, key.FrameOffset - removedFrames)).ToList();
        clip.CropKeyframes.Insert(0, ToKeyframe(0, startValue));
    }

    public static void TrimEnd(TimelineClipProject clip, int retainedFrames)
    {
        if (clip.CropKeyframes.Count == 0)
        {
            return;
        }

        if (retainedFrames <= 0)
        {
            clip.CropKeyframes.Clear();
            return;
        }

        VideoCropValue startValue = GetValue(clip, 0);
        int endFrame = retainedFrames - 1;
        VideoCropValue endValue = GetValue(clip, endFrame);
        clip.CropKeyframes = clip.CropKeyframes.Where(key => key.FrameOffset < retainedFrames)
            .Select(key => Clone(key)).ToList();
        if (clip.CropKeyframes.Count == 0 || clip.CropKeyframes[0].FrameOffset != 0)
        {
            clip.CropKeyframes.Insert(0, ToKeyframe(0, startValue));
        }
        if (!clip.CropKeyframes.Any(key => key.FrameOffset == endFrame))
        {
            clip.CropKeyframes.Add(ToKeyframe(endFrame, endValue));
            clip.CropKeyframes.Sort((first, second) => first.FrameOffset.CompareTo(second.FrameOffset));
        }
    }

    public static void RescaleFrameRate(TimelineClipProject clip, double oldFrameRate, double newFrameRate)
    {
        foreach (VideoCropKeyframe keyframe in clip.CropKeyframes)
        {
            keyframe.FrameOffset = (int)Math.Round(keyframe.FrameOffset * newFrameRate / oldFrameRate);
        }
        clip.CropKeyframes = clip.CropKeyframes.OrderBy(key => key.FrameOffset)
            .GroupBy(key => key.FrameOffset).Select(group => group.Last()).ToList();
    }

    public static void ClearInvalid(IReadOnlyList<TimelineTrackProject> tracks, double frameRate)
    {
        foreach (TimelineTrackProject track in tracks)
        {
            foreach (TimelineClipProject clip in track.Clips)
            {
                clip.CropKeyframes ??= [];
                if (!IsSupportedKind(track.Kind))
                {
                    clip.CropKeyframes.Clear();
                    continue;
                }

                clip.CropKeyframes.RemoveAll(key => key.FrameOffset < 0 ||
                    !IsInClip(clip, track.Kind, key.FrameOffset, frameRate) || !IsValidValue(GetValue(key)));
                clip.CropKeyframes = clip.CropKeyframes.OrderBy(key => key.FrameOffset)
                    .GroupBy(key => key.FrameOffset).Select(group => group.Last()).ToList();
            }
        }
    }

    public static List<VideoCropKeyframe> CloneKeys(IEnumerable<VideoCropKeyframe> keyframes) =>
        keyframes.Select(key => Clone(key)).ToList();

    public static string? Validate(TimelineClipProject clip, TimelineTrackKind kind, double frameRate)
    {
        if (clip.CropKeyframes is null || clip.CropKeyframes.Any(key => key is null))
        {
            return "Crop keyframes cannot be missing.";
        }
        if (!IsSupportedKind(kind) && clip.CropKeyframes.Count > 0)
        {
            return "Only video and image clips can have crop keyframes.";
        }

        int previousFrame = -1;
        foreach (VideoCropKeyframe key in clip.CropKeyframes)
        {
            if (key.FrameOffset <= previousFrame || !IsInClip(clip, kind, key.FrameOffset, frameRate) ||
                !IsValidValue(GetValue(key)))
            {
                return "Crop keyframes must be ordered, unique, inside their clip, and leave a visible source area.";
            }
            previousFrame = key.FrameOffset;
        }

        return null;
    }

    private static bool IsSupportedKind(TimelineTrackKind kind) =>
        kind is TimelineTrackKind.Video or TimelineTrackKind.Image;

    private static bool IsInClip(TimelineClipProject clip, TimelineTrackKind kind, int frameOffset, double frameRate)
    {
        if (frameOffset < 0 || !double.IsFinite(frameRate) || frameRate <= 0)
        {
            return false;
        }

        double duration = VideoTimelineEditing.GetDurationSeconds(clip, kind);
        return double.IsFinite(duration) && duration > 0 &&
            frameOffset < Math.Ceiling((duration * frameRate) - Tolerance);
    }

    private static bool IsValidValue(VideoCropValue value) =>
        double.IsFinite(value.Left) && value.Left is >= 0 and <= 49 &&
        double.IsFinite(value.Top) && value.Top is >= 0 and <= 49 &&
        double.IsFinite(value.Right) && value.Right is >= 0 and <= 49 &&
        double.IsFinite(value.Bottom) && value.Bottom is >= 0 and <= 49 &&
        value.Left + value.Right < 100 && value.Top + value.Bottom < 100;

    private static VideoCropValue GetStaticValue(TimelineClipProject clip) =>
        new(clip.CropLeftPercent, clip.CropTopPercent, clip.CropRightPercent, clip.CropBottomPercent);

    private static VideoCropValue GetValue(VideoCropKeyframe keyframe) =>
        new(keyframe.Left, keyframe.Top, keyframe.Right, keyframe.Bottom);

    private static VideoCropValue Lerp(VideoCropValue left, VideoCropValue right, double amount) =>
        new(left.Left + ((right.Left - left.Left) * amount),
            left.Top + ((right.Top - left.Top) * amount),
            left.Right + ((right.Right - left.Right) * amount),
            left.Bottom + ((right.Bottom - left.Bottom) * amount));

    private static void SetStaticValue(TimelineClipProject clip, VideoCropValue value)
    {
        clip.CropLeftPercent = value.Left;
        clip.CropTopPercent = value.Top;
        clip.CropRightPercent = value.Right;
        clip.CropBottomPercent = value.Bottom;
    }

    private static VideoCropKeyframe ToKeyframe(int frameOffset, VideoCropValue value) => new()
    {
        FrameOffset = frameOffset,
        Left = value.Left,
        Top = value.Top,
        Right = value.Right,
        Bottom = value.Bottom,
    };

    private static VideoCropKeyframe Clone(VideoCropKeyframe key, int? frameOffset = null) => new()
    {
        FrameOffset = frameOffset ?? key.FrameOffset,
        Left = key.Left,
        Top = key.Top,
        Right = key.Right,
        Bottom = key.Bottom,
    };
}
