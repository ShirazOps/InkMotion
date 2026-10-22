namespace InkMotion;

public readonly record struct VideoTransformValue(double X, double Y, double Scale, double RotationDegrees);

public sealed class VideoTransformKeyframe
{
    public int FrameOffset { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Scale { get; set; } = 1;
    public double RotationDegrees { get; set; }
}

public static class VideoTransformKeyframes
{
    private const double Tolerance = 1e-6;

    public static VideoTransformValue GetValue(TimelineClipProject clip, double frameOffset)
    {
        ArgumentNullException.ThrowIfNull(clip);
        VideoTransformKeyframe[] keys = clip.TransformKeyframes.OrderBy(key => key.FrameOffset).ToArray();
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
            VideoTransformKeyframe right = keys[index];
            if (frameOffset > right.FrameOffset)
            {
                continue;
            }

            VideoTransformKeyframe left = keys[index - 1];
            double amount = (frameOffset - left.FrameOffset) / (right.FrameOffset - left.FrameOffset);
            return Lerp(GetValue(left), GetValue(right), amount);
        }

        return GetValue(keys[^1]);
    }

    public static VideoTransformValue GetValueAtTime(TimelineClipProject clip, double seconds, double frameRate) =>
        GetValue(clip, (seconds * frameRate) - (clip.StartFrame - 1));

