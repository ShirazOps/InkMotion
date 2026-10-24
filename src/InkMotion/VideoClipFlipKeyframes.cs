namespace InkMotion;

public sealed record VideoClipFlipKeyframe
{
    public int FrameOffset { get; set; }
    public bool Horizontal { get; set; }
    public bool Vertical { get; set; }
}

public readonly record struct VideoClipFlipValue(bool Horizontal, bool Vertical);

public static class VideoClipFlipKeyframes
{
    private const double Tolerance = 1e-6;

    public static VideoClipFlipValue GetValue(TimelineClipProject clip, double frameOffset)
    {
        ArgumentNullException.ThrowIfNull(clip);
        VideoClipFlipKeyframe[] keys = clip.FlipKeyframes.OrderBy(key => key.FrameOffset).ToArray();
        if (keys.Length == 0)
        {
            return new VideoClipFlipValue(clip.FlipHorizontal, clip.FlipVertical);
        }

        VideoClipFlipKeyframe? current = keys.LastOrDefault(key => key.FrameOffset <= frameOffset);
        VideoClipFlipKeyframe value = current ?? keys[0];
        return new VideoClipFlipValue(value.Horizontal, value.Vertical);
    }

    public static VideoClipFlipValue GetValueAtTime(TimelineClipProject clip, double seconds, double frameRate) =>
        GetValue(clip, GetFrameOffset(clip, seconds, frameRate));

    public static int GetFrameOffset(TimelineClipProject clip, double seconds, double frameRate) =>
        (int)Math.Round(seconds * frameRate) - (clip.StartFrame - 1);

    public static bool CanSetAtFrame(
        TimelineClipProject clip, TimelineTrackKind kind, int frameOffset, double frameRate) =>
        IsSupportedKind(kind) && IsInClip(clip, kind, frameOffset, frameRate);

