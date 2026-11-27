namespace InkMotion;

public static class LinkedAudioVideoEditing
{
    public static TimelineClipProject? SplitVideo(
        TimelineTrackProject track, TimelineClipProject clip, int frame, double frameRate,
        AudioTrackProject? linkedAudio, double audioSourceDuration, out AudioTrackProject? rightAudio)
    {
        rightAudio = null;
        AudioTrackProject? leftAudio = null;
        if (linkedAudio is not null)
        {
            if (linkedAudio.LinkedVideoClipId != clip.Id || !AudioClipEditing.HasValidClipSettings(linkedAudio))
                return null;
            double duration = AudioClipEditing.GetTimelineDuration(linkedAudio, audioSourceDuration);
            if (!double.IsFinite(duration))
                return null;
            double splitOffset = (frame - linkedAudio.StartFrame) / frameRate;
            if (splitOffset > 0 && splitOffset < duration)
            {
                leftAudio = AudioClipEditing.Clone(linkedAudio);
                rightAudio = AudioClipEditing.Split(leftAudio, frame, frameRate, audioSourceDuration);
                if (rightAudio is null)
                    return null;
            }
        }

        TimelineClipProject? right = VideoTimelineEditing.Split(track, clip, frame, frameRate);
        if (right is null)
        {
            rightAudio = null;
            return null;
        }
        if (linkedAudio is not null)
        {
            if (leftAudio is not null)
            {
                linkedAudio.SourceOutSeconds = leftAudio.SourceOutSeconds;
                linkedAudio.FadeInSeconds = leftAudio.FadeInSeconds;
                linkedAudio.FadeOutSeconds = leftAudio.FadeOutSeconds;
                rightAudio!.LinkedVideoClipId = right.Id;
            }
            else if (linkedAudio.StartFrame >= frame)
            {
                linkedAudio.LinkedVideoClipId = right.Id;
            }
        }
        return right;
    }

    public static bool TryCopyLinkedAudio(
        TimelineClipProject source, TimelineClipProject copy, AudioTrackProject? linkedAudio,
        out AudioTrackProject? audioCopy)
    {
        audioCopy = null;
        if (linkedAudio is null)
            return true;
        long start = (long)linkedAudio.StartFrame + copy.StartFrame - source.StartFrame;
        if (linkedAudio.LinkedVideoClipId != source.Id || start < 1 || start > int.MaxValue)
            return false;
        audioCopy = AudioClipEditing.Clone(linkedAudio);
        audioCopy.StartFrame = (int)start;
        audioCopy.LinkedVideoClipId = copy.Id;
        return true;
    }

    public static bool DeleteVideo(
        TimelineTrackProject track, TimelineClipProject clip, List<AudioTrackProject> audioClips,
        double frameRate, bool ripple = false)
    {
        if (track.IsLocked || !track.Clips.Contains(clip))
            return false;
        double end = VideoTimelineEditing.GetEndSeconds(clip, track.Kind, frameRate);
        int shift = ripple ? (int)Math.Floor(VideoTimelineEditing.GetDurationSeconds(clip, track.Kind) * frameRate + 1e-6) : 0;
        var changes = track.Clips
            .Where(other => other != clip && VideoTimelineEditing.GetStartSeconds(other, frameRate) >= end - 1e-6)
            .ToDictionary(other => other.Id, other => Math.Max(1, other.StartFrame - shift) - other.StartFrame);
        if (audioClips.Any(audio => audio.LinkedVideoClipId is { } id &&
            changes.TryGetValue(id, out int delta) && (long)audio.StartFrame + delta < 1))
            return false;
        bool deleted = ripple
            ? VideoTimelineEditing.RippleDelete(track, clip, frameRate)
            : VideoTimelineEditing.Delete(track, clip);
        if (!deleted)
            return false;
        audioClips.RemoveAll(audio => audio.LinkedVideoClipId == clip.Id);
        foreach (AudioTrackProject audio in audioClips)
        {
            if (audio.LinkedVideoClipId is { } id && changes.TryGetValue(id, out int delta))
                audio.StartFrame += delta;
        }
        return true;
    }

    public static bool TryLink(
        TimelineTrackProject videoTrack,
        TimelineClipProject videoClip,
        AudioTrackProject audioClip,
        IEnumerable<AudioTrackProject> audioClips)
    {
        ArgumentNullException.ThrowIfNull(videoTrack);
        ArgumentNullException.ThrowIfNull(videoClip);
        ArgumentNullException.ThrowIfNull(audioClip);
        ArgumentNullException.ThrowIfNull(audioClips);

        if (videoTrack.Kind != TimelineTrackKind.Video ||
            !videoTrack.Clips.Contains(videoClip) ||
            string.IsNullOrWhiteSpace(audioClip.AudioPath) ||
            audioClip.LinkedVideoClipId is not null ||
            audioClips.Any(other => other.LinkedVideoClipId == videoClip.Id))
        {
            return false;
        }

        audioClip.LinkedVideoClipId = videoClip.Id;
        return true;
    }

