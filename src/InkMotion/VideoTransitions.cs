namespace InkMotion;

public sealed record VideoCrossDissolve(TimelineClipProject OutgoingClip, TimelineClipProject IncomingClip, double Progress);

public readonly record struct VideoAudioTransition(double InSeconds, double OutSeconds);

public readonly record struct VideoAudioSourceRange(double StartSeconds, double EndSeconds)
{
    public double DurationSeconds(double playbackSpeed) => (EndSeconds - StartSeconds) / playbackSpeed;
}

public static class VideoTransitions
{
    private const double Tolerance = 1e-6;

    public static bool TrySetCrossDissolve(
        TimelineTrackProject track,
        TimelineClipProject incoming,
        double durationSeconds,
        double frameRate)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(incoming);
        if (track.IsLocked || track.Kind != TimelineTrackKind.Video ||
            !track.Clips.Contains(incoming) ||
            !TryGetIncomingNeighbour(track, incoming, frameRate, out TimelineClipProject? outgoing) ||
            !IsValidIncomingTransition(track, incoming, outgoing, durationSeconds, frameRate))
        {
            return false;
        }

        incoming.TransitionInSeconds = durationSeconds;
        return true;
    }

    public static bool RemoveCrossDissolve(TimelineTrackProject track, TimelineClipProject incoming)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(incoming);
        if (track.IsLocked || !track.Clips.Contains(incoming) || incoming.TransitionInSeconds <= 0)
        {
            return false;
        }

        incoming.TransitionInSeconds = 0;
        return true;
    }

    public static VideoCrossDissolve? GetAt(TimelineTrackProject track, double seconds, double frameRate)
    {
        if (track.Kind != TimelineTrackKind.Video || !double.IsFinite(seconds) ||
            !double.IsFinite(frameRate) || frameRate <= 0)
        {
            return null;
        }

        foreach (TimelineClipProject incoming in track.Clips.OrderBy(clip => clip.StartFrame))
        {
            double duration = incoming.TransitionInSeconds;
            if (duration <= 0 ||
                !TryGetIncomingNeighbour(track, incoming, frameRate, out TimelineClipProject? outgoing) ||
                !IsValidIncomingTransition(track, incoming, outgoing, duration, frameRate))
            {
                continue;
            }

            double start = VideoTimelineEditing.GetStartSeconds(incoming, frameRate) - (duration / 2);
            double progress = (seconds - start) / duration;
            if (progress is >= 0 and <= 1)
            {
                return new VideoCrossDissolve(outgoing, incoming, Math.Clamp(progress, 0, 1));
            }
        }

        return null;
    }

    public static double GetOpacity(TimelineClipLocation location, double seconds, double frameRate)
    {
        if (location.Track.Kind == TimelineTrackKind.Video &&
            GetAt(location.Track, seconds, frameRate) is { } transition)
        {
            if (ReferenceEquals(location.Clip, transition.OutgoingClip))
            {
                return transition.OutgoingClip.Opacity;
            }

            if (ReferenceEquals(location.Clip, transition.IncomingClip))
            {
                return transition.IncomingClip.Opacity * transition.Progress;
            }
        }

        return VideoTimelineEditing.GetOpacity(location.Clip, location.Track.Kind, seconds, frameRate);
    }

    public static VideoAudioTransition GetAudioTransition(
        TimelineTrackProject track,
        TimelineClipProject clip,
        double frameRate)
    {
        if (track.Kind != TimelineTrackKind.Video || !double.IsFinite(frameRate) || frameRate <= 0)
        {
            return default;
        }

        TimelineClipProject[] clips = track.Clips.OrderBy(item => item.StartFrame).ToArray();
        int index = Array.IndexOf(clips, clip);
        if (index < 0)
        {
            return default;
        }

        double incoming = index > 0 &&
            IsValidIncomingTransition(track, clip, clips[index - 1], clip.TransitionInSeconds, frameRate)
                ? clip.TransitionInSeconds
                : 0;
        double outgoing = index + 1 < clips.Length &&
            IsValidIncomingTransition(track, clips[index + 1], clip, clips[index + 1].TransitionInSeconds, frameRate)
                ? clips[index + 1].TransitionInSeconds
                : 0;
        return new VideoAudioTransition(incoming, outgoing);
    }

    public static VideoAudioSourceRange GetAudioSourceRange(
        TimelineClipProject clip,
        VideoAudioTransition transition)
    {
        ArgumentNullException.ThrowIfNull(clip);
        double sourceEnd = clip.SourceOutSeconds ?? clip.SourceDurationSeconds;
        double incomingHandle = transition.InSeconds / 2 * clip.PlaybackSpeed;
        double outgoingHandle = transition.OutSeconds / 2 * clip.PlaybackSpeed;
        double start = clip.IsReversed
            ? clip.SourceInSeconds - outgoingHandle
            : clip.SourceInSeconds - incomingHandle;
        double end = clip.IsReversed
            ? sourceEnd + incomingHandle
            : sourceEnd + outgoingHandle;
        start = Math.Max(0, start);
        if (clip.SourceDurationSeconds > 0)
        {
            end = Math.Min(clip.SourceDurationSeconds, end);
        }

        return new VideoAudioSourceRange(start, Math.Max(start, end));
    }

    public static string? Validate(TimelineTrackProject track, double frameRate)
    {
        if (track.Kind != TimelineTrackKind.Video)
        {
            return track.Clips.Any(clip => clip.TransitionInSeconds != 0)
                ? "Only video clips can have cross-dissolve transitions."
                : null;
        }

        TimelineClipProject[] clips = track.Clips.OrderBy(clip => clip.StartFrame).ToArray();
        for (int index = 0; index < clips.Length; index++)
        {
            TimelineClipProject incoming = clips[index];
            if (incoming.TransitionInSeconds == 0)
            {
                continue;
            }

            if (index == 0 ||
                !IsValidIncomingTransition(track, incoming, clips[index - 1], incoming.TransitionInSeconds, frameRate))
            {
                return "A cross-dissolve needs adjacent video clips with enough unused source handles and no overlapping fades or transitions.";
            }
        }

        return null;
    }

    public static void ClearInvalid(IReadOnlyList<TimelineTrackProject> tracks, double frameRate)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        foreach (TimelineTrackProject track in tracks)
        {
            if (track.Kind != TimelineTrackKind.Video)
            {
                foreach (TimelineClipProject clip in track.Clips)
                {
                    clip.TransitionInSeconds = 0;
                }
                continue;
            }

            TimelineClipProject[] clips = track.Clips.OrderBy(clip => clip.StartFrame).ToArray();
            for (int index = 0; index < clips.Length; index++)
            {
                TimelineClipProject incoming = clips[index];
                if (incoming.TransitionInSeconds > 0 &&
                    (index == 0 || !IsValidIncomingTransition(
                        track, incoming, clips[index - 1], incoming.TransitionInSeconds, frameRate)))
                {
                    incoming.TransitionInSeconds = 0;
                }
            }
        }
    }

    private static bool TryGetIncomingNeighbour(
        TimelineTrackProject track,
        TimelineClipProject incoming,
        double frameRate,
        out TimelineClipProject outgoing)
    {
        TimelineClipProject[] clips = track.Clips.OrderBy(clip => clip.StartFrame).ToArray();
        int index = Array.IndexOf(clips, incoming);
        if (index <= 0 || !double.IsFinite(frameRate) || frameRate <= 0)
        {
            outgoing = null!;
            return false;
        }

        outgoing = clips[index - 1];
        return Math.Abs(
            VideoTimelineEditing.GetEndSeconds(outgoing, TimelineTrackKind.Video, frameRate) -
            VideoTimelineEditing.GetStartSeconds(incoming, frameRate)) <= Tolerance;
    }

    private static bool IsValidIncomingTransition(
        TimelineTrackProject track,
        TimelineClipProject incoming,
        TimelineClipProject? outgoing,
        double durationSeconds,
        double frameRate)
    {
        if (track.Kind != TimelineTrackKind.Video || outgoing is null ||
            durationSeconds < 1 / frameRate ||
            !double.IsFinite(durationSeconds) ||
            incoming.Opacity != 1 || outgoing.Opacity != 1 ||
            incoming.FadeInSeconds > 0 || outgoing.FadeOutSeconds > 0 ||
            incoming.SourceDurationSeconds <= 0 || outgoing.SourceDurationSeconds <= 0 ||
            (outgoing.TransitionInSeconds / 2) + (durationSeconds / 2) >
                VideoTimelineEditing.GetDurationSeconds(outgoing, TimelineTrackKind.Video) + Tolerance)
        {
            return false;
        }

        double incomingEnd = incoming.SourceOutSeconds ?? incoming.SourceDurationSeconds;
        double outgoingEnd = outgoing.SourceOutSeconds ?? outgoing.SourceDurationSeconds;
        double incomingHandle = incoming.IsReversed
            ? incoming.SourceDurationSeconds - incomingEnd
            : incoming.SourceInSeconds;
        double outgoingHandle = outgoing.IsReversed
            ? outgoing.SourceInSeconds
            : outgoing.SourceDurationSeconds - outgoingEnd;
        return incomingHandle + Tolerance >= durationSeconds / 2 * incoming.PlaybackSpeed &&
            outgoingHandle + Tolerance >= durationSeconds / 2 * outgoing.PlaybackSpeed &&
            Math.Abs(
                VideoTimelineEditing.GetEndSeconds(outgoing, TimelineTrackKind.Video, frameRate) -
                VideoTimelineEditing.GetStartSeconds(incoming, frameRate)) <= Tolerance;
    }
}
