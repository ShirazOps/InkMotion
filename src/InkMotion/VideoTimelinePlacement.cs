namespace InkMotion;

public enum VideoClipPlacementMode
{
    Insert,
    Overwrite,
}

public static class VideoTimelinePlacement
{
    private const double Tolerance = 1e-7;

    public static bool TryPlace(
        TimelineTrackProject track, TimelineClipProject sourceClip, int startFrame,
        double frameRate, VideoClipPlacementMode mode)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(sourceClip);
        if (track.Kind != TimelineTrackKind.Video || track.IsLocked || startFrame < 1 ||
            !double.IsFinite(frameRate) || frameRate <= 0 ||
            track.Clips.Any(clip => VideoTimelineEditing.GetDurationSeconds(clip, track.Kind) <= 0))
        {
            return false;
        }

        TimelineTrackProject workingTrack = VideoTimelineEditing.Clone(track);
        TimelineClipProject inserted = VideoTimelineEditing.Clone(sourceClip);
        inserted.StartFrame = startFrame;
        if (workingTrack.Clips.Any(clip => clip.Id == inserted.Id) ||
            VideoTimelineEditing.Validate([workingTrack], frameRate) is not null ||
            VideoTimelineEditing.Validate([new TimelineTrackProject { Kind = TimelineTrackKind.Video, Clips = [inserted] }], frameRate) is not null)
        {
            return false;
        }

        double insertedDuration = VideoTimelineEditing.GetDurationSeconds(inserted, TimelineTrackKind.Video);
        double shiftValue = Math.Ceiling(insertedDuration * frameRate - Tolerance);
        if (!double.IsFinite(shiftValue) || shiftValue < 1 || shiftValue > int.MaxValue - (double)startFrame)
        {
            return false;
        }

        int shiftFrames = (int)shiftValue;
        int endFrame;
        bool changed;
        try
        {
            endFrame = checked(startFrame + shiftFrames);
            changed = mode switch
            {
                VideoClipPlacementMode.Insert => Insert(workingTrack, startFrame, shiftFrames, frameRate),
                VideoClipPlacementMode.Overwrite => Overwrite(workingTrack, startFrame, endFrame, frameRate),
                _ => false,
            };
        }
        catch (OverflowException)
        {
            return false;
        }
        if (!changed)
        {
            return false;
        }

        workingTrack.Clips.Add(inserted);
        VideoTimelineEditing.SortClips(workingTrack);
        if (VideoTimelineEditing.Validate([workingTrack], frameRate) is not null)
        {
            return false;
        }

