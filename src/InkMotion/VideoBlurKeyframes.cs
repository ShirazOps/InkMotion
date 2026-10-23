namespace InkMotion;

public sealed class VideoBlurKeyframe
{
    public int FrameOffset { get; set; }
    public double Amount { get; set; }
}

public static class VideoBlurKeyframes
{
    private const double Tolerance = 1e-6;

    public static double GetValue(TimelineClipProject clip, double frameOffset)
    {
        ArgumentNullException.ThrowIfNull(clip);
        VideoBlurKeyframe[] keys = clip.BlurKeyframes.OrderBy(key => key.FrameOffset).ToArray();
        if (keys.Length == 0)
        {
            return clip.BlurAmount;
        }
        if (frameOffset <= keys[0].FrameOffset)
        {
            return keys[0].Amount;
        }

        for (int index = 1; index < keys.Length; index++)
        {
            VideoBlurKeyframe right = keys[index];
            if (frameOffset <= right.FrameOffset)
            {
                VideoBlurKeyframe left = keys[index - 1];
                double amount = (frameOffset - left.FrameOffset) / (right.FrameOffset - left.FrameOffset);
                return left.Amount + ((right.Amount - left.Amount) * amount);
            }
        }

        return keys[^1].Amount;
    }

    public static double GetValueAtTime(TimelineClipProject clip, double seconds, double frameRate) =>
        GetValue(clip, GetFrameOffset(clip, seconds, frameRate));

    public static int GetFrameOffset(TimelineClipProject clip, double seconds, double frameRate) =>
        (int)Math.Round(seconds * frameRate) - (clip.StartFrame - 1);

    public static bool CanSetAtFrame(TimelineClipProject clip, TimelineTrackKind kind, int frameOffset, double frameRate) =>
        IsSupportedKind(kind) && IsInClip(clip, kind, frameOffset, frameRate);

    public static bool TrySetAtFrame(TimelineClipProject clip, TimelineTrackKind kind, int frameOffset, double frameRate)
    {
        if (!CanSetAtFrame(clip, kind, frameOffset, frameRate))
        {
            return false;
        }

        return SetAtFrame(clip, frameOffset, GetValue(clip, frameOffset));
    }

