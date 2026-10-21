namespace InkMotion;

public static class AudioSourceRelinking
{
    public static int RelinkMatchingSources(
        IReadOnlyList<AudioTrackProject> tracks, AudioTrackProject selected, string replacementPath,
        double replacementDuration, int replacementChannels,
        IReadOnlyDictionary<AudioTrackProject, double> originalDurations, bool useReplacementEndForMissing = false)
    {
        if (!tracks.Contains(selected) || string.IsNullOrWhiteSpace(selected.AudioPath) ||
            string.IsNullOrWhiteSpace(replacementPath))
        {
            throw new InvalidOperationException("Select an audio track with a source path to relink.");
        }
        if (!double.IsFinite(replacementDuration) || replacementDuration <= 0 || replacementChannels <= 0)
        {
            throw new ArgumentException("The replacement must have a valid decoded audio format and duration.");
        }

        string original = Path.GetFullPath(selected.AudioPath);
        string replacement = Path.GetFullPath(replacementPath);
        var updates = new List<(AudioTrackProject Track, double End)>();
        foreach (AudioTrackProject track in tracks.Where(track => !string.IsNullOrWhiteSpace(track.AudioPath) &&
            string.Equals(Path.GetFullPath(track.AudioPath), original, StringComparison.OrdinalIgnoreCase)))
        {
            if (!AudioClipEditing.HasValidClipSettings(track) || (track.SpatialPanEnabled && replacementChannels != 1))
            {
                throw new InvalidOperationException("No sources were changed. Matching clips must have valid settings; spatial panning requires a mono replacement.");
            }

            double end;
            if (track.SourceOutSeconds is double explicitEnd)
            {
                end = explicitEnd;
            }
            else if (originalDurations.TryGetValue(track, out double duration) && double.IsFinite(duration) && duration > 0)
            {
                end = duration;
            }
            else if (useReplacementEndForMissing)
            {
                end = replacementDuration;
            }
            else
            {
                throw new InvalidOperationException(
                    "No sources were changed. A missing source has no saved end point. Enable 'Use replacement end for unresolved clips' only if changing those clip lengths is intended.");
            }

            double timelineDuration = (end - track.SourceInSeconds) / AudioClipEditing.GetTempoRatio(track);
            if (!double.IsFinite(end) || end > replacementDuration || end <= track.SourceInSeconds ||
                timelineDuration < track.FadeInSeconds + track.FadeOutSeconds)
            {
                throw new InvalidOperationException("No sources were changed. The replacement must cover every matching source range and its tempo-adjusted fades.");
            }

            updates.Add((track, end));
        }

        foreach (var update in updates)
        {
            update.Track.AudioPath = replacement;
            update.Track.SourceOutSeconds = update.End;
        }
        return updates.Count;
    }
}
