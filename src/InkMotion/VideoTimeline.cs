namespace InkMotion;

public enum TimelineTrackKind
{
    Video,
    Image,
    Text,
}

public sealed class TimelineTrackProject
{
    public string Id { get; set; } = VideoTimelineEditing.NewTrackId();
    public TimelineTrackKind Kind { get; set; }
    public string Name { get; set; } = "Video 1";
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; }
    public bool IsMuted { get; set; }
    public List<TimelineClipProject> Clips { get; set; } = [];
}

public sealed class TimelineClipProject
{
    public string Id { get; set; } = VideoTimelineEditing.NewClipId();
    public int StartFrame { get; set; } = 1;
    public string? SourcePath { get; set; }
    public double SourceInSeconds { get; set; }
    public double? SourceOutSeconds { get; set; }
    /// <summary>Length of the source video, or 0 while it has not been probed.</summary>
    public double SourceDurationSeconds { get; set; }
    public double PlaybackSpeed { get; set; } = 1;
    public bool IsReversed { get; set; }
    public bool ReverseEmbeddedAudio { get; set; } = true;
    /// <summary>Timeline length of image and text clips.</summary>
    public double DurationSeconds { get; set; } = VideoTimelineEditing.DefaultStillDurationSeconds;
    public double Opacity { get; set; } = 1;
    public double Volume { get; set; } = 1;
    public double FadeInSeconds { get; set; }
    public double FadeOutSeconds { get; set; }
    public double TransitionInSeconds { get; set; }
    public double CropLeftPercent { get; set; }
    public double CropTopPercent { get; set; }
    public double CropRightPercent { get; set; }
    public double CropBottomPercent { get; set; }
    public double X { get; set; } = 0.5;
    public double Y { get; set; } = 0.5;
    public double Scale { get; set; } = 1;
    public double RotationDegrees { get; set; }
    public List<VideoTransformKeyframe> TransformKeyframes { get; set; } = [];
    public string Text { get; set; } = string.Empty;
    public string FontFamily { get; set; } = "Segoe UI";
    /// <summary>Font size in pixels relative to a 1080-pixel-high frame.</summary>
    public double FontSize { get; set; } = 72;
    public uint Colour { get; set; } = 0xFFFFFFFF;
}

public sealed record TimelineClipLocation(TimelineTrackProject Track, TimelineClipProject Clip);

public static class VideoTimelineEditing
{
    public const double DefaultStillDurationSeconds = 5;
    public const double MaximumStillDurationSeconds = 3600;
    public const double MinimumScale = 0.05;
    public const double MaximumScale = 4;
    public const double MinimumFontSize = 8;
    public const double MaximumFontSize = 400;
    public const double MinimumPlaybackSpeed = 0.25;
    public const double MaximumPlaybackSpeed = 4;
    private const double Tolerance = 1e-6;

    public static IReadOnlyList<string> ImageExtensions { get; } =
        Array.AsReadOnly(new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp" });

    public static string NewTrackId() => $"track-{Guid.NewGuid():N}";

    public static string NewClipId() => $"clip-{Guid.NewGuid():N}";

    public static string KindLabel(TimelineTrackKind kind) => kind switch
    {
        TimelineTrackKind.Video => "Video",
        TimelineTrackKind.Image => "Image",
        TimelineTrackKind.Text => "Title",
        _ => kind.ToString(),
    };

    public static double GetStartSeconds(TimelineClipProject clip, double frameRate) =>
        (clip.StartFrame - 1) / frameRate;

    public static double GetDurationSeconds(TimelineClipProject clip, TimelineTrackKind kind)
    {
        if (kind != TimelineTrackKind.Video)
        {
            return clip.DurationSeconds;
        }

        double end = clip.SourceOutSeconds ?? clip.SourceDurationSeconds;
        return Math.Max(0, end - clip.SourceInSeconds) / clip.PlaybackSpeed;
    }

    public static double GetEndSeconds(TimelineClipProject clip, TimelineTrackKind kind, double frameRate) =>
        GetStartSeconds(clip, frameRate) + GetDurationSeconds(clip, kind);

    public static double FrameToSeconds(int frameNumber, double frameRate) => (frameNumber - 1) / frameRate;

    public static TimelineTrackProject CreateTrack(IReadOnlyCollection<TimelineTrackProject> tracks, TimelineTrackKind kind)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        string label = KindLabel(kind);
        int number = tracks.Count(track => track.Kind == kind) + 1;
        while (tracks.Any(track => string.Equals(track.Name, $"{label} {number}", StringComparison.OrdinalIgnoreCase)))
        {
            number++;
        }

        return new TimelineTrackProject { Kind = kind, Name = $"{label} {number}" };
    }