    public static bool TrySetAtFrame(TimelineClipProject clip, TimelineTrackKind kind, int frameOffset, double frameRate)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!CanSetAtFrame(clip, kind, frameOffset, frameRate))
        {
            return false;
        }

        VideoTransformValue value = GetValue(clip, frameOffset);
        int index = clip.TransformKeyframes.FindIndex(key => key.FrameOffset == frameOffset);
        var keyframe = new VideoTransformKeyframe
        {
            FrameOffset = frameOffset,
            X = value.X,
            Y = value.Y,
            Scale = value.Scale,
            RotationDegrees = value.RotationDegrees,
        };
        if (index >= 0)
        {
            clip.TransformKeyframes[index] = keyframe;
        }
        else
        {
            clip.TransformKeyframes.Add(keyframe);
            clip.TransformKeyframes.Sort((left, right) => left.FrameOffset.CompareTo(right.FrameOffset));
        }

        return true;
    }

    public static bool CanSetAtFrame(TimelineClipProject clip, TimelineTrackKind kind, int frameOffset, double frameRate)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return IsSupportedKind(kind) && IsInClip(clip, kind, frameOffset, frameRate);
    }

    public static bool SetValueAtTime(
        TimelineClipProject clip,
        TimelineTrackKind kind,
        double seconds,
        double frameRate,
        VideoTransformValue value)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!IsSupportedKind(kind) || !IsValidValue(value))
        {
            return false;
        }

        if (clip.TransformKeyframes.Count == 0)
        {
            SetStaticValue(clip, value);
            return true;
        }

        int frameOffset = GetFrameOffset(clip, seconds, frameRate);
        if (!IsInClip(clip, kind, frameOffset, frameRate))
        {
            return false;
        }

        int index = clip.TransformKeyframes.FindIndex(key => key.FrameOffset == frameOffset);
        VideoTransformKeyframe keyframe = ToKeyframe(frameOffset, value);
        if (index >= 0)
        {
            clip.TransformKeyframes[index] = keyframe;
        }
        else
        {
            clip.TransformKeyframes.Add(keyframe);
            clip.TransformKeyframes.Sort((left, right) => left.FrameOffset.CompareTo(right.FrameOffset));
        }

        return true;
    }

    public static bool TryRemoveAtFrame(TimelineClipProject clip, int frameOffset)
    {
        ArgumentNullException.ThrowIfNull(clip);
        int index = clip.TransformKeyframes.FindIndex(key => key.FrameOffset == frameOffset);
        if (index < 0)
        {
            return false;
        }

        if (clip.TransformKeyframes.Count == 1)
        {
            SetStaticValue(clip, GetValue(clip, frameOffset));
        }

        clip.TransformKeyframes.RemoveAt(index);
        return true;
    }

    public static int? FindAdjacentFrame(TimelineClipProject clip, int frameOffset, bool next)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return next
            ? clip.TransformKeyframes.Where(key => key.FrameOffset > frameOffset).Select(key => (int?)key.FrameOffset).Min()
            : clip.TransformKeyframes.Where(key => key.FrameOffset < frameOffset).Select(key => (int?)key.FrameOffset).Max();
    }

    public static bool IsKeyframeAt(TimelineClipProject clip, int frameOffset)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return clip.TransformKeyframes.Any(key => key.FrameOffset == frameOffset);
    }

    public static List<VideoTransformKeyframe> CloneKeys(IEnumerable<VideoTransformKeyframe> keyframes) =>
        keyframes.Select(keyframe => Clone(keyframe)).ToList();

    public static string? Validate(TimelineClipProject clip, TimelineTrackKind kind, double frameRate)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.TransformKeyframes is null)
        {
            return "Transform keyframes are missing.";
        }
        if (clip.TransformKeyframes.Any(key => key is null))
        {
            return "Transform keyframes cannot contain missing entries.";
        }

        if (!IsSupportedKind(kind) && clip.TransformKeyframes.Count != 0)
        {
            return "Only video and image clips can have transform keyframes.";
        }

        int previousFrame = -1;
        foreach (VideoTransformKeyframe key in clip.TransformKeyframes)
        {
            if (key.FrameOffset <= previousFrame ||
                !IsInClip(clip, kind, key.FrameOffset, frameRate) ||
                !IsValidValue(GetValue(key)))
            {
                return "Transform keyframes must be ordered, unique, inside their clip, and use valid transform values.";
            }

            previousFrame = key.FrameOffset;
        }

        return null;
    }

    public static int GetFrameOffset(TimelineClipProject clip, double seconds, double frameRate) =>
        (int)Math.Round(seconds * frameRate) - (clip.StartFrame - 1);

    public static void Split(TimelineClipProject left, TimelineClipProject right, int splitFrameOffset)
    {
        if (left.TransformKeyframes.Count == 0)
        {
            return;
        }

        VideoTransformValue splitValue = GetValue(left, splitFrameOffset);
        VideoTransformValue startValue = GetValue(left, 0);
        int leftEndFrame = Math.Max(0, splitFrameOffset - 1);
        VideoTransformValue leftEndValue = GetValue(left, leftEndFrame);
        List<VideoTransformKeyframe> original = left.TransformKeyframes;
        left.TransformKeyframes = original
            .Where(key => key.FrameOffset < splitFrameOffset)
            .Select(key => Clone(key))
            .ToList();
        if (!left.TransformKeyframes.Any(key => key.FrameOffset == 0))
        {
            left.TransformKeyframes.Add(ToKeyframe(0, startValue));
        }
        if (!left.TransformKeyframes.Any(key => key.FrameOffset == leftEndFrame))
        {
            left.TransformKeyframes.Add(ToKeyframe(leftEndFrame, leftEndValue));
        }
        left.TransformKeyframes.Sort((first, second) => first.FrameOffset.CompareTo(second.FrameOffset));
        right.TransformKeyframes = original
            .Where(key => key.FrameOffset >= splitFrameOffset)
            .Select(key => Clone(key, key.FrameOffset - splitFrameOffset))
            .ToList();
        if (right.TransformKeyframes.Count == 0 || right.TransformKeyframes[0].FrameOffset != 0)
        {
            right.TransformKeyframes.Insert(0, ToKeyframe(0, splitValue));
        }
    }

    public static void TrimStart(TimelineClipProject clip, int removedFrames)
    {
        if (clip.TransformKeyframes.Count == 0 || removedFrames <= 0)
        {
            return;
        }

        VideoTransformValue startValue = GetValue(clip, removedFrames);
        clip.TransformKeyframes = clip.TransformKeyframes
            .Where(key => key.FrameOffset > removedFrames)
            .Select(key => Clone(key, key.FrameOffset - removedFrames))
            .ToList();
        clip.TransformKeyframes.Insert(0, ToKeyframe(0, startValue));
    }

    public static void TrimEnd(TimelineClipProject clip, int retainedFrames)
    {
        if (clip.TransformKeyframes.Count == 0)
        {
            return;
        }

        if (retainedFrames <= 0)
        {
            clip.TransformKeyframes.Clear();
            return;
        }

        VideoTransformValue startValue = GetValue(clip, 0);
        int endFrame = retainedFrames - 1;
        VideoTransformValue endValue = GetValue(clip, endFrame);
        clip.TransformKeyframes = clip.TransformKeyframes
            .Where(key => key.FrameOffset < retainedFrames)
            .Select(key => Clone(key))
            .ToList();
        if (clip.TransformKeyframes.Count == 0 || clip.TransformKeyframes[0].FrameOffset != 0)
        {
            clip.TransformKeyframes.Insert(0, ToKeyframe(0, startValue));
        }
        if (!clip.TransformKeyframes.Any(key => key.FrameOffset == endFrame))
        {
            clip.TransformKeyframes.Add(ToKeyframe(endFrame, endValue));
            clip.TransformKeyframes.Sort((first, second) => first.FrameOffset.CompareTo(second.FrameOffset));
        }
    }

    public static void ClearInvalid(IReadOnlyList<TimelineTrackProject> tracks, double frameRate)
    {
        foreach (TimelineTrackProject track in tracks)
        {
            foreach (TimelineClipProject clip in track.Clips)
            {
                clip.TransformKeyframes ??= [];
                if (!IsSupportedKind(track.Kind))
                {
                    clip.TransformKeyframes.Clear();
                    continue;
                }

                clip.TransformKeyframes.RemoveAll(key =>
                    key.FrameOffset < 0 ||
                    !IsInClip(clip, track.Kind, key.FrameOffset, frameRate) ||
                    !IsValidValue(GetValue(key)));
                clip.TransformKeyframes.Sort((left, right) => left.FrameOffset.CompareTo(right.FrameOffset));
                for (int index = clip.TransformKeyframes.Count - 1; index > 0; index--)
                {
                    if (clip.TransformKeyframes[index].FrameOffset == clip.TransformKeyframes[index - 1].FrameOffset)
                    {
                        clip.TransformKeyframes.RemoveAt(index);
                    }
                }
            }
        }
    }

    private static bool IsInClip(TimelineClipProject clip, TimelineTrackKind kind, int frameOffset, double frameRate)
    {
        if (frameOffset < 0 || !double.IsFinite(frameRate) || frameRate <= 0)
        {
            return false;
        }

        double duration = VideoTimelineEditing.GetDurationSeconds(clip, kind);
        return double.IsFinite(duration) && duration > 0 && frameOffset < Math.Ceiling((duration * frameRate) - Tolerance);
    }

    private static bool IsSupportedKind(TimelineTrackKind kind) =>
        kind is TimelineTrackKind.Video or TimelineTrackKind.Image;

    private static bool IsValidValue(VideoTransformValue value) =>
        double.IsFinite(value.X) && value.X is >= 0 and <= 1 &&
        double.IsFinite(value.Y) && value.Y is >= 0 and <= 1 &&
        double.IsFinite(value.Scale) && value.Scale is >= VideoTimelineEditing.MinimumScale and <= VideoTimelineEditing.MaximumScale &&
        double.IsFinite(value.RotationDegrees) && value.RotationDegrees is >= -180 and <= 180;

    private static VideoTransformValue GetStaticValue(TimelineClipProject clip) =>
        new(clip.X, clip.Y, clip.Scale, clip.RotationDegrees);

    private static void SetStaticValue(TimelineClipProject clip, VideoTransformValue value)
    {
        clip.X = value.X;
        clip.Y = value.Y;
        clip.Scale = value.Scale;
        clip.RotationDegrees = value.RotationDegrees;
    }

    private static VideoTransformValue GetValue(VideoTransformKeyframe keyframe) =>
        new(keyframe.X, keyframe.Y, keyframe.Scale, keyframe.RotationDegrees);

    private static VideoTransformValue Lerp(VideoTransformValue left, VideoTransformValue right, double amount) =>
        new(
            left.X + ((right.X - left.X) * amount),
            left.Y + ((right.Y - left.Y) * amount),
            left.Scale + ((right.Scale - left.Scale) * amount),
            left.RotationDegrees + ((right.RotationDegrees - left.RotationDegrees) * amount));

    private static VideoTransformKeyframe ToKeyframe(int frameOffset, VideoTransformValue value) =>
        new()
        {
            FrameOffset = frameOffset,
            X = value.X,
            Y = value.Y,
            Scale = value.Scale,
            RotationDegrees = value.RotationDegrees,
        };

    private static VideoTransformKeyframe Clone(VideoTransformKeyframe keyframe, int? frameOffset = null) =>
        new()
        {
            FrameOffset = frameOffset ?? keyframe.FrameOffset,
            X = keyframe.X,
            Y = keyframe.Y,
            Scale = keyframe.Scale,
            RotationDegrees = keyframe.RotationDegrees,
        };
}
