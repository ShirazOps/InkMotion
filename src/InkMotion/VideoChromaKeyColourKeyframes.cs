namespace InkMotion;

public sealed record VideoChromaKeyColourKeyframe
{
    public int FrameOffset { get; set; }
    public uint Colour { get; set; }
}

public static class VideoChromaKeyColourKeyframes
{
    private const double Tolerance = 1e-6;

    public static uint GetValue(TimelineClipProject clip, double frameOffset)
    {
        ArgumentNullException.ThrowIfNull(clip);
        VideoChromaKeyColourKeyframe[] keys = clip.ChromaKeyColourKeyframes
            .OrderBy(key => key.FrameOffset).ToArray();
        if (keys.Length == 0)
        {
            return clip.ChromaKeyColour;
        }

        VideoChromaKeyColourKeyframe? current = keys.LastOrDefault(key => key.FrameOffset <= frameOffset);
        return current?.Colour ?? keys[0].Colour;
    }

    public static uint GetValueAtTime(TimelineClipProject clip, double seconds, double frameRate) =>
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
        uint colour)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!IsSupportedKind(kind) || !VideoChromaKey.IsSupportedColour(colour))
        {
            return false;
        }
        if (clip.ChromaKeyColourKeyframes.Count == 0)
        {
            clip.ChromaKeyColour = colour;
            return true;
        }

        int frameOffset = GetFrameOffset(clip, seconds, frameRate);
        return CanSetAtFrame(clip, kind, frameOffset, frameRate) && SetAtFrame(clip, frameOffset, colour);
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
        clip.ChromaKeyColourKeyframes.Any(key => key.FrameOffset == frameOffset);

    public static bool TryRemoveAtFrame(TimelineClipProject clip, int frameOffset)
    {
        ArgumentNullException.ThrowIfNull(clip);
        int index = clip.ChromaKeyColourKeyframes.FindIndex(key => key.FrameOffset == frameOffset);
        if (index < 0)
        {
            return false;
        }
        if (clip.ChromaKeyColourKeyframes.Count == 1)
        {
            clip.ChromaKeyColour = GetValue(clip, frameOffset);
        }

        clip.ChromaKeyColourKeyframes.RemoveAt(index);
        return true;
    }

    public static void Split(TimelineClipProject left, TimelineClipProject right, int splitFrameOffset)
    {
        if (left.ChromaKeyColourKeyframes.Count == 0)
        {
            return;
        }

        uint splitValue = GetValue(left, splitFrameOffset);
        uint startValue = GetValue(left, 0);
        int leftEndFrame = Math.Max(0, splitFrameOffset - 1);
        uint leftEndValue = GetValue(left, leftEndFrame);
        List<VideoChromaKeyColourKeyframe> original = left.ChromaKeyColourKeyframes;
        left.ChromaKeyColourKeyframes = original.Where(key => key.FrameOffset < splitFrameOffset)
            .Select(key => Clone(key)).ToList();
        if (!left.ChromaKeyColourKeyframes.Any(key => key.FrameOffset == 0))
        {
            left.ChromaKeyColourKeyframes.Add(ToKeyframe(0, startValue));
        }
        if (!left.ChromaKeyColourKeyframes.Any(key => key.FrameOffset == leftEndFrame))
        {
            left.ChromaKeyColourKeyframes.Add(ToKeyframe(leftEndFrame, leftEndValue));
        }
        left.ChromaKeyColourKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
        right.ChromaKeyColourKeyframes = original.Where(key => key.FrameOffset >= splitFrameOffset)
            .Select(key => Clone(key, key.FrameOffset - splitFrameOffset)).ToList();
        if (right.ChromaKeyColourKeyframes.Count == 0 || right.ChromaKeyColourKeyframes[0].FrameOffset != 0)
        {
            right.ChromaKeyColourKeyframes.Insert(0, ToKeyframe(0, splitValue));
        }
    }

    public static void TrimStart(TimelineClipProject clip, int removedFrames)
    {
        if (clip.ChromaKeyColourKeyframes.Count == 0 || removedFrames <= 0)
        {
            return;
        }

        uint startValue = GetValue(clip, removedFrames);
        clip.ChromaKeyColourKeyframes = clip.ChromaKeyColourKeyframes
            .Where(key => key.FrameOffset > removedFrames)
            .Select(key => Clone(key, key.FrameOffset - removedFrames)).ToList();
        clip.ChromaKeyColourKeyframes.Insert(0, ToKeyframe(0, startValue));
    }

    public static void TrimEnd(TimelineClipProject clip, int retainedFrames)
    {
        if (clip.ChromaKeyColourKeyframes.Count == 0)
        {
            return;
        }
        if (retainedFrames <= 0)
        {
            clip.ChromaKeyColourKeyframes.Clear();
            return;
        }

        uint startValue = GetValue(clip, 0);
        int endFrame = retainedFrames - 1;
        uint endValue = GetValue(clip, endFrame);
        clip.ChromaKeyColourKeyframes = clip.ChromaKeyColourKeyframes
            .Where(key => key.FrameOffset < retainedFrames).Select(key => Clone(key)).ToList();
        if (clip.ChromaKeyColourKeyframes.Count == 0 || clip.ChromaKeyColourKeyframes[0].FrameOffset != 0)
        {
            clip.ChromaKeyColourKeyframes.Insert(0, ToKeyframe(0, startValue));
        }
        if (!clip.ChromaKeyColourKeyframes.Any(key => key.FrameOffset == endFrame))
        {
            clip.ChromaKeyColourKeyframes.Add(ToKeyframe(endFrame, endValue));
            clip.ChromaKeyColourKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
        }
    }

    public static void RescaleFrameRate(TimelineClipProject clip, double oldFrameRate, double newFrameRate)
    {
        foreach (VideoChromaKeyColourKeyframe key in clip.ChromaKeyColourKeyframes)
        {
            key.FrameOffset = (int)Math.Round(key.FrameOffset * newFrameRate / oldFrameRate);
        }
        clip.ChromaKeyColourKeyframes = clip.ChromaKeyColourKeyframes.OrderBy(key => key.FrameOffset)
            .GroupBy(key => key.FrameOffset).Select(group => group.Last()).ToList();
    }

    public static void ClearInvalid(IReadOnlyList<TimelineTrackProject> tracks, double frameRate)
    {
        foreach (TimelineTrackProject track in tracks)
        {
            foreach (TimelineClipProject clip in track.Clips)
            {
                clip.ChromaKeyColourKeyframes ??= [];
                if (!IsSupportedKind(track.Kind))
                {
                    clip.ChromaKeyColourKeyframes.Clear();
                    continue;
                }

                clip.ChromaKeyColourKeyframes.RemoveAll(key => key is null || key.FrameOffset < 0 ||
                    !IsInClip(clip, track.Kind, key.FrameOffset, frameRate) ||
                    !VideoChromaKey.IsSupportedColour(key.Colour));
                clip.ChromaKeyColourKeyframes = clip.ChromaKeyColourKeyframes
                    .OrderBy(key => key.FrameOffset).GroupBy(key => key.FrameOffset)
                    .Select(group => group.Last()).ToList();
            }
        }
    }

    public static List<VideoChromaKeyColourKeyframe> CloneKeys(
        IEnumerable<VideoChromaKeyColourKeyframe> keys) =>
        keys.Select(key => Clone(key)).ToList();

    public static string? Validate(TimelineClipProject clip, TimelineTrackKind kind, double frameRate)
    {
        if (clip.ChromaKeyColourKeyframes is null || clip.ChromaKeyColourKeyframes.Any(key => key is null))
        {
            return "Chroma-key colour keyframes cannot be missing.";
        }
        if (!IsSupportedKind(kind) && clip.ChromaKeyColourKeyframes.Count > 0)
        {
            return "Only video and image clips can have chroma-key colour keyframes.";
        }

        int previousFrame = -1;
        foreach (VideoChromaKeyColourKeyframe key in clip.ChromaKeyColourKeyframes)
        {
            if (key.FrameOffset <= previousFrame || !IsInClip(clip, kind, key.FrameOffset, frameRate) ||
                !VideoChromaKey.IsSupportedColour(key.Colour))
            {
                return "Chroma-key colour keyframes must be ordered, unique, use a supported colour, and be inside their clip.";
            }
            previousFrame = key.FrameOffset;
        }

        return null;
    }

    private static bool SetAtFrame(TimelineClipProject clip, int frameOffset, uint colour)
    {
        int index = clip.ChromaKeyColourKeyframes.FindIndex(key => key.FrameOffset == frameOffset);
        VideoChromaKeyColourKeyframe keyframe = ToKeyframe(frameOffset, colour);
        if (index >= 0)
        {
            clip.ChromaKeyColourKeyframes[index] = keyframe;
        }
        else
        {
            clip.ChromaKeyColourKeyframes.Add(keyframe);
            clip.ChromaKeyColourKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
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

    private static VideoChromaKeyColourKeyframe ToKeyframe(int frameOffset, uint colour) =>
        new() { FrameOffset = frameOffset, Colour = colour };

    private static VideoChromaKeyColourKeyframe Clone(
        VideoChromaKeyColourKeyframe key, int? frameOffset = null) =>
        ToKeyframe(frameOffset ?? key.FrameOffset, key.Colour);
}
