namespace InkMotion;

public static class VideoEditTools
{
    public static bool Relink(TimelineTrackProject track, TimelineClipProject clip, string path, double sourceDuration)
    {
        if (track.IsLocked || !track.Clips.Contains(clip) || track.Kind == TimelineTrackKind.Text ||
            string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (track.Kind == TimelineTrackKind.Video)
        {
            double end = clip.SourceOutSeconds ?? clip.SourceDurationSeconds;
            if (!double.IsFinite(sourceDuration) || sourceDuration <= 0 || end > sourceDuration + 1e-8 ||
                end <= clip.SourceInSeconds)
            {
                return false;
            }

            clip.SourceOutSeconds = end;
            clip.SourceDurationSeconds = sourceDuration;
        }

        clip.SourcePath = path;
        return true;
    }

    public static int SnapStart(
        TimelineTrackProject target, TimelineClipProject clip, int desiredStart,
        IEnumerable<double> anchors, double frameRate, double toleranceSeconds)
    {
        if (!double.IsFinite(frameRate) || frameRate <= 0 ||
            !double.IsFinite(toleranceSeconds) || toleranceSeconds < 0 || desiredStart < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(frameRate));
        }

        if (target.IsLocked)
        {
            return desiredStart;
        }

        double duration = VideoTimelineEditing.GetDurationSeconds(clip, target.Kind);
        var candidates = new HashSet<int>();
        foreach (double anchor in anchors.Where(value => double.IsFinite(value) && value >= 0))
        {
            foreach (double candidate in new[]
            {
                Math.Round(anchor * frameRate) + 1,
                Math.Floor((anchor - duration) * frameRate + 1e-8) + 1,
            })
            {
                if (candidate >= 1 && candidate <= int.MaxValue)
                {
                    candidates.Add((int)candidate);
                }
            }
        }

        return candidates
            .Where(start => Math.Abs((double)start - desiredStart) / frameRate <= toleranceSeconds + 1e-8 &&
                VideoTimelineEditing.IsRangeFree(target, VideoTimelineEditing.FrameToSeconds(start, frameRate), duration, frameRate, clip))
            .OrderBy(start => Math.Abs((double)start - desiredStart))
            .ThenBy(start => start)
            .Cast<int?>()
            .FirstOrDefault() ?? desiredStart;
    }

    public static bool Slip(TimelineTrackProject track, TimelineClipProject clip, double seconds)
    {
        if (track.Kind != TimelineTrackKind.Video || track.IsLocked || !track.Clips.Contains(clip) ||
            !double.IsFinite(seconds) || Math.Abs(seconds) < 1e-10)
        {
            return false;
        }

        double end = clip.SourceOutSeconds ?? clip.SourceDurationSeconds;
        double newIn = clip.SourceInSeconds + seconds;
        double newOut = end + seconds;
        if (newIn < -1e-8 || newOut > clip.SourceDurationSeconds + 1e-8 ||
            !double.IsFinite(newIn) || !double.IsFinite(newOut) || newOut <= newIn)
        {
            return false;
        }

        clip.SourceInSeconds = Math.Max(0, newIn);
        clip.SourceOutSeconds = Math.Min(clip.SourceDurationSeconds, newOut);
        return true;
    }
}
