namespace InkMotion;

public sealed record VideoMaskShapeKeyframe
{
    public int FrameOffset { get; set; }
    public VideoMaskShape Shape { get; set; }
}

public static class VideoMaskShapeKeyframes
{
    private const double Tolerance = 1e-6;

    public static VideoMaskShape GetValue(TimelineClipProject clip, double frameOffset)
    {
        ArgumentNullException.ThrowIfNull(clip);
        VideoMaskShapeKeyframe[] keys = clip.MaskShapeKeyframes
            .OrderBy(key => key.FrameOffset).ToArray();
        if (keys.Length == 0)
        {
            return clip.MaskShape;
        }

        VideoMaskShapeKeyframe? current = keys.LastOrDefault(key => key.FrameOffset <= frameOffset);
        return current?.Shape ?? keys[0].Shape;
    }

    public static int GetFrameOffset(TimelineClipProject clip, double seconds, double frameRate) =>
        (int)Math.Round(seconds * frameRate) - (clip.StartFrame - 1);

    public static bool CanSetAtFrame(TimelineClipProject clip, TimelineTrackKind kind, int frameOffset, double frameRate) =>
        (kind is TimelineTrackKind.Video or TimelineTrackKind.Image) &&
        IsInClip(clip, kind, frameOffset, frameRate);

