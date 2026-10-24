namespace InkMotion;

public sealed class VideoSharpenKeyframe
{
    public int FrameOffset { get; set; }
    public double Amount { get; set; }
}

public static class VideoSharpenKeyframes
{
    private const double Tolerance = 1e-6;

    public static double GetValue(TimelineClipProject clip, double frameOffset)
    {
        ArgumentNullException.ThrowIfNull(clip);
        VideoSharpenKeyframe[] keys = clip.SharpenKeyframes.OrderBy(key => key.FrameOffset).ToArray();
        if (keys.Length == 0)
        {
            return clip.SharpenAmount;
        }
        if (frameOffset <= keys[0].FrameOffset)
        {
            return keys[0].Amount;
        }

        for (int index = 1; index < keys.Length; index++)
        {
            VideoSharpenKeyframe right = keys[index];
            if (frameOffset <= right.FrameOffset)
            {
                VideoSharpenKeyframe left = keys[index - 1];
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
        if (clip.SharpenKeyframes.Count == 0)
        {
            clip.SharpenAmount = value;
            return true;
        }

        int frameOffset = GetFrameOffset(clip, seconds, frameRate);
        return IsInClip(clip, kind, frameOffset, frameRate) && SetAtFrame(clip, frameOffset, value);
    }

    public static bool TryRemoveAtFrame(TimelineClipProject clip, int frameOffset)
    {
        ArgumentNullException.ThrowIfNull(clip);
        int index = clip.SharpenKeyframes.FindIndex(key => key.FrameOffset == frameOffset);
        if (index < 0)
        {
            return false;
        }
        if (clip.SharpenKeyframes.Count == 1)
        {
            clip.SharpenAmount = GetValue(clip, frameOffset);
        }

        clip.SharpenKeyframes.RemoveAt(index);
        return true;
    }

    public static bool IsKeyframeAt(TimelineClipProject clip, int frameOffset) =>
        clip.SharpenKeyframes.Any(key => key.FrameOffset == frameOffset);

    public static void Split(TimelineClipProject left, TimelineClipProject right, int splitFrameOffset)
    {
        if (left.SharpenKeyframes.Count == 0)
        {
            return;
        }

        double splitValue = GetValue(left, splitFrameOffset);
        double startValue = GetValue(left, 0);
        int leftEndFrame = Math.Max(0, splitFrameOffset - 1);
        double leftEndValue = GetValue(left, leftEndFrame);
        List<VideoSharpenKeyframe> original = left.SharpenKeyframes;
        left.SharpenKeyframes = original.Where(key => key.FrameOffset < splitFrameOffset)
            .Select(key => Clone(key)).ToList();
        if (!left.SharpenKeyframes.Any(key => key.FrameOffset == 0))
        {
            left.SharpenKeyframes.Add(ToKeyframe(0, startValue));
        }
        if (!left.SharpenKeyframes.Any(key => key.FrameOffset == leftEndFrame))
        {
            left.SharpenKeyframes.Add(ToKeyframe(leftEndFrame, leftEndValue));
        }
        left.SharpenKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
        right.SharpenKeyframes = original.Where(key => key.FrameOffset >= splitFrameOffset)
            .Select(key => Clone(key, key.FrameOffset - splitFrameOffset)).ToList();
        if (right.SharpenKeyframes.Count == 0 || right.SharpenKeyframes[0].FrameOffset != 0)
        {
            right.SharpenKeyframes.Insert(0, ToKeyframe(0, splitValue));
        }
    }

    public static void TrimStart(TimelineClipProject clip, int removedFrames)
    {
        if (clip.SharpenKeyframes.Count == 0 || removedFrames <= 0)
        {
            return;
        }

        double startValue = GetValue(clip, removedFrames);
        clip.SharpenKeyframes = clip.SharpenKeyframes
            .Where(key => key.FrameOffset > removedFrames)
            .Select(key => Clone(key, key.FrameOffset - removedFrames)).ToList();
        clip.SharpenKeyframes.Insert(0, ToKeyframe(0, startValue));
    }

    public static void TrimEnd(TimelineClipProject clip, int retainedFrames)
    {
        if (clip.SharpenKeyframes.Count == 0)
        {
            return;
        }
        if (retainedFrames <= 0)
        {
            clip.SharpenKeyframes.Clear();
            return;
        }

        double startValue = GetValue(clip, 0);
        int endFrame = retainedFrames - 1;
        double endValue = GetValue(clip, endFrame);
        clip.SharpenKeyframes = clip.SharpenKeyframes
            .Where(key => key.FrameOffset < retainedFrames).Select(key => Clone(key)).ToList();
        if (clip.SharpenKeyframes.Count == 0 || clip.SharpenKeyframes[0].FrameOffset != 0)
        {
            clip.SharpenKeyframes.Insert(0, ToKeyframe(0, startValue));
        }
        if (!clip.SharpenKeyframes.Any(key => key.FrameOffset == endFrame))
        {
            clip.SharpenKeyframes.Add(ToKeyframe(endFrame, endValue));
            clip.SharpenKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
        }
    }

    public static void RescaleFrameRate(TimelineClipProject clip, double oldFrameRate, double newFrameRate)
    {
        foreach (VideoSharpenKeyframe key in clip.SharpenKeyframes)
        {
            key.FrameOffset = (int)Math.Round(key.FrameOffset * newFrameRate / oldFrameRate);
        }
        clip.SharpenKeyframes = clip.SharpenKeyframes.OrderBy(key => key.FrameOffset)
            .GroupBy(key => key.FrameOffset).Select(group => group.Last()).ToList();
    }

    public static void ClearInvalid(IReadOnlyList<TimelineTrackProject> tracks, double frameRate)
    {
        foreach (TimelineTrackProject track in tracks)
        {
            foreach (TimelineClipProject clip in track.Clips)
            {
                clip.SharpenKeyframes ??= [];
                if (!IsSupportedKind(track.Kind))
                {
                    clip.SharpenKeyframes.Clear();
                    continue;
                }
                clip.SharpenKeyframes.RemoveAll(key => key is null || key.FrameOffset < 0 ||
                    !IsInClip(clip, track.Kind, key.FrameOffset, frameRate) || !IsValidValue(key.Amount));
                clip.SharpenKeyframes = clip.SharpenKeyframes.OrderBy(key => key.FrameOffset)
                    .GroupBy(key => key.FrameOffset).Select(group => group.Last()).ToList();
            }
        }
    }

    public static List<VideoSharpenKeyframe> CloneKeys(IEnumerable<VideoSharpenKeyframe> keys) =>
        keys.Select(key => Clone(key)).ToList();

    public static string? Validate(TimelineClipProject clip, TimelineTrackKind kind, double frameRate)
    {
        if (clip.SharpenKeyframes is null || clip.SharpenKeyframes.Any(key => key is null))
        {
            return "Sharpen keyframes cannot be missing.";
        }
        if (!IsSupportedKind(kind) && clip.SharpenKeyframes.Count > 0)
        {
            return "Only video and image clips can have sharpen keyframes.";
        }

        int previousFrame = -1;
        foreach (VideoSharpenKeyframe key in clip.SharpenKeyframes)
        {
            if (key.FrameOffset <= previousFrame || !IsInClip(clip, kind, key.FrameOffset, frameRate) ||
                !IsValidValue(key.Amount))
            {
                return "Sharpen keyframes must be ordered, unique, inside their clip, and within supported ranges.";
            }
            previousFrame = key.FrameOffset;
        }

        return null;
    }

    private static bool SetAtFrame(TimelineClipProject clip, int frameOffset, double value)
    {
        int index = clip.SharpenKeyframes.FindIndex(key => key.FrameOffset == frameOffset);
        VideoSharpenKeyframe keyframe = ToKeyframe(frameOffset, value);
        if (index >= 0)
        {
            clip.SharpenKeyframes[index] = keyframe;
        }
        else
        {
            clip.SharpenKeyframes.Add(keyframe);
            clip.SharpenKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
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
        double.IsFinite(value) && value is >= 0 and <= VideoSharpen.MaximumAmount;

    private static VideoSharpenKeyframe ToKeyframe(int frameOffset, double value) =>
        new() { FrameOffset = frameOffset, Amount = value };

    private static VideoSharpenKeyframe Clone(VideoSharpenKeyframe key, int? frameOffset = null) =>
        ToKeyframe(frameOffset ?? key.FrameOffset, key.Amount);
}
