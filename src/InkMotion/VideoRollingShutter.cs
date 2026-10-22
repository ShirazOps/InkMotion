namespace InkMotion;

public sealed class VideoRollingShutterOffset
{
    public double X { get; set; }
    public double Y { get; set; }
}

public sealed class VideoRollingShutterKeyframe
{
    public double SourceSeconds { get; set; }
    public List<VideoRollingShutterOffset> Rows { get; set; } = [];
}

public static class VideoRollingShutter
{
    public const int AnalysisWidth = 64;
    public const int AnalysisHeight = 36;
    public const int RowBands = 6;
    public const double MaximumCorrection = 0.25;
    private const int SearchRadius = 2;
    private const int MaximumSamples = 1000;
    private const int MaximumProfileSamples = 10000;
    private const int SmoothingRadius = 4;

    public static List<VideoRollingShutterKeyframe> CloneProfile(
        IEnumerable<VideoRollingShutterKeyframe>? profile) =>
        profile?.Select(keyframe => new VideoRollingShutterKeyframe
        {
            SourceSeconds = keyframe.SourceSeconds,
            Rows = keyframe.Rows.Select(row => new VideoRollingShutterOffset { X = row.X, Y = row.Y }).ToList(),
        }).ToList() ?? [];

    public static IReadOnlyList<VideoRollingShutterKeyframe> Analyse(
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
        if (!double.IsFinite(duration) || duration <= 0)
        {
            throw new InvalidOperationException("The selected video has no usable source duration.");
        }

        int sampleCount = (int)Math.Clamp(Math.Ceiling(duration * 5) + 1, 2, MaximumSamples);
        using var reader = new MediaFoundationVideoReader(clip.SourcePath);
        byte[]? previous = null;
        var samples = new List<(double Source, double[][] Motion)>(sampleCount);
        double[] accumulatedX = new double[RowBands];
        double[] accumulatedY = new double[RowBands];
        for (int sample = 0; sample < sampleCount; sample++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double elapsed = duration * sample / (sampleCount - 1);
            double timeline = VideoTimelineEditing.GetStartSeconds(clip, frameRate) + elapsed;
            double sourceSeconds = VideoTimelineEditing.GetSourceSeconds(clip, timeline, frameRate);
            if (!reader.TryGetFrame(sourceSeconds) || reader.Width <= 0 || reader.Height <= 0)
            {
                continue;
            }
            byte[] current = DownsampleLuma(reader.Pixels, reader.Width, reader.Height);
            if (previous is not null)
            {
                IReadOnlyList<(int X, int Y)> motion =
                    EstimateRowBandMotion(previous, current, AnalysisWidth, AnalysisHeight);
                for (int band = 0; band < RowBands; band++)
                {
                    accumulatedX[band] += motion[band].X;
                    accumulatedY[band] += motion[band].Y;
                }
                double centreX = accumulatedX[RowBands / 2];
                double centreY = accumulatedY[RowBands / 2];
                double[][] relative = new double[2][];
                relative[0] = accumulatedX.Select(value => value - centreX).ToArray();
                relative[1] = accumulatedY.Select(value => value - centreY).ToArray();
                samples.Add((sourceSeconds, relative));
            }
            else
            {
                samples.Add((sourceSeconds, [new double[RowBands], new double[RowBands]]));
            }
            previous = current;
        }

        if (samples.Count < 2)
        {
            throw new InvalidOperationException("The source did not provide enough readable frames for rolling-shutter analysis.");
        }

        var smoothedX = new double[samples.Count][];
        var smoothedY = new double[samples.Count][];
        for (int band = 0; band < RowBands; band++)
        {
            double[] x = Smooth(samples.Select(sample => sample.Motion[0][band]).ToArray());
            double[] y = Smooth(samples.Select(sample => sample.Motion[1][band]).ToArray());
            for (int index = 0; index < samples.Count; index++)
            {
                smoothedX[index] ??= new double[RowBands];
                smoothedY[index] ??= new double[RowBands];
                smoothedX[index][band] = x[index];
                smoothedY[index][band] = y[index];
            }
        }

        return samples.Select((sample, index) => new VideoRollingShutterKeyframe
            {
                SourceSeconds = sample.Source,
                Rows = Enumerable.Range(0, RowBands).Select(band => new VideoRollingShutterOffset
                {
                    X = Math.Clamp((smoothedX[index][band] - sample.Motion[0][band]) / AnalysisWidth,
                        -MaximumCorrection, MaximumCorrection),
                    Y = Math.Clamp((smoothedY[index][band] - sample.Motion[1][band]) / AnalysisHeight,
                        -MaximumCorrection, MaximumCorrection),
                }).ToList(),
            })
            .OrderBy(keyframe => keyframe.SourceSeconds)
            .GroupBy(keyframe => keyframe.SourceSeconds)
            .Select(group => group.First())
            .ToArray();
    }

