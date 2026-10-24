namespace InkMotion;

public sealed class VideoClipVolumeKeyframe
{
    public int FrameOffset { get; set; }
    public double Volume { get; set; } = 1;
}

public static class VideoClipVolumeKeyframes
{
    private const double Tolerance = 1e-6;

    public static double GetValue(TimelineClipProject clip, double frameOffset)
    {
        ArgumentNullException.ThrowIfNull(clip);
        List<VideoClipVolumeKeyframe> keys = clip.VolumeKeyframes;
        if (keys.Count == 0 || frameOffset <= keys[0].FrameOffset)
        {
            return keys.Count == 0 ? 1 : keys[0].Volume;
        }

        for (int index = 1; index < keys.Count; index++)
        {
            VideoClipVolumeKeyframe right = keys[index];
            if (frameOffset > right.FrameOffset)
            {
                continue;
            }

            VideoClipVolumeKeyframe left = keys[index - 1];
            double amount = (frameOffset - left.FrameOffset) / (right.FrameOffset - left.FrameOffset);
            return left.Volume + ((right.Volume - left.Volume) * amount);
        }

        return keys[^1].Volume;
    }

    public static double GetValueAtTime(TimelineClipProject clip, double seconds, double frameRate) =>
        GetValue(clip, (seconds * frameRate) - (clip.StartFrame - 1));

    public static double GetVolumeAtTime(TimelineClipProject clip, double seconds, double frameRate) =>
        clip.Volume * GetValueAtTime(clip, seconds, frameRate);

    public static int GetFrameOffset(TimelineClipProject clip, double seconds, double frameRate) =>
        (int)Math.Round(seconds * frameRate) - (clip.StartFrame - 1);

    public static bool CanSetAtFrame(TimelineClipProject clip, TimelineTrackKind kind, int frameOffset, double frameRate) =>
        kind == TimelineTrackKind.Video && IsInClip(clip, frameOffset, frameRate);

    public static bool TrySetAtFrame(TimelineClipProject clip, TimelineTrackKind kind, int frameOffset, double frameRate)
    {
        if (!CanSetAtFrame(clip, kind, frameOffset, frameRate))
        {
            return false;
        }

        return SetValueAtFrame(clip, frameOffset, GetValue(clip, frameOffset));
    }

    public static bool SetVolumeAtTime(
        TimelineClipProject clip,
        TimelineTrackKind kind,
        double seconds,
        double frameRate,
        double volume)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (kind != TimelineTrackKind.Video || !double.IsFinite(volume) ||
            volume < 0 || volume > clip.Volume)
        {
            return false;
        }

        if (clip.VolumeKeyframes.Count == 0)
        {
            clip.Volume = volume;
            return true;
        }

        int frameOffset = GetFrameOffset(clip, seconds, frameRate);
        if (!IsInClip(clip, frameOffset, frameRate))
        {
            return false;
        }

