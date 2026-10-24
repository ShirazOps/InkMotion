namespace InkMotion;

public sealed class VideoChromaKeyKeyframe
{
    public int FrameOffset { get; set; }
    public double Tolerance { get; set; }
}

public static class VideoChromaKeyKeyframes
{
    private const double Tolerance = 1e-6;

    public static double GetValue(TimelineClipProject clip, double frameOffset)
    {
        ArgumentNullException.ThrowIfNull(clip);
        VideoChromaKeyKeyframe[] keys = clip.ChromaKeyKeyframes.OrderBy(key => key.FrameOffset).ToArray();
        if (keys.Length == 0)
        {
            return clip.ChromaKeyTolerance;
        }
        if (frameOffset <= keys[0].FrameOffset)
        {
            return keys[0].Tolerance;
        }

        for (int index = 1; index < keys.Length; index++)
        {
            VideoChromaKeyKeyframe right = keys[index];
            if (frameOffset <= right.FrameOffset)
            {
                VideoChromaKeyKeyframe left = keys[index - 1];
                double amount = (frameOffset - left.FrameOffset) / (right.FrameOffset - left.FrameOffset);
                return left.Tolerance + ((right.Tolerance - left.Tolerance) * amount);
            }
        }

        return keys[^1].Tolerance;
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
        if (clip.ChromaKeyKeyframes.Count == 0)
        {
            clip.ChromaKeyTolerance = value;
            return true;
        }

        int frameOffset = GetFrameOffset(clip, seconds, frameRate);
        return IsInClip(clip, kind, frameOffset, frameRate) && SetAtFrame(clip, frameOffset, value);
    }

    public static bool TryRemoveAtFrame(TimelineClipProject clip, int frameOffset)
    {
        ArgumentNullException.ThrowIfNull(clip);
        int index = clip.ChromaKeyKeyframes.FindIndex(key => key.FrameOffset == frameOffset);
        if (index < 0)
        {
            return false;
        }
        if (clip.ChromaKeyKeyframes.Count == 1)
        {
            clip.ChromaKeyTolerance = GetValue(clip, frameOffset);
        }

        clip.ChromaKeyKeyframes.RemoveAt(index);
        return true;
    }

    public static bool IsKeyframeAt(TimelineClipProject clip, int frameOffset) =>
        clip.ChromaKeyKeyframes.Any(key => key.FrameOffset == frameOffset);

    public static void Split(TimelineClipProject left, TimelineClipProject right, int splitFrameOffset)
    {
        if (left.ChromaKeyKeyframes.Count == 0)
        {
            return;
        }

        double splitValue = GetValue(left, splitFrameOffset);
        double startValue = GetValue(left, 0);
        int leftEndFrame = Math.Max(0, splitFrameOffset - 1);
        double leftEndValue = GetValue(left, leftEndFrame);
        List<VideoChromaKeyKeyframe> original = left.ChromaKeyKeyframes;
        left.ChromaKeyKeyframes = original.Where(key => key.FrameOffset < splitFrameOffset)
            .Select(key => Clone(key)).ToList();
        if (!left.ChromaKeyKeyframes.Any(key => key.FrameOffset == 0))
        {
            left.ChromaKeyKeyframes.Add(ToKeyframe(0, startValue));
        }
        if (!left.ChromaKeyKeyframes.Any(key => key.FrameOffset == leftEndFrame))
        {
            left.ChromaKeyKeyframes.Add(ToKeyframe(leftEndFrame, leftEndValue));
        }
        left.ChromaKeyKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
        right.ChromaKeyKeyframes = original.Where(key => key.FrameOffset >= splitFrameOffset)
            .Select(key => Clone(key, key.FrameOffset - splitFrameOffset)).ToList();
        if (right.ChromaKeyKeyframes.Count == 0 || right.ChromaKeyKeyframes[0].FrameOffset != 0)
        {
            right.ChromaKeyKeyframes.Insert(0, ToKeyframe(0, splitValue));
        }
    }

    public static void TrimStart(TimelineClipProject clip, int removedFrames)
    {
        if (clip.ChromaKeyKeyframes.Count == 0 || removedFrames <= 0)
        {
            return;
        }

        double startValue = GetValue(clip, removedFrames);
        clip.ChromaKeyKeyframes = clip.ChromaKeyKeyframes
            .Where(key => key.FrameOffset > removedFrames)
            .Select(key => Clone(key, key.FrameOffset - removedFrames)).ToList();
        clip.ChromaKeyKeyframes.Insert(0, ToKeyframe(0, startValue));
    }

