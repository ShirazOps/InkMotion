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

        bool changed = ResizeEdge(workingTrack, workingClip, edge, edgeFrame, frameRate);
        if (!changed || VideoTimelineEditing.Validate([workingTrack], frameRate) is not null)
        {
            return false;
        }

        ResizeEdge(track, clip, edge, edgeFrame, frameRate);
        VideoTimelineEditing.SortClips(track);
        return true;
    }

    private static bool ResizeEdge(TimelineTrackProject track, TimelineClipProject clip,
        TimelineTrimEdge edge, int edgeFrame, double frameRate)
    {
        double start = VideoTimelineEditing.GetStartSeconds(clip, frameRate);
        double end = VideoTimelineEditing.GetEndSeconds(clip, track.Kind, frameRate);
        double requested = (edgeFrame - 1d) / frameRate;
        double duration = edge == TimelineTrimEdge.Start ? end - requested : requested - start;
        if (!double.IsFinite(duration) || duration < 1 / frameRate - 1e-8 ||
            Math.Abs(requested - (edge == TimelineTrimEdge.Start ? start : end)) < 1e-8 ||
            edge is not (TimelineTrimEdge.Start or TimelineTrimEdge.End))
        {
            return false;
        }

        bool shortening = edge == TimelineTrimEdge.Start ? requested > start : requested < end;
        if (shortening)
        {
            return edge == TimelineTrimEdge.Start
                ? VideoTimelineEditing.TryTrimStart(track, clip, edgeFrame, frameRate)
                : VideoTimelineEditing.TryTrimEnd(track, clip, edgeFrame, frameRate);
        }

        if (track.Kind == TimelineTrackKind.Video)
        {
            double added = Math.Abs(requested - (edge == TimelineTrimEdge.Start ? start : end));
            // Lengthening a ramped clip holds its edge speed over the added time.
            double delta = added * (edge == TimelineTrimEdge.Start
                ? VideoSpeedRamp.GetStartSpeed(clip)
                : VideoSpeedRamp.GetEndSpeed(clip));
            List<VideoSpeedRampPoint>? ramp = VideoSpeedRamp.IsRamped(clip)
                ? VideoSpeedRamp.Extend(
                    clip, edge == TimelineTrimEdge.Start ? added : 0, edge == TimelineTrimEdge.End ? added : 0)
                : null;
            double sourceIn = clip.SourceInSeconds;
            double sourceOut = clip.SourceOutSeconds ?? clip.SourceDurationSeconds;
            if (edge == TimelineTrimEdge.Start)
            {
                if (clip.IsReversed) sourceOut += delta;
                else sourceIn -= delta;
            }
            else
            {
                if (clip.IsReversed) sourceIn -= delta;
                else sourceOut += delta;
            }
            if (!CanUseRange(clip, sourceIn, sourceOut, frameRate) ||
                (ramp is not null && VideoSpeedRamp.ValidatePoints(ramp) is not null))
            {
                return false;
            }
            clip.SourceInSeconds = Math.Max(0, sourceIn);
            clip.SourceOutSeconds = Math.Min(clip.SourceDurationSeconds, sourceOut);
            VideoSpeedRamp.Apply(clip, ramp);
        }
        else
        {
            if (track.Kind == TimelineTrackKind.Compound)
            {
                double sourceIn = clip.SourceInSeconds -
                    (edge == TimelineTrimEdge.Start ? start - requested : 0);
                if (sourceIn < -1e-8 || sourceIn + duration > clip.SourceDurationSeconds + 1e-8)
                {
                    return false;
                }
                clip.SourceInSeconds = Math.Max(0, sourceIn);
            }
            clip.DurationSeconds = duration;
        }

        if (edge == TimelineTrimEdge.Start)
        {
            int addedFrames = clip.StartFrame - edgeFrame;
            ShiftClipKeyframes(clip, addedFrames);
            clip.StartFrame = edgeFrame;
        }
        return true;
    }

    private static void ShiftClipKeyframes(TimelineClipProject clip, int frames)
    {
        clip.TransformKeyframes.ForEach(key => key.FrameOffset += frames);
        clip.CropKeyframes.ForEach(key => key.FrameOffset += frames);
        clip.FlipKeyframes.ForEach(key => key.FrameOffset += frames);
        clip.OpacityKeyframes.ForEach(key => key.FrameOffset += frames);
        clip.VolumeKeyframes.ForEach(key => key.FrameOffset += frames);
        clip.EmbeddedAudioBalanceKeyframes.ForEach(key => key.FrameOffset += frames);
        clip.ColourGradingKeyframes.ForEach(key => key.FrameOffset += frames);
        clip.VignetteKeyframes.ForEach(key => key.FrameOffset += frames);
        clip.BlurKeyframes.ForEach(key => key.FrameOffset += frames);
        clip.SharpenKeyframes.ForEach(key => key.FrameOffset += frames);
        clip.ChromaKeyKeyframes.ForEach(key => key.FrameOffset += frames);
        clip.ChromaKeyEnabledKeyframes.ForEach(key => key.FrameOffset += frames);
        clip.ChromaKeyColourKeyframes.ForEach(key => key.FrameOffset += frames);
        clip.ChromaKeyFeatherKeyframes.ForEach(key => key.FrameOffset += frames);
        clip.ChromaKeySpillKeyframes.ForEach(key => key.FrameOffset += frames);
        clip.MaskKeyframes.ForEach(key => key.FrameOffset += frames);
        clip.MaskShapeKeyframes.ForEach(key => key.FrameOffset += frames);
        clip.MaskInversionKeyframes.ForEach(key => key.FrameOffset += frames);
        clip.MaskFeatherKeyframes.ForEach(key => key.FrameOffset += frames);
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
            selectedTrack.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image) ||
            string.IsNullOrWhiteSpace(selectedClip.SourcePath) ||
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

        // A positive ripple start shortens the clip from its start; a positive ripple end lengthens it.
        double change = frames / frameRate;
        EdgePlan? plan = PlanEdge(clip, trimStart ? TimelineTrimEdge.Start : TimelineTrimEdge.End,
            trimStart ? -change : change, frameRate);
        if (plan is null)
        {
            return false;
        }

        TimelineClipProject[] following = track.Clips.Where(candidate => candidate.StartFrame > clip.StartFrame).ToArray();
        long shift = trimStart ? -(long)frames : frames;
        if (following.Any(candidate => candidate.StartFrame + shift is < 1 or > int.MaxValue))
        {
            return false;
        }

        ApplyPlan(clip, plan);
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

        double change = frames / frameRate;
        long nextStartFrame = (long)next.StartFrame + frames;
        EdgePlan? clipPlan = PlanEdge(clip, TimelineTrimEdge.End, change, frameRate);
        EdgePlan? nextPlan = PlanEdge(next, TimelineTrimEdge.Start, -change, frameRate);
        if (nextStartFrame < 1 || nextStartFrame > int.MaxValue || clipPlan is null || nextPlan is null)
        {
            return false;
        }

        ApplyPlan(clip, clipPlan);
        ApplyPlan(next, nextPlan);
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

        double change = frames / frameRate;
        long start = (long)clip.StartFrame + frames;
        long nextStart = (long)next.StartFrame + frames;
        EdgePlan? previousPlan = PlanEdge(previous, TimelineTrimEdge.End, change, frameRate);
        EdgePlan? nextPlan = PlanEdge(next, TimelineTrimEdge.Start, -change, frameRate);
        if (start < 1 || start > int.MaxValue || nextStart < 1 || nextStart > int.MaxValue ||
            previousPlan is null || nextPlan is null)
        {
            return false;
        }

        ApplyPlan(previous, previousPlan);
        clip.StartFrame = (int)start;
        ApplyPlan(next, nextPlan);
        next.StartFrame = (int)nextStart;
        return true;
    }

    private sealed record EdgePlan(double SourceIn, double SourceOut, List<VideoSpeedRampPoint>? Ramp);

    /// <summary>
    /// Plans moving one edge of a video clip by <paramref name="change"/> timeline seconds (positive lengthens).
    /// Ramped clips keep their source-time map: shortening slices the ramp and lengthening holds the edge speed.
    /// Returns null when the result leaves the source, drops below one frame, cannot hold the fades or the ramp.
    /// </summary>
    private static EdgePlan? PlanEdge(TimelineClipProject clip, TimelineTrimEdge edge, double change, double frameRate)
    {
        bool atStart = edge == TimelineTrimEdge.Start;
        // The source in point is the leading source edge for forward clips and the trailing one for reversed clips.
        bool movesIn = atStart != clip.IsReversed;
        double sourceIn = clip.SourceInSeconds;
        double sourceOut = clip.SourceOutSeconds ?? clip.SourceDurationSeconds;
        if (!VideoSpeedRamp.IsRamped(clip))
        {
            double sourceDelta = change * clip.PlaybackSpeed;
            if (movesIn) sourceIn -= sourceDelta;
            else sourceOut += sourceDelta;
            return CanUseRange(clip, sourceIn, sourceOut, frameRate) ? new EdgePlan(sourceIn, sourceOut, null) : null;
        }

        double duration = VideoTimelineEditing.GetDurationSeconds(clip, TimelineTrackKind.Video);
        List<VideoSpeedRampPoint> ramp;
        if (change >= 0)
        {
            double sourceDelta = change * (atStart ? VideoSpeedRamp.GetStartSpeed(clip) : VideoSpeedRamp.GetEndSpeed(clip));
            ramp = VideoSpeedRamp.Extend(clip, atStart ? change : 0, atStart ? 0 : change);
            if (movesIn) sourceIn -= sourceDelta;
            else sourceOut += sourceDelta;
        }
        else
        {
            double removed = -change;
            double kept = duration - removed;
            if (!double.IsFinite(kept) || kept <= 0)
            {
                return null;
            }

            if (atStart)
            {
                double sourceRemoved = VideoSpeedRamp.GetSourceElapsed(clip, removed);
                ramp = VideoSpeedRamp.Slice(clip, removed, duration)!;
                if (movesIn) sourceIn += sourceRemoved;
                else sourceOut -= sourceRemoved;
            }
            else
            {
                double sourceKept = VideoSpeedRamp.GetSourceElapsed(clip, kept);
                ramp = VideoSpeedRamp.Slice(clip, 0, kept)!;
                if (movesIn) sourceIn = sourceOut - sourceKept;
                else sourceOut = sourceIn + sourceKept;
            }
        }

        if (VideoSpeedRamp.ValidatePoints(ramp) is not null)
        {
            return null;
        }

        double newDuration = (sourceOut - sourceIn) / VideoSpeedRamp.GetAverageSpeed(ramp);
        return double.IsFinite(sourceIn) && double.IsFinite(sourceOut) &&
            double.IsFinite(clip.SourceDurationSeconds) &&
            sourceIn >= -1e-8 && sourceOut <= clip.SourceDurationSeconds + 1e-8 &&
            double.IsFinite(newDuration) && newDuration >= 1 / frameRate - 1e-8 &&
            newDuration >= clip.FadeInSeconds + clip.FadeOutSeconds - 1e-8
            ? new EdgePlan(sourceIn, sourceOut, ramp)
            : null;
    }

    private static void ApplyPlan(TimelineClipProject clip, EdgePlan plan)
    {
        clip.SourceInSeconds = Math.Max(0, plan.SourceIn);
        clip.SourceOutSeconds = Math.Min(clip.SourceDurationSeconds, plan.SourceOut);
        VideoSpeedRamp.Apply(clip, plan.Ramp);
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
        if (track.IsLocked || !track.Clips.Contains(clip) ||
            track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image) ||
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
