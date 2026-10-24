namespace InkMotion;

public sealed record VideoChromaKeyEnabledKeyframe
{
    public int FrameOffset { get; set; }
    public bool Enabled { get; set; }
}

public static class VideoChromaKeyEnabledKeyframes
{
    private const double Tolerance = 1e-6;

    public static bool GetValue(TimelineClipProject clip, double frameOffset)
    {
        ArgumentNullException.ThrowIfNull(clip);
        VideoChromaKeyEnabledKeyframe[] keys = clip.ChromaKeyEnabledKeyframes
            .OrderBy(key => key.FrameOffset).ToArray();
        if (keys.Length == 0)
        {
            return clip.ChromaKeyEnabled;
        }

        VideoChromaKeyEnabledKeyframe? current = keys.LastOrDefault(key => key.FrameOffset <= frameOffset);
        return current?.Enabled ?? keys[0].Enabled;
    }

    public static bool GetValueAtTime(TimelineClipProject clip, double seconds, double frameRate) =>
        GetValue(clip, GetFrameOffset(clip, seconds, frameRate));

    public static int GetFrameOffset(TimelineClipProject clip, double seconds, double frameRate) =>
        (int)Math.Round(seconds * frameRate) - (clip.StartFrame - 1);

    public static bool CanSetAtFrame(TimelineClipProject clip, TimelineTrackKind kind, int frameOffset, double frameRate) =>
        IsSupportedKind(kind) && IsInClip(clip, kind, frameOffset, frameRate);