    public static bool TrySetAtFrame(
        TimelineClipProject clip, TimelineTrackKind kind, int frameOffset, double frameRate)
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
        VideoMaskShape shape)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!Enum.IsDefined(shape) || kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image))
        {
            return false;
        }
        if (clip.MaskShapeKeyframes.Count == 0)
        {
            clip.MaskShape = shape;
            return true;
        }

        int frameOffset = GetFrameOffset(clip, seconds, frameRate);
        return CanSetAtFrame(clip, kind, frameOffset, frameRate) && SetAtFrame(clip, frameOffset, shape);
    }

    public static bool IsKeyframeAt(TimelineClipProject clip, int frameOffset) =>
        clip.MaskShapeKeyframes.Any(key => key.FrameOffset == frameOffset);

    public static bool TryRemoveAtFrame(TimelineClipProject clip, int frameOffset)
    {
        ArgumentNullException.ThrowIfNull(clip);
        int index = clip.MaskShapeKeyframes.FindIndex(key => key.FrameOffset == frameOffset);
        if (index < 0)
        {
            return false;
        }
        if (clip.MaskShapeKeyframes.Count == 1)
        {
            clip.MaskShape = GetValue(clip, frameOffset);
        }

        clip.MaskShapeKeyframes.RemoveAt(index);
        return true;
    }

    public static void Split(TimelineClipProject left, TimelineClipProject right, int splitFrameOffset)
    {
        if (left.MaskShapeKeyframes.Count == 0)
        {
            return;
        }

        VideoMaskShape splitValue = GetValue(left, splitFrameOffset);
        VideoMaskShape startValue = GetValue(left, 0);
        int leftEndFrame = Math.Max(0, splitFrameOffset - 1);
        VideoMaskShape leftEndValue = GetValue(left, leftEndFrame);
        List<VideoMaskShapeKeyframe> original = left.MaskShapeKeyframes;
        left.MaskShapeKeyframes = original.Where(key => key.FrameOffset < splitFrameOffset)
            .Select(key => Clone(key)).ToList();
        if (!left.MaskShapeKeyframes.Any(key => key.FrameOffset == 0))
        {
            left.MaskShapeKeyframes.Add(ToKeyframe(0, startValue));
        }
        if (!left.MaskShapeKeyframes.Any(key => key.FrameOffset == leftEndFrame))
        {
            left.MaskShapeKeyframes.Add(ToKeyframe(leftEndFrame, leftEndValue));
        }
        left.MaskShapeKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
        right.MaskShapeKeyframes = original.Where(key => key.FrameOffset >= splitFrameOffset)
            .Select(key => Clone(key, key.FrameOffset - splitFrameOffset)).ToList();
        if (right.MaskShapeKeyframes.Count == 0 || right.MaskShapeKeyframes[0].FrameOffset != 0)
        {
            right.MaskShapeKeyframes.Insert(0, ToKeyframe(0, splitValue));
        }
    }

    public static void TrimStart(TimelineClipProject clip, int removedFrames)
    {
        if (clip.MaskShapeKeyframes.Count == 0 || removedFrames <= 0)
        {
            return;
        }

        VideoMaskShape startValue = GetValue(clip, removedFrames);
        clip.MaskShapeKeyframes = clip.MaskShapeKeyframes
            .Where(key => key.FrameOffset > removedFrames)
            .Select(key => Clone(key, key.FrameOffset - removedFrames)).ToList();
        clip.MaskShapeKeyframes.Insert(0, ToKeyframe(0, startValue));
    }

    public static void TrimEnd(TimelineClipProject clip, int retainedFrames)
    {
        if (clip.MaskShapeKeyframes.Count == 0)
        {
            return;
        }
        if (retainedFrames <= 0)
        {
            clip.MaskShapeKeyframes.Clear();
            return;
        }

        VideoMaskShape startValue = GetValue(clip, 0);
        int endFrame = retainedFrames - 1;
        VideoMaskShape endValue = GetValue(clip, endFrame);
        clip.MaskShapeKeyframes = clip.MaskShapeKeyframes
            .Where(key => key.FrameOffset < retainedFrames).Select(key => Clone(key)).ToList();
        if (clip.MaskShapeKeyframes.Count == 0 || clip.MaskShapeKeyframes[0].FrameOffset != 0)
        {
            clip.MaskShapeKeyframes.Insert(0, ToKeyframe(0, startValue));
        }
        if (!clip.MaskShapeKeyframes.Any(key => key.FrameOffset == endFrame))
        {
            clip.MaskShapeKeyframes.Add(ToKeyframe(endFrame, endValue));
            clip.MaskShapeKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
        }
    }

    public static void RescaleFrameRate(TimelineClipProject clip, double oldFrameRate, double newFrameRate)
    {
        foreach (VideoMaskShapeKeyframe key in clip.MaskShapeKeyframes)
        {
            key.FrameOffset = (int)Math.Round(key.FrameOffset * newFrameRate / oldFrameRate);
        }
        clip.MaskShapeKeyframes = clip.MaskShapeKeyframes.OrderBy(key => key.FrameOffset)
            .GroupBy(key => key.FrameOffset).Select(group => group.Last()).ToList();
    }

    public static void ClearInvalid(IReadOnlyList<TimelineTrackProject> tracks, double frameRate)
    {
        foreach (TimelineTrackProject track in tracks)
        {
            foreach (TimelineClipProject clip in track.Clips)
            {
                clip.MaskShapeKeyframes ??= [];
                if (track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image))
                {
                    clip.MaskShapeKeyframes.Clear();
                    continue;
                }

                clip.MaskShapeKeyframes.RemoveAll(key => key is null || key.FrameOffset < 0 ||
                    !Enum.IsDefined(key.Shape) || !IsInClip(clip, track.Kind, key.FrameOffset, frameRate));
                clip.MaskShapeKeyframes = clip.MaskShapeKeyframes
                    .OrderBy(key => key.FrameOffset).GroupBy(key => key.FrameOffset)
                    .Select(group => group.Last()).ToList();
            }
        }
    }

    public static List<VideoMaskShapeKeyframe> CloneKeys(IEnumerable<VideoMaskShapeKeyframe> keys) =>
        keys.Select(key => Clone(key)).ToList();

    public static string? Validate(TimelineClipProject clip, TimelineTrackKind kind, double frameRate)
    {
        if (clip.MaskShapeKeyframes is null || clip.MaskShapeKeyframes.Any(key => key is null))
        {
            return "Mask shape keyframes cannot be missing.";
        }
        if (clip.MaskShapeKeyframes.Count > 0 &&
            kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image))
        {
            return "Mask shape keyframes are supported only for video and image clips.";
        }

        int previousFrame = -1;
        foreach (VideoMaskShapeKeyframe key in clip.MaskShapeKeyframes)
        {
            if (key.FrameOffset <= previousFrame || !Enum.IsDefined(key.Shape) ||
                !IsInClip(clip, kind, key.FrameOffset, frameRate))
            {
                return "Mask shape keyframes must be valid, ordered, unique, and inside their clip.";
            }
            previousFrame = key.FrameOffset;
        }

        return null;
    }

    private static bool SetAtFrame(TimelineClipProject clip, int frameOffset, VideoMaskShape shape)
    {
        int index = clip.MaskShapeKeyframes.FindIndex(key => key.FrameOffset == frameOffset);
        VideoMaskShapeKeyframe keyframe = ToKeyframe(frameOffset, shape);
        if (index >= 0)
        {
            clip.MaskShapeKeyframes[index] = keyframe;
        }
        else
        {
            clip.MaskShapeKeyframes.Add(keyframe);
            clip.MaskShapeKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
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

    private static VideoMaskShapeKeyframe ToKeyframe(int frameOffset, VideoMaskShape shape) =>
        new() { FrameOffset = frameOffset, Shape = shape };

    private static VideoMaskShapeKeyframe Clone(VideoMaskShapeKeyframe key, int? frameOffset = null) =>
        ToKeyframe(frameOffset ?? key.FrameOffset, key.Shape);
}
