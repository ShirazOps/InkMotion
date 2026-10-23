namespace InkMotion;

public sealed class VideoVignetteKeyframe
{
    public int FrameOffset { get; set; }
    public double Amount { get; set; }
}

public static class VideoVignetteKeyframes
{
    private const double Tolerance = 1e-6;

    public static double GetValue(TimelineClipProject clip, double frameOffset)
    {
        ArgumentNullException.ThrowIfNull(clip);
        VideoVignetteKeyframe[] keys = clip.VignetteKeyframes.OrderBy(key => key.FrameOffset).ToArray();
        if (keys.Length == 0)
        {
            return clip.VignetteAmount;
        }
        if (frameOffset <= keys[0].FrameOffset)
        {
            return keys[0].Amount;
        }

        for (int index = 1; index < keys.Length; index++)
        {
            VideoVignetteKeyframe right = keys[index];
            if (frameOffset <= right.FrameOffset)
            {
                VideoVignetteKeyframe left = keys[index - 1];
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

        if (clip.VignetteKeyframes.Count == 0)
        {
            clip.VignetteAmount = value;
            return true;
        }

        int frameOffset = GetFrameOffset(clip, seconds, frameRate);
        return IsInClip(clip, kind, frameOffset, frameRate) && SetAtFrame(clip, frameOffset, value);
    }

    public static bool TryRemoveAtFrame(TimelineClipProject clip, int frameOffset)
    {
        ArgumentNullException.ThrowIfNull(clip);
        int index = clip.VignetteKeyframes.FindIndex(key => key.FrameOffset == frameOffset);
        if (index < 0)
        {
            return false;
        }
        if (clip.VignetteKeyframes.Count == 1)
        {
            clip.VignetteAmount = GetValue(clip, frameOffset);
        }

        clip.VignetteKeyframes.RemoveAt(index);
        return true;
    }

    public static bool IsKeyframeAt(TimelineClipProject clip, int frameOffset) =>
        clip.VignetteKeyframes.Any(key => key.FrameOffset == frameOffset);

    public static void Split(TimelineClipProject left, TimelineClipProject right, int splitFrameOffset)
    {
        if (left.VignetteKeyframes.Count == 0)
        {
            return;
        }

        double splitValue = GetValue(left, splitFrameOffset);
        double startValue = GetValue(left, 0);
        int leftEndFrame = Math.Max(0, splitFrameOffset - 1);
        double leftEndValue = GetValue(left, leftEndFrame);
        List<VideoVignetteKeyframe> original = left.VignetteKeyframes;
        left.VignetteKeyframes = original.Where(key => key.FrameOffset < splitFrameOffset)
            .Select(key => Clone(key)).ToList();
        if (!left.VignetteKeyframes.Any(key => key.FrameOffset == 0))
        {
            left.VignetteKeyframes.Add(ToKeyframe(0, startValue));
        }
        if (!left.VignetteKeyframes.Any(key => key.FrameOffset == leftEndFrame))
        {
            left.VignetteKeyframes.Add(ToKeyframe(leftEndFrame, leftEndValue));
        }
        left.VignetteKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
        right.VignetteKeyframes = original.Where(key => key.FrameOffset >= splitFrameOffset)
            .Select(key => Clone(key, key.FrameOffset - splitFrameOffset)).ToList();
        if (right.VignetteKeyframes.Count == 0 || right.VignetteKeyframes[0].FrameOffset != 0)
        {
            right.VignetteKeyframes.Insert(0, ToKeyframe(0, splitValue));
        }
    }

    public static void TrimStart(TimelineClipProject clip, int removedFrames)
    {
        if (clip.VignetteKeyframes.Count == 0 || removedFrames <= 0)
        {
            return;
        }

        double startValue = GetValue(clip, removedFrames);
        clip.VignetteKeyframes = clip.VignetteKeyframes
            .Where(key => key.FrameOffset > removedFrames)
            .Select(key => Clone(key, key.FrameOffset - removedFrames)).ToList();
        clip.VignetteKeyframes.Insert(0, ToKeyframe(0, startValue));
    }

    public static void TrimEnd(TimelineClipProject clip, int retainedFrames)
    {
        if (clip.VignetteKeyframes.Count == 0)
        {
            return;
        }
        if (retainedFrames <= 0)
        {
            clip.VignetteKeyframes.Clear();
            return;
        }

        double startValue = GetValue(clip, 0);
        int endFrame = retainedFrames - 1;
        double endValue = GetValue(clip, endFrame);
        clip.VignetteKeyframes = clip.VignetteKeyframes
            .Where(key => key.FrameOffset < retainedFrames).Select(key => Clone(key)).ToList();
        if (clip.VignetteKeyframes.Count == 0 || clip.VignetteKeyframes[0].FrameOffset != 0)
        {
            clip.VignetteKeyframes.Insert(0, ToKeyframe(0, startValue));
        }
        if (!clip.VignetteKeyframes.Any(key => key.FrameOffset == endFrame))
        {
            clip.VignetteKeyframes.Add(ToKeyframe(endFrame, endValue));
            clip.VignetteKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
        }
    }

    public static void RescaleFrameRate(TimelineClipProject clip, double oldFrameRate, double newFrameRate)
    {
        foreach (VideoVignetteKeyframe key in clip.VignetteKeyframes)
        {
            key.FrameOffset = (int)Math.Round(key.FrameOffset * newFrameRate / oldFrameRate);
        }
        clip.VignetteKeyframes = clip.VignetteKeyframes.OrderBy(key => key.FrameOffset)
            .GroupBy(key => key.FrameOffset).Select(group => group.Last()).ToList();
    }

    public static void ClearInvalid(IReadOnlyList<TimelineTrackProject> tracks, double frameRate)
    {
        foreach (TimelineTrackProject track in tracks)
        {
            foreach (TimelineClipProject clip in track.Clips)
            {
                clip.VignetteKeyframes ??= [];
                if (!IsSupportedKind(track.Kind))
                {
                    clip.VignetteKeyframes.Clear();
                    continue;
                }
                clip.VignetteKeyframes.RemoveAll(key => key is null || key.FrameOffset < 0 ||
                    !IsInClip(clip, track.Kind, key.FrameOffset, frameRate) || !IsValidValue(key.Amount));
                clip.VignetteKeyframes = clip.VignetteKeyframes.OrderBy(key => key.FrameOffset)
                    .GroupBy(key => key.FrameOffset).Select(group => group.Last()).ToList();
            }
        }
    }

    public static List<VideoVignetteKeyframe> CloneKeys(IEnumerable<VideoVignetteKeyframe> keys) =>
        keys.Select(key => Clone(key)).ToList();

    public static string? Validate(TimelineClipProject clip, TimelineTrackKind kind, double frameRate)
    {
        if (clip.VignetteKeyframes is null || clip.VignetteKeyframes.Any(key => key is null))
        {
            return "Vignette keyframes cannot be missing.";
        }
        if (!IsSupportedKind(kind) && clip.VignetteKeyframes.Count > 0)
        {
            return "Only video and image clips can have vignette keyframes.";
        }

        int previousFrame = -1;
        foreach (VideoVignetteKeyframe key in clip.VignetteKeyframes)
        {
            if (key.FrameOffset <= previousFrame || !IsInClip(clip, kind, key.FrameOffset, frameRate) ||
                !IsValidValue(key.Amount))
            {
                return "Vignette keyframes must be ordered, unique, inside their clip, and within supported ranges.";
            }
            previousFrame = key.FrameOffset;
        }

        return null;
    }

    private static bool SetAtFrame(TimelineClipProject clip, int frameOffset, double value)
    {
        int index = clip.VignetteKeyframes.FindIndex(key => key.FrameOffset == frameOffset);
        VideoVignetteKeyframe keyframe = ToKeyframe(frameOffset, value);
        if (index >= 0)
        {
            clip.VignetteKeyframes[index] = keyframe;
        }
        else
        {
            clip.VignetteKeyframes.Add(keyframe);
            clip.VignetteKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
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
        double.IsFinite(value) && value is >= 0 and <= VideoVignette.MaximumAmount;

    private static VideoVignetteKeyframe ToKeyframe(int frameOffset, double value) =>
        new() { FrameOffset = frameOffset, Amount = value };

    private static VideoVignetteKeyframe Clone(VideoVignetteKeyframe key, int? frameOffset = null) =>
        ToKeyframe(frameOffset ?? key.FrameOffset, key.Amount);
}
