namespace InkMotion;

public static class VideoEditTools
{
    public static bool CloseGapBefore(TimelineTrackProject track, TimelineClipProject clip, double frameRate)
    {
        if (track.IsLocked || !track.Clips.Contains(clip) || !double.IsFinite(frameRate) || frameRate <= 0 ||
            VideoTimelineEditing.Validate([track], frameRate) is not null ||
            track.Clips.Any(candidate => VideoTimelineEditing.GetDurationSeconds(candidate, track.Kind) <= 0))
        {
            return false;
        }

        TimelineClipProject? previous = track.Clips.Where(candidate => candidate.StartFrame < clip.StartFrame)
            .MaxBy(candidate => candidate.StartFrame);
        double destination = previous is null ? 1 :
            Math.Ceiling(previous.StartFrame - 1d +
                VideoTimelineEditing.GetDurationSeconds(previous, track.Kind) * frameRate - 1e-8) + 1;
        if (!double.IsFinite(destination) || destination < 1 || destination >= clip.StartFrame)
        {
            return false;
        }

        int shift = clip.StartFrame - (int)destination;
        foreach (TimelineClipProject following in track.Clips.Where(candidate => candidate.StartFrame >= clip.StartFrame).ToArray())
        {
            following.StartFrame -= shift;
        }

        return true;
    }

    public static bool SetSourceRange(TimelineTrackProject track, TimelineClipProject clip,
        double sourceIn, double sourceOut, double frameRate)
    {
        if (track.IsLocked || track.Kind != TimelineTrackKind.Video || !track.Clips.Contains(clip) ||
            !double.IsFinite(frameRate) || frameRate <= 0 ||
            !CanUseRange(clip, sourceIn, sourceOut, frameRate) ||
            !VideoTimelineEditing.IsRangeFree(track, VideoTimelineEditing.GetStartSeconds(clip, frameRate),
                sourceOut - sourceIn, frameRate, clip) ||
            (clip.SourceInSeconds == sourceIn && (clip.SourceOutSeconds ?? clip.SourceDurationSeconds) == sourceOut))
        {
            return false;
        }

        clip.SourceInSeconds = Math.Max(0, sourceIn);
        clip.SourceOutSeconds = Math.Min(clip.SourceDurationSeconds, sourceOut);
        return true;
    }

    public static int RelinkMatchingSources(IReadOnlyList<TimelineTrackProject> tracks,
        TimelineTrackProject selectedTrack, TimelineClipProject selectedClip, string path, double sourceDuration)
    {
        if (!tracks.Contains(selectedTrack) || !selectedTrack.Clips.Contains(selectedClip) ||
            selectedTrack.Kind == TimelineTrackKind.Text || string.IsNullOrWhiteSpace(selectedClip.SourcePath) ||
            string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Select an existing video or image clip to relink its matching sources.");
        }

        string source = Path.GetFullPath(selectedClip.SourcePath);
        string replacement = Path.GetFullPath(path);
        var updates = new List<(TimelineClipProject Original, TimelineClipProject Replacement)>();
        foreach (TimelineTrackProject track in tracks.Where(track => track.Kind == selectedTrack.Kind))
        {
            foreach (TimelineClipProject clip in track.Clips.Where(clip =>
                !string.IsNullOrWhiteSpace(clip.SourcePath) &&
                string.Equals(Path.GetFullPath(clip.SourcePath), source, StringComparison.OrdinalIgnoreCase)))
            {
                TimelineClipProject copy = VideoTimelineEditing.Clone(clip);
                var validationTrack = new TimelineTrackProject { Kind = track.Kind, IsLocked = track.IsLocked, Clips = [copy] };
                if (!Relink(validationTrack, copy, replacement, sourceDuration))
                {
                    throw new InvalidOperationException("No clips were relinked. Unlock every matching track and choose a replacement long enough for every source range.");
                }

                updates.Add((clip, copy));
            }
        }

        foreach (var update in updates)
        {
            update.Original.SourcePath = update.Replacement.SourcePath;
            update.Original.SourceDurationSeconds = update.Replacement.SourceDurationSeconds;
            update.Original.SourceOutSeconds = update.Replacement.SourceOutSeconds;
        }

        return updates.Count;
    }

    public static bool RippleTrimEnd(TimelineTrackProject track, TimelineClipProject clip, int frames, double frameRate) =>
        RippleTrim(track, clip, frames, frameRate, trimStart: false);

    public static bool RippleTrimStart(TimelineTrackProject track, TimelineClipProject clip, int frames, double frameRate) =>
        RippleTrim(track, clip, frames, frameRate, trimStart: true);

