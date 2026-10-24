namespace InkMotion;

public enum TimelineTrimEdge
{
    Start,
    End,
}

public static class VideoEditTools
{
    public static bool TryTrimEdge(
        TimelineTrackProject track, TimelineClipProject clip, TimelineTrimEdge edge, int edgeFrame, double frameRate)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(clip);
        if (track.IsLocked || !track.Clips.Contains(clip) || edgeFrame < 1 ||
            !double.IsFinite(frameRate) || frameRate <= 0)
        {
            return false;
        }

        TimelineTrackProject workingTrack = VideoTimelineEditing.Clone(track);
        TimelineClipProject? workingClip = workingTrack.Clips.FirstOrDefault(candidate => candidate.Id == clip.Id);
        if (workingClip is null)
        {
            return false;
        }

        bool changed = edge switch
        {
            TimelineTrimEdge.Start => VideoTimelineEditing.TryTrimStart(workingTrack, workingClip, edgeFrame, frameRate),
            TimelineTrimEdge.End => VideoTimelineEditing.TryTrimEnd(workingTrack, workingClip, edgeFrame, frameRate),
            _ => false,
        };
        if (!changed || VideoTimelineEditing.Validate([workingTrack], frameRate) is not null)
        {
            return false;
        }

        clip.StartFrame = workingClip.StartFrame;
        clip.SourceInSeconds = workingClip.SourceInSeconds;
        clip.SourceOutSeconds = workingClip.SourceOutSeconds;
        clip.PlaybackSpeed = workingClip.PlaybackSpeed;
        clip.IsReversed = workingClip.IsReversed;
        clip.ReverseEmbeddedAudio = workingClip.ReverseEmbeddedAudio;
        clip.DurationSeconds = workingClip.DurationSeconds;
        clip.FadeInSeconds = workingClip.FadeInSeconds;
        clip.FadeOutSeconds = workingClip.FadeOutSeconds;
        VideoTimelineEditing.SortClips(track);
        return true;
    }

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
                (sourceOut - sourceIn) / clip.PlaybackSpeed, frameRate, clip) ||
            (clip.SourceInSeconds == sourceIn && (clip.SourceOutSeconds ?? clip.SourceDurationSeconds) == sourceOut))
        {
            return false;
        }

        clip.SourceInSeconds = Math.Max(0, sourceIn);
        clip.SourceOutSeconds = Math.Min(clip.SourceDurationSeconds, sourceOut);
        int retainedFrames = (int)Math.Ceiling(
            VideoTimelineEditing.GetDurationSeconds(clip, track.Kind) * frameRate - 1e-6);
        VideoClipVolumeKeyframes.TrimEnd(clip, retainedFrames);
        VideoClipBalanceKeyframes.TrimEnd(clip, retainedFrames);
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

        double sourceDelta = frames / frameRate * clip.PlaybackSpeed;
        double start = clip.SourceInSeconds +
            (trimStart ? (clip.IsReversed ? 0 : sourceDelta) : (clip.IsReversed ? -sourceDelta : 0));
        double end = (clip.SourceOutSeconds ?? clip.SourceDurationSeconds) +
            (trimStart ? (clip.IsReversed ? -sourceDelta : 0) : (clip.IsReversed ? 0 : sourceDelta));
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
        double clipDelta = delta * clip.PlaybackSpeed;
        double nextDelta = delta * next.PlaybackSpeed;
        double clipBoundary = clip.IsReversed
            ? clip.SourceInSeconds - clipDelta
            : (clip.SourceOutSeconds ?? clip.SourceDurationSeconds) + clipDelta;
        double nextBoundary = next.IsReversed
            ? (next.SourceOutSeconds ?? next.SourceDurationSeconds) - nextDelta
            : next.SourceInSeconds + nextDelta;
        long nextStartFrame = (long)next.StartFrame + frames;
        if (nextStartFrame < 1 || nextStartFrame > int.MaxValue ||
            !CanUseRange(clip, clip.IsReversed ? clipBoundary : clip.SourceInSeconds,
                clip.IsReversed ? (clip.SourceOutSeconds ?? clip.SourceDurationSeconds) : clipBoundary, frameRate) ||
            !CanUseRange(next, next.IsReversed ? next.SourceInSeconds : nextBoundary,
                next.IsReversed ? nextBoundary : (next.SourceOutSeconds ?? next.SourceDurationSeconds), frameRate))
        {
            return false;
        }

        if (clip.IsReversed)
        {
            clip.SourceInSeconds = Math.Max(0, clipBoundary);
        }
        else
        {
            clip.SourceOutSeconds = Math.Min(clip.SourceDurationSeconds, clipBoundary);
        }
        if (next.IsReversed)
        {
            next.SourceOutSeconds = Math.Max(0, nextBoundary);
        }
        else
        {
            next.SourceInSeconds = Math.Max(0, nextBoundary);
        }
        next.StartFrame = (int)nextStartFrame;
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
        double previousDelta = delta * previous.PlaybackSpeed;
        double nextDelta = delta * next.PlaybackSpeed;
        double previousBoundary = previous.IsReversed
            ? previous.SourceInSeconds - previousDelta
            : (previous.SourceOutSeconds ?? previous.SourceDurationSeconds) + previousDelta;
        double nextBoundary = next.IsReversed
            ? (next.SourceOutSeconds ?? next.SourceDurationSeconds) - nextDelta
            : next.SourceInSeconds + nextDelta;
        long start = (long)clip.StartFrame + frames;
        long nextStart = (long)next.StartFrame + frames;
        if (start < 1 || start > int.MaxValue || nextStart < 1 || nextStart > int.MaxValue ||
            !CanUseRange(previous, previous.IsReversed ? previousBoundary : previous.SourceInSeconds,
                previous.IsReversed ? (previous.SourceOutSeconds ?? previous.SourceDurationSeconds) : previousBoundary, frameRate) ||
            !CanUseRange(next, next.IsReversed ? next.SourceInSeconds : nextBoundary,
                next.IsReversed ? nextBoundary : (next.SourceOutSeconds ?? next.SourceDurationSeconds), frameRate))
        {
            return false;
        }

        if (previous.IsReversed)
        {
            previous.SourceInSeconds = Math.Max(0, previousBoundary);
        }
        else
        {
            previous.SourceOutSeconds = Math.Min(previous.SourceDurationSeconds, previousBoundary);
        }
        clip.StartFrame = (int)start;
        if (next.IsReversed)
        {
            next.SourceOutSeconds = Math.Max(0, nextBoundary);
        }
        else
        {
            next.SourceInSeconds = Math.Max(0, nextBoundary);
        }
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
            start >= -1e-8 && end <= clip.SourceDurationSeconds + 1e-8 &&
            duration / clip.PlaybackSpeed >= 1 / frameRate - 1e-8 &&
            duration / clip.PlaybackSpeed >= clip.FadeInSeconds + clip.FadeOutSeconds - 1e-8;
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
