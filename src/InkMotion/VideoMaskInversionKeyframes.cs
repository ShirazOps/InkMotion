namespace InkMotion;

public sealed record VideoMaskInversionKeyframe
{
    public int FrameOffset { get; set; }
    public bool Inverted { get; set; }
}

public static class VideoMaskInversionKeyframes
{
    private const double Tolerance = 1e-6;

    public static bool GetValue(TimelineClipProject clip, double frameOffset)
    {
        ArgumentNullException.ThrowIfNull(clip);
        VideoMaskInversionKeyframe[] keys = clip.MaskInversionKeyframes
            .OrderBy(key => key.FrameOffset).ToArray();
        if (keys.Length == 0)
        {
            return clip.MaskInverted;
        }

        VideoMaskInversionKeyframe? current = keys.LastOrDefault(key => key.FrameOffset <= frameOffset);
        return current?.Inverted ?? keys[0].Inverted;
    }

    public static int GetFrameOffset(TimelineClipProject clip, double seconds, double frameRate) =>
        (int)Math.Round(seconds * frameRate) - (clip.StartFrame - 1);

    public static bool CanSetAtFrame(TimelineClipProject clip, TimelineTrackKind kind, int frameOffset, double frameRate) =>
        (kind is TimelineTrackKind.Video or TimelineTrackKind.Image) &&
        IsInClip(clip, kind, frameOffset, frameRate);

