namespace InkMotion;

public static class VideoMaskTracking
{
    private const int PatchSize = 9;
    private const int SamplesPerSecond = 5;
    private const int MaximumSamples = 1000;

    public static IReadOnlyList<VideoMaskKeyframe> Analyse(
        TimelineClipProject clip,
        double frameRate,
        int startFrameOffset,
        VideoMaskValue initialMask,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (string.IsNullOrWhiteSpace(clip.SourcePath) || !File.Exists(clip.SourcePath))
        {
            throw new FileNotFoundException("The selected clip source is unavailable.", clip.SourcePath);
        }

        if (!double.IsFinite(frameRate) || frameRate <= 0 || startFrameOffset < 0 ||
            !double.IsFinite(initialMask.X) || !double.IsFinite(initialMask.Y) ||
            !double.IsFinite(initialMask.Width) || !double.IsFinite(initialMask.Height) ||
            initialMask.X is < 0 or > 1 || initialMask.Y is < 0 or > 1 ||
            initialMask.Width is <= 0 or > 1 || initialMask.Height is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(startFrameOffset));
        }

        double duration = VideoTimelineEditing.GetDurationSeconds(clip, TimelineTrackKind.Video);
        int lastFrameOffset = Math.Max(0, (int)Math.Ceiling(duration * frameRate) - 1);
        if (startFrameOffset > lastFrameOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(startFrameOffset), "The playhead is outside the selected clip.");
        }

        int interval = Math.Max(
            Math.Max(1, (int)Math.Round(frameRate / SamplesPerSecond)),
            (int)Math.Ceiling(frameRate * duration / MaximumSamples));
        using var reader = new MediaFoundationVideoReader(clip.SourcePath);
        var result = new List<VideoMaskKeyframe>();
        byte[]? template = null;
        int centreX = 0;
        int centreY = 0;
        int searchRadius = 0;
        int width = 0;
        int height = 0;
        var offsets = new List<int>();
        for (int offset = startFrameOffset; offset <= lastFrameOffset; offset += interval)
        {
            offsets.Add(offset);
        }
        if (offsets[^1] != lastFrameOffset)
        {
            offsets.Add(lastFrameOffset);
        }

        foreach (int offset in offsets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double timelineSeconds = VideoTimelineEditing.GetStartSeconds(clip, frameRate) + offset / frameRate;
            double sourceSeconds = VideoTimelineEditing.GetSourceSeconds(clip, timelineSeconds, frameRate);
            if (!reader.TryGetFrame(sourceSeconds) || reader.Width < PatchSize || reader.Height < PatchSize)
            {
                throw new InvalidDataException($"Could not decode a frame for mask tracking at clip frame {offset}.");
            }

            byte[] luma = ToLuma(reader.Pixels, reader.Width, reader.Height);
            if (template is null)
            {
                width = reader.Width;
                height = reader.Height;
                centreX = Math.Clamp((int)Math.Round(initialMask.X * (width - 1)), PatchSize / 2, width - PatchSize / 2 - 1);
                centreY = Math.Clamp((int)Math.Round(initialMask.Y * (height - 1)), PatchSize / 2, height - PatchSize / 2 - 1);
                template = ReadPatch(luma, width, centreX, centreY);
                if (GetVariance(template) < 24)
                {
                    throw new InvalidOperationException("Choose a point with visible detail; a featureless area cannot be tracked reliably.");
                }

                searchRadius = Math.Clamp(Math.Min(width, height) / 48, 4, 32);
            }
            else
            {
                if (reader.Width != width || reader.Height != height)
                {
                    throw new InvalidDataException("The video dimensions changed during mask tracking.");
                }

                (int trackedX, int trackedY, double error) = TrackPoint(
                    template, luma, width, height, centreX, centreY, searchRadius);
                centreX = trackedX;
                centreY = trackedY;
                if (error > 72)
                {
                    throw new InvalidOperationException(
                        $"Tracking lost the selected feature near clip frame {offset}; no later keys were written.");
                }
            }

            result.Add(new VideoMaskKeyframe
            {
                FrameOffset = offset,
                X = (double)centreX / (width - 1),
                Y = (double)centreY / (height - 1),
                Width = initialMask.Width,
                Height = initialMask.Height,
                RotationDegrees = initialMask.RotationDegrees,
            });
        }

        if (result.Count < 2)
        {
            throw new InvalidOperationException("The selected clip is too short to track beyond the playhead.");
        }

        return result;
    }

    public static (int X, int Y, double Error) TrackPoint(
        IReadOnlyList<byte> template,
        IReadOnlyList<byte> current,
        int width,
        int height,
        int centreX,
        int centreY,
        int searchRadius)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(current);
        if (template.Count != PatchSize * PatchSize || current.Count != width * height ||
            width < PatchSize || height < PatchSize || searchRadius < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Tracking frames or patch dimensions are invalid.");
        }

        double bestError = double.MaxValue;
        int bestX = centreX;
        int bestY = centreY;
        int half = PatchSize / 2;
        int minX = Math.Max(half, centreX - searchRadius);
        int maxX = Math.Min(width - half - 1, centreX + searchRadius);
        int minY = Math.Max(half, centreY - searchRadius);
        int maxY = Math.Min(height - half - 1, centreY + searchRadius);
        double templateMean = 0;
        int templateSampleCount = 0;
        for (int patchY = 0; patchY < PatchSize; patchY += 2)
        {
            for (int patchX = 0; patchX < PatchSize; patchX += 2)
            {
                templateMean += template[(patchY * PatchSize) + patchX];
                templateSampleCount++;
            }
        }
        templateMean /= templateSampleCount;
        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                double candidateMean = 0;
                int sampleCount = 0;
                for (int patchY = 0; patchY < PatchSize; patchY += 2)
                {
                    for (int patchX = 0; patchX < PatchSize; patchX += 2)
                    {
                        int sourceIndex = ((y + patchY - half) * width) + x + patchX - half;
                        candidateMean += current[sourceIndex];
                        sampleCount++;
                    }
                }

                candidateMean /= sampleCount;
                double error = 0;
                for (int patchY = 0; patchY < PatchSize; patchY += 2)
                {
                    for (int patchX = 0; patchX < PatchSize; patchX += 2)
                    {
                        int patchIndex = (patchY * PatchSize) + patchX;
                        int sourceIndex = ((y + patchY - half) * width) + x + patchX - half;
                        error += Math.Abs(
                            (template[patchIndex] - templateMean) -
                            (current[sourceIndex] - candidateMean));
                    }
                }

                double meanError = error / sampleCount;
                if (meanError < bestError)
                {
                    bestError = meanError;
                    bestX = x;
                    bestY = y;
                }
            }
        }

        return (bestX, bestY, bestError);
    }

    private static byte[] ToLuma(byte[] bgra, int width, int height)
    {
        var result = new byte[width * height];
        for (int index = 0, pixel = 0; pixel < result.Length; pixel++, index += 4)
        {
            result[pixel] = (byte)((bgra[index] * 29 + bgra[index + 1] * 150 + bgra[index + 2] * 77) >> 8);
        }

        return result;
    }

    private static byte[] ReadPatch(IReadOnlyList<byte> luma, int width, int centreX, int centreY)
    {
        int half = PatchSize / 2;
        var patch = new byte[PatchSize * PatchSize];
        for (int y = 0; y < PatchSize; y++)
        {
            for (int x = 0; x < PatchSize; x++)
            {
                patch[y * PatchSize + x] = luma[((centreY + y - half) * width) + centreX + x - half];
            }
        }

        return patch;
    }

    private static double GetVariance(IReadOnlyList<byte> values)
    {
        double mean = values.Average(value => (double)value);
        return values.Average(value => Math.Pow(value - mean, 2));
    }
}
