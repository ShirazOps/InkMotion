namespace InkMotion;

public static class VideoClipJoining
{
    public static TimelineClipProject Join(TimelineTrackProject track, TimelineClipProject left,
        TimelineClipProject right, double frameRate)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (!double.IsFinite(frameRate) || frameRate <= 0 ||
            track.Kind != TimelineTrackKind.Video || track.IsLocked ||
            ReferenceEquals(left, right) || !track.Clips.Contains(left) || !track.Clips.Contains(right))
        {
            throw new InvalidOperationException("Select two different video clips on the same unlocked video track.");
        }

        if (VideoTimelineEditing.Validate([track], frameRate) is string error)
        {
            throw new InvalidOperationException($"Cannot join an invalid video track: {error}");
        }

        TimelineClipProject[] ordered = track.Clips.OrderBy(clip => clip.StartFrame).ToArray();
        int index = Array.IndexOf(ordered, left);
        if (index + 1 >= ordered.Length || !ReferenceEquals(ordered[index + 1], right))
        {
            throw new InvalidOperationException("Join requires the immediately following clip on this track.");
        }

        if (!string.Equals(Path.GetFullPath(left.SourcePath!), Path.GetFullPath(right.SourcePath!),
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only consecutive portions of the same video source can be joined without rendering.");
        }

        double leftEnd = left.SourceOutSeconds ?? left.SourceDurationSeconds;
        double rightEnd = right.SourceOutSeconds ?? right.SourceDurationSeconds;
        double leftDuration = VideoTimelineEditing.GetDurationSeconds(left, track.Kind);
        double rightDuration = VideoTimelineEditing.GetDurationSeconds(right, track.Kind);
        if (left.SourceDurationSeconds <= 0 || right.SourceDurationSeconds <= 0 ||
            left.SourceDurationSeconds != right.SourceDurationSeconds ||
            left.PlaybackSpeed != right.PlaybackSpeed || left.IsReversed != right.IsReversed ||
            left.ReverseEmbeddedAudio != right.ReverseEmbeddedAudio ||
            (left.IsReversed
                ? Math.Abs(left.SourceInSeconds - rightEnd) > 1e-7
                : Math.Abs(leftEnd - right.SourceInSeconds) > 1e-7) ||
            Math.Abs(VideoTimelineEditing.GetEndSeconds(left, track.Kind, frameRate) -
                VideoTimelineEditing.GetStartSeconds(right, frameRate)) > 1e-7)
        {
            throw new InvalidOperationException("Clips must have matching probed source lengths and touch in timeline and source order, with no gap, overlap or repeated section.");
        }

        if (left.FadeOutSeconds != 0 || right.FadeInSeconds != 0 ||
            left.FadeInSeconds > leftDuration || right.FadeOutSeconds > rightDuration ||
            right.TransitionInSeconds > 0)
        {
            throw new InvalidOperationException("Remove transitions or fades at the join boundary and keep outer fades within their original clips before joining.");
        }

        if (left.TransformKeyframes.Count != 0 || right.TransformKeyframes.Count != 0)
        {
            throw new InvalidOperationException("Remove transform keyframes before joining; keyframed motion cannot be combined without changing its timing.");
        }

        if (left.CropKeyframes.Count != 0 || right.CropKeyframes.Count != 0)
        {
            throw new InvalidOperationException("Remove crop keyframes before joining; animated crop cannot be combined without changing its timing.");
        }

        if (left.OpacityKeyframes.Count != 0 || right.OpacityKeyframes.Count != 0)
        {
            throw new InvalidOperationException("Remove opacity keyframes before joining; animated opacity cannot be combined without changing its timing.");
        }

        if (left.ColourGradingKeyframes.Count != 0 || right.ColourGradingKeyframes.Count != 0)
        {
            throw new InvalidOperationException("Remove colour grading keyframes before joining; animated grading cannot be combined without changing its timing.");
        }

        if (left.VignetteKeyframes.Count != 0 || right.VignetteKeyframes.Count != 0)
        {
            throw new InvalidOperationException("Remove vignette keyframes before joining; animated vignette cannot be combined without changing its timing.");
        }

        if (left.BlurKeyframes.Count != 0 || right.BlurKeyframes.Count != 0)
        {
            throw new InvalidOperationException("Remove blur keyframes before joining; animated blur cannot be combined without changing its timing.");
        }

        if (left.SharpenKeyframes.Count != 0 || right.SharpenKeyframes.Count != 0)
        {
            throw new InvalidOperationException("Remove sharpen keyframes before joining; animated sharpening cannot be combined without changing its timing.");
        }

        if (left.ChromaKeyKeyframes.Count != 0 || right.ChromaKeyKeyframes.Count != 0)
        {
            throw new InvalidOperationException("Remove chroma-key keyframes before joining; animated tolerance cannot be combined without changing its timing.");
        }

        if (left.MaskFeatherKeyframes.Count != 0 || right.MaskFeatherKeyframes.Count != 0)
        {
            throw new InvalidOperationException("Remove mask feather keyframes before joining; animated feather cannot be combined without changing its timing.");
        }

        if (left.Opacity != right.Opacity || left.Volume != right.Volume ||
            left.X != right.X || left.Y != right.Y || left.Scale != right.Scale || left.RotationDegrees != right.RotationDegrees ||
            left.CropLeftPercent != right.CropLeftPercent || left.CropTopPercent != right.CropTopPercent ||
            left.CropRightPercent != right.CropRightPercent || left.CropBottomPercent != right.CropBottomPercent ||
            left.Exposure != right.Exposure || left.Contrast != right.Contrast ||
            left.WhiteBalanceTemperature != right.WhiteBalanceTemperature || left.WhiteBalanceTint != right.WhiteBalanceTint ||
            left.Saturation != right.Saturation ||
            !left.MasterCurve.SequenceEqual(right.MasterCurve) ||
            !left.RedCurve.SequenceEqual(right.RedCurve) ||
            !left.GreenCurve.SequenceEqual(right.GreenCurve) ||
            !left.BlueCurve.SequenceEqual(right.BlueCurve) ||
            !string.Equals(left.ColourLutPath, right.ColourLutPath, StringComparison.OrdinalIgnoreCase) ||
            left.ChromaKeyEnabled != right.ChromaKeyEnabled ||
            left.ChromaKeyColour != right.ChromaKeyColour ||
            left.ChromaKeyTolerance != right.ChromaKeyTolerance ||
            left.ChromaKeyFeather != right.ChromaKeyFeather ||
            left.ChromaKeySpillSuppression != right.ChromaKeySpillSuppression ||
            left.BlurAmount != right.BlurAmount ||
            left.SharpenAmount != right.SharpenAmount ||
            left.VignetteAmount != right.VignetteAmount ||
            left.MaskShape != right.MaskShape ||
            left.MaskInverted != right.MaskInverted || left.MaskFeather != right.MaskFeather ||
            left.UseProxy != right.UseProxy ||
            left.MaskX != right.MaskX || left.MaskY != right.MaskY ||
            left.MaskWidth != right.MaskWidth || left.MaskHeight != right.MaskHeight ||
            !left.MaskKeyframes.SequenceEqual(right.MaskKeyframes) ||
            left.Text != right.Text || left.FontFamily != right.FontFamily ||
            left.FontSize != right.FontSize || left.Colour != right.Colour)
        {
            throw new InvalidOperationException("Match the clip opacity, volume, transform, colour, text and styling before joining; no clip properties will be discarded.");
        }

        TimelineClipProject joined = VideoTimelineEditing.Clone(left);
        if (left.IsReversed)
        {
            joined.SourceInSeconds = right.SourceInSeconds;
        }
        else
        {
            joined.SourceOutSeconds = rightEnd;
        }
        joined.FadeOutSeconds = right.FadeOutSeconds;
        joined.RollingShutterProfile = left.RollingShutterProfile
            .Concat(right.RollingShutterProfile)
            .GroupBy(keyframe => keyframe.SourceSeconds)
            .Select(group => group.Last())
            .OrderBy(keyframe => keyframe.SourceSeconds)
            .ToList();
        return joined;
    }
}