        double multiplier = clip.Volume > 0 ? volume / clip.Volume : volume == 0 ? 0 : double.PositiveInfinity;
        return multiplier is >= 0 and <= 1 && SetValueAtFrame(clip, frameOffset, multiplier);
    }

    public static bool TryRemoveAtFrame(TimelineClipProject clip, int frameOffset)
    {
        ArgumentNullException.ThrowIfNull(clip);
        int index = clip.VolumeKeyframes.FindIndex(key => key.FrameOffset == frameOffset);
        if (index < 0)
        {
            return false;
        }

        if (clip.VolumeKeyframes.Count == 1)
        {
            clip.Volume *= GetValue(clip, frameOffset);
        }

        clip.VolumeKeyframes.RemoveAt(index);
        return true;
    }

    public static bool IsKeyframeAt(TimelineClipProject clip, int frameOffset) =>
        clip.VolumeKeyframes.Any(key => key.FrameOffset == frameOffset);

    public static int? FindAdjacentFrame(TimelineClipProject clip, int frameOffset, bool next)
    {
        ArgumentNullException.ThrowIfNull(clip);
        int? result = null;
        foreach (VideoClipVolumeKeyframe key in clip.VolumeKeyframes)
        {
            if (next && key.FrameOffset > frameOffset && (result is null || key.FrameOffset < result))
            {
                result = key.FrameOffset;
            }
            else if (!next && key.FrameOffset < frameOffset && (result is null || key.FrameOffset > result))
            {
                result = key.FrameOffset;
            }
        }

        return result;
    }

    public static void Split(TimelineClipProject left, TimelineClipProject right, int splitFrameOffset)
    {
        if (left.VolumeKeyframes.Count == 0)
        {
            return;
        }

        double splitValue = GetValue(left, splitFrameOffset);
        double startValue = GetValue(left, 0);
        int leftEndFrame = Math.Max(0, splitFrameOffset - 1);
        double leftEndValue = GetValue(left, leftEndFrame);
        List<VideoClipVolumeKeyframe> original = left.VolumeKeyframes;
        left.VolumeKeyframes = original.Where(key => key.FrameOffset < splitFrameOffset)
            .Select(key => Clone(key)).ToList();
        if (!left.VolumeKeyframes.Any(key => key.FrameOffset == 0))
        {
            left.VolumeKeyframes.Add(new() { FrameOffset = 0, Volume = startValue });
        }
        if (!left.VolumeKeyframes.Any(key => key.FrameOffset == leftEndFrame))
        {
            left.VolumeKeyframes.Add(new() { FrameOffset = leftEndFrame, Volume = leftEndValue });
        }
        left.VolumeKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
        right.VolumeKeyframes = original.Where(key => key.FrameOffset >= splitFrameOffset)
            .Select(key => Clone(key, key.FrameOffset - splitFrameOffset)).ToList();
        if (right.VolumeKeyframes.Count == 0 || right.VolumeKeyframes[0].FrameOffset != 0)
        {
            right.VolumeKeyframes.Insert(0, new() { FrameOffset = 0, Volume = splitValue });
        }
    }

    public static void TrimStart(TimelineClipProject clip, int removedFrames)
    {
        if (clip.VolumeKeyframes.Count == 0 || removedFrames <= 0)
        {
            return;
        }

        double startValue = GetValue(clip, removedFrames);
        clip.VolumeKeyframes = clip.VolumeKeyframes.Where(key => key.FrameOffset > removedFrames)
            .Select(key => Clone(key, key.FrameOffset - removedFrames)).ToList();
        clip.VolumeKeyframes.Insert(0, new() { FrameOffset = 0, Volume = startValue });
    }

    public static void TrimEnd(TimelineClipProject clip, int retainedFrames)
    {
        if (clip.VolumeKeyframes.Count == 0)
        {
            return;
        }
        if (retainedFrames <= 0)
        {
            clip.VolumeKeyframes.Clear();
            return;
        }

        double startValue = GetValue(clip, 0);
        int endFrame = retainedFrames - 1;
        double endValue = GetValue(clip, endFrame);
        clip.VolumeKeyframes = clip.VolumeKeyframes.Where(key => key.FrameOffset < retainedFrames)
            .Select(key => Clone(key)).ToList();
        if (clip.VolumeKeyframes.Count == 0 || clip.VolumeKeyframes[0].FrameOffset != 0)
        {
            clip.VolumeKeyframes.Insert(0, new() { FrameOffset = 0, Volume = startValue });
        }
        if (!clip.VolumeKeyframes.Any(key => key.FrameOffset == endFrame))
        {
            clip.VolumeKeyframes.Add(new() { FrameOffset = endFrame, Volume = endValue });
            clip.VolumeKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
        }
    }

    public static void RescaleFrameRate(TimelineClipProject clip, double oldFrameRate, double newFrameRate)
    {
        foreach (VideoClipVolumeKeyframe key in clip.VolumeKeyframes)
        {
            key.FrameOffset = (int)Math.Round(key.FrameOffset * newFrameRate / oldFrameRate);
        }
        clip.VolumeKeyframes = clip.VolumeKeyframes.OrderBy(key => key.FrameOffset)
            .GroupBy(key => key.FrameOffset).Select(group => group.Last()).ToList();
    }

    public static void ClearInvalid(IReadOnlyList<TimelineTrackProject> tracks, double frameRate)
    {
        foreach (TimelineTrackProject track in tracks)
        {
            foreach (TimelineClipProject clip in track.Clips)
            {
                clip.VolumeKeyframes ??= [];
                if (track.Kind != TimelineTrackKind.Video)
                {
                    clip.VolumeKeyframes.Clear();
                    continue;
                }

                clip.VolumeKeyframes.RemoveAll(key => key.FrameOffset < 0 ||
                    !IsInClip(clip, key.FrameOffset, frameRate) ||
                    !double.IsFinite(key.Volume) || key.Volume is < 0 or > 1);
                clip.VolumeKeyframes = clip.VolumeKeyframes.OrderBy(key => key.FrameOffset)
                    .GroupBy(key => key.FrameOffset).Select(group => group.Last()).ToList();
            }
        }
    }

    public static List<VideoClipVolumeKeyframe> CloneKeys(IEnumerable<VideoClipVolumeKeyframe> keys) =>
        keys.Select(key => Clone(key)).ToList();

    public static string? Validate(TimelineClipProject clip, TimelineTrackKind kind, double frameRate)
    {
        if (clip.VolumeKeyframes is null || clip.VolumeKeyframes.Any(key => key is null))
        {
            return "Clip volume keyframes cannot be missing.";
        }
        if (kind != TimelineTrackKind.Video && clip.VolumeKeyframes.Count > 0)
        {
            return "Only video clips can have audio volume keyframes.";
        }

        int previousFrame = -1;
        foreach (VideoClipVolumeKeyframe key in clip.VolumeKeyframes)
        {
            if (key.FrameOffset <= previousFrame || !IsInClip(clip, key.FrameOffset, frameRate) ||
                !double.IsFinite(key.Volume) || key.Volume is < 0 or > 1)
            {
                return "Clip volume keyframes must be ordered, unique, inside the video clip, and between 0 and 1.";
            }
            previousFrame = key.FrameOffset;
        }

        return null;
    }

    private static bool SetValueAtFrame(TimelineClipProject clip, int frameOffset, double volume)
    {
        int index = clip.VolumeKeyframes.FindIndex(key => key.FrameOffset == frameOffset);
        VideoClipVolumeKeyframe value = new() { FrameOffset = frameOffset, Volume = volume };
        if (index >= 0)
        {
            clip.VolumeKeyframes[index] = value;
        }
        else
        {
            clip.VolumeKeyframes.Add(value);
            clip.VolumeKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
        }
        return true;
    }

    private static bool IsInClip(TimelineClipProject clip, int frameOffset, double frameRate)
    {
        if (frameOffset < 0 || !double.IsFinite(frameRate) || frameRate <= 0)
        {
            return false;
        }

        double duration = VideoTimelineEditing.GetDurationSeconds(clip, TimelineTrackKind.Video);
        return double.IsFinite(duration) && duration > 0 &&
            frameOffset < Math.Ceiling(duration * frameRate - Tolerance);
    }

    private static VideoClipVolumeKeyframe Clone(VideoClipVolumeKeyframe key, int? frameOffset = null) => new()
    {
        FrameOffset = frameOffset ?? key.FrameOffset,
        Volume = key.Volume,
    };
}