    public static bool SetValueAtTime(
        TimelineClipProject clip,
        TimelineTrackKind kind,
        double seconds,
        double frameRate,
        bool value)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!IsSupportedKind(kind))
        {
            return false;
        }
        if (clip.ChromaKeyEnabledKeyframes.Count == 0)
        {
            clip.ChromaKeyEnabled = value;
            return true;
        }

        int frameOffset = GetFrameOffset(clip, seconds, frameRate);
        return CanSetAtFrame(clip, kind, frameOffset, frameRate) && SetAtFrame(clip, frameOffset, value);
    }

    public static bool TrySetAtFrame(TimelineClipProject clip, TimelineTrackKind kind, int frameOffset, double frameRate)
    {
        if (!CanSetAtFrame(clip, kind, frameOffset, frameRate))
        {
            return false;
        }

        return SetAtFrame(clip, frameOffset, GetValue(clip, frameOffset));
    }

    public static bool IsKeyframeAt(TimelineClipProject clip, int frameOffset) =>
        clip.ChromaKeyEnabledKeyframes.Any(key => key.FrameOffset == frameOffset);

    public static bool TryRemoveAtFrame(TimelineClipProject clip, int frameOffset)
    {
        ArgumentNullException.ThrowIfNull(clip);
        int index = clip.ChromaKeyEnabledKeyframes.FindIndex(key => key.FrameOffset == frameOffset);
        if (index < 0)
        {
            return false;
        }
        if (clip.ChromaKeyEnabledKeyframes.Count == 1)
        {
            clip.ChromaKeyEnabled = GetValue(clip, frameOffset);
        }

        clip.ChromaKeyEnabledKeyframes.RemoveAt(index);
        return true;
    }

    public static void Split(TimelineClipProject left, TimelineClipProject right, int splitFrameOffset)
    {
        if (left.ChromaKeyEnabledKeyframes.Count == 0)
        {
            return;
        }

        bool splitValue = GetValue(left, splitFrameOffset);
        bool startValue = GetValue(left, 0);
        int leftEndFrame = Math.Max(0, splitFrameOffset - 1);
        bool leftEndValue = GetValue(left, leftEndFrame);
        List<VideoChromaKeyEnabledKeyframe> original = left.ChromaKeyEnabledKeyframes;
        left.ChromaKeyEnabledKeyframes = original.Where(key => key.FrameOffset < splitFrameOffset)
            .Select(key => Clone(key)).ToList();
        if (!left.ChromaKeyEnabledKeyframes.Any(key => key.FrameOffset == 0))
        {
            left.ChromaKeyEnabledKeyframes.Add(ToKeyframe(0, startValue));
        }
        if (!left.ChromaKeyEnabledKeyframes.Any(key => key.FrameOffset == leftEndFrame))
        {
            left.ChromaKeyEnabledKeyframes.Add(ToKeyframe(leftEndFrame, leftEndValue));
        }
        left.ChromaKeyEnabledKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
        right.ChromaKeyEnabledKeyframes = original.Where(key => key.FrameOffset >= splitFrameOffset)
            .Select(key => Clone(key, key.FrameOffset - splitFrameOffset)).ToList();
        if (right.ChromaKeyEnabledKeyframes.Count == 0 || right.ChromaKeyEnabledKeyframes[0].FrameOffset != 0)
        {
            right.ChromaKeyEnabledKeyframes.Insert(0, ToKeyframe(0, splitValue));
        }
    }

    public static void TrimStart(TimelineClipProject clip, int removedFrames)
    {
        if (clip.ChromaKeyEnabledKeyframes.Count == 0 || removedFrames <= 0)
        {
            return;
        }

        bool startValue = GetValue(clip, removedFrames);
        clip.ChromaKeyEnabledKeyframes = clip.ChromaKeyEnabledKeyframes
            .Where(key => key.FrameOffset > removedFrames)
            .Select(key => Clone(key, key.FrameOffset - removedFrames)).ToList();
        clip.ChromaKeyEnabledKeyframes.Insert(0, ToKeyframe(0, startValue));
    }

    public static void TrimEnd(TimelineClipProject clip, int retainedFrames)
    {
        if (clip.ChromaKeyEnabledKeyframes.Count == 0)
        {
            return;
        }
        if (retainedFrames <= 0)
        {
            clip.ChromaKeyEnabledKeyframes.Clear();
            return;
        }

        bool startValue = GetValue(clip, 0);
        int endFrame = retainedFrames - 1;
        bool endValue = GetValue(clip, endFrame);
        clip.ChromaKeyEnabledKeyframes = clip.ChromaKeyEnabledKeyframes
            .Where(key => key.FrameOffset < retainedFrames).Select(key => Clone(key)).ToList();
        if (clip.ChromaKeyEnabledKeyframes.Count == 0 || clip.ChromaKeyEnabledKeyframes[0].FrameOffset != 0)
        {
            clip.ChromaKeyEnabledKeyframes.Insert(0, ToKeyframe(0, startValue));
        }
        if (!clip.ChromaKeyEnabledKeyframes.Any(key => key.FrameOffset == endFrame))
        {
            clip.ChromaKeyEnabledKeyframes.Add(ToKeyframe(endFrame, endValue));
            clip.ChromaKeyEnabledKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
        }
    }

    public static void RescaleFrameRate(TimelineClipProject clip, double oldFrameRate, double newFrameRate)
    {
        foreach (VideoChromaKeyEnabledKeyframe key in clip.ChromaKeyEnabledKeyframes)
        {
            key.FrameOffset = (int)Math.Round(key.FrameOffset * newFrameRate / oldFrameRate);
        }
        clip.ChromaKeyEnabledKeyframes = clip.ChromaKeyEnabledKeyframes.OrderBy(key => key.FrameOffset)
            .GroupBy(key => key.FrameOffset).Select(group => group.Last()).ToList();
    }

    public static void ClearInvalid(IReadOnlyList<TimelineTrackProject> tracks, double frameRate)
    {
        foreach (TimelineTrackProject track in tracks)
        {
            foreach (TimelineClipProject clip in track.Clips)
            {
                clip.ChromaKeyEnabledKeyframes ??= [];
                if (!IsSupportedKind(track.Kind))
                {
                    clip.ChromaKeyEnabledKeyframes.Clear();
                    continue;
                }

                clip.ChromaKeyEnabledKeyframes.RemoveAll(key => key is null || key.FrameOffset < 0 ||
                    !IsInClip(clip, track.Kind, key.FrameOffset, frameRate));
                clip.ChromaKeyEnabledKeyframes = clip.ChromaKeyEnabledKeyframes
                    .OrderBy(key => key.FrameOffset).GroupBy(key => key.FrameOffset)
                    .Select(group => group.Last()).ToList();
            }
        }
    }

    public static List<VideoChromaKeyEnabledKeyframe> CloneKeys(
        IEnumerable<VideoChromaKeyEnabledKeyframe> keys) =>
        keys.Select(key => Clone(key)).ToList();

    public static string? Validate(TimelineClipProject clip, TimelineTrackKind kind, double frameRate)
    {
        if (clip.ChromaKeyEnabledKeyframes is null || clip.ChromaKeyEnabledKeyframes.Any(key => key is null))
        {
            return "Chroma-key enable keyframes cannot be missing.";
        }
        if (!IsSupportedKind(kind) && clip.ChromaKeyEnabledKeyframes.Count > 0)
        {
            return "Only video and image clips can have chroma-key enable keyframes.";
        }

        int previousFrame = -1;
        foreach (VideoChromaKeyEnabledKeyframe key in clip.ChromaKeyEnabledKeyframes)
        {
            if (key.FrameOffset <= previousFrame || !IsInClip(clip, kind, key.FrameOffset, frameRate))
            {
                return "Chroma-key enable keyframes must be ordered, unique, and inside their clip.";
            }
            previousFrame = key.FrameOffset;
        }

        return null;
    }

    private static bool SetAtFrame(TimelineClipProject clip, int frameOffset, bool value)
    {
        int index = clip.ChromaKeyEnabledKeyframes.FindIndex(key => key.FrameOffset == frameOffset);
        VideoChromaKeyEnabledKeyframe keyframe = ToKeyframe(frameOffset, value);
        if (index >= 0)
        {
            clip.ChromaKeyEnabledKeyframes[index] = keyframe;
        }
        else
        {
            clip.ChromaKeyEnabledKeyframes.Add(keyframe);
            clip.ChromaKeyEnabledKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
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

    private static VideoChromaKeyEnabledKeyframe ToKeyframe(int frameOffset, bool enabled) =>
        new() { FrameOffset = frameOffset, Enabled = enabled };

    private static VideoChromaKeyEnabledKeyframe Clone(
        VideoChromaKeyEnabledKeyframe key, int? frameOffset = null) =>
        ToKeyframe(frameOffset ?? key.FrameOffset, key.Enabled);
}