    public static void TrimEnd(TimelineClipProject clip, int retainedFrames)
    {
        if (clip.ChromaKeyKeyframes.Count == 0)
        {
            return;
        }
        if (retainedFrames <= 0)
        {
            clip.ChromaKeyKeyframes.Clear();
            return;
        }

        double startValue = GetValue(clip, 0);
        int endFrame = retainedFrames - 1;
        double endValue = GetValue(clip, endFrame);
        clip.ChromaKeyKeyframes = clip.ChromaKeyKeyframes
            .Where(key => key.FrameOffset < retainedFrames).Select(key => Clone(key)).ToList();
        if (clip.ChromaKeyKeyframes.Count == 0 || clip.ChromaKeyKeyframes[0].FrameOffset != 0)
        {
            clip.ChromaKeyKeyframes.Insert(0, ToKeyframe(0, startValue));
        }
        if (!clip.ChromaKeyKeyframes.Any(key => key.FrameOffset == endFrame))
        {
            clip.ChromaKeyKeyframes.Add(ToKeyframe(endFrame, endValue));
            clip.ChromaKeyKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
        }
    }

    public static void RescaleFrameRate(TimelineClipProject clip, double oldFrameRate, double newFrameRate)
    {
        foreach (VideoChromaKeyKeyframe key in clip.ChromaKeyKeyframes)
        {
            key.FrameOffset = (int)Math.Round(key.FrameOffset * newFrameRate / oldFrameRate);
        }
        clip.ChromaKeyKeyframes = clip.ChromaKeyKeyframes.OrderBy(key => key.FrameOffset)
            .GroupBy(key => key.FrameOffset).Select(group => group.Last()).ToList();
    }

    public static void ClearInvalid(IReadOnlyList<TimelineTrackProject> tracks, double frameRate)
    {
        foreach (TimelineTrackProject track in tracks)
        {
            foreach (TimelineClipProject clip in track.Clips)
            {
                clip.ChromaKeyKeyframes ??= [];
                if (!IsSupportedKind(track.Kind))
                {
                    clip.ChromaKeyKeyframes.Clear();
                    continue;
                }
                clip.ChromaKeyKeyframes.RemoveAll(key => key is null || key.FrameOffset < 0 ||
                    !IsInClip(clip, track.Kind, key.FrameOffset, frameRate) || !IsValidValue(key.Tolerance));
                clip.ChromaKeyKeyframes = clip.ChromaKeyKeyframes.OrderBy(key => key.FrameOffset)
                    .GroupBy(key => key.FrameOffset).Select(group => group.Last()).ToList();
            }
        }
    }

    public static List<VideoChromaKeyKeyframe> CloneKeys(IEnumerable<VideoChromaKeyKeyframe> keys) =>
        keys.Select(key => Clone(key)).ToList();

    public static string? Validate(TimelineClipProject clip, TimelineTrackKind kind, double frameRate)
    {
        if (clip.ChromaKeyKeyframes is null || clip.ChromaKeyKeyframes.Any(key => key is null))
        {
            return "Chroma-key keyframes cannot be missing.";
        }
        if (!IsSupportedKind(kind) && clip.ChromaKeyKeyframes.Count > 0)
        {
            return "Only video and image clips can have chroma-key keyframes.";
        }

        int previousFrame = -1;
        foreach (VideoChromaKeyKeyframe key in clip.ChromaKeyKeyframes)
        {
            if (key.FrameOffset <= previousFrame || !IsInClip(clip, kind, key.FrameOffset, frameRate) ||
                !IsValidValue(key.Tolerance))
            {
                return "Chroma-key keyframes must be ordered, unique, inside their clip, and within supported ranges.";
            }
            previousFrame = key.FrameOffset;
        }

        return null;
    }

    private static bool SetAtFrame(TimelineClipProject clip, int frameOffset, double value)
    {
        int index = clip.ChromaKeyKeyframes.FindIndex(key => key.FrameOffset == frameOffset);
        VideoChromaKeyKeyframe keyframe = ToKeyframe(frameOffset, value);
        if (index >= 0)
        {
            clip.ChromaKeyKeyframes[index] = keyframe;
        }
        else
        {
            clip.ChromaKeyKeyframes.Add(keyframe);
            clip.ChromaKeyKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
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

    private static VideoChromaKeyKeyframe ToKeyframe(int frameOffset, double value) =>
        new() { FrameOffset = frameOffset, Tolerance = value };

    private static VideoChromaKeyKeyframe Clone(VideoChromaKeyKeyframe key, int? frameOffset = null) =>
        ToKeyframe(frameOffset ?? key.FrameOffset, key.Tolerance);
}
