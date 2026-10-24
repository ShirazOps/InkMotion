namespace InkMotion;

public readonly record struct VideoColourGradingValue(
    double Exposure,
    double Contrast,
    double Temperature,
    double Tint,
    double Saturation,
    double Gamma = 1);

public sealed class VideoColourGradingKeyframe
{
    public int FrameOffset { get; set; }
    public double Exposure { get; set; }
    public double Contrast { get; set; }
    public double Temperature { get; set; }
    public double Tint { get; set; }
    public double Saturation { get; set; } = 1;
    public double Gamma { get; set; } = 1;
}

public static class VideoColourGradingKeyframes
{
    private const double Tolerance = 1e-6;

    public static VideoColourGradingValue GetStaticValue(TimelineClipProject clip) => new(
        clip.Exposure, clip.Contrast, clip.WhiteBalanceTemperature, clip.WhiteBalanceTint, clip.Saturation, clip.Gamma);

    public static VideoColourGradingValue GetValue(TimelineClipProject clip, double frameOffset)
    {
        ArgumentNullException.ThrowIfNull(clip);
        VideoColourGradingKeyframe[] keys = clip.ColourGradingKeyframes
            .OrderBy(key => key.FrameOffset).ToArray();
        if (keys.Length == 0)
        {
            return GetStaticValue(clip);
        }
        if (frameOffset <= keys[0].FrameOffset)
        {
            return GetValue(keys[0]);
        }

        for (int index = 1; index < keys.Length; index++)
        {
            VideoColourGradingKeyframe right = keys[index];
            if (frameOffset > right.FrameOffset)
            {
                continue;
            }

            VideoColourGradingKeyframe left = keys[index - 1];
            double amount = (frameOffset - left.FrameOffset) / (right.FrameOffset - left.FrameOffset);
            return Lerp(GetValue(left), GetValue(right), amount);
        }

        return GetValue(keys[^1]);
    }

