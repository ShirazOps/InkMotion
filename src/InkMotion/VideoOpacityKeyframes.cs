namespace InkMotion;

public sealed class VideoOpacityKeyframe
{
    public int FrameOffset { get; set; }
    public double Opacity { get; set; }
}

public static class VideoOpacityKeyframes
{
    private const double Tolerance = 1e-6;

    public static double GetValue(TimelineClipProject clip, double frameOffset)
    {
        ArgumentNullException.ThrowIfNull(clip);
        VideoOpacityKeyframe[] keys = clip.OpacityKeyframes.OrderBy(key => key.FrameOffset).ToArray();
        if (keys.Length == 0)
        {
            return 1;
        }
        if (frameOffset <= keys[0].FrameOffset)
        {
            return keys[0].Opacity;
        }

        for (int index = 1; index < keys.Length; index++)
        {
            VideoOpacityKeyframe right = keys[index];
            if (frameOffset > right.FrameOffset)
            {
                continue;
            }

            VideoOpacityKeyframe left = keys[index - 1];
            double amount = (frameOffset - left.FrameOffset) / (right.FrameOffset - left.FrameOffset);
            return left.Opacity + ((right.Opacity - left.Opacity) * amount);
        }

        return keys[^1].Opacity;
    }

    public static double GetValueAtTime(TimelineClipProject clip, double seconds, double frameRate) =>
        GetValue(clip, (seconds * frameRate) - (clip.StartFrame - 1));

    public static double GetOpacityAtTime(TimelineClipProject clip, double seconds, double frameRate) =>
        clip.Opacity * GetValueAtTime(clip, seconds, frameRate);

    public static bool CanSetAtFrame(TimelineClipProject clip, TimelineTrackKind kind, int frameOffset, double frameRate) =>
        IsSupportedKind(kind) && IsInClip(clip, kind, frameOffset, frameRate);

    public static bool TrySetAtFrame(TimelineClipProject clip, TimelineTrackKind kind, int frameOffset, double frameRate)
    {
        if (!CanSetAtFrame(clip, kind, frameOffset, frameRate))
        {
            return false;
        }

        return SetValueAtFrame(clip, frameOffset, GetValue(clip, frameOffset));
    }