        Apply(track, workingTrack);
        return true;
    }

    private static bool Insert(TimelineTrackProject track, int startFrame, int shiftFrames, double frameRate)
    {
        double insertionTime = VideoTimelineEditing.FrameToSeconds(startFrame, frameRate);
        foreach (TimelineClipProject clip in track.Clips.ToArray())
        {
            if (clip.StartFrame >= startFrame)
            {
                clip.StartFrame = checked(clip.StartFrame + shiftFrames);
            }
            else if (VideoTimelineEditing.GetEndSeconds(clip, track.Kind, frameRate) > insertionTime + Tolerance)
            {
                TimelineClipProject? right = VideoTimelineEditing.Split(track, clip, startFrame, frameRate);
                if (right is null)
                {
                    return false;
                }
                right.StartFrame = checked(right.StartFrame + shiftFrames);
            }
        }
        return true;
    }

    private static bool Overwrite(TimelineTrackProject track, int startFrame, int endFrame, double frameRate)
    {
        double overwriteStart = VideoTimelineEditing.FrameToSeconds(startFrame, frameRate);
        double overwriteEnd = VideoTimelineEditing.FrameToSeconds(endFrame, frameRate);
        foreach (TimelineClipProject clip in track.Clips.ToArray())
        {
            double clipStart = VideoTimelineEditing.GetStartSeconds(clip, frameRate);
            double clipEnd = VideoTimelineEditing.GetEndSeconds(clip, track.Kind, frameRate);
            if (clipStart >= overwriteEnd - Tolerance || clipEnd <= overwriteStart + Tolerance)
            {
                continue;
            }

            track.Clips.Remove(clip);
            if (clipStart < overwriteStart - Tolerance)
            {
                TimelineClipProject left = VideoTimelineEditing.Clone(clip);
                double sourceBoundary = VideoTimelineEditing.GetSourceBoundarySeconds(
                    clip, overwriteStart, frameRate);
                if (clip.IsReversed)
                {
                    left.SourceInSeconds = sourceBoundary;
                }
                else
                {
                    left.SourceOutSeconds = sourceBoundary;
                }
                VideoTransformKeyframes.TrimEnd(
                    left,
                    (int)Math.Round((overwriteStart - clipStart) * frameRate));
                VideoCropKeyframes.TrimEnd(
                    left,
                    (int)Math.Round((overwriteStart - clipStart) * frameRate));
                VideoClipMask.TrimEnd(left, (int)Math.Round((overwriteStart - clipStart) * frameRate));
                VideoOpacityKeyframes.TrimEnd(left, (int)Math.Round((overwriteStart - clipStart) * frameRate));
                VideoColourGradingKeyframes.TrimEnd(left, (int)Math.Round((overwriteStart - clipStart) * frameRate));
                VideoVignetteKeyframes.TrimEnd(left, (int)Math.Round((overwriteStart - clipStart) * frameRate));
                left.FadeOutSeconds = 0;
                VideoTimelineEditing.ClampFades(left, track.Kind);
                track.Clips.Add(left);
            }

            if (clipEnd > overwriteEnd + Tolerance)
            {
                TimelineClipProject right = VideoTimelineEditing.Clone(clip);
                right.Id = VideoTimelineEditing.NewClipId();
                right.StartFrame = endFrame;
                double sourceBoundary = VideoTimelineEditing.GetSourceBoundarySeconds(
                    clip, overwriteEnd, frameRate);
                if (clip.IsReversed)
                {
                    right.SourceOutSeconds = sourceBoundary;
                }
                else
                {
                    right.SourceInSeconds = sourceBoundary;
                }
                VideoTransformKeyframes.TrimStart(
                    right,
                    (int)Math.Round((overwriteEnd - clipStart) * frameRate));
                VideoCropKeyframes.TrimStart(
                    right,
                    (int)Math.Round((overwriteEnd - clipStart) * frameRate));
                VideoClipMask.TrimStart(right, (int)Math.Round((overwriteEnd - clipStart) * frameRate));
                VideoOpacityKeyframes.TrimStart(right, (int)Math.Round((overwriteEnd - clipStart) * frameRate));
                VideoColourGradingKeyframes.TrimStart(right, (int)Math.Round((overwriteEnd - clipStart) * frameRate));
                VideoVignetteKeyframes.TrimStart(right, (int)Math.Round((overwriteEnd - clipStart) * frameRate));
                right.FadeInSeconds = 0;
                VideoTimelineEditing.ClampFades(right, track.Kind);
                track.Clips.Add(right);
            }
        }
        return true;
    }

    private static void Apply(TimelineTrackProject original, TimelineTrackProject edited)
    {
        var originalById = original.Clips.ToDictionary(clip => clip.Id);
        var result = new List<TimelineClipProject>(edited.Clips.Count);
        foreach (TimelineClipProject candidate in edited.Clips)
        {
            if (originalById.TryGetValue(candidate.Id, out TimelineClipProject? existing))
            {
                existing.StartFrame = candidate.StartFrame;
                existing.SourceInSeconds = candidate.SourceInSeconds;
                existing.SourceOutSeconds = candidate.SourceOutSeconds;
                existing.PlaybackSpeed = candidate.PlaybackSpeed;
                existing.IsReversed = candidate.IsReversed;
                existing.ReverseEmbeddedAudio = candidate.ReverseEmbeddedAudio;
                existing.DurationSeconds = candidate.DurationSeconds;
                existing.FadeInSeconds = candidate.FadeInSeconds;
                existing.FadeOutSeconds = candidate.FadeOutSeconds;
                existing.TransformKeyframes = VideoTransformKeyframes.CloneKeys(candidate.TransformKeyframes);
                existing.CropKeyframes = VideoCropKeyframes.CloneKeys(candidate.CropKeyframes);
                existing.OpacityKeyframes = VideoOpacityKeyframes.CloneKeys(candidate.OpacityKeyframes);
                existing.ColourGradingKeyframes = VideoColourGradingKeyframes.CloneKeys(candidate.ColourGradingKeyframes);
                existing.VignetteKeyframes = VideoVignetteKeyframes.CloneKeys(candidate.VignetteKeyframes);
                result.Add(existing);
            }
            else
            {
                result.Add(candidate);
            }
        }

        original.Clips.Clear();
        original.Clips.AddRange(result);
    }
}
