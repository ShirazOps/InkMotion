namespace InkMotion;

public static class LinkedAudioVideoEditing
{
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