    public static bool TryUnlink(AudioTrackProject audioClip)
    {
        ArgumentNullException.ThrowIfNull(audioClip);
        if (audioClip.LinkedVideoClipId is null)
        {
            return false;
        }

        audioClip.LinkedVideoClipId = null;
        return true;
    }

    public static bool TryMoveVideo(
        TimelineTrackProject videoTrack,
        TimelineClipProject videoClip,
        int startFrame,
        IReadOnlyList<AudioTrackProject> audioClips,
        double frameRate)
    {
        ArgumentNullException.ThrowIfNull(videoTrack);
        ArgumentNullException.ThrowIfNull(videoClip);
        ArgumentNullException.ThrowIfNull(audioClips);
        if (startFrame < 1)
        {
            return false;
        }

        int frameDelta = startFrame - videoClip.StartFrame;
        AudioTrackProject[] linked = audioClips
            .Where(audio => audio.LinkedVideoClipId == videoClip.Id)
            .ToArray();
        if (linked.Any(audio => (long)audio.StartFrame + frameDelta < 1 ||
            (long)audio.StartFrame + frameDelta > int.MaxValue))
        {
            return false;
        }

        if (!VideoTimelineEditing.TryMove(videoTrack, videoClip, startFrame, frameRate))
        {
            return false;
        }

        foreach (AudioTrackProject audio in linked)
        {
            audio.StartFrame += frameDelta;
        }

        return true;
    }

    public static bool TryMoveVideoToTrack(
        TimelineTrackProject from,
        TimelineTrackProject to,
        TimelineClipProject videoClip,
        int startFrame,
        IReadOnlyList<AudioTrackProject> audioClips,
        double frameRate)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(videoClip);
        ArgumentNullException.ThrowIfNull(audioClips);
        if (startFrame < 1)
        {
            return false;
        }

        int previousStart = videoClip.StartFrame;
        int frameDelta = startFrame - previousStart;
        AudioTrackProject[] linked = audioClips
            .Where(audio => audio.LinkedVideoClipId == videoClip.Id)
            .ToArray();
        if (linked.Any(audio => (long)audio.StartFrame + frameDelta < 1 ||
            (long)audio.StartFrame + frameDelta > int.MaxValue))
        {
            return false;
        }

        videoClip.StartFrame = startFrame;
        if (!VideoTimelineEditing.TryMoveToTrack(from, to, videoClip, frameRate))
        {
            videoClip.StartFrame = previousStart;
            return false;
        }

        foreach (AudioTrackProject audio in linked)
        {
            audio.StartFrame += frameDelta;
        }

        return true;
    }

    public static bool TryMoveAudio(
        AudioTrackProject audioClip,
        int startFrame,
        IReadOnlyList<TimelineTrackProject> videoTracks,
        double frameRate)
    {
        ArgumentNullException.ThrowIfNull(audioClip);
        ArgumentNullException.ThrowIfNull(videoTracks);
        if (startFrame < 1)
        {
            return false;
        }

        if (audioClip.LinkedVideoClipId is not string linkedVideoClipId)
        {
            audioClip.StartFrame = startFrame;
            return true;
        }

        TimelineClipLocation? video = VideoTimelineEditing.FindClip(videoTracks, linkedVideoClipId);
        if (video is null)
        {
            return false;
        }

        int frameDelta = startFrame - audioClip.StartFrame;
        long videoStartFrame = (long)video.Clip.StartFrame + frameDelta;
        if (videoStartFrame < 1 || videoStartFrame > int.MaxValue ||
            !VideoTimelineEditing.TryMove(video.Track, video.Clip, (int)videoStartFrame, frameRate))
        {
            return false;
        }

        audioClip.StartFrame = startFrame;
        return true;
    }

    public static void ClearMissingLinks(
        IReadOnlyList<TimelineTrackProject> videoTracks,
        IEnumerable<AudioTrackProject> audioClips)
    {
        ArgumentNullException.ThrowIfNull(videoTracks);
        ArgumentNullException.ThrowIfNull(audioClips);
        var videoClipIds = videoTracks.Where(track => track.Kind == TimelineTrackKind.Video)
            .SelectMany(track => track.Clips)
            .Select(clip => clip.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (AudioTrackProject audio in audioClips)
        {
            if (audio.LinkedVideoClipId is string id && !videoClipIds.Contains(id))
            {
                audio.LinkedVideoClipId = null;
            }
        }
    }

    public static string? Validate(
        IReadOnlyList<TimelineTrackProject> videoTracks,
        IReadOnlyList<AudioTrackProject> audioClips)
    {
        ArgumentNullException.ThrowIfNull(videoTracks);
        ArgumentNullException.ThrowIfNull(audioClips);
        var videoClips = videoTracks.Where(track => track.Kind == TimelineTrackKind.Video)
            .SelectMany(track => track.Clips)
            .ToDictionary(clip => clip.Id, StringComparer.Ordinal);
        var linkedVideoIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (AudioTrackProject audio in audioClips)
        {
            if (audio.LinkedVideoClipId is not string id)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(audio.AudioPath) || !videoClips.ContainsKey(id) ||
                !linkedVideoIds.Add(id))
            {
                return "Linked audio clips must reference one existing video clip and each video clip may have only one linked audio clip.";
            }
        }

        return null;
    }
}