    public static VideoColourGradingValue GetValueAtTime(TimelineClipProject clip, double seconds, double frameRate) =>
        GetValue(clip, GetFrameOffset(clip, seconds, frameRate));

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
        VideoColourGradingValue value)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!IsSupportedKind(kind) || !IsValidValue(value))
        {
            return false;
        }

        if (clip.ColourGradingKeyframes.Count == 0)
        {
            SetStaticValue(clip, value);
            return true;
        }

        int frameOffset = GetFrameOffset(clip, seconds, frameRate);
        return IsInClip(clip, kind, frameOffset, frameRate) && SetAtFrame(clip, frameOffset, value);
    }

    public static bool TryRemoveAtFrame(TimelineClipProject clip, int frameOffset)
    {
        ArgumentNullException.ThrowIfNull(clip);
        int index = clip.ColourGradingKeyframes.FindIndex(key => key.FrameOffset == frameOffset);
        if (index < 0)
        {
            return false;
        }

        if (clip.ColourGradingKeyframes.Count == 1)
        {
            SetStaticValue(clip, GetValue(clip, frameOffset));
        }

        clip.ColourGradingKeyframes.RemoveAt(index);
        return true;
    }

    public static bool IsKeyframeAt(TimelineClipProject clip, int frameOffset) =>
        clip.ColourGradingKeyframes.Any(key => key.FrameOffset == frameOffset);

    public static void Split(TimelineClipProject left, TimelineClipProject right, int splitFrameOffset)
    {
        if (left.ColourGradingKeyframes.Count == 0)
        {
            return;
        }

        VideoColourGradingValue splitValue = GetValue(left, splitFrameOffset);
        VideoColourGradingValue startValue = GetValue(left, 0);
        int leftEndFrame = Math.Max(0, splitFrameOffset - 1);
        VideoColourGradingValue leftEndValue = GetValue(left, leftEndFrame);
        List<VideoColourGradingKeyframe> original = left.ColourGradingKeyframes;
        left.ColourGradingKeyframes = original.Where(key => key.FrameOffset < splitFrameOffset)
            .Select(key => Clone(key)).ToList();
        if (!left.ColourGradingKeyframes.Any(key => key.FrameOffset == 0))
        {
            left.ColourGradingKeyframes.Add(ToKeyframe(0, startValue));
        }
        if (!left.ColourGradingKeyframes.Any(key => key.FrameOffset == leftEndFrame))
        {
            left.ColourGradingKeyframes.Add(ToKeyframe(leftEndFrame, leftEndValue));
        }
        left.ColourGradingKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
        right.ColourGradingKeyframes = original.Where(key => key.FrameOffset >= splitFrameOffset)
            .Select(key => Clone(key, key.FrameOffset - splitFrameOffset)).ToList();
        if (right.ColourGradingKeyframes.Count == 0 || right.ColourGradingKeyframes[0].FrameOffset != 0)
        {
            right.ColourGradingKeyframes.Insert(0, ToKeyframe(0, splitValue));
        }
    }

    public static void TrimStart(TimelineClipProject clip, int removedFrames)
    {
        if (clip.ColourGradingKeyframes.Count == 0 || removedFrames <= 0)
        {
            return;
        }

        VideoColourGradingValue startValue = GetValue(clip, removedFrames);
        clip.ColourGradingKeyframes = clip.ColourGradingKeyframes
            .Where(key => key.FrameOffset > removedFrames)
            .Select(key => Clone(key, key.FrameOffset - removedFrames)).ToList();
        clip.ColourGradingKeyframes.Insert(0, ToKeyframe(0, startValue));
    }

    public static void TrimEnd(TimelineClipProject clip, int retainedFrames)
    {
        if (clip.ColourGradingKeyframes.Count == 0)
        {
            return;
        }
        if (retainedFrames <= 0)
        {
            clip.ColourGradingKeyframes.Clear();
            return;
        }

        VideoColourGradingValue startValue = GetValue(clip, 0);
        int endFrame = retainedFrames - 1;
        VideoColourGradingValue endValue = GetValue(clip, endFrame);
        clip.ColourGradingKeyframes = clip.ColourGradingKeyframes
            .Where(key => key.FrameOffset < retainedFrames).Select(key => Clone(key)).ToList();
        if (clip.ColourGradingKeyframes.Count == 0 || clip.ColourGradingKeyframes[0].FrameOffset != 0)
        {
            clip.ColourGradingKeyframes.Insert(0, ToKeyframe(0, startValue));
        }
        if (!clip.ColourGradingKeyframes.Any(key => key.FrameOffset == endFrame))
        {
            clip.ColourGradingKeyframes.Add(ToKeyframe(endFrame, endValue));
            clip.ColourGradingKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
        }
    }

    public static void RescaleFrameRate(TimelineClipProject clip, double oldFrameRate, double newFrameRate)
    {
        foreach (VideoColourGradingKeyframe key in clip.ColourGradingKeyframes)
        {
            key.FrameOffset = (int)Math.Round(key.FrameOffset * newFrameRate / oldFrameRate);
        }
        clip.ColourGradingKeyframes = clip.ColourGradingKeyframes.OrderBy(key => key.FrameOffset)
            .GroupBy(key => key.FrameOffset).Select(group => group.Last()).ToList();
    }

    public static void ClearInvalid(IReadOnlyList<TimelineTrackProject> tracks, double frameRate)
    {
        foreach (TimelineTrackProject track in tracks)
        {
            foreach (TimelineClipProject clip in track.Clips)
            {
                clip.ColourGradingKeyframes ??= [];
                if (!IsSupportedKind(track.Kind))
                {
                    clip.ColourGradingKeyframes.Clear();
                    continue;
                }

                clip.ColourGradingKeyframes.RemoveAll(key => key is null || key.FrameOffset < 0 ||
                    !IsInClip(clip, track.Kind, key.FrameOffset, frameRate) ||
                    !IsValidValue(GetValue(key)));
                clip.ColourGradingKeyframes = clip.ColourGradingKeyframes.OrderBy(key => key.FrameOffset)
                    .GroupBy(key => key.FrameOffset).Select(group => group.Last()).ToList();
            }
        }
    }

    public static List<VideoColourGradingKeyframe> CloneKeys(IEnumerable<VideoColourGradingKeyframe> keys) =>
        keys.Select(key => Clone(key)).ToList();

    public static string? Validate(TimelineClipProject clip, TimelineTrackKind kind, double frameRate)
    {
        if (clip.ColourGradingKeyframes is null || clip.ColourGradingKeyframes.Any(key => key is null))
        {
            return "Colour grading keyframes cannot be missing.";
        }
        if (!IsSupportedKind(kind) && clip.ColourGradingKeyframes.Count > 0)
        {
            return "Only video and image clips can have colour grading keyframes.";
        }

        int previousFrame = -1;
        foreach (VideoColourGradingKeyframe key in clip.ColourGradingKeyframes)
        {
            if (key.FrameOffset <= previousFrame || !IsInClip(clip, kind, key.FrameOffset, frameRate) ||
                !IsValidValue(GetValue(key)))
            {
                return "Colour grading keyframes must be ordered, unique, inside their clip, and within supported ranges.";
            }
            previousFrame = key.FrameOffset;
        }

        return null;
    }

    public static int GetFrameOffset(TimelineClipProject clip, double seconds, double frameRate) =>
        (int)Math.Round(seconds * frameRate) - (clip.StartFrame - 1);

    private static bool SetAtFrame(TimelineClipProject clip, int frameOffset, VideoColourGradingValue value)
    {
        int index = clip.ColourGradingKeyframes.FindIndex(key => key.FrameOffset == frameOffset);
        VideoColourGradingKeyframe keyframe = ToKeyframe(frameOffset, value);
        if (index >= 0)
        {
            clip.ColourGradingKeyframes[index] = keyframe;
        }
        else
        {
            clip.ColourGradingKeyframes.Add(keyframe);
            clip.ColourGradingKeyframes.Sort((a, b) => a.FrameOffset.CompareTo(b.FrameOffset));
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

    private static bool IsValidValue(VideoColourGradingValue value) =>
        double.IsFinite(value.Exposure) &&
        value.Exposure is >= VideoColourGrading.MinimumExposure and <= VideoColourGrading.MaximumExposure &&
        double.IsFinite(value.Contrast) &&
        value.Contrast is >= VideoColourGrading.MinimumContrast and <= VideoColourGrading.MaximumContrast &&
        double.IsFinite(value.Temperature) &&
        value.Temperature is >= VideoColourGrading.MinimumTemperature and <= VideoColourGrading.MaximumTemperature &&
        double.IsFinite(value.Tint) &&
        value.Tint is >= VideoColourGrading.MinimumTint and <= VideoColourGrading.MaximumTint &&
        double.IsFinite(value.Saturation) &&
        value.Saturation is >= VideoColourGrading.MinimumSaturation and <= VideoColourGrading.MaximumSaturation &&
        double.IsFinite(value.Gamma) &&
        value.Gamma is >= VideoColourGrading.MinimumGamma and <= VideoColourGrading.MaximumGamma;

    private static VideoColourGradingValue GetValue(VideoColourGradingKeyframe key) =>
        new(key.Exposure, key.Contrast, key.Temperature, key.Tint, key.Saturation, key.Gamma);

    private static void SetStaticValue(TimelineClipProject clip, VideoColourGradingValue value)
    {
        clip.Exposure = value.Exposure;
        clip.Contrast = value.Contrast;
        clip.WhiteBalanceTemperature = value.Temperature;
        clip.WhiteBalanceTint = value.Tint;
        clip.Saturation = value.Saturation;
        clip.Gamma = value.Gamma;
    }

    private static VideoColourGradingValue Lerp(
        VideoColourGradingValue left,
        VideoColourGradingValue right,
        double amount) => new(
            left.Exposure + ((right.Exposure - left.Exposure) * amount),
            left.Contrast + ((right.Contrast - left.Contrast) * amount),
            left.Temperature + ((right.Temperature - left.Temperature) * amount),
            left.Tint + ((right.Tint - left.Tint) * amount),
            left.Saturation + ((right.Saturation - left.Saturation) * amount),
            left.Gamma + ((right.Gamma - left.Gamma) * amount));

    private static VideoColourGradingKeyframe ToKeyframe(int frameOffset, VideoColourGradingValue value) => new()
    {
        FrameOffset = frameOffset,
        Exposure = value.Exposure,
        Contrast = value.Contrast,
        Temperature = value.Temperature,
        Tint = value.Tint,
        Saturation = value.Saturation,
        Gamma = value.Gamma,
    };

    private static VideoColourGradingKeyframe Clone(VideoColourGradingKeyframe key, int? frameOffset = null) =>
        ToKeyframe(frameOffset ?? key.FrameOffset, GetValue(key));
}
