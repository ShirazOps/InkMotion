namespace InkMotion;

/// <summary>A speed-ramp point at a normalised position (0 = clip start, 1 = clip end) along the clip's timeline length.</summary>
public sealed record VideoSpeedRampPoint(double Position, double Speed);

/// <summary>
/// Animated playback speed for video clips. Speed is interpolated linearly between points and held constant
/// before the first point and after the last. Points are positioned along the clip's timeline length, so the
/// ramp stretches with the clip. While a ramp is present, <see cref="TimelineClipProject.PlaybackSpeed"/> holds
/// its average speed, which keeps the clip's timeline length equal to its source range divided by that average.
/// Outside the clip, mappings extrapolate with the first and last speeds so transition handles stay continuous.
/// </summary>
public static class VideoSpeedRamp
{
    public const int MaximumPoints = 64;
    private const double PositionTolerance = 1e-9;
    private const double SpeedTolerance = 1e-9;
    private const double AverageTolerance = 1e-6;

    public static bool IsRamped(TimelineClipProject clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return clip.SpeedRamp is { Count: > 0 };
    }

    public static double GetAverageSpeed(IReadOnlyList<VideoSpeedRampPoint> points) => Integrate(points, 1);

    public static double GetSpeedAt(IReadOnlyList<VideoSpeedRampPoint> points, double position)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
        {
            throw new ArgumentException("A speed ramp needs at least one point.", nameof(points));
        }

        if (position <= points[0].Position)
        {
            return points[0].Speed;
        }

        for (int index = 1; index < points.Count; index++)
        {
            VideoSpeedRampPoint previous = points[index - 1];
            VideoSpeedRampPoint next = points[index];
            if (position <= next.Position)
            {
                return Lerp(previous, next, position);
            }
        }

