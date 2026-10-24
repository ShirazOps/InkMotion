namespace InkMotion;

public sealed record VideoChromaKeySpillKeyframe
{
    public int FrameOffset { get; set; }
    public double Amount { get; set; }
}

public static class VideoChromaKeySpillKeyframes
{
    private const double Tolerance = 1e-6;

    public static double GetValue(TimelineClipProject clip, double frameOffset)
    {
        ArgumentNullException.ThrowIfNull(clip);
        VideoChromaKeySpillKeyframe[] keys = clip.ChromaKeySpillKeyframes
            .OrderBy(key => key.FrameOffset).ToArray();
        if (keys.Length == 0)
        {
            return clip.ChromaKeySpillSuppression;
        }
        if (frameOffset <= keys[0].FrameOffset)
        {
            return keys[0].Amount;
        }

        for (int index = 1; index < keys.Length; index++)
        {
            VideoChromaKeySpillKeyframe right = keys[index];
            if (frameOffset <= right.FrameOffset)
            {
                VideoChromaKeySpillKeyframe left = keys[index - 1];
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
        if (clip.ChromaKeySpillKeyframes.Count == 0)
        {
            clip.ChromaKeySpillSuppression = value;
            return true;
        }

        int frameOffset = GetFrameOffset(clip, seconds, frameRate);
        return IsInClip(clip, kind, frameOffset, frameRate) && SetAtFrame(clip, frameOffset, value);
    }

    public static bool TryRemoveAtFrame(TimelineClipProject clip, int frameOffset)
    {
        ArgumentNullException.ThrowIfNull(clip);
        int index = clip.ChromaKeySpillKeyframes.FindIndex(key => key.FrameOffset == frameOffset);
        if (index < 0)
        {
            return false;
        }
        if (clip.ChromaKeySpillKeyframes.Count == 1)
        {
            clip.ChromaKeySpillSuppression = GetValue(clip, frameOffset);
        }

        clip.ChromaKeySpillKeyframes.RemoveAt(index);
        return true;
    }

    public static bool IsKeyframeAt(TimelineClipProject clip, int frameOffset) =>
        clip.ChromaKeySpillKeyframes.Any(key => key.FrameOffset == frameOffset);

    public static void Split(TimelineClipProject left, TimelineClipProject right, int splitFrameOffset)
    {
        if (left.ChromaKeySpillKeyframes.Count == 0)
        {
            return;
        }

        double splitValue = GetValue(left, splitFrameOffset);
        double startValue = GetValue(left, 0);
        int leftEndFrame = Math.Max(0, splitFrameOffset - 1);
        double leftEndValue = GetValue(left, leftEndFrame);
        List<VideoChromaKeySpillKeyframe> original = left.ChromaKeySpillKeyframes;
        left.ChromaKeySpillKeyframes = original.Where(key => key.FrameOffset < splitFrameOffset)
            .Select(key => Clone(key)).ToList();
        if (!left.ChromaKeySpillKeyframes.Any(key => key.FrameOffset == 0))
        {
            left.ChromaKeySpillKeyframes.Add(ToKeyframe(0, startValue));
        }
        if (!left.ChromaKeySpillKeyframes.Any(key => key.FrameOffset == leftEndFrame))
        {
            left.ChromaKeySpillKeyframes.Add(ToKeyframe(leftEndFrame, leftEndValue));
        }
        left.ChromaKeySpillKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
        right.ChromaKeySpillKeyframes = original.Where(key => key.FrameOffset >= splitFrameOffset)
            .Select(key => Clone(key, key.FrameOffset - splitFrameOffset)).ToList();
        if (right.ChromaKeySpillKeyframes.Count == 0 || right.ChromaKeySpillKeyframes[0].FrameOffset != 0)
        {
            right.ChromaKeySpillKeyframes.Insert(0, ToKeyframe(0, splitValue));
        }
    }

    public static void TrimStart(TimelineClipProject clip, int removedFrames)
    {
        if (clip.ChromaKeySpillKeyframes.Count == 0 || removedFrames <= 0)
        {
            return;
        }

        double startValue = GetValue(clip, removedFrames);
        clip.ChromaKeySpillKeyframes = clip.ChromaKeySpillKeyframes
            .Where(key => key.FrameOffset > removedFrames)
            .Select(key => Clone(key, key.FrameOffset - removedFrames)).ToList();
        clip.ChromaKeySpillKeyframes.Insert(0, ToKeyframe(0, startValue));
    }

    public static void TrimEnd(TimelineClipProject clip, int retainedFrames)
    {
        if (clip.ChromaKeySpillKeyframes.Count == 0)
        {
            return;
        }
        if (retainedFrames <= 0)
        {
            clip.ChromaKeySpillKeyframes.Clear();
            return;
        }

        double startValue = GetValue(clip, 0);
        int endFrame = retainedFrames - 1;
        double endValue = GetValue(clip, endFrame);
        clip.ChromaKeySpillKeyframes = clip.ChromaKeySpillKeyframes
            .Where(key => key.FrameOffset < retainedFrames).Select(key => Clone(key)).ToList();
        if (clip.ChromaKeySpillKeyframes.Count == 0 || clip.ChromaKeySpillKeyframes[0].FrameOffset != 0)
        {
            clip.ChromaKeySpillKeyframes.Insert(0, ToKeyframe(0, startValue));
        }
        if (!clip.ChromaKeySpillKeyframes.Any(key => key.FrameOffset == endFrame))
        {
            clip.ChromaKeySpillKeyframes.Add(ToKeyframe(endFrame, endValue));
            clip.ChromaKeySpillKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
        }
    }

    public static void RescaleFrameRate(TimelineClipProject clip, double oldFrameRate, double newFrameRate)
    {
        foreach (VideoChromaKeySpillKeyframe key in clip.ChromaKeySpillKeyframes)
        {
            key.FrameOffset = (int)Math.Round(key.FrameOffset * newFrameRate / oldFrameRate);
        }
        clip.ChromaKeySpillKeyframes = clip.ChromaKeySpillKeyframes.OrderBy(key => key.FrameOffset)
            .GroupBy(key => key.FrameOffset).Select(group => group.Last()).ToList();
    }

    public static void ClearInvalid(IReadOnlyList<TimelineTrackProject> tracks, double frameRate)
    {
        foreach (TimelineTrackProject track in tracks)
        {
            foreach (TimelineClipProject clip in track.Clips)
            {
                clip.ChromaKeySpillKeyframes ??= [];
                if (!IsSupportedKind(track.Kind))
                {
                    clip.ChromaKeySpillKeyframes.Clear();
                    continue;
                }

                clip.ChromaKeySpillKeyframes.RemoveAll(key => key is null || key.FrameOffset < 0 ||
                    !IsInClip(clip, track.Kind, key.FrameOffset, frameRate) || !IsValidValue(key.Amount));
                clip.ChromaKeySpillKeyframes = clip.ChromaKeySpillKeyframes.OrderBy(key => key.FrameOffset)
                    .GroupBy(key => key.FrameOffset).Select(group => group.Last()).ToList();
            }
        }
    }

    public static List<VideoChromaKeySpillKeyframe> CloneKeys(IEnumerable<VideoChromaKeySpillKeyframe> keys) =>
        keys.Select(key => Clone(key)).ToList();

    public static string? Validate(TimelineClipProject clip, TimelineTrackKind kind, double frameRate)
    {
        if (clip.ChromaKeySpillKeyframes is null || clip.ChromaKeySpillKeyframes.Any(key => key is null))
        {
            return "Chroma-key spill keyframes cannot be missing.";
        }
        if (!IsSupportedKind(kind) && clip.ChromaKeySpillKeyframes.Count > 0)
        {
            return "Only video and image clips can have chroma-key spill keyframes.";
        }

        int previousFrame = -1;
        foreach (VideoChromaKeySpillKeyframe key in clip.ChromaKeySpillKeyframes)
        {
            if (key.FrameOffset <= previousFrame || !IsInClip(clip, kind, key.FrameOffset, frameRate) ||
                !IsValidValue(key.Amount))
            {
                return "Chroma-key spill keyframes must be ordered, unique, inside their clip, and within supported ranges.";
            }
            previousFrame = key.FrameOffset;
        }

        return null;
    }

    private static bool SetAtFrame(TimelineClipProject clip, int frameOffset, double value)
    {
        int index = clip.ChromaKeySpillKeyframes.FindIndex(key => key.FrameOffset == frameOffset);
        VideoChromaKeySpillKeyframe keyframe = ToKeyframe(frameOffset, value);
        if (index >= 0)
        {
            clip.ChromaKeySpillKeyframes[index] = keyframe;
        }
        else
        {
            clip.ChromaKeySpillKeyframes.Add(keyframe);
            clip.ChromaKeySpillKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
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
        double.IsFinite(value) && value is >= 0 and <= 1;

    private static VideoChromaKeySpillKeyframe ToKeyframe(int frameOffset, double value) =>
        new() { FrameOffset = frameOffset, Amount = value };

    private static VideoChromaKeySpillKeyframe Clone(VideoChromaKeySpillKeyframe key, int? frameOffset = null) =>
        ToKeyframe(frameOffset ?? key.FrameOffset, key.Amount);
}