    public static bool SetValueAtTime(
        TimelineClipProject clip,
        TimelineTrackKind kind,
        double seconds,
        double frameRate,
        double opacity)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!IsSupportedKind(kind) || !double.IsFinite(opacity) || opacity is < 0 or > 1)
        {
            return false;
        }

        if (clip.OpacityKeyframes.Count == 0)
        {
            clip.Opacity = opacity;
            return true;
        }

        int frameOffset = GetFrameOffset(clip, seconds, frameRate);
        if (!IsInClip(clip, kind, frameOffset, frameRate))
        {
            return false;
        }

        double multiplier = clip.Opacity > 0
            ? opacity / clip.Opacity
            : opacity == 0 ? 0 : double.PositiveInfinity;
        return multiplier is >= 0 and <= 1 && SetValueAtFrame(clip, frameOffset, multiplier);
    }

    public static bool TryRemoveAtFrame(TimelineClipProject clip, int frameOffset)
    {
        ArgumentNullException.ThrowIfNull(clip);
        int index = clip.OpacityKeyframes.FindIndex(key => key.FrameOffset == frameOffset);
        if (index < 0)
        {
            return false;
        }

        if (clip.OpacityKeyframes.Count == 1)
        {
            clip.Opacity *= GetValue(clip, frameOffset);
        }

        clip.OpacityKeyframes.RemoveAt(index);
        return true;
    }

    public static bool IsKeyframeAt(TimelineClipProject clip, int frameOffset) =>
        clip.OpacityKeyframes.Any(key => key.FrameOffset == frameOffset);

    public static void Split(TimelineClipProject left, TimelineClipProject right, int splitFrameOffset)
    {
        if (left.OpacityKeyframes.Count == 0)
        {
            return;
        }

        double splitValue = GetValue(left, splitFrameOffset);
        double startValue = GetValue(left, 0);
        int leftEndFrame = Math.Max(0, splitFrameOffset - 1);
        double leftEndValue = GetValue(left, leftEndFrame);
        List<VideoOpacityKeyframe> original = left.OpacityKeyframes;
        left.OpacityKeyframes = original.Where(key => key.FrameOffset < splitFrameOffset)
            .Select(key => Clone(key)).ToList();
        if (!left.OpacityKeyframes.Any(key => key.FrameOffset == 0))
        {
            left.OpacityKeyframes.Add(new() { FrameOffset = 0, Opacity = startValue });
        }
        if (!left.OpacityKeyframes.Any(key => key.FrameOffset == leftEndFrame))
        {
            left.OpacityKeyframes.Add(new() { FrameOffset = leftEndFrame, Opacity = leftEndValue });
        }
        left.OpacityKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
        right.OpacityKeyframes = original.Where(key => key.FrameOffset >= splitFrameOffset)
            .Select(key => Clone(key, key.FrameOffset - splitFrameOffset)).ToList();
        if (right.OpacityKeyframes.Count == 0 || right.OpacityKeyframes[0].FrameOffset != 0)
        {
            right.OpacityKeyframes.Insert(0, new() { FrameOffset = 0, Opacity = splitValue });
        }
    }

    public static void TrimStart(TimelineClipProject clip, int removedFrames)
    {
        if (clip.OpacityKeyframes.Count == 0 || removedFrames <= 0)
        {
            return;
        }

        double startValue = GetValue(clip, removedFrames);
        clip.OpacityKeyframes = clip.OpacityKeyframes.Where(key => key.FrameOffset > removedFrames)
            .Select(key => Clone(key, key.FrameOffset - removedFrames)).ToList();
        clip.OpacityKeyframes.Insert(0, new() { FrameOffset = 0, Opacity = startValue });
    }

    public static void TrimEnd(TimelineClipProject clip, int retainedFrames)
    {
        if (clip.OpacityKeyframes.Count == 0)
        {
            return;
        }
        if (retainedFrames <= 0)
        {
            clip.OpacityKeyframes.Clear();
            return;
        }

        double startValue = GetValue(clip, 0);
        int endFrame = retainedFrames - 1;
        double endValue = GetValue(clip, endFrame);
        clip.OpacityKeyframes = clip.OpacityKeyframes.Where(key => key.FrameOffset < retainedFrames)
            .Select(key => Clone(key)).ToList();
        if (clip.OpacityKeyframes.Count == 0 || clip.OpacityKeyframes[0].FrameOffset != 0)
        {
            clip.OpacityKeyframes.Insert(0, new() { FrameOffset = 0, Opacity = startValue });
        }
        if (!clip.OpacityKeyframes.Any(key => key.FrameOffset == endFrame))
        {
            clip.OpacityKeyframes.Add(new() { FrameOffset = endFrame, Opacity = endValue });
            clip.OpacityKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
        }
    }

    public static void RescaleFrameRate(TimelineClipProject clip, double oldFrameRate, double newFrameRate)
    {
        foreach (VideoOpacityKeyframe key in clip.OpacityKeyframes)
        {
            key.FrameOffset = (int)Math.Round(key.FrameOffset * newFrameRate / oldFrameRate);
        }
        clip.OpacityKeyframes = clip.OpacityKeyframes.OrderBy(key => key.FrameOffset)
            .GroupBy(key => key.FrameOffset).Select(group => group.Last()).ToList();
    }

    public static void ClearInvalid(IReadOnlyList<TimelineTrackProject> tracks, double frameRate)
    {
        foreach (TimelineTrackProject track in tracks)
        {
            foreach (TimelineClipProject clip in track.Clips)
            {
                clip.OpacityKeyframes ??= [];
                if (!IsSupportedKind(track.Kind))
                {
                    clip.OpacityKeyframes.Clear();
                    continue;
                }

                clip.OpacityKeyframes.RemoveAll(key => key.FrameOffset < 0 ||
                    !IsInClip(clip, track.Kind, key.FrameOffset, frameRate) ||
                    !double.IsFinite(key.Opacity) || key.Opacity is < 0 or > 1);
                clip.OpacityKeyframes = clip.OpacityKeyframes.OrderBy(key => key.FrameOffset)
                    .GroupBy(key => key.FrameOffset).Select(group => group.Last()).ToList();
            }
        }
    }

    public static List<VideoOpacityKeyframe> CloneKeys(IEnumerable<VideoOpacityKeyframe> keys) =>
        keys.Select(key => Clone(key)).ToList();

    public static string? Validate(TimelineClipProject clip, TimelineTrackKind kind, double frameRate)
    {
        if (clip.OpacityKeyframes is null || clip.OpacityKeyframes.Any(key => key is null))
        {
            return "Opacity keyframes cannot be missing.";
        }
        if (!IsSupportedKind(kind) && clip.OpacityKeyframes.Count > 0)
        {
            return "Only video, image, and title clips can have opacity keyframes.";
        }

        int previousFrame = -1;
        foreach (VideoOpacityKeyframe key in clip.OpacityKeyframes)
        {
            if (key.FrameOffset <= previousFrame || !IsInClip(clip, kind, key.FrameOffset, frameRate) ||
                !double.IsFinite(key.Opacity) || key.Opacity is < 0 or > 1)
            {
                return "Opacity keyframes must be ordered, unique, inside their clip, and between 0 and 1.";
            }
            previousFrame = key.FrameOffset;
        }

        return null;
    }

    public static int GetFrameOffset(TimelineClipProject clip, double seconds, double frameRate) =>
        (int)Math.Round(seconds * frameRate) - (clip.StartFrame - 1);

    private static bool SetValueAtFrame(TimelineClipProject clip, int frameOffset, double opacity)
    {
        int index = clip.OpacityKeyframes.FindIndex(key => key.FrameOffset == frameOffset);
        VideoOpacityKeyframe value = new() { FrameOffset = frameOffset, Opacity = opacity };
        if (index >= 0)
        {
            clip.OpacityKeyframes[index] = value;
        }
        else
        {
            clip.OpacityKeyframes.Add(value);
            clip.OpacityKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
        }
        return true;
    }

    private static bool IsSupportedKind(TimelineTrackKind kind) =>
        kind is TimelineTrackKind.Video or TimelineTrackKind.Image or TimelineTrackKind.Text;

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

    private static VideoOpacityKeyframe Clone(VideoOpacityKeyframe key, int? frameOffset = null) => new()
    {
        FrameOffset = frameOffset ?? key.FrameOffset,
        Opacity = key.Opacity,
    };
}