    /// <summary>
    /// Inserts a track of the given kind. Titles go above everything, images above the video tracks,
    /// and video tracks below the existing video tracks, so overlays stay visible by default.
    /// </summary>
    public static int InsertTrack(List<TimelineTrackProject> tracks, TimelineTrackProject track, int? aboveIndex = null)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(track);
        int index;
        if (aboveIndex is int requested)
        {
            index = Math.Clamp(requested, 0, tracks.Count);
        }
        else
        {
            index = track.Kind switch
            {
                TimelineTrackKind.Text => 0,
                TimelineTrackKind.Image => FirstIndexOrCount(tracks, existing => existing.Kind == TimelineTrackKind.Video),
                _ => tracks.Count,
            };
        }

        tracks.Insert(index, track);
        return index;
    }

    public static bool IsRangeFree(
        TimelineTrackProject track,
        double startSeconds,
        double durationSeconds,
        double frameRate,
        TimelineClipProject? ignore = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        double end = startSeconds + durationSeconds;
        return track.Clips.All(existing =>
            ReferenceEquals(existing, ignore) ||
            GetEndSeconds(existing, track.Kind, frameRate) <= startSeconds + Tolerance ||
            GetStartSeconds(existing, frameRate) >= end - Tolerance);
    }

    /// <summary>
    /// Places a clip at its start frame on the preferred track when free; otherwise on another unlocked
    /// track of the same kind, or on a new track inserted above the preferred one.
    /// </summary>
    public static TimelineTrackProject PlaceClip(
        List<TimelineTrackProject> tracks,
        TimelineTrackKind kind,
        TimelineTrackProject? preferred,
        TimelineClipProject clip,
        double frameRate)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(clip);
        double start = GetStartSeconds(clip, frameRate);
        double duration = GetDurationSeconds(clip, kind);
        IEnumerable<TimelineTrackProject> candidates = tracks.Where(track => track.Kind == kind && !track.IsLocked);
        if (preferred is { IsLocked: false } && preferred.Kind == kind)
        {
            candidates = candidates.Where(track => !ReferenceEquals(track, preferred)).Prepend(preferred);
        }

        TimelineTrackProject? target = candidates.FirstOrDefault(track => IsRangeFree(track, start, duration, frameRate));
        if (target is null)
        {
            target = CreateTrack(tracks, kind);
            int? above = preferred is not null && preferred.Kind == kind ? tracks.IndexOf(preferred) : null;
            InsertTrack(tracks, target, above is >= 0 ? above : null);
        }

        target.Clips.Add(clip);
        SortClips(target);
        return target;
    }

    public static bool TryMove(TimelineTrackProject track, TimelineClipProject clip, int startFrame, double frameRate)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(clip);
        if (track.IsLocked || startFrame < 1)
        {
            return false;
        }

        if (!IsRangeFree(track, FrameToSeconds(startFrame, frameRate), GetDurationSeconds(clip, track.Kind), frameRate, clip))
        {
            return false;
        }

        clip.StartFrame = startFrame;
        SortClips(track);
        return true;
    }

    /// <summary>Moves a clip to another track of the same kind, keeping its timing.</summary>
    public static bool TryMoveToTrack(TimelineTrackProject from, TimelineTrackProject to, TimelineClipProject clip, double frameRate)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(clip);
        if (ReferenceEquals(from, to) || from.Kind != to.Kind || from.IsLocked || to.IsLocked ||
            !IsRangeFree(to, GetStartSeconds(clip, frameRate), GetDurationSeconds(clip, from.Kind), frameRate))
        {
            return false;
        }

        from.Clips.Remove(clip);
        to.Clips.Add(clip);
        SortClips(to);
        return true;
    }

    public static bool TryTrimStart(TimelineTrackProject track, TimelineClipProject clip, int playheadFrame, double frameRate)
    {
        if (!TryGetOffset(track, clip, playheadFrame, frameRate, out double offset))
        {
            return false;
        }

        if (track.Kind == TimelineTrackKind.Video)
        {
            double sourceOffset = offset * clip.PlaybackSpeed;
            if (clip.IsReversed)
            {
                clip.SourceOutSeconds = (clip.SourceOutSeconds ?? clip.SourceDurationSeconds) - sourceOffset;
            }
            else
            {
                clip.SourceInSeconds += sourceOffset;
            }
        }
        else
        {
            clip.DurationSeconds -= offset;
        }

        VideoTransformKeyframes.TrimStart(clip, (int)Math.Round(offset * frameRate));
        clip.StartFrame = playheadFrame;
        ClampFades(clip, track.Kind);
        return true;
    }

    public static bool TryTrimEnd(TimelineTrackProject track, TimelineClipProject clip, int playheadFrame, double frameRate)
    {
        if (!TryGetOffset(track, clip, playheadFrame, frameRate, out double offset))
        {
            return false;
        }

        if (track.Kind == TimelineTrackKind.Video)
        {
            double sourceOffset = offset * clip.PlaybackSpeed;
            clip.SourceOutSeconds = clip.IsReversed
                ? clip.SourceOutSeconds ?? clip.SourceDurationSeconds
                : clip.SourceInSeconds + sourceOffset;
            if (clip.IsReversed)
            {
                clip.SourceInSeconds = clip.SourceOutSeconds.Value - sourceOffset;
            }
        }
        else
        {
            clip.DurationSeconds = offset;
        }

        VideoTransformKeyframes.TrimEnd(clip, (int)Math.Round(offset * frameRate));
        ClampFades(clip, track.Kind);
        return true;
    }

    /// <summary>Splits a clip at the playhead in place; the right-hand part stays on the same track.</summary>
    public static TimelineClipProject? Split(TimelineTrackProject track, TimelineClipProject clip, int playheadFrame, double frameRate)
    {
        if (!TryGetOffset(track, clip, playheadFrame, frameRate, out double offset))
        {
            return null;
        }

        TimelineClipProject right = Clone(clip);
        right.Id = NewClipId();
        right.StartFrame = playheadFrame;
        right.TransitionInSeconds = 0;
        VideoTransformKeyframes.Split(clip, right, (int)Math.Round(offset * frameRate));
        if (track.Kind == TimelineTrackKind.Video)
        {
            double sourceSplit = GetSourceBoundarySeconds(clip, FrameToSeconds(playheadFrame, frameRate), frameRate);
            if (clip.IsReversed)
            {
                right.SourceOutSeconds = sourceSplit;
                clip.SourceInSeconds = sourceSplit;
            }
            else
            {
                right.SourceInSeconds = sourceSplit;
                clip.SourceOutSeconds = sourceSplit;
            }
        }
        else
        {
            right.DurationSeconds = clip.DurationSeconds - offset;
            clip.DurationSeconds = offset;
        }

        clip.FadeOutSeconds = 0;
        right.FadeInSeconds = 0;
        ClampFades(clip, track.Kind);
        ClampFades(right, track.Kind);
        track.Clips.Add(right);
        SortClips(track);
        return right;
    }

    public static bool Delete(TimelineTrackProject track, TimelineClipProject clip)
    {
        ArgumentNullException.ThrowIfNull(track);
        return !track.IsLocked && track.Clips.Remove(clip);
    }

    /// <summary>Deletes a clip and closes the gap by moving later clips on the same track earlier.</summary>
    public static bool RippleDelete(TimelineTrackProject track, TimelineClipProject clip, double frameRate)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(clip);
        if (track.IsLocked || !track.Clips.Contains(clip))
        {
            return false;
        }

        double end = GetEndSeconds(clip, track.Kind, frameRate);
        int shift = (int)Math.Floor(GetDurationSeconds(clip, track.Kind) * frameRate + Tolerance);
        track.Clips.Remove(clip);
        foreach (TimelineClipProject later in track.Clips.Where(other => GetStartSeconds(other, frameRate) >= end - Tolerance))
        {
            later.StartFrame = Math.Max(1, later.StartFrame - shift);
        }

        SortClips(track);
        return true;
    }

    public static bool MoveTrack(List<TimelineTrackProject> tracks, TimelineTrackProject track, int direction)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        int index = tracks.IndexOf(track);
        int target = index + Math.Sign(direction);
        if (index < 0 || direction == 0 || target < 0 || target >= tracks.Count)
        {
            return false;
        }

        tracks.RemoveAt(index);
        tracks.Insert(target, track);
        return true;
    }

    public static TimelineClipLocation? FindClipAt(TimelineTrackProject track, double seconds, double frameRate)
    {
        ArgumentNullException.ThrowIfNull(track);
        TimelineClipProject? clip = track.Clips.FirstOrDefault(candidate =>
            GetStartSeconds(candidate, frameRate) <= seconds + Tolerance &&
            seconds < GetEndSeconds(candidate, track.Kind, frameRate) - Tolerance);
        return clip is null ? null : new TimelineClipLocation(track, clip);
    }

    public static TimelineClipLocation? FindClip(IEnumerable<TimelineTrackProject> tracks, string? clipId)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (clipId is null)
        {
            return null;
        }

        foreach (TimelineTrackProject track in tracks)
        {
            TimelineClipProject? clip = track.Clips.FirstOrDefault(candidate => candidate.Id == clipId);
            if (clip is not null)
            {
                return new TimelineClipLocation(track, clip);
            }
        }

        return null;
    }

    /// <summary>Returns the top-most visible video clip at the given time (tracks are ordered top to bottom).</summary>
    public static TimelineClipLocation? GetActiveVideoClip(IEnumerable<TimelineTrackProject> tracks, double seconds, double frameRate)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        foreach (TimelineTrackProject track in tracks)
        {
            if (track.Kind == TimelineTrackKind.Video && track.IsVisible && FindClipAt(track, seconds, frameRate) is { } location)
            {
                return location;
            }
        }

        return null;
    }

    /// <summary>Returns visible image and title clips at the given time in drawing order (bottom track first).</summary>
    public static IReadOnlyList<TimelineClipLocation> GetVisibleOverlays(
        IReadOnlyList<TimelineTrackProject> tracks,
        double seconds,
        double frameRate)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        var overlays = new List<TimelineClipLocation>();
        for (int index = tracks.Count - 1; index >= 0; index--)
        {
            TimelineTrackProject track = tracks[index];
            if (track.Kind != TimelineTrackKind.Video && track.IsVisible && FindClipAt(track, seconds, frameRate) is { } location)
            {
                overlays.Add(location);
            }
        }

        return overlays;
    }

    /// <summary>
    /// Returns every visible clip at the given time, of any kind, in compositing order (bottom track first).
    /// Export uses this so video, image and title tracks stack exactly as they are ordered on the timeline.
    /// </summary>
    public static IReadOnlyList<TimelineClipLocation> GetVisibleLayers(
        IReadOnlyList<TimelineTrackProject> tracks,
        double seconds,
        double frameRate)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        var layers = new List<TimelineClipLocation>();
        for (int index = tracks.Count - 1; index >= 0; index--)
        {
            TimelineTrackProject track = tracks[index];
            if (!track.IsVisible)
            {
                continue;
            }

            if (track.Kind == TimelineTrackKind.Video && VideoTransitions.GetAt(track, seconds, frameRate) is { } transition)
            {
                layers.Add(new TimelineClipLocation(track, transition.OutgoingClip));
                layers.Add(new TimelineClipLocation(track, transition.IncomingClip));
            }
            else if (FindClipAt(track, seconds, frameRate) is { } location)
            {
                layers.Add(location);
            }
        }

        return layers;
    }

    public static double GetSourceSeconds(TimelineClipProject clip, double timelineSeconds, double frameRate) =>
        GetSourceBoundarySeconds(clip, timelineSeconds, frameRate) -
            (clip.IsReversed ? clip.PlaybackSpeed / frameRate : 0);

    public static double GetSourceBoundarySeconds(TimelineClipProject clip, double timelineSeconds, double frameRate)
    {
        double elapsed = timelineSeconds - GetStartSeconds(clip, frameRate);
        return clip.IsReversed
            ? (clip.SourceOutSeconds ?? clip.SourceDurationSeconds) - elapsed * clip.PlaybackSpeed
            : clip.SourceInSeconds + elapsed * clip.PlaybackSpeed;
    }

    public static bool TrySetPlaybackSpeed(
        TimelineTrackProject track,
        TimelineClipProject clip,
        double speed,
        double frameRate)
    {
        if (track.IsLocked || track.Kind != TimelineTrackKind.Video || !track.Clips.Contains(clip) ||
            !double.IsFinite(speed) || speed is < MinimumPlaybackSpeed or > MaximumPlaybackSpeed ||
            !double.IsFinite(frameRate) || frameRate <= 0 ||
            !IsValidPlaybackDuration(clip, speed, frameRate) ||
            !IsRangeFree(track, GetStartSeconds(clip, frameRate), GetPlaybackDuration(clip, speed), frameRate, clip))
        {
            return false;
        }

        double previousDuration = GetDurationSeconds(clip, track.Kind);
        clip.PlaybackSpeed = speed;
        double newDuration = GetDurationSeconds(clip, track.Kind);
        ClampFades(clip, track.Kind);
        if (newDuration < previousDuration - Tolerance)
        {
            int retainedFrames = (int)Math.Clamp(Math.Ceiling(newDuration * frameRate - Tolerance), 1, int.MaxValue);
            VideoTransformKeyframes.TrimEnd(clip, retainedFrames);
        }
        VideoTransitions.ClearInvalid([track], frameRate);
        return true;
    }

    public static bool TrySetReversed(
        TimelineTrackProject track,
        TimelineClipProject clip,
        bool reversed,
        double frameRate)
    {
        if (track.IsLocked || track.Kind != TimelineTrackKind.Video || !track.Clips.Contains(clip) ||
            !double.IsFinite(frameRate) || frameRate <= 0)
        {
            return false;
        }

        clip.IsReversed = reversed;
        VideoTransitions.ClearInvalid([track], frameRate);
        return true;
    }

    /// <summary>Clip opacity at a timeline time, including fade-in and fade-out ramps.</summary>
    public static double GetOpacity(TimelineClipProject clip, TimelineTrackKind kind, double timelineSeconds, double frameRate)
    {
        double elapsed = timelineSeconds - GetStartSeconds(clip, frameRate);
        double remaining = GetDurationSeconds(clip, kind) - elapsed;
        if (elapsed < 0 || remaining <= 0)
        {
            return 0;
        }

        double opacity = clip.Opacity;
        if (clip.FadeInSeconds > 0 && elapsed < clip.FadeInSeconds)
        {
            opacity *= elapsed / clip.FadeInSeconds;
        }

        if (clip.FadeOutSeconds > 0 && remaining < clip.FadeOutSeconds)
        {
            opacity *= remaining / clip.FadeOutSeconds;
        }

        return Math.Clamp(opacity, 0, 1);
    }

    public static double GetOpacity(TimelineClipLocation location, double timelineSeconds, double frameRate) =>
        VideoTransitions.GetOpacity(location, timelineSeconds, frameRate);

    public static void ClampFades(TimelineClipProject clip, TimelineTrackKind kind)
    {
        ArgumentNullException.ThrowIfNull(clip);
        double duration = GetDurationSeconds(clip, kind);
        if (duration <= 0)
        {
            return;
        }

        clip.FadeInSeconds = Math.Clamp(clip.FadeInSeconds, 0, duration);
        clip.FadeOutSeconds = Math.Clamp(clip.FadeOutSeconds, 0, duration - clip.FadeInSeconds);
    }

    public static double GetTimelineEndSeconds(IEnumerable<TimelineTrackProject> tracks, double frameRate)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        double end = 0;
        foreach (TimelineTrackProject track in tracks)
        {
            foreach (TimelineClipProject clip in track.Clips)
            {
                end = Math.Max(end, GetEndSeconds(clip, track.Kind, frameRate));
            }
        }

        return end;
    }

    /// <summary>Sets the timeline length of an image or title clip when the new range stays free.</summary>
    public static bool TrySetStillDuration(TimelineTrackProject track, TimelineClipProject clip, double seconds, double frameRate)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(clip);
        if (track.IsLocked || track.Kind == TimelineTrackKind.Video ||
            !double.IsFinite(seconds) || seconds <= 0 || seconds > MaximumStillDurationSeconds ||
            !IsRangeFree(track, GetStartSeconds(clip, frameRate), seconds, frameRate, clip))
        {
            return false;
        }

        clip.DurationSeconds = seconds;
        VideoTransformKeyframes.TrimEnd(clip, (int)Math.Ceiling((seconds * frameRate) - Tolerance));
        ClampFades(clip, track.Kind);
        return true;
    }

    /// <summary>
    /// Re-times clip start frames for a new project frame rate, keeping their start times as close as
    /// possible while never letting a clip start before the previous clip on its track ends.
    /// </summary>
    public static void RescaleFrameRate(IEnumerable<TimelineTrackProject> tracks, double oldFrameRate, double newFrameRate)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (oldFrameRate <= 0 || newFrameRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(newFrameRate));
        }

        foreach (TimelineTrackProject track in tracks)
        {
            SortClips(track);
            double previousEnd = 0;
            foreach (TimelineClipProject clip in track.Clips)
            {
                double start = (clip.StartFrame - 1) / oldFrameRate;
                int rounded = (int)Math.Round(start * newFrameRate, MidpointRounding.AwayFromZero);
                int earliest = (int)Math.Ceiling(previousEnd * newFrameRate - Tolerance);
                clip.StartFrame = Math.Max(rounded, earliest) + 1;
                foreach (VideoTransformKeyframe keyframe in clip.TransformKeyframes)
                {
                    keyframe.FrameOffset = (int)Math.Round(keyframe.FrameOffset * newFrameRate / oldFrameRate);
                }
                clip.TransformKeyframes = clip.TransformKeyframes
                    .OrderBy(keyframe => keyframe.FrameOffset)
                    .GroupBy(keyframe => keyframe.FrameOffset)
                    .Select(group => group.Last())
                    .ToList();
                previousEnd = GetEndSeconds(clip, track.Kind, newFrameRate);
            }
        }
    }

    public static void SortClips(TimelineTrackProject track)
    {
        ArgumentNullException.ThrowIfNull(track);
        track.Clips.Sort((first, second) => first.StartFrame.CompareTo(second.StartFrame));
    }

    public static TimelineTrackProject Clone(TimelineTrackProject track) => new()
    {
        Id = track.Id,
        Kind = track.Kind,
        Name = track.Name,
        IsVisible = track.IsVisible,
        IsLocked = track.IsLocked,
        IsMuted = track.IsMuted,
        Clips = track.Clips.Select(Clone).ToList(),
    };

    public static TimelineClipProject CopyForPlacement(TimelineClipProject source, int startFrame)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (startFrame < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(startFrame));
        }

        TimelineClipProject copy = Clone(source);
        copy.Id = Guid.NewGuid().ToString("N");
        copy.StartFrame = startFrame;
        copy.TransitionInSeconds = 0;
        return copy;
    }

    public static TimelineClipProject Clone(TimelineClipProject clip) => new()
    {
        Id = clip.Id,
        StartFrame = clip.StartFrame,
        SourcePath = clip.SourcePath,
        SourceInSeconds = clip.SourceInSeconds,
        SourceOutSeconds = clip.SourceOutSeconds,
        SourceDurationSeconds = clip.SourceDurationSeconds,
        PlaybackSpeed = clip.PlaybackSpeed,
        IsReversed = clip.IsReversed,
        ReverseEmbeddedAudio = clip.ReverseEmbeddedAudio,
        DurationSeconds = clip.DurationSeconds,
        Opacity = clip.Opacity,
        Volume = clip.Volume,
        FadeInSeconds = clip.FadeInSeconds,
        FadeOutSeconds = clip.FadeOutSeconds,
        TransitionInSeconds = clip.TransitionInSeconds,
        CropLeftPercent = clip.CropLeftPercent,
        CropTopPercent = clip.CropTopPercent,
        CropRightPercent = clip.CropRightPercent,
        CropBottomPercent = clip.CropBottomPercent,
        X = clip.X,
        Y = clip.Y,
        Scale = clip.Scale,
        RotationDegrees = clip.RotationDegrees,
        TransformKeyframes = VideoTransformKeyframes.CloneKeys(clip.TransformKeyframes),
        Text = clip.Text,
        FontFamily = clip.FontFamily,
        FontSize = clip.FontSize,
        Colour = clip.Colour,
    };

    /// <summary>Validates a timeline, returning an error message or null when it is valid.</summary>
    public static string? Validate(IReadOnlyList<TimelineTrackProject>? tracks, double frameRate)
    {
        if (tracks is null || tracks.Any(track => track is null || track.Clips is null))
        {
            return "Timeline tracks are missing.";
        }

        if (tracks.Any(track => string.IsNullOrWhiteSpace(track.Id) || string.IsNullOrWhiteSpace(track.Name) ||
                track.Name.Length > 64 || !Enum.IsDefined(track.Kind)) ||
            tracks.Select(track => track.Id).Distinct(StringComparer.Ordinal).Count() != tracks.Count)
        {
            return "Timeline tracks must have unique ids, a name, and a supported kind.";
        }

        List<TimelineClipProject> clips = tracks.SelectMany(track => track.Clips).ToList();
        if (clips.Any(clip => clip is null || string.IsNullOrWhiteSpace(clip.Id)) ||
            clips.Select(clip => clip.Id).Distinct(StringComparer.Ordinal).Count() != clips.Count)
        {
            return "Timeline clips must have unique ids.";
        }

        foreach (TimelineTrackProject track in tracks)
        {
            foreach (TimelineClipProject clip in track.Clips)
            {
                if (!IsValidClip(clip, track.Kind))
                {
                    return $"A clip on {track.Name} has invalid timing, source, or styling.";
                }

                if (track.Kind == TimelineTrackKind.Video && clip.SourceDurationSeconds > 0 &&
                    GetDurationSeconds(clip, track.Kind) < (1 / frameRate) - Tolerance)
                {
                    return $"A video clip on {track.Name} must span at least one project frame.";
                }

                if (VideoTransformKeyframes.Validate(clip, track.Kind, frameRate) is string keyframeError)
                {
                    return keyframeError;
                }

                if (VideoCrop.Validate(clip, track.Kind) is string cropError)
                {
                    return cropError;
                }
            }

            TimelineClipProject[] ordered = track.Clips.OrderBy(clip => clip.StartFrame).ToArray();
            for (int index = 1; index < ordered.Length; index++)
            {
                if (GetEndSeconds(ordered[index - 1], track.Kind, frameRate) > GetStartSeconds(ordered[index], frameRate) + Tolerance)
                {
                    return $"Clips on {track.Name} overlap.";
                }
            }

            if (VideoTransitions.Validate(track, frameRate) is string transitionError)
            {
                return transitionError;
            }
        }

        return null;
    }

    private static bool IsValidClip(TimelineClipProject clip, TimelineTrackKind kind)
    {
        bool common = clip.StartFrame >= 1 &&
            IsUnit(clip.Opacity) &&
            IsFiniteNonNegative(clip.FadeInSeconds) &&
            IsFiniteNonNegative(clip.FadeOutSeconds) &&
            IsUnit(clip.X) &&
            IsUnit(clip.Y) &&
            double.IsFinite(clip.RotationDegrees) &&
            clip.RotationDegrees is >= -180 and <= 180 &&
            (kind is TimelineTrackKind.Image or TimelineTrackKind.Video || clip.RotationDegrees == 0);
        if (!common)
        {
            return false;
        }

        switch (kind)
        {
            case TimelineTrackKind.Video:
                double sourceEnd = clip.SourceOutSeconds ?? clip.SourceDurationSeconds;
                return !string.IsNullOrWhiteSpace(clip.SourcePath) &&
                    IsUnit(clip.Volume) &&
                    IsFiniteNonNegative(clip.SourceInSeconds) &&
                    IsFiniteNonNegative(clip.SourceDurationSeconds) &&
                    double.IsFinite(clip.PlaybackSpeed) &&
                    clip.PlaybackSpeed is >= MinimumPlaybackSpeed and <= MaximumPlaybackSpeed &&
                    double.IsFinite(sourceEnd) &&
                    // An unprobed source with no out point is allowed; its length is resolved when the media opens.
                    ((clip.SourceOutSeconds is null && clip.SourceDurationSeconds == 0) || sourceEnd > clip.SourceInSeconds) &&
                    (clip.SourceDurationSeconds == 0 || sourceEnd <= clip.SourceDurationSeconds + Tolerance) &&
                    double.IsFinite(clip.TransitionInSeconds) && clip.TransitionInSeconds >= 0 &&
                    double.IsFinite(clip.Scale) && clip.Scale is >= MinimumScale and <= MaximumScale;
            case TimelineTrackKind.Image:
                return !string.IsNullOrWhiteSpace(clip.SourcePath) &&
                    clip.TransitionInSeconds == 0 &&
                    IsValidStillDuration(clip.DurationSeconds) &&
                    double.IsFinite(clip.Scale) &&
                    clip.Scale is >= MinimumScale and <= MaximumScale;
            case TimelineTrackKind.Text:
                return !string.IsNullOrWhiteSpace(clip.Text) &&
                    clip.TransitionInSeconds == 0 &&
                    clip.Text.Length <= 2048 &&
                    IsValidStillDuration(clip.DurationSeconds) &&
                    !string.IsNullOrWhiteSpace(clip.FontFamily) &&
                    clip.FontFamily.Length <= 128 &&
                    double.IsFinite(clip.FontSize) &&
                    clip.FontSize is >= MinimumFontSize and <= MaximumFontSize;
            default:
                return false;
        }
    }

    private static bool TryGetOffset(
        TimelineTrackProject track,
        TimelineClipProject clip,
        int playheadFrame,
        double frameRate,
        out double offset)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(clip);
        offset = FrameToSeconds(playheadFrame, frameRate) - GetStartSeconds(clip, frameRate);
        return !track.IsLocked &&
            track.Clips.Contains(clip) &&
            offset > Tolerance &&
            offset < GetDurationSeconds(clip, track.Kind) - Tolerance;
    }

    private static bool IsValidStillDuration(double seconds) =>
        double.IsFinite(seconds) && seconds > 0 && seconds <= MaximumStillDurationSeconds;

    private static bool IsValidPlaybackDuration(TimelineClipProject clip, double speed, double frameRate)
    {
        double sourceDuration = Math.Max(0, (clip.SourceOutSeconds ?? clip.SourceDurationSeconds) - clip.SourceInSeconds);
        return sourceDuration == 0 || sourceDuration / speed >= (1 / frameRate) - Tolerance;
    }

    private static double GetPlaybackDuration(TimelineClipProject clip, double speed) =>
        Math.Max(0, (clip.SourceOutSeconds ?? clip.SourceDurationSeconds) - clip.SourceInSeconds) / speed;

    private static bool IsUnit(double value) => double.IsFinite(value) && value is >= 0 and <= 1;

    private static bool IsFiniteNonNegative(double value) => double.IsFinite(value) && value >= 0;

    private static int FirstIndexOrCount(List<TimelineTrackProject> tracks, Func<TimelineTrackProject, bool> predicate)
    {
        int index = tracks.FindIndex(track => predicate(track));
        return index < 0 ? tracks.Count : index;
    }
}
