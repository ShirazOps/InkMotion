namespace InkMotion;

public sealed record AudioRangeParts(AudioTrackProject? Before, AudioTrackProject Range, AudioTrackProject? After);

public static class AudioRangeEditing
{
    public static (int StartFrame, int EndFrame) GetWaveformDragRange(
        AudioTrackProject track, double startX, double endX, double width, double frameRate, double sourceDuration)
    {
        ArgumentNullException.ThrowIfNull(track);
        double duration = AudioClipEditing.GetTimelineDuration(track, sourceDuration);
        if (!double.IsFinite(startX) || !double.IsFinite(endX) || !double.IsFinite(width) || width <= 0 ||
            !double.IsFinite(frameRate) || frameRate <= 0 || !double.IsFinite(duration) || duration <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Waveform selection coordinates and clip timing must be finite and positive.");
        }

        double clipEnd = track.StartFrame + duration * frameRate;
        if (!double.IsFinite(clipEnd) || clipEnd > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(track), "The audio clip extends beyond the supported timeline frame range.");
        }

        int maximumEndFrame = (int)Math.Ceiling(clipEnd - 1e-8);
        if (maximumEndFrame <= track.StartFrame)
        {
            throw new ArgumentOutOfRangeException(nameof(track), "The audio clip is shorter than one selectable timeline frame.");
        }

        double left = Math.Clamp(Math.Min(startX, endX) / width, 0, 1);
        double right = Math.Clamp(Math.Max(startX, endX) / width, 0, 1);
        double frameLength = duration * frameRate;
        int rangeStart = track.StartFrame + (int)Math.Floor(left * frameLength + 1e-8);
        int rangeEnd = track.StartFrame + (int)Math.Ceiling(right * frameLength - 1e-8);
        rangeStart = Math.Clamp(rangeStart, track.StartFrame, maximumEndFrame - 1);
        rangeEnd = Math.Clamp(rangeEnd, rangeStart + 1, maximumEndFrame);
        return (rangeStart, rangeEnd);
    }

    public static AudioRangeParts Partition(
        AudioTrackProject source, int startFrame, int endFrame, double frameRate, double sourceDuration)
    {
        ArgumentNullException.ThrowIfNull(source);
        double duration = AudioClipEditing.GetTimelineDuration(source, sourceDuration);
        double end = source.StartFrame + duration * frameRate;
        if (!double.IsFinite(frameRate) || frameRate <= 0 || !double.IsFinite(duration) || duration <= 0 ||
            startFrame < source.StartFrame || endFrame <= startFrame ||
            endFrame > Math.Ceiling(end - 1e-8) || startFrame >= end - 1e-8)
        {
            throw new ArgumentOutOfRangeException(nameof(startFrame), "Select a non-empty range within the audio clip.");
        }

        AudioTrackProject range = AudioClipEditing.Clone(source);
        AudioTrackProject? before = null;
        if (startFrame > source.StartFrame)
        {
            before = range;
            range = AudioClipEditing.Split(before, startFrame, frameRate, sourceDuration)
                ?? throw new InvalidOperationException("The selection start could not split the audio clip.");
        }

        AudioTrackProject? after = null;
        // The final partial timeline frame includes the exact source endpoint.
        if (endFrame < end - 1e-8)
        {
            after = AudioClipEditing.Split(range, endFrame, frameRate, sourceDuration)
                ?? throw new InvalidOperationException("The selection end could not split the audio clip.");
        }

        return new AudioRangeParts(before, range, after);
    }
}