        return points[^1].Speed;
    }

    /// <summary>Integral of the speed curve from position 0 to <paramref name="position"/>, clamped to the clip.</summary>
    public static double Integrate(IReadOnlyList<VideoSpeedRampPoint> points, double position)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
        {
            throw new ArgumentException("A speed ramp needs at least one point.", nameof(points));
        }

        position = Math.Clamp(position, 0, 1);
        double total = 0;
        double previousPosition = 0;
        double previousSpeed = points[0].Speed;
        foreach (VideoSpeedRampPoint point in points)
        {
            if (point.Position <= previousPosition)
            {
                previousSpeed = point.Speed;
                continue;
            }

            if (position <= point.Position)
            {
                double speed = previousSpeed +
                    ((point.Speed - previousSpeed) * (position - previousPosition) / (point.Position - previousPosition));
                return total + ((position - previousPosition) * (previousSpeed + speed) / 2);
            }

            total += (point.Position - previousPosition) * (previousSpeed + point.Speed) / 2;
            previousPosition = point.Position;
            previousSpeed = point.Speed;
        }

        return total + ((position - previousPosition) * previousSpeed);
    }

    /// <summary>Inverse of <see cref="Integrate"/> for an integral between 0 and the average speed.</summary>
    public static double InverseIntegrate(IReadOnlyList<VideoSpeedRampPoint> points, double integral)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
        {
            throw new ArgumentException("A speed ramp needs at least one point.", nameof(points));
        }

        if (integral <= 0)
        {
            return 0;
        }

        double remaining = integral;
        double previousPosition = 0;
        double previousSpeed = points[0].Speed;
        foreach (VideoSpeedRampPoint point in points)
        {
            if (point.Position <= previousPosition)
            {
                previousSpeed = point.Speed;
                continue;
            }

            double width = point.Position - previousPosition;
            double area = width * (previousSpeed + point.Speed) / 2;
            if (remaining <= area)
            {
                return Math.Min(1, previousPosition + SolveSegment(previousSpeed, point.Speed, width, remaining));
            }

            remaining -= area;
            previousPosition = point.Position;
            previousSpeed = point.Speed;
        }

        return Math.Min(1, previousPosition + (remaining / previousSpeed));
    }

    /// <summary>Source time advanced since the clip's leading source edge after <paramref name="timelineElapsed"/> seconds.</summary>
    public static double GetSourceElapsed(TimelineClipProject clip, double timelineElapsed)
    {
        ArgumentNullException.ThrowIfNull(clip);
        double duration = VideoTimelineEditing.GetDurationSeconds(clip, TimelineTrackKind.Video);
        if (!IsRamped(clip) || duration <= 0)
        {
            return timelineElapsed * clip.PlaybackSpeed;
        }

        List<VideoSpeedRampPoint> points = clip.SpeedRamp;
        if (timelineElapsed <= 0)
        {
            return timelineElapsed * points[0].Speed;
        }

        if (timelineElapsed >= duration)
        {
            return (duration * Integrate(points, 1)) + ((timelineElapsed - duration) * points[^1].Speed);
        }

        return duration * Integrate(points, timelineElapsed / duration);
    }

    /// <summary>Timeline time since the clip start at which <paramref name="sourceElapsed"/> source seconds have played.</summary>
    public static double GetTimelineElapsed(TimelineClipProject clip, double sourceElapsed)
    {
        ArgumentNullException.ThrowIfNull(clip);
        double duration = VideoTimelineEditing.GetDurationSeconds(clip, TimelineTrackKind.Video);
        if (!IsRamped(clip) || duration <= 0)
        {
            return sourceElapsed / clip.PlaybackSpeed;
        }

        List<VideoSpeedRampPoint> points = clip.SpeedRamp;
        if (sourceElapsed <= 0)
        {
            return sourceElapsed / points[0].Speed;
        }

        double total = duration * Integrate(points, 1);
        if (sourceElapsed >= total)
        {
            return duration + ((sourceElapsed - total) / points[^1].Speed);
        }

        return duration * InverseIntegrate(points, sourceElapsed / duration);
    }

    /// <summary>Instantaneous playback speed at a timeline time since the clip start.</summary>
    public static double GetSpeedAtElapsed(TimelineClipProject clip, double timelineElapsed)
    {
        ArgumentNullException.ThrowIfNull(clip);
        double duration = VideoTimelineEditing.GetDurationSeconds(clip, TimelineTrackKind.Video);
        return !IsRamped(clip) || duration <= 0
            ? clip.PlaybackSpeed
            : GetSpeedAt(clip.SpeedRamp, timelineElapsed / duration);
    }

    public static double GetStartSpeed(TimelineClipProject clip) =>
        IsRamped(clip) ? clip.SpeedRamp[0].Speed : clip.PlaybackSpeed;

    public static double GetEndSpeed(TimelineClipProject clip) =>
        IsRamped(clip) ? clip.SpeedRamp[^1].Speed : clip.PlaybackSpeed;

    /// <summary>
    /// Captures the part of a clip's ramp between two timeline times since its start, rescaled to a full clip.
    /// Returns null for clips without a ramp. Capture before changing the clip's source range.
    /// </summary>
    public static List<VideoSpeedRampPoint>? Slice(TimelineClipProject clip, double startElapsed, double endElapsed)
    {
        ArgumentNullException.ThrowIfNull(clip);
        double duration = VideoTimelineEditing.GetDurationSeconds(clip, TimelineTrackKind.Video);
        if (!IsRamped(clip) || duration <= 0)
        {
            return null;
        }

        double start = Math.Clamp(startElapsed / duration, 0, 1);
        double end = Math.Clamp(endElapsed / duration, 0, 1);
        if (end - start <= PositionTolerance)
        {
            return [new VideoSpeedRampPoint(0, GetSpeedAt(clip.SpeedRamp, start))];
        }

        var result = new List<VideoSpeedRampPoint> { new(0, GetSpeedAt(clip.SpeedRamp, start)) };
        foreach (VideoSpeedRampPoint point in clip.SpeedRamp)
        {
            if (point.Position > start + PositionTolerance && point.Position < end - PositionTolerance)
            {
                result.Add(new VideoSpeedRampPoint((point.Position - start) / (end - start), point.Speed));
            }
        }

        result.Add(new VideoSpeedRampPoint(1, GetSpeedAt(clip.SpeedRamp, end)));
        return result;
    }

    /// <summary>
    /// Stores a ramp and sets the clip's average speed. A ramp whose points share one speed becomes a constant
    /// speed; an empty list clears the ramp and keeps the current average. Returns false for invalid points.
    /// </summary>
    public static bool Apply(TimelineClipProject clip, IReadOnlyList<VideoSpeedRampPoint>? points)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (points is null)
        {
            return true;
        }

        if (points.Count == 0)
        {
            clip.SpeedRamp = [];
            return true;
        }

        if (ValidatePoints(points) is not null)
        {
            return false;
        }

        double first = points[0].Speed;
        if (points.All(point => Math.Abs(point.Speed - first) <= SpeedTolerance))
        {
            clip.SpeedRamp = [];
            clip.PlaybackSpeed = first;
            return true;
        }

        clip.SpeedRamp = points.ToList();
        clip.PlaybackSpeed = GetAverageSpeed(points);
        return true;
    }

    /// <summary>Adds or replaces a point at a normalised position, starting from the clip's constant speed if needed.</summary>
    public static List<VideoSpeedRampPoint> WithPoint(TimelineClipProject clip, double position, double speed, double minimumSpacing)
    {
        ArgumentNullException.ThrowIfNull(clip);
        position = Math.Clamp(position, 0, 1);
        List<VideoSpeedRampPoint> points = IsRamped(clip)
            ? clip.SpeedRamp.ToList()
            : [new VideoSpeedRampPoint(0, clip.PlaybackSpeed), new VideoSpeedRampPoint(1, clip.PlaybackSpeed)];
        double spacing = Math.Max(PositionTolerance, minimumSpacing);
        points.RemoveAll(point => Math.Abs(point.Position - position) < spacing);
        points.Add(new VideoSpeedRampPoint(position, speed));
        points.Sort((first, second) => first.Position.CompareTo(second.Position));
        return points;
    }

    /// <summary>
    /// Lengthens a ramp by holding its first speed for <paramref name="addedBefore"/> seconds and its last speed for
    /// <paramref name="addedAfter"/> seconds, keeping existing points at the same timeline times.
    /// </summary>
    public static List<VideoSpeedRampPoint> Extend(TimelineClipProject clip, double addedBefore, double addedAfter)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!IsRamped(clip))
        {
            throw new InvalidOperationException("Only a ramped clip can be extended.");
        }

        double duration = VideoTimelineEditing.GetDurationSeconds(clip, TimelineTrackKind.Video);
        addedBefore = Math.Max(0, addedBefore);
        addedAfter = Math.Max(0, addedAfter);
        double newDuration = duration + addedBefore + addedAfter;
        var points = new List<VideoSpeedRampPoint>();
        if (addedBefore > 0)
        {
            points.Add(new VideoSpeedRampPoint(0, clip.SpeedRamp[0].Speed));
        }

        points.AddRange(clip.SpeedRamp.Select(point =>
            point with { Position = (addedBefore + (point.Position * duration)) / newDuration }));
        if (addedAfter > 0)
        {
            points.Add(new VideoSpeedRampPoint(1, clip.SpeedRamp[^1].Speed));
        }

        return points;
    }

    /// <summary>Scales every ramp point so the ramp has a new average speed, or returns null if a point leaves the range.</summary>
    public static List<VideoSpeedRampPoint>? Rescale(IReadOnlyList<VideoSpeedRampPoint> points, double averageSpeed)
    {
        ArgumentNullException.ThrowIfNull(points);
        double factor = averageSpeed / GetAverageSpeed(points);
        List<VideoSpeedRampPoint> scaled = points
            .Select(point => point with { Speed = point.Speed * factor })
            .ToList();
        return ValidatePoints(scaled) is null ? scaled : null;
    }

    public static string? ValidatePoints(IReadOnlyList<VideoSpeedRampPoint>? points)
    {
        if (points is null || points.Count == 0)
        {
            return null;
        }

        if (points.Count > MaximumPoints)
        {
            return $"A speed ramp can have at most {MaximumPoints} points.";
        }

        double previous = double.NegativeInfinity;
        foreach (VideoSpeedRampPoint? point in points)
        {
            if (point is null || !double.IsFinite(point.Position) || point.Position is < 0 or > 1 ||
                point.Position <= previous ||
                !double.IsFinite(point.Speed) ||
                point.Speed < VideoTimelineEditing.MinimumPlaybackSpeed - SpeedTolerance ||
                point.Speed > VideoTimelineEditing.MaximumPlaybackSpeed + SpeedTolerance)
            {
                return "Speed-ramp points need increasing positions between 0 and 1 and speeds from 0.25x to 4x.";
            }

            previous = point.Position;
        }

        return null;
    }

    public static bool IsValid(TimelineClipProject clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.SpeedRamp is not { Count: > 0 } points)
        {
            return true;
        }

        return ValidatePoints(points) is null &&
            Math.Abs(clip.PlaybackSpeed - GetAverageSpeed(points)) <= AverageTolerance * Math.Max(1, clip.PlaybackSpeed);
    }

    public static string Describe(TimelineClipProject clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!IsRamped(clip))
        {
            return "Constant speed. Set a ramp point at the playhead to animate speed.";
        }

        double minimum = clip.SpeedRamp.Min(point => point.Speed);
        double maximum = clip.SpeedRamp.Max(point => point.Speed);
        return $"Speed ramp: {clip.SpeedRamp.Count} points, {minimum:0.##}x–{maximum:0.##}x (average {clip.PlaybackSpeed:0.##}x).";
    }

    public static string Signature(TimelineClipProject clip) =>
        IsRamped(clip)
            ? string.Join(';', clip.SpeedRamp.Select(point => $"{point.Position:R}:{point.Speed:R}"))
            : string.Empty;

    private static double Lerp(VideoSpeedRampPoint previous, VideoSpeedRampPoint next, double position)
    {
        double width = next.Position - previous.Position;
        return width <= 0
            ? next.Speed
            : previous.Speed + ((next.Speed - previous.Speed) * (position - previous.Position) / width);
    }

    private static double SolveSegment(double startSpeed, double endSpeed, double width, double area)
    {
        double slope = (endSpeed - startSpeed) / width;
        // Solves startSpeed * x + slope * x² / 2 = area in a form that stays stable as the slope approaches zero.
        double discriminant = Math.Max(0, (startSpeed * startSpeed) + (2 * slope * area));
        return Math.Clamp(2 * area / (startSpeed + Math.Sqrt(discriminant)), 0, width);
    }
}
