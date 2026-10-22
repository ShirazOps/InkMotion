namespace InkMotion;

public static class VideoStabilisation
{
    private const int AnalysisWidth = 64;
    private const int AnalysisHeight = 36;
    private const int SearchRadius = 6;
    private const int SmoothingRadius = 4;
    private const int MaximumSamples = 1000;

    public static IReadOnlyList<VideoTransformKeyframe> Analyse(
        TimelineClipProject clip,
        double frameRate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (string.IsNullOrWhiteSpace(clip.SourcePath) || !File.Exists(clip.SourcePath))
        {
            throw new FileNotFoundException("The selected clip source is unavailable.", clip.SourcePath);
        }

        if (!double.IsFinite(frameRate) || frameRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameRate));
        }

        double duration = VideoTimelineEditing.GetDurationSeconds(clip, TimelineTrackKind.Video);
        if (duration <= 0)
        {
            throw new InvalidOperationException("The selected video has no usable source duration.");
        }

        int sampleCount = Math.Clamp((int)Math.Ceiling(duration * 5), 2, MaximumSamples);
        double sampleInterval = duration / (sampleCount - 1);
        using var reader = new MediaFoundationVideoReader(clip.SourcePath);
        var positions = new List<(double Seconds, int X, int Y, double Rotation)>(sampleCount) { (0, 0, 0, 0) };
        byte[]? previous = null;
        int width = 0;
        int height = 0;
        int pathX = 0;
        int pathY = 0;
        double pathRotation = 0;

        for (int sample = 0; sample < sampleCount; sample++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double elapsed = Math.Min(duration, sample * sampleInterval);
            double timelineSeconds = VideoTimelineEditing.GetStartSeconds(clip, frameRate) + elapsed;
            double sourceSeconds = VideoTimelineEditing.GetSourceSeconds(clip, timelineSeconds, frameRate);
            if (!reader.TryGetFrame(sourceSeconds) || reader.Width <= 0 || reader.Height <= 0)
            {
                continue;
            }

            byte[] current = DownsampleLuma(reader.Pixels, reader.Width, reader.Height);
            if (previous is not null && width == reader.Width && height == reader.Height)
            {
                (int x, int y, double rotation) =
                    EstimateMotion(previous, current, AnalysisWidth, AnalysisHeight, SearchRadius);
                pathX += x;
                pathY += y;
                pathRotation += rotation;
                positions.Add((elapsed, pathX, pathY, pathRotation));
            }
            else if (previous is not null)
            {
                pathX = 0;
                pathY = 0;
                positions.Add((elapsed, pathX, pathY, pathRotation));
            }

            previous = current;
            width = reader.Width;
            height = reader.Height;
        }

        if (positions.Count < 2 || width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("The source did not provide enough readable frames for stabilisation.");
        }

        double[] smoothX = Smooth(positions.Select(position => (double)position.X).ToArray());
        double[] smoothY = Smooth(positions.Select(position => (double)position.Y).ToArray());
        double[] smoothRotation = Smooth(positions.Select(position => position.Rotation).ToArray());
        double maximumCorrectionX = 0;
        double maximumCorrectionY = 0;
        for (int i = 0; i < positions.Count; i++)
        {
            double rotationPixels = Math.Abs(smoothRotation[i] - positions[i].Rotation) *
                Math.PI / 180 * Math.Sqrt(Math.Pow(AnalysisWidth / 2d, 2) + Math.Pow(AnalysisHeight / 2d, 2));
            maximumCorrectionX = Math.Max(
                maximumCorrectionX, (Math.Abs(smoothX[i] - positions[i].X) + rotationPixels) / AnalysisWidth);
            maximumCorrectionY = Math.Max(
                maximumCorrectionY, (Math.Abs(smoothY[i] - positions[i].Y) + rotationPixels) / AnalysisHeight);
        }

        double zoom = Math.Min(1.25, 1 / Math.Max(0.8, 1 - (2 * Math.Max(maximumCorrectionX, maximumCorrectionY))));
        int clipFrameCount = Math.Max(1, (int)Math.Ceiling(duration * frameRate));
        var keyframes = new List<VideoTransformKeyframe>(positions.Count);
        for (int i = 0; i < positions.Count; i++)
        {
            double offsetX = (smoothX[i] - positions[i].X) / AnalysisWidth;
            double offsetY = (smoothY[i] - positions[i].Y) / AnalysisHeight;
            int frameOffset = Math.Clamp((int)Math.Round(positions[i].Seconds * frameRate), 0, clipFrameCount - 1);
            VideoTransformValue baseline = VideoTransformKeyframes.GetValue(clip, frameOffset);
            keyframes.Add(new VideoTransformKeyframe
            {
                FrameOffset = frameOffset,
                X = Math.Clamp(baseline.X + offsetX, -1, 2),
                Y = Math.Clamp(baseline.Y + offsetY, -1, 2),
                Scale = Math.Min(VideoTimelineEditing.MaximumScale, baseline.Scale * zoom),
                RotationDegrees = Math.Clamp(
                    baseline.RotationDegrees + smoothRotation[i] - positions[i].Rotation, -180, 180),
            });
        }

        return keyframes
            .GroupBy(keyframe => keyframe.FrameOffset)
            .Select(group => group.Last())
            .OrderBy(keyframe => keyframe.FrameOffset)
            .ToArray();
    }

    public static (int X, int Y) EstimateTranslation(
        IReadOnlyList<byte> previous,
        IReadOnlyList<byte> current,
        int width,
        int height,
        int searchRadius = SearchRadius)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        if (width <= 0 || height <= 0 || previous.Count != width * height || current.Count != width * height ||
            searchRadius < 0 || searchRadius >= Math.Min(width, height) / 2)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Frames or search radius are invalid.");
        }

        if (GetVariance(previous) < 16 && GetVariance(current) < 16)
        {
            return (0, 0);
        }

        long bestError = long.MaxValue;
        int bestX = 0;
        int bestY = 0;
        for (int dy = -searchRadius; dy <= searchRadius; dy++)
        {
            for (int dx = -searchRadius; dx <= searchRadius; dx++)
            {
                long error = 0;
                int count = 0;
                for (int y = searchRadius; y < height - searchRadius; y += 2)
                {
                    int previousY = y - dy;
                    for (int x = searchRadius; x < width - searchRadius; x += 2)
                    {
                        int previousX = x - dx;
                        error += Math.Abs(previous[previousY * width + previousX] - current[y * width + x]);
                        count++;
                    }
                }

                if (count > 0 && error < bestError)
                {
                    bestError = error;
                    bestX = dx;
                    bestY = dy;
                }
            }
        }

        return (bestX, bestY);
    }

    public static (int X, int Y, double RotationDegrees) EstimateMotion(
        IReadOnlyList<byte> previous,
        IReadOnlyList<byte> current,
        int width,
        int height,
        int searchRadius = SearchRadius)
    {
        (int x, int y) = EstimateTranslation(previous, current, width, height, searchRadius);
        double bestRotation = 0;
        double bestError = GetAlignmentError(previous, current, width, height, x, y, 0);
        for (int rotationIndex = -8; rotationIndex <= 8; rotationIndex++)
        {
            if (rotationIndex == 0)
            {
                continue;
            }

            double degrees = rotationIndex * 0.5;
            double error = GetAlignmentError(previous, current, width, height, x, y, degrees);
            if (error < bestError)
            {
                bestError = error;
                bestRotation = degrees;
            }
        }

        return (x, y, bestRotation);
    }

    private static byte[] DownsampleLuma(byte[] bgra, int width, int height)
    {
        var result = new byte[AnalysisWidth * AnalysisHeight];
        for (int y = 0; y < AnalysisHeight; y++)
        {
            int sourceY = Math.Min(height - 1, (int)((y + 0.5) * height / AnalysisHeight));
            for (int x = 0; x < AnalysisWidth; x++)
            {
                int sourceX = Math.Min(width - 1, (int)((x + 0.5) * width / AnalysisWidth));
                int offset = (sourceY * width + sourceX) * 4;
                result[y * AnalysisWidth + x] = (byte)((bgra[offset] * 29 + bgra[offset + 1] * 150 +
                    bgra[offset + 2] * 77) >> 8);
            }
        }

        return result;
    }

    private static double[] Smooth(double[] values)
    {
        var result = new double[values.Length];
        for (int index = 0; index < values.Length; index++)
        {
            int start = Math.Max(0, index - SmoothingRadius);
            int end = Math.Min(values.Length - 1, index + SmoothingRadius);
            double total = 0;
            for (int sample = start; sample <= end; sample++)
            {
                total += values[sample];
            }

            result[index] = total / (end - start + 1);
        }

        return result;
    }

    private static double GetVariance(IReadOnlyList<byte> values)
    {
        double mean = values.Average(value => (double)value);
        return values.Average(value => Math.Pow(value - mean, 2));
    }

    private static double GetAlignmentError(
        IReadOnlyList<byte> previous,
        IReadOnlyList<byte> current,
        int width,
        int height,
        int translationX,
        int translationY,
        double rotationDegrees)
    {
        double radians = rotationDegrees * Math.PI / 180;
        double cosine = Math.Cos(radians);
        double sine = Math.Sin(radians);
        double centreX = (width - 1) / 2d;
        double centreY = (height - 1) / 2d;
        long error = 0;
        int count = 0;
        for (int y = SearchRadius + 2; y < height - SearchRadius - 2; y += 2)
        {
            for (int x = SearchRadius + 2; x < width - SearchRadius - 2; x += 2)
            {
                int sourceX = (int)Math.Round(
                    centreX + (cosine * (x - centreX)) + (sine * (y - centreY)) - translationX);
                int sourceY = (int)Math.Round(
                    centreY - (sine * (x - centreX)) + (cosine * (y - centreY)) - translationY);
                if ((uint)sourceX >= (uint)width || (uint)sourceY >= (uint)height)
                {
                    continue;
                }

                error += Math.Abs(previous[sourceY * width + sourceX] - current[y * width + x]);
                count++;
            }
        }

        return count == 0 ? double.MaxValue : (double)error / count;
    }
}
