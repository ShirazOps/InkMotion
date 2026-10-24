namespace InkMotion;

public sealed class VideoClipBalanceKeyframe
{
    public int FrameOffset { get; set; }
    public double Balance { get; set; }
}

public static class VideoClipBalanceKeyframes
{
    private const double Tolerance = 1e-6;

    public static double GetValue(TimelineClipProject clip, double frameOffset)
    {
        ArgumentNullException.ThrowIfNull(clip);
        List<VideoClipBalanceKeyframe> keys = clip.EmbeddedAudioBalanceKeyframes;
        if (keys.Count == 0)
        {
            return clip.EmbeddedAudioBalance;
        }
        if (frameOffset <= keys[0].FrameOffset)
        {
            return keys[0].Balance;
        }

        for (int index = 1; index < keys.Count; index++)
        {
            VideoClipBalanceKeyframe right = keys[index];
            if (frameOffset <= right.FrameOffset)
            {
                VideoClipBalanceKeyframe left = keys[index - 1];
                double amount = (frameOffset - left.FrameOffset) / (right.FrameOffset - left.FrameOffset);
                return left.Balance + ((right.Balance - left.Balance) * amount);
            }
        }

        return keys[^1].Balance;
    }

    public static double GetValueAtTime(TimelineClipProject clip, double seconds, double frameRate) =>
        GetValue(clip, (seconds * frameRate) - (clip.StartFrame - 1));

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

