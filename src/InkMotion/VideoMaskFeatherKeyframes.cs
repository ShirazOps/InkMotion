namespace InkMotion;

public sealed record VideoMaskFeatherKeyframe
{
    public int FrameOffset { get; set; }
    public double Feather { get; set; }
}

public static class VideoMaskFeatherKeyframes
{
    private const double Tolerance = 1e-6;

    public static double GetValue(TimelineClipProject clip, double frameOffset)
    {
        VideoMaskFeatherKeyframe[] keys = clip.MaskFeatherKeyframes.OrderBy(key => key.FrameOffset).ToArray();
        if (keys.Length == 0)
        {
            return clip.MaskFeather;
        }
        if (frameOffset <= keys[0].FrameOffset)
        {
            return keys[0].Feather;
        }

        for (int index = 1; index < keys.Length; index++)
        {
            VideoMaskFeatherKeyframe right = keys[index];
            if (frameOffset <= right.FrameOffset)
            {
                VideoMaskFeatherKeyframe left = keys[index - 1];
                double amount = (frameOffset - left.FrameOffset) / (right.FrameOffset - left.FrameOffset);
                return left.Feather + ((right.Feather - left.Feather) * amount);
            }
        }

        return keys[^1].Feather;
    }

    public static int GetFrameOffset(TimelineClipProject clip, double seconds, double frameRate) =>
        (int)Math.Round(seconds * frameRate) - (clip.StartFrame - 1);