    public static IReadOnlyList<(int X, int Y)> EstimateRowBandMotion(
        IReadOnlyList<byte> previous,
        IReadOnlyList<byte> current,
        int width,
        int height)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        if (width <= 0 || height < RowBands * (SearchRadius * 2 + 1) ||
            previous.Count != (long)width * height || current.Count != (long)width * height)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Frames are too small or their luma buffers do not match.");
        }

        int bandHeight = height / RowBands;
        var result = new (int X, int Y)[RowBands];
        for (int band = 0; band < RowBands; band++)
        {
            int top = band * bandHeight;
            int bottom = band == RowBands - 1 ? height : top + bandHeight;
            result[band] = EstimateTranslationInRows(previous, current, width, height, top, bottom);
        }
        return result;
    }

    public static void ValidateProfile(IReadOnlyList<VideoRollingShutterKeyframe>? profile)
    {
        if (profile is null || profile.Count > MaximumProfileSamples)
        {
            throw new InvalidDataException("Rolling-shutter profiles contain too many samples.");
        }
        double previous = -1;
        foreach (VideoRollingShutterKeyframe keyframe in profile)
        {
            if (keyframe is null || !double.IsFinite(keyframe.SourceSeconds) ||
                keyframe.SourceSeconds < 0 || keyframe.SourceSeconds <= previous ||
                keyframe.Rows is null || keyframe.Rows.Count != RowBands ||
                keyframe.Rows.Any(row => row is null || !double.IsFinite(row.X) ||
                    !double.IsFinite(row.Y) || Math.Abs(row.X) > MaximumCorrection ||
                    Math.Abs(row.Y) > MaximumCorrection))
            {
                throw new InvalidDataException("Rolling-shutter profile samples must be ordered, finite, and contain six bounded row offsets.");
            }
            previous = keyframe.SourceSeconds;
        }
        if (profile.Count == 1)
        {
            throw new InvalidDataException("A rolling-shutter profile needs at least two samples.");
        }
    }

    public static bool HasCorrection(IReadOnlyList<VideoRollingShutterOffset> offsets) =>
        offsets.Any(offset => offset.X != 0 || offset.Y != 0);

    public static VideoRollingShutterOffset[] GetOffsets(
        IReadOnlyList<VideoRollingShutterKeyframe> profile,
        double sourceSeconds)
    {
        if (profile.Count == 0)
        {
            return Enumerable.Range(0, RowBands).Select(_ => new VideoRollingShutterOffset()).ToArray();
        }

        int right = 0;
        while (right < profile.Count && profile[right].SourceSeconds < sourceSeconds)
        {
            right++;
        }
        if (right == 0)
        {
            return profile[0].Rows.ToArray();
        }
        if (right == profile.Count)
        {
            return profile[^1].Rows.ToArray();
        }
        VideoRollingShutterKeyframe leftKey = profile[right - 1];
        VideoRollingShutterKeyframe rightKey = profile[right];
        double amount = (sourceSeconds - leftKey.SourceSeconds) / (rightKey.SourceSeconds - leftKey.SourceSeconds);
        return Enumerable.Range(0, RowBands).Select(index => new VideoRollingShutterOffset
        {
            X = leftKey.Rows[index].X + (rightKey.Rows[index].X - leftKey.Rows[index].X) * amount,
            Y = leftKey.Rows[index].Y + (rightKey.Rows[index].Y - leftKey.Rows[index].Y) * amount,
        }).ToArray();
    }

    public static void WarpBgra(
        IReadOnlyList<byte> source,
        Span<byte> destination,
        int width,
        int height,
        IReadOnlyList<VideoRollingShutterOffset> offsets)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(offsets);
        if (width <= 0 || height <= 0 || source.Count != (long)width * height * 4 ||
            destination.Length != source.Count || offsets.Count != RowBands ||
            offsets.Any(offset => offset is null || !double.IsFinite(offset.X) || !double.IsFinite(offset.Y) ||
                Math.Abs(offset.X) > MaximumCorrection || Math.Abs(offset.Y) > MaximumCorrection))
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions or row-offset profile are invalid.");
        }
        if (source is byte[] sourceBytes && sourceBytes.AsSpan().Overlaps(destination))
        {
            throw new ArgumentException("Rolling-shutter source and destination buffers must not overlap.", nameof(destination));
        }
        destination.Clear();
        for (int y = 0; y < height; y++)
        {
            double bandPosition = height == 1 ? 0 : (double)y * (RowBands - 1) / (height - 1);
            int band = Math.Min(RowBands - 2, (int)bandPosition);
            double bandAmount = bandPosition - band;
            double dx = (offsets[band].X + (offsets[band + 1].X - offsets[band].X) * bandAmount) * width;
            double dy = (offsets[band].Y + (offsets[band + 1].Y - offsets[band].Y) * bandAmount) * height;
            for (int x = 0; x < width; x++)
            {
                double sourceX = x - dx;
                double sourceY = y - dy;
                if (sourceX < 0 || sourceY < 0 || sourceX > width - 1 || sourceY > height - 1)
                {
                    continue;
                }
                int x0 = (int)sourceX;
                int y0 = (int)sourceY;
                int x1 = Math.Min(x0 + 1, width - 1);
                int y1 = Math.Min(y0 + 1, height - 1);
                double fx = sourceX - x0;
                double fy = sourceY - y0;
                int output = (y * width + x) * 4;
                for (int channel = 0; channel < 4; channel++)
                {
                    double top = source[(y0 * width + x0) * 4 + channel] * (1 - fx) +
                        source[(y0 * width + x1) * 4 + channel] * fx;
                    double bottom = source[(y1 * width + x0) * 4 + channel] * (1 - fx) +
                        source[(y1 * width + x1) * 4 + channel] * fx;
                    destination[output + channel] = (byte)Math.Clamp((int)Math.Round(top * (1 - fy) + bottom * fy), 0, 255);
                }
            }
        }
    }

    private static (int X, int Y) EstimateTranslationInRows(
        IReadOnlyList<byte> previous, IReadOnlyList<byte> current, int width, int height, int top, int bottom)
    {
        long bestError = long.MaxValue;
        int bestX = 0;
        int bestY = 0;
        for (int dy = -SearchRadius; dy <= SearchRadius; dy++)
        {
            int startY = Math.Max(top + SearchRadius, SearchRadius + dy);
            int endY = Math.Min(bottom - SearchRadius, height - SearchRadius + dy);
            for (int dx = -SearchRadius; dx <= SearchRadius; dx++)
            {
                long error = 0;
                int count = 0;
                for (int y = startY; y < endY; y += 2)
                {
                    int previousY = y - dy;
                    for (int x = SearchRadius; x < width - SearchRadius; x += 2)
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
            result[index] = values.Skip(start).Take(end - start + 1).Average();
        }
        return result;
    }
}