    public static bool SetValueAtTime(
        TimelineClipProject clip,
        TimelineTrackKind kind,
        double seconds,
        double frameRate,
        VideoClipFlipValue value)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!IsSupportedKind(kind))
        {
            return false;
        }
        if (clip.FlipKeyframes.Count == 0)
        {
            clip.FlipHorizontal = value.Horizontal;
            clip.FlipVertical = value.Vertical;
            return true;
        }

        int frameOffset = GetFrameOffset(clip, seconds, frameRate);
        return CanSetAtFrame(clip, kind, frameOffset, frameRate) && SetAtFrame(clip, frameOffset, value);
    }

    public static bool TrySetAtFrame(
        TimelineClipProject clip, TimelineTrackKind kind, int frameOffset, double frameRate)
    {
        if (!CanSetAtFrame(clip, kind, frameOffset, frameRate))
        {
            return false;
        }

        return SetAtFrame(clip, frameOffset, GetValue(clip, frameOffset));
    }

    public static bool IsKeyframeAt(TimelineClipProject clip, int frameOffset) =>
        clip.FlipKeyframes.Any(key => key.FrameOffset == frameOffset);

    public static bool TryRemoveAtFrame(TimelineClipProject clip, int frameOffset)
    {
        ArgumentNullException.ThrowIfNull(clip);
        int index = clip.FlipKeyframes.FindIndex(key => key.FrameOffset == frameOffset);
        if (index < 0)
        {
            return false;
        }
        if (clip.FlipKeyframes.Count == 1)
        {
            VideoClipFlipValue value = GetValue(clip, frameOffset);
            clip.FlipHorizontal = value.Horizontal;
            clip.FlipVertical = value.Vertical;
        }

        clip.FlipKeyframes.RemoveAt(index);
        return true;
    }

    public static void Split(TimelineClipProject left, TimelineClipProject right, int splitFrameOffset)
    {
        if (left.FlipKeyframes.Count == 0)
        {
            return;
        }

        VideoClipFlipValue splitValue = GetValue(left, splitFrameOffset);
        VideoClipFlipValue startValue = GetValue(left, 0);
        int leftEndFrame = Math.Max(0, splitFrameOffset - 1);
        VideoClipFlipValue leftEndValue = GetValue(left, leftEndFrame);
        List<VideoClipFlipKeyframe> original = left.FlipKeyframes;
        left.FlipKeyframes = original.Where(key => key.FrameOffset < splitFrameOffset)
            .Select(key => Clone(key)).ToList();
        if (!left.FlipKeyframes.Any(key => key.FrameOffset == 0))
        {
            left.FlipKeyframes.Add(ToKeyframe(0, startValue));
        }
        if (!left.FlipKeyframes.Any(key => key.FrameOffset == leftEndFrame))
        {
            left.FlipKeyframes.Add(ToKeyframe(leftEndFrame, leftEndValue));
        }
        left.FlipKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
        right.FlipKeyframes = original.Where(key => key.FrameOffset >= splitFrameOffset)
            .Select(key => Clone(key, key.FrameOffset - splitFrameOffset)).ToList();
        if (right.FlipKeyframes.Count == 0 || right.FlipKeyframes[0].FrameOffset != 0)
        {
            right.FlipKeyframes.Insert(0, ToKeyframe(0, splitValue));
        }
    }

    public static void TrimStart(TimelineClipProject clip, int removedFrames)
    {
        if (clip.FlipKeyframes.Count == 0 || removedFrames <= 0)
        {
            return;
        }

        VideoClipFlipValue startValue = GetValue(clip, removedFrames);
        clip.FlipKeyframes = clip.FlipKeyframes
            .Where(key => key.FrameOffset > removedFrames)
            .Select(key => Clone(key, key.FrameOffset - removedFrames)).ToList();
        clip.FlipKeyframes.Insert(0, ToKeyframe(0, startValue));
    }

    public static void TrimEnd(TimelineClipProject clip, int retainedFrames)
    {
        if (clip.FlipKeyframes.Count == 0)
        {
            return;
        }
        if (retainedFrames <= 0)
        {
            clip.FlipKeyframes.Clear();
            return;
        }

        VideoClipFlipValue startValue = GetValue(clip, 0);
        int endFrame = retainedFrames - 1;
        VideoClipFlipValue endValue = GetValue(clip, endFrame);
        clip.FlipKeyframes = clip.FlipKeyframes
            .Where(key => key.FrameOffset < retainedFrames).Select(key => Clone(key)).ToList();
        if (clip.FlipKeyframes.Count == 0 || clip.FlipKeyframes[0].FrameOffset != 0)
        {
            clip.FlipKeyframes.Insert(0, ToKeyframe(0, startValue));
        }
        if (!clip.FlipKeyframes.Any(key => key.FrameOffset == endFrame))
        {
            clip.FlipKeyframes.Add(ToKeyframe(endFrame, endValue));
            clip.FlipKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
        }
    }

    public static void RescaleFrameRate(TimelineClipProject clip, double oldFrameRate, double newFrameRate)
    {
        foreach (VideoClipFlipKeyframe key in clip.FlipKeyframes)
        {
            key.FrameOffset = (int)Math.Round(key.FrameOffset * newFrameRate / oldFrameRate);
        }
        clip.FlipKeyframes = clip.FlipKeyframes.OrderBy(key => key.FrameOffset)
            .GroupBy(key => key.FrameOffset).Select(group => group.Last()).ToList();
    }

    public static void ClearInvalid(IReadOnlyList<TimelineTrackProject> tracks, double frameRate)
    {
        foreach (TimelineTrackProject track in tracks)
        {
            foreach (TimelineClipProject clip in track.Clips)
            {
                clip.FlipKeyframes ??= [];
                if (!IsSupportedKind(track.Kind))
                {
                    clip.FlipKeyframes.Clear();
                    continue;
                }

                clip.FlipKeyframes.RemoveAll(key => key is null || key.FrameOffset < 0 ||
                    !IsInClip(clip, track.Kind, key.FrameOffset, frameRate));
                clip.FlipKeyframes = clip.FlipKeyframes.OrderBy(key => key.FrameOffset)
                    .GroupBy(key => key.FrameOffset).Select(group => group.Last()).ToList();
            }
        }
    }

    public static List<VideoClipFlipKeyframe> CloneKeys(IEnumerable<VideoClipFlipKeyframe> keys) =>
        keys.Select(key => Clone(key)).ToList();

    public static string? Validate(TimelineClipProject clip, TimelineTrackKind kind, double frameRate)
    {
        if (clip.FlipKeyframes is null || clip.FlipKeyframes.Any(key => key is null))
        {
            return "Flip keyframes cannot be missing.";
        }
        if (!IsSupportedKind(kind) && clip.FlipKeyframes.Count > 0)
        {
            return "Flip keyframes are supported only for video and image clips.";
        }

        int previousFrame = -1;
        foreach (VideoClipFlipKeyframe key in clip.FlipKeyframes)
        {
            if (key.FrameOffset <= previousFrame || !IsInClip(clip, kind, key.FrameOffset, frameRate))
            {
                return "Flip keyframes must be ordered, unique, and inside their clip.";
            }
            previousFrame = key.FrameOffset;
        }

        return null;
    }

    private static bool SetAtFrame(TimelineClipProject clip, int frameOffset, VideoClipFlipValue value)
    {
        int index = clip.FlipKeyframes.FindIndex(key => key.FrameOffset == frameOffset);
        VideoClipFlipKeyframe keyframe = ToKeyframe(frameOffset, value);
        if (index >= 0)
        {
            clip.FlipKeyframes[index] = keyframe;
        }
        else
        {
            clip.FlipKeyframes.Add(keyframe);
            clip.FlipKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
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

    private static VideoClipFlipKeyframe ToKeyframe(int frameOffset, VideoClipFlipValue value) =>
        new() { FrameOffset = frameOffset, Horizontal = value.Horizontal, Vertical = value.Vertical };

    private static VideoClipFlipKeyframe Clone(VideoClipFlipKeyframe key, int? frameOffset = null) =>
        new()
        {
            FrameOffset = frameOffset ?? key.FrameOffset,
            Horizontal = key.Horizontal,
            Vertical = key.Vertical,
        };
}