    private static bool RippleTrim(TimelineTrackProject track, TimelineClipProject clip, int frames,
        double frameRate, bool trimStart)
    {
        if (!TryGetNeighbours(track, clip, frames, frameRate, out _, out _) ||
            VideoTimelineEditing.Validate([track], frameRate) is not null)
        {
            return false;
        }

        double start = clip.SourceInSeconds + (trimStart ? frames / frameRate : 0);
        double end = (clip.SourceOutSeconds ?? clip.SourceDurationSeconds) + (trimStart ? 0 : frames / frameRate);
        if (!CanUseRange(clip, start, end, frameRate))
        {
            return false;
        }

        TimelineClipProject[] following = track.Clips.Where(candidate => candidate.StartFrame > clip.StartFrame).ToArray();
        long shift = trimStart ? -(long)frames : frames;
        if (following.Any(candidate => candidate.StartFrame + shift is < 1 or > int.MaxValue))
        {
            return false;
        }

        clip.SourceInSeconds = Math.Max(0, start);
        clip.SourceOutSeconds = Math.Min(clip.SourceDurationSeconds, end);
        foreach (TimelineClipProject candidate in following)
        {
            candidate.StartFrame = (int)(candidate.StartFrame + shift);
        }
        return true;
    }

    public static bool RollEnd(TimelineTrackProject track, TimelineClipProject clip, int frames, double frameRate)
    {
        if (!TryGetNeighbours(track, clip, frames, frameRate, out _, out TimelineClipProject? next) ||
            next is null || !AreAdjacent(clip, next, frameRate))
        {
            return false;
        }

        double delta = frames / frameRate;
        double end = (clip.SourceOutSeconds ?? clip.SourceDurationSeconds) + delta;
        double nextIn = next.SourceInSeconds + delta;
        long nextStart = (long)next.StartFrame + frames;
        if (nextStart < 1 || nextStart > int.MaxValue ||
            !CanUseRange(clip, clip.SourceInSeconds, end, frameRate) ||
            !CanUseRange(next, nextIn, next.SourceOutSeconds ?? next.SourceDurationSeconds, frameRate))
        {
            return false;
        }

        clip.SourceOutSeconds = Math.Min(clip.SourceDurationSeconds, end);
        next.SourceInSeconds = Math.Max(0, nextIn);
        next.StartFrame = (int)nextStart;
        return true;
    }

    public static bool Slide(TimelineTrackProject track, TimelineClipProject clip, int frames, double frameRate)
    {
        if (!TryGetNeighbours(track, clip, frames, frameRate, out TimelineClipProject? previous, out TimelineClipProject? next) ||
            previous is null || next is null || !AreAdjacent(previous, clip, frameRate) ||
            !AreAdjacent(clip, next, frameRate) ||
            !CanUseRange(clip, clip.SourceInSeconds, clip.SourceOutSeconds ?? clip.SourceDurationSeconds, frameRate))
        {
            return false;
        }

        double delta = frames / frameRate;
        double previousEnd = (previous.SourceOutSeconds ?? previous.SourceDurationSeconds) + delta;
        double nextIn = next.SourceInSeconds + delta;
        long start = (long)clip.StartFrame + frames;
        long nextStart = (long)next.StartFrame + frames;
        if (start < 1 || start > int.MaxValue || nextStart < 1 || nextStart > int.MaxValue ||
            !CanUseRange(previous, previous.SourceInSeconds, previousEnd, frameRate) ||
            !CanUseRange(next, nextIn, next.SourceOutSeconds ?? next.SourceDurationSeconds, frameRate))
        {
            return false;
        }

        previous.SourceOutSeconds = Math.Min(previous.SourceDurationSeconds, previousEnd);
        clip.StartFrame = (int)start;
        next.SourceInSeconds = Math.Max(0, nextIn);
        next.StartFrame = (int)nextStart;
        return true;
    }

    private static bool TryGetNeighbours(TimelineTrackProject track, TimelineClipProject clip, int frames,
        double frameRate, out TimelineClipProject? previous, out TimelineClipProject? next)
    {
        previous = null;
        next = null;
        if (track.IsLocked || track.Kind != TimelineTrackKind.Video || frames == 0 ||
            !double.IsFinite(frameRate) || frameRate <= 0 || !track.Clips.Contains(clip))
        {
            return false;
        }

        TimelineClipProject[] ordered = track.Clips.OrderBy(candidate => candidate.StartFrame).ToArray();
        int index = Array.IndexOf(ordered, clip);
        previous = index > 0 ? ordered[index - 1] : null;
        next = index + 1 < ordered.Length ? ordered[index + 1] : null;
        return true;
    }

    private static bool AreAdjacent(TimelineClipProject left, TimelineClipProject right, double frameRate) =>
        Math.Abs(VideoTimelineEditing.GetEndSeconds(left, TimelineTrackKind.Video, frameRate) -
            VideoTimelineEditing.GetStartSeconds(right, frameRate)) < 1e-6;

    private static bool CanUseRange(TimelineClipProject clip, double start, double end, double frameRate)
    {
        double duration = end - start;
        return double.IsFinite(start) && double.IsFinite(end) && double.IsFinite(clip.SourceDurationSeconds) &&
            start >= -1e-8 && end <= clip.SourceDurationSeconds + 1e-8 && duration >= 1 / frameRate - 1e-8 &&
            duration >= clip.FadeInSeconds + clip.FadeOutSeconds - 1e-8;
    }

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