        SetValueAtFrame(clip, frameOffset, GetValue(clip, frameOffset));
        return true;
    }

    public static bool SetAtTime(
        TimelineClipProject clip,
        TimelineTrackKind kind,
        double seconds,
        double frameRate,
        double balance)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (kind != TimelineTrackKind.Video || !double.IsFinite(balance) || balance is < -1 or > 1)
        {
            return false;
        }

        if (clip.EmbeddedAudioBalanceKeyframes.Count == 0)
        {
            clip.EmbeddedAudioBalance = balance;
            return true;
        }

        int frameOffset = GetFrameOffset(clip, seconds, frameRate);
        if (!IsInClip(clip, frameOffset, frameRate))
        {
            return false;
        }

        SetValueAtFrame(clip, frameOffset, balance);
        return true;
    }

    public static bool TryRemoveAtFrame(TimelineClipProject clip, int frameOffset)
    {
        ArgumentNullException.ThrowIfNull(clip);
        int index = clip.EmbeddedAudioBalanceKeyframes.FindIndex(key => key.FrameOffset == frameOffset);
        if (index < 0)
        {
            return false;
        }

        if (clip.EmbeddedAudioBalanceKeyframes.Count == 1)
        {
            clip.EmbeddedAudioBalance = GetValue(clip, frameOffset);
        }
        clip.EmbeddedAudioBalanceKeyframes.RemoveAt(index);
        return true;
    }

    public static bool IsKeyframeAt(TimelineClipProject clip, int frameOffset) =>
        clip.EmbeddedAudioBalanceKeyframes.Any(key => key.FrameOffset == frameOffset);

    public static int? FindAdjacentFrame(TimelineClipProject clip, int frameOffset, bool next)
    {
        ArgumentNullException.ThrowIfNull(clip);
        int? result = null;
        foreach (VideoClipBalanceKeyframe key in clip.EmbeddedAudioBalanceKeyframes)
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
        if (left.EmbeddedAudioBalanceKeyframes.Count == 0)
        {
            return;
        }

        List<VideoClipBalanceKeyframe> original = left.EmbeddedAudioBalanceKeyframes;
        double startValue = GetValue(left, 0);
        int leftEnd = Math.Max(0, splitFrameOffset - 1);
        double leftEndValue = GetValue(left, leftEnd);
        double splitValue = GetValue(left, splitFrameOffset);
        left.EmbeddedAudioBalanceKeyframes = original.Where(key => key.FrameOffset < splitFrameOffset)
            .Select(key => Clone(key)).ToList();
        EnsureBoundary(left.EmbeddedAudioBalanceKeyframes, 0, startValue);
        EnsureBoundary(left.EmbeddedAudioBalanceKeyframes, leftEnd, leftEndValue);
        left.EmbeddedAudioBalanceKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
        right.EmbeddedAudioBalanceKeyframes = original.Where(key => key.FrameOffset >= splitFrameOffset)
            .Select(key => Clone(key, key.FrameOffset - splitFrameOffset)).ToList();
        EnsureBoundary(right.EmbeddedAudioBalanceKeyframes, 0, splitValue, insertFirst: true);
    }

    public static void TrimStart(TimelineClipProject clip, int removedFrames)
    {
        if (clip.EmbeddedAudioBalanceKeyframes.Count == 0 || removedFrames <= 0)
        {
            return;
        }

        double value = GetValue(clip, removedFrames);
        clip.EmbeddedAudioBalanceKeyframes = clip.EmbeddedAudioBalanceKeyframes
            .Where(key => key.FrameOffset > removedFrames)
            .Select(key => Clone(key, key.FrameOffset - removedFrames)).ToList();
        clip.EmbeddedAudioBalanceKeyframes.Insert(0, new() { FrameOffset = 0, Balance = value });
    }

    public static void TrimEnd(TimelineClipProject clip, int retainedFrames)
    {
        if (clip.EmbeddedAudioBalanceKeyframes.Count == 0)
        {
            return;
        }
        if (retainedFrames <= 0)
        {
            clip.EmbeddedAudioBalanceKeyframes.Clear();
            return;
        }

        int endFrame = retainedFrames - 1;
        double startValue = GetValue(clip, 0);
        double endValue = GetValue(clip, endFrame);
        clip.EmbeddedAudioBalanceKeyframes = clip.EmbeddedAudioBalanceKeyframes
            .Where(key => key.FrameOffset < retainedFrames).Select(key => Clone(key)).ToList();
        EnsureBoundary(clip.EmbeddedAudioBalanceKeyframes, 0, startValue, insertFirst: true);
        EnsureBoundary(clip.EmbeddedAudioBalanceKeyframes, endFrame, endValue);
        clip.EmbeddedAudioBalanceKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
    }

    public static void RescaleFrameRate(TimelineClipProject clip, double oldFrameRate, double newFrameRate)
    {
        foreach (VideoClipBalanceKeyframe key in clip.EmbeddedAudioBalanceKeyframes)
        {
            key.FrameOffset = (int)Math.Round(key.FrameOffset * newFrameRate / oldFrameRate);
        }
        clip.EmbeddedAudioBalanceKeyframes = clip.EmbeddedAudioBalanceKeyframes.OrderBy(key => key.FrameOffset)
            .GroupBy(key => key.FrameOffset).Select(group => group.Last()).ToList();
    }

    public static void ClearInvalid(IReadOnlyList<TimelineTrackProject> tracks, double frameRate)
    {
        foreach (TimelineTrackProject track in tracks)
        {
            foreach (TimelineClipProject clip in track.Clips)
            {
                clip.EmbeddedAudioBalanceKeyframes ??= [];
                if (track.Kind != TimelineTrackKind.Video)
                {
                    clip.EmbeddedAudioBalanceKeyframes.Clear();
                    continue;
                }

                clip.EmbeddedAudioBalanceKeyframes.RemoveAll(key => key.FrameOffset < 0 ||
                    !IsInClip(clip, key.FrameOffset, frameRate) ||
                    !double.IsFinite(key.Balance) || key.Balance is < -1 or > 1);
                clip.EmbeddedAudioBalanceKeyframes = clip.EmbeddedAudioBalanceKeyframes
                    .OrderBy(key => key.FrameOffset).GroupBy(key => key.FrameOffset)
                    .Select(group => group.Last()).ToList();
            }
        }
    }

    public static List<VideoClipBalanceKeyframe> CloneKeys(IEnumerable<VideoClipBalanceKeyframe> keys) =>
        keys.Select(key => Clone(key)).ToList();

    public static string? Validate(TimelineClipProject clip, TimelineTrackKind kind, double frameRate)
    {
        if (clip.EmbeddedAudioBalanceKeyframes is null ||
            clip.EmbeddedAudioBalanceKeyframes.Any(key => key is null))
        {
            return "Embedded audio balance keyframes cannot be missing.";
        }
        if (kind != TimelineTrackKind.Video && clip.EmbeddedAudioBalanceKeyframes.Count > 0)
        {
            return "Only video clips can have embedded audio balance keyframes.";
        }

        int previousFrame = -1;
        foreach (VideoClipBalanceKeyframe key in clip.EmbeddedAudioBalanceKeyframes)
        {
            if (key.FrameOffset <= previousFrame || !IsInClip(clip, key.FrameOffset, frameRate) ||
                !double.IsFinite(key.Balance) || key.Balance is < -1 or > 1)
            {
                return "Embedded audio balance keyframes must be ordered, unique, inside the video clip, and between -1 and 1.";
            }
            previousFrame = key.FrameOffset;
        }
        return null;
    }

    private static void SetValueAtFrame(TimelineClipProject clip, int frameOffset, double balance)
    {
        int index = clip.EmbeddedAudioBalanceKeyframes.FindIndex(key => key.FrameOffset == frameOffset);
        var value = new VideoClipBalanceKeyframe { FrameOffset = frameOffset, Balance = balance };
        if (index >= 0)
        {
            clip.EmbeddedAudioBalanceKeyframes[index] = value;
        }
        else
        {
            clip.EmbeddedAudioBalanceKeyframes.Add(value);
            clip.EmbeddedAudioBalanceKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
        }
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

    private static void EnsureBoundary(List<VideoClipBalanceKeyframe> keys, int frameOffset,
        double value, bool insertFirst = false)
    {
        if (keys.Any(key => key.FrameOffset == frameOffset))
        {
            return;
        }
        var key = new VideoClipBalanceKeyframe { FrameOffset = frameOffset, Balance = value };
        if (insertFirst)
        {
            keys.Insert(0, key);
        }
        else
        {
            keys.Add(key);
        }
    }

    private static VideoClipBalanceKeyframe Clone(VideoClipBalanceKeyframe key, int? frameOffset = null) => new()
    {
        FrameOffset = frameOffset ?? key.FrameOffset,
        Balance = key.Balance,
    };
}