    public static bool SetValueAtTime(
        TimelineClipProject clip,
        TimelineTrackKind kind,
        double seconds,
        double frameRate,
        double value)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!IsSupportedKind(kind) || !IsValidValue(value))
        {
            return false;
        }
        if (clip.BlurKeyframes.Count == 0)
        {
            clip.BlurAmount = value;
            return true;
        }

        int frameOffset = GetFrameOffset(clip, seconds, frameRate);
        return IsInClip(clip, kind, frameOffset, frameRate) && SetAtFrame(clip, frameOffset, value);
    }

    public static bool TryRemoveAtFrame(TimelineClipProject clip, int frameOffset)
    {
        ArgumentNullException.ThrowIfNull(clip);
        int index = clip.BlurKeyframes.FindIndex(key => key.FrameOffset == frameOffset);
        if (index < 0)
        {
            return false;
        }
        if (clip.BlurKeyframes.Count == 1)
        {
            clip.BlurAmount = GetValue(clip, frameOffset);
        }

        clip.BlurKeyframes.RemoveAt(index);
        return true;
    }

    public static bool IsKeyframeAt(TimelineClipProject clip, int frameOffset) =>
        clip.BlurKeyframes.Any(key => key.FrameOffset == frameOffset);

    public static void Split(TimelineClipProject left, TimelineClipProject right, int splitFrameOffset)
    {
        if (left.BlurKeyframes.Count == 0)
        {
            return;
        }

        double splitValue = GetValue(left, splitFrameOffset);
        double startValue = GetValue(left, 0);
        int leftEndFrame = Math.Max(0, splitFrameOffset - 1);
        double leftEndValue = GetValue(left, leftEndFrame);
        List<VideoBlurKeyframe> original = left.BlurKeyframes;
        left.BlurKeyframes = original.Where(key => key.FrameOffset < splitFrameOffset)
            .Select(key => Clone(key)).ToList();
        if (!left.BlurKeyframes.Any(key => key.FrameOffset == 0))
        {
            left.BlurKeyframes.Add(ToKeyframe(0, startValue));
        }
        if (!left.BlurKeyframes.Any(key => key.FrameOffset == leftEndFrame))
        {
            left.BlurKeyframes.Add(ToKeyframe(leftEndFrame, leftEndValue));
        }
        left.BlurKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
        right.BlurKeyframes = original.Where(key => key.FrameOffset >= splitFrameOffset)
            .Select(key => Clone(key, key.FrameOffset - splitFrameOffset)).ToList();
        if (right.BlurKeyframes.Count == 0 || right.BlurKeyframes[0].FrameOffset != 0)
        {
            right.BlurKeyframes.Insert(0, ToKeyframe(0, splitValue));
        }
    }

    public static void TrimStart(TimelineClipProject clip, int removedFrames)
    {
        if (clip.BlurKeyframes.Count == 0 || removedFrames <= 0)
        {
            return;
        }

        double startValue = GetValue(clip, removedFrames);
        clip.BlurKeyframes = clip.BlurKeyframes
            .Where(key => key.FrameOffset > removedFrames)
            .Select(key => Clone(key, key.FrameOffset - removedFrames)).ToList();
        clip.BlurKeyframes.Insert(0, ToKeyframe(0, startValue));
    }

    public static void TrimEnd(TimelineClipProject clip, int retainedFrames)
    {
        if (clip.BlurKeyframes.Count == 0)
        {
            return;
        }
        if (retainedFrames <= 0)
        {
            clip.BlurKeyframes.Clear();
            return;
        }

        double startValue = GetValue(clip, 0);
        int endFrame = retainedFrames - 1;
        double endValue = GetValue(clip, endFrame);
        clip.BlurKeyframes = clip.BlurKeyframes
            .Where(key => key.FrameOffset < retainedFrames).Select(key => Clone(key)).ToList();
        if (clip.BlurKeyframes.Count == 0 || clip.BlurKeyframes[0].FrameOffset != 0)
        {
            clip.BlurKeyframes.Insert(0, ToKeyframe(0, startValue));
        }
        if (!clip.BlurKeyframes.Any(key => key.FrameOffset == endFrame))
        {
            clip.BlurKeyframes.Add(ToKeyframe(endFrame, endValue));
            clip.BlurKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
        }
    }

    public static void RescaleFrameRate(TimelineClipProject clip, double oldFrameRate, double newFrameRate)
    {
        foreach (VideoBlurKeyframe key in clip.BlurKeyframes)
        {
            key.FrameOffset = (int)Math.Round(key.FrameOffset * newFrameRate / oldFrameRate);
        }
        clip.BlurKeyframes = clip.BlurKeyframes.OrderBy(key => key.FrameOffset)
            .GroupBy(key => key.FrameOffset).Select(group => group.Last()).ToList();
    }

    public static void ClearInvalid(IReadOnlyList<TimelineTrackProject> tracks, double frameRate)
    {
        foreach (TimelineTrackProject track in tracks)
        {
            foreach (TimelineClipProject clip in track.Clips)
            {
                clip.BlurKeyframes ??= [];
                if (!IsSupportedKind(track.Kind))
                {
                    clip.BlurKeyframes.Clear();
                    continue;
                }
                clip.BlurKeyframes.RemoveAll(key => key is null || key.FrameOffset < 0 ||
                    !IsInClip(clip, track.Kind, key.FrameOffset, frameRate) || !IsValidValue(key.Amount));
                clip.BlurKeyframes = clip.BlurKeyframes.OrderBy(key => key.FrameOffset)
                    .GroupBy(key => key.FrameOffset).Select(group => group.Last()).ToList();
            }
        }
    }

    public static List<VideoBlurKeyframe> CloneKeys(IEnumerable<VideoBlurKeyframe> keys) =>
        keys.Select(key => Clone(key)).ToList();

    public static string? Validate(TimelineClipProject clip, TimelineTrackKind kind, double frameRate)
    {
        if (clip.BlurKeyframes is null || clip.BlurKeyframes.Any(key => key is null))
        {
            return "Blur keyframes cannot be missing.";
        }
        if (!IsSupportedKind(kind) && clip.BlurKeyframes.Count > 0)
        {
            return "Only video and image clips can have blur keyframes.";
        }

        int previousFrame = -1;
        foreach (VideoBlurKeyframe key in clip.BlurKeyframes)
        {
            if (key.FrameOffset <= previousFrame || !IsInClip(clip, kind, key.FrameOffset, frameRate) ||
                !IsValidValue(key.Amount))
            {
                return "Blur keyframes must be ordered, unique, inside their clip, and within supported ranges.";
            }
            previousFrame = key.FrameOffset;
        }

        return null;
    }

    private static bool SetAtFrame(TimelineClipProject clip, int frameOffset, double value)
    {
        int index = clip.BlurKeyframes.FindIndex(key => key.FrameOffset == frameOffset);
        VideoBlurKeyframe keyframe = ToKeyframe(frameOffset, value);
        if (index >= 0)
        {
            clip.BlurKeyframes[index] = keyframe;
        }
        else
        {
            clip.BlurKeyframes.Add(keyframe);
            clip.BlurKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
        }
        return true;
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
            frameOffset < Math.Ceiling(duration * frameRate - Tolerance);
    }

    private static bool IsValidValue(double value) =>
        double.IsFinite(value) && value is >= 0 and <= VideoBlurEffect.MaximumAmount;

    private static VideoBlurKeyframe ToKeyframe(int frameOffset, double value) =>
        new() { FrameOffset = frameOffset, Amount = value };

    private static VideoBlurKeyframe Clone(VideoBlurKeyframe key, int? frameOffset = null) =>
        ToKeyframe(frameOffset ?? key.FrameOffset, key.Amount);
}