    public static bool SetValueAtFrame(
        TimelineClipProject clip,
        TimelineTrackKind kind,
        int frameOffset,
        double frameRate,
        double value)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!IsValidValue(value) || kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image))
        {
            return false;
        }
        if (clip.MaskFeatherKeyframes.Count == 0)
        {
            clip.MaskFeather = value;
            return true;
        }
        if (!CanSetAtFrame(clip, kind, frameOffset, frameRate))
        {
            return false;
        }

        SetAtFrame(clip, frameOffset, value);
        return true;
    }

    public static bool SetKeyframe(TimelineClipProject clip, TimelineTrackKind kind, int frameOffset, double frameRate)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!CanSetAtFrame(clip, kind, frameOffset, frameRate))
        {
            return false;
        }

        SetAtFrame(clip, frameOffset, GetValue(clip, frameOffset));
        return true;
    }

    public static bool CanSetAtFrame(TimelineClipProject clip, TimelineTrackKind kind, int frameOffset, double frameRate) =>
        kind is TimelineTrackKind.Video or TimelineTrackKind.Image &&
        VideoMaskShapeKeyframes.GetValue(clip, frameOffset) != VideoMaskShape.None &&
        double.IsFinite(frameRate) && frameRate > 0 &&
        IsInClip(clip, kind, frameOffset, frameRate);

    public static bool IsKeyframeAt(TimelineClipProject clip, int frameOffset) =>
        clip.MaskFeatherKeyframes.Any(key => key.FrameOffset == frameOffset);

    public static bool TryRemoveAtFrame(TimelineClipProject clip, int frameOffset)
    {
        ArgumentNullException.ThrowIfNull(clip);
        int index = clip.MaskFeatherKeyframes.FindIndex(key => key.FrameOffset == frameOffset);
        if (index < 0)
        {
            return false;
        }
        if (clip.MaskFeatherKeyframes.Count == 1)
        {
            clip.MaskFeather = GetValue(clip, frameOffset);
        }

        clip.MaskFeatherKeyframes.RemoveAt(index);
        return true;
    }

    public static void Split(TimelineClipProject left, TimelineClipProject right, int splitFrameOffset)
    {
        if (left.MaskFeatherKeyframes.Count == 0)
        {
            return;
        }

        double splitValue = GetValue(left, splitFrameOffset);
        double startValue = GetValue(left, 0);
        int leftEndFrame = Math.Max(0, splitFrameOffset - 1);
        double leftEndValue = GetValue(left, leftEndFrame);
        List<VideoMaskFeatherKeyframe> original = left.MaskFeatherKeyframes;
        left.MaskFeatherKeyframes = original.Where(key => key.FrameOffset < splitFrameOffset)
            .Select(key => Clone(key)).ToList();
        if (!left.MaskFeatherKeyframes.Any(key => key.FrameOffset == 0))
        {
            left.MaskFeatherKeyframes.Add(ToKeyframe(0, startValue));
        }
        if (!left.MaskFeatherKeyframes.Any(key => key.FrameOffset == leftEndFrame))
        {
            left.MaskFeatherKeyframes.Add(ToKeyframe(leftEndFrame, leftEndValue));
        }
        left.MaskFeatherKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
        right.MaskFeatherKeyframes = original.Where(key => key.FrameOffset >= splitFrameOffset)
            .Select(key => Clone(key, key.FrameOffset - splitFrameOffset)).ToList();
        if (right.MaskFeatherKeyframes.Count == 0 || right.MaskFeatherKeyframes[0].FrameOffset != 0)
        {
            right.MaskFeatherKeyframes.Insert(0, ToKeyframe(0, splitValue));
        }
    }

    public static void TrimStart(TimelineClipProject clip, int removedFrames)
    {
        if (clip.MaskFeatherKeyframes.Count == 0 || removedFrames <= 0)
        {
            return;
        }

        double startValue = GetValue(clip, removedFrames);
        clip.MaskFeatherKeyframes = clip.MaskFeatherKeyframes
            .Where(key => key.FrameOffset > removedFrames)
            .Select(key => Clone(key, key.FrameOffset - removedFrames)).ToList();
        clip.MaskFeatherKeyframes.Insert(0, ToKeyframe(0, startValue));
    }

    public static void TrimEnd(TimelineClipProject clip, int retainedFrames)
    {
        if (clip.MaskFeatherKeyframes.Count == 0)
        {
            return;
        }
        if (retainedFrames <= 0)
        {
            clip.MaskFeatherKeyframes.Clear();
            return;
        }

        double startValue = GetValue(clip, 0);
        int endFrame = retainedFrames - 1;
        double endValue = GetValue(clip, endFrame);
        clip.MaskFeatherKeyframes = clip.MaskFeatherKeyframes
            .Where(key => key.FrameOffset < retainedFrames).Select(key => Clone(key)).ToList();
        if (clip.MaskFeatherKeyframes.Count == 0 || clip.MaskFeatherKeyframes[0].FrameOffset != 0)
        {
            clip.MaskFeatherKeyframes.Insert(0, ToKeyframe(0, startValue));
        }
        if (!clip.MaskFeatherKeyframes.Any(key => key.FrameOffset == endFrame))
        {
            clip.MaskFeatherKeyframes.Add(ToKeyframe(endFrame, endValue));
            clip.MaskFeatherKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
        }
    }

    public static void RescaleFrameRate(TimelineClipProject clip, double oldFrameRate, double newFrameRate)
    {
        foreach (VideoMaskFeatherKeyframe key in clip.MaskFeatherKeyframes)
        {
            key.FrameOffset = (int)Math.Round(key.FrameOffset * newFrameRate / oldFrameRate);
        }
        clip.MaskFeatherKeyframes = clip.MaskFeatherKeyframes.OrderBy(key => key.FrameOffset)
            .GroupBy(key => key.FrameOffset).Select(group => group.Last()).ToList();
    }

    public static void ClearInvalid(IReadOnlyList<TimelineTrackProject> tracks, double frameRate)
    {
        foreach (TimelineTrackProject track in tracks)
        {
            foreach (TimelineClipProject clip in track.Clips)
            {
                clip.MaskFeatherKeyframes ??= [];
                if (track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image))
                {
                    clip.MaskFeatherKeyframes.Clear();
                    continue;
                }

                clip.MaskFeatherKeyframes.RemoveAll(key => key is null || key.FrameOffset < 0 ||
                    !IsInClip(clip, track.Kind, key.FrameOffset, frameRate) || !IsValidValue(key.Feather));
                clip.MaskFeatherKeyframes = clip.MaskFeatherKeyframes.OrderBy(key => key.FrameOffset)
                    .GroupBy(key => key.FrameOffset).Select(group => group.Last()).ToList();
            }
        }
    }

    public static List<VideoMaskFeatherKeyframe> CloneKeys(IEnumerable<VideoMaskFeatherKeyframe> keys) =>
        keys.Select(key => Clone(key)).ToList();

    public static string? Validate(TimelineClipProject clip, TimelineTrackKind kind, double frameRate)
    {
        if (clip.MaskFeatherKeyframes is null || clip.MaskFeatherKeyframes.Any(key => key is null))
        {
            return "Mask feather keyframes cannot be missing.";
        }
        if (kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image) &&
            clip.MaskFeatherKeyframes.Count > 0)
        {
            return "Only video and image clips can have mask feather keyframes.";
        }

        int previousFrame = -1;
        foreach (VideoMaskFeatherKeyframe key in clip.MaskFeatherKeyframes)
        {
            if (key.FrameOffset <= previousFrame || !IsInClip(clip, kind, key.FrameOffset, frameRate) ||
                !IsValidValue(key.Feather))
            {
                return "Mask feather keyframes must be ordered, unique, inside their clip, and within supported ranges.";
            }
            previousFrame = key.FrameOffset;
        }

        return null;
    }

    private static void SetAtFrame(TimelineClipProject clip, int frameOffset, double value)
    {
        int index = clip.MaskFeatherKeyframes.FindIndex(key => key.FrameOffset == frameOffset);
        VideoMaskFeatherKeyframe keyframe = ToKeyframe(frameOffset, value);
        if (index >= 0)
        {
            clip.MaskFeatherKeyframes[index] = keyframe;
        }
        else
        {
            clip.MaskFeatherKeyframes.Add(keyframe);
            clip.MaskFeatherKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
        }
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

    private static bool IsValidValue(double value) =>
        double.IsFinite(value) && value is >= 0 and <= 0.25;

    private static VideoMaskFeatherKeyframe ToKeyframe(int frameOffset, double value) =>
        new() { FrameOffset = frameOffset, Feather = value };

    private static VideoMaskFeatherKeyframe Clone(VideoMaskFeatherKeyframe key, int? frameOffset = null) =>
        ToKeyframe(frameOffset ?? key.FrameOffset, key.Feather);
}