    public static bool SetValueAtTime(
        TimelineClipProject clip,
        TimelineTrackKind kind,
        double seconds,
        double frameRate,
        bool value)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image))
        {
            return false;
        }
        if (clip.MaskInversionKeyframes.Count == 0)
        {
            clip.MaskInverted = value;
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
        clip.MaskInversionKeyframes.Any(key => key.FrameOffset == frameOffset);

    public static bool TryRemoveAtFrame(TimelineClipProject clip, int frameOffset)
    {
        ArgumentNullException.ThrowIfNull(clip);
        int index = clip.MaskInversionKeyframes.FindIndex(key => key.FrameOffset == frameOffset);
        if (index < 0)
        {
            return false;
        }
        if (clip.MaskInversionKeyframes.Count == 1)
        {
            clip.MaskInverted = GetValue(clip, frameOffset);
        }

        clip.MaskInversionKeyframes.RemoveAt(index);
        return true;
    }

    public static void Split(TimelineClipProject left, TimelineClipProject right, int splitFrameOffset)
    {
        if (left.MaskInversionKeyframes.Count == 0)
        {
            return;
        }

        bool splitValue = GetValue(left, splitFrameOffset);
        bool startValue = GetValue(left, 0);
        int leftEndFrame = Math.Max(0, splitFrameOffset - 1);
        bool leftEndValue = GetValue(left, leftEndFrame);
        List<VideoMaskInversionKeyframe> original = left.MaskInversionKeyframes;
        left.MaskInversionKeyframes = original.Where(key => key.FrameOffset < splitFrameOffset)
            .Select(key => Clone(key)).ToList();
        if (!left.MaskInversionKeyframes.Any(key => key.FrameOffset == 0))
        {
            left.MaskInversionKeyframes.Add(ToKeyframe(0, startValue));
        }
        if (!left.MaskInversionKeyframes.Any(key => key.FrameOffset == leftEndFrame))
        {
            left.MaskInversionKeyframes.Add(ToKeyframe(leftEndFrame, leftEndValue));
        }
        left.MaskInversionKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
        right.MaskInversionKeyframes = original.Where(key => key.FrameOffset >= splitFrameOffset)
            .Select(key => Clone(key, key.FrameOffset - splitFrameOffset)).ToList();
        if (right.MaskInversionKeyframes.Count == 0 || right.MaskInversionKeyframes[0].FrameOffset != 0)
        {
            right.MaskInversionKeyframes.Insert(0, ToKeyframe(0, splitValue));
        }
    }

    public static void TrimStart(TimelineClipProject clip, int removedFrames)
    {
        if (clip.MaskInversionKeyframes.Count == 0 || removedFrames <= 0)
        {
            return;
        }

        bool startValue = GetValue(clip, removedFrames);
        clip.MaskInversionKeyframes = clip.MaskInversionKeyframes
            .Where(key => key.FrameOffset > removedFrames)
            .Select(key => Clone(key, key.FrameOffset - removedFrames)).ToList();
        clip.MaskInversionKeyframes.Insert(0, ToKeyframe(0, startValue));
    }

    public static void TrimEnd(TimelineClipProject clip, int retainedFrames)
    {
        if (clip.MaskInversionKeyframes.Count == 0)
        {
            return;
        }
        if (retainedFrames <= 0)
        {
            clip.MaskInversionKeyframes.Clear();
            return;
        }

        bool startValue = GetValue(clip, 0);
        int endFrame = retainedFrames - 1;
        bool endValue = GetValue(clip, endFrame);
        clip.MaskInversionKeyframes = clip.MaskInversionKeyframes
            .Where(key => key.FrameOffset < retainedFrames).Select(key => Clone(key)).ToList();
        if (clip.MaskInversionKeyframes.Count == 0 || clip.MaskInversionKeyframes[0].FrameOffset != 0)
        {
            clip.MaskInversionKeyframes.Insert(0, ToKeyframe(0, startValue));
        }
        if (!clip.MaskInversionKeyframes.Any(key => key.FrameOffset == endFrame))
        {
            clip.MaskInversionKeyframes.Add(ToKeyframe(endFrame, endValue));
            clip.MaskInversionKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
        }
    }

    public static void RescaleFrameRate(TimelineClipProject clip, double oldFrameRate, double newFrameRate)
    {
        foreach (VideoMaskInversionKeyframe key in clip.MaskInversionKeyframes)
        {
            key.FrameOffset = (int)Math.Round(key.FrameOffset * newFrameRate / oldFrameRate);
        }
        clip.MaskInversionKeyframes = clip.MaskInversionKeyframes.OrderBy(key => key.FrameOffset)
            .GroupBy(key => key.FrameOffset).Select(group => group.Last()).ToList();
    }

    public static void ClearInvalid(IReadOnlyList<TimelineTrackProject> tracks, double frameRate)
    {
        foreach (TimelineTrackProject track in tracks)
        {
            foreach (TimelineClipProject clip in track.Clips)
            {
                clip.MaskInversionKeyframes ??= [];
                if (track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image))
                {
                    clip.MaskInversionKeyframes.Clear();
                    continue;
                }

                clip.MaskInversionKeyframes.RemoveAll(key => key is null || key.FrameOffset < 0 ||
                    !IsInClip(clip, track.Kind, key.FrameOffset, frameRate));
                clip.MaskInversionKeyframes = clip.MaskInversionKeyframes
                    .OrderBy(key => key.FrameOffset).GroupBy(key => key.FrameOffset)
                    .Select(group => group.Last()).ToList();
            }
        }
    }

    public static List<VideoMaskInversionKeyframe> CloneKeys(IEnumerable<VideoMaskInversionKeyframe> keys) =>
        keys.Select(key => Clone(key)).ToList();

    public static string? Validate(TimelineClipProject clip, TimelineTrackKind kind, double frameRate)
    {
        if (clip.MaskInversionKeyframes is null || clip.MaskInversionKeyframes.Any(key => key is null))
        {
            return "Mask inversion keyframes cannot be missing.";
        }
        if (clip.MaskInversionKeyframes.Count > 0 &&
            kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image))
        {
            return "Mask inversion keyframes are supported only for video and image clips.";
        }

        int previousFrame = -1;
        foreach (VideoMaskInversionKeyframe key in clip.MaskInversionKeyframes)
        {
            if (key.FrameOffset <= previousFrame || !IsInClip(clip, kind, key.FrameOffset, frameRate))
            {
                return "Mask inversion keyframes must be ordered, unique, and inside their clip.";
            }
            previousFrame = key.FrameOffset;
        }

        return null;
    }

    private static bool SetAtFrame(TimelineClipProject clip, int frameOffset, bool inverted)
    {
        int index = clip.MaskInversionKeyframes.FindIndex(key => key.FrameOffset == frameOffset);
        VideoMaskInversionKeyframe keyframe = ToKeyframe(frameOffset, inverted);
        if (index >= 0)
        {
            clip.MaskInversionKeyframes[index] = keyframe;
        }
        else
        {
            clip.MaskInversionKeyframes.Add(keyframe);
            clip.MaskInversionKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
        }
        return true;
    }

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

    private static VideoMaskInversionKeyframe ToKeyframe(int frameOffset, bool inverted) =>
        new() { FrameOffset = frameOffset, Inverted = inverted };

    private static VideoMaskInversionKeyframe Clone(VideoMaskInversionKeyframe key, int? frameOffset = null) =>
        ToKeyframe(frameOffset ?? key.FrameOffset, key.Inverted);
}
