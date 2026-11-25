using System.Numerics;
using Microsoft.ML.OnnxRuntime;

namespace InkMotion;

/// <summary>
/// MobileSAM segmentation. The encoder takes "image" [1, 3, 1024, 1024] (longest side resized to 1024, SAM
/// normalisation, zero padding bottom-right) and returns "image_embeddings" [1, 256, 64, 64]. The decoder follows the
/// official SAM ONNX export: image_embeddings, point_coords, point_labels, mask_input, has_mask_input and
/// orig_im_size in; masks, iou_predictions and low_res_masks out.
/// </summary>
public sealed class MobileSamModel : IDisposable
{
    public const int InputSize = 1024;
    public const int LowResolution = 256;
    private static readonly float[] Mean = [123.675f, 116.28f, 103.53f];
    private static readonly float[] Deviation = [58.395f, 57.12f, 57.375f];

    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;

    public MobileSamModel(string encoderPath, string decoderPath)
    {
        _encoder = WindowsMlRuntime.CreateSession(encoderPath);
        try
        {
            _decoder = WindowsMlRuntime.CreateSession(decoderPath);
            WindowsMlRuntime.RequireContract(_encoder, "The segmentation encoder", ["image"], ["image_embeddings"]);
            WindowsMlRuntime.RequireContract(_decoder, "The segmentation decoder",
                ["image_embeddings", "point_coords", "point_labels", "mask_input", "has_mask_input", "orig_im_size"],
                ["masks", "iou_predictions", "low_res_masks"]);
        }
        catch
        {
            _encoder.Dispose();
            _decoder?.Dispose();
            throw;
        }
    }

    public static float ResizeScale(int width, int height) => InputSize / (float)Math.Max(width, height);

    public static float[] Preprocess(byte[] bgra, int width, int height)
    {
        float scale = ResizeScale(width, height);
        int resizedWidth = (int)Math.Round(width * scale), resizedHeight = (int)Math.Round(height * scale);
        var tensor = new float[3 * InputSize * InputSize];
        int plane = InputSize * InputSize;
        Span<float> rgb = stackalloc float[3];
        for (int y = 0; y < resizedHeight; y++)
        for (int x = 0; x < resizedWidth; x++)
        {
            RtmPoseModel.Sample(bgra, width, height, (x + 0.5f) / scale - 0.5f, (y + 0.5f) / scale - 0.5f, rgb);
            int index = y * InputSize + x;
            for (int channel = 0; channel < 3; channel++)
                tensor[channel * plane + index] = (rgb[channel] - Mean[channel]) / Deviation[channel];
        }
        return tensor;
    }

    /// <summary>SAM encodes a box as its two corners with labels 2 (top-left) and 3 (bottom-right).</summary>
    public static (float[] Coordinates, float[] Labels) BoxPrompt(Vector2 topLeft, Vector2 bottomRight, float scale) =>
        ([topLeft.X * scale, topLeft.Y * scale, bottomRight.X * scale, bottomRight.Y * scale], [2, 3]);

    public sealed record Result(bool[] Mask, float Score, float[] LowResolutionMask);

    public Result Segment(byte[] bgra, int width, int height, Vector2 topLeft, Vector2 bottomRight, float[]? previousLowResolution)
    {
        float scale = ResizeScale(width, height);
        float[] embeddings;
        using (var encoded = _encoder.Run([WindowsMlRuntime.Tensor("image", Preprocess(bgra, width, height), 1, 3, InputSize, InputSize)]))
            embeddings = encoded.First(value => value.Name == "image_embeddings").AsEnumerable<float>().ToArray();
        (float[] coordinates, float[] labels) = BoxPrompt(topLeft, bottomRight, scale);
        float[] maskInput = previousLowResolution ?? new float[LowResolution * LowResolution];
        using var decoded = _decoder.Run(
        [
            WindowsMlRuntime.Tensor("image_embeddings", embeddings, 1, 256, 64, 64),
            WindowsMlRuntime.Tensor("point_coords", coordinates, 1, 2, 2),
            WindowsMlRuntime.Tensor("point_labels", labels, 1, 2),
            WindowsMlRuntime.Tensor("mask_input", maskInput, 1, 1, LowResolution, LowResolution),
            WindowsMlRuntime.Tensor("has_mask_input", [previousLowResolution is null ? 0 : 1], 1),
            WindowsMlRuntime.Tensor("orig_im_size", [height, width], 2),
        ]);
        float[] logits = decoded.First(value => value.Name == "masks").AsEnumerable<float>().ToArray();
        float score = decoded.First(value => value.Name == "iou_predictions").AsEnumerable<float>().First();
        float[] low = decoded.First(value => value.Name == "low_res_masks").AsEnumerable<float>()
            .Take(LowResolution * LowResolution).ToArray();
        if (logits.Length < width * height) throw new InvalidDataException("The segmentation decoder returned an unexpected mask size.");
        var mask = new bool[width * height];
        for (int i = 0; i < mask.Length; i++) mask[i] = logits[i] > 0;
        return new(mask, score, low);
    }

    public void Dispose()
    {
        _encoder.Dispose();
        _decoder.Dispose();
    }
}

/// <summary>Turns a binary mask into simplified outline polygons that can be drawn as a vector matte.</summary>
public static class MaskContours
{
    public static (int Left, int Top, int Right, int Bottom, int Area)? Bounds(bool[] mask, int width, int height)
    {
        int left = width, top = height, right = -1, bottom = -1, area = 0;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            if (!mask[y * width + x]) continue;
            area++;
            left = Math.Min(left, x); right = Math.Max(right, x);
            top = Math.Min(top, y); bottom = Math.Max(bottom, y);
        }
        return area == 0 ? null : (left, top, right, bottom, area);
    }

    /// <summary>
    /// Traces the outer boundary of each 8-connected region (holes are filled), drops regions smaller than
    /// <paramref name="minimumArea"/> pixels and simplifies outlines. Coordinates lie on pixel corners, so a single
    /// pixel becomes a unit square; regions touching the frame edge are traced along it.
    /// </summary>
    public static List<Vector2[]> Trace(bool[] mask, int width, int height, int minimumArea = 16,
        float tolerance = 0.75f, int maximumPolygons = 32)
    {
        if (mask.Length != width * height) throw new ArgumentException("Mask size does not match its dimensions.", nameof(mask));
        var labels = new int[mask.Length];
        var polygons = new List<(Vector2[] Points, int Area)>();
        int label = 0;
        var stack = new Stack<int>();
        for (int start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || labels[start] != 0) continue;
            label++;
            int area = 0;
            labels[start] = label;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int index = stack.Pop();
                area++;
                int x = index % width, y = index / width;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    int next = ny * width + nx;
                    if (mask[next] && labels[next] == 0) { labels[next] = label; stack.Push(next); }
                }
            }
            if (area < minimumArea) continue;
            List<Vector2> outline = TraceOuter(labels, width, height, start, label);
            Vector2[] simplified = Simplify(outline, tolerance);
            if (simplified.Length >= 3) polygons.Add((simplified, area));
        }
        return polygons.OrderByDescending(polygon => polygon.Area).Take(maximumPolygons)
            .Select(polygon => polygon.Points).ToList();
    }

    /// <summary>
    /// Walks pixel-corner edges with the region on the right. <paramref name="start"/> is the region's first pixel in
    /// raster order, so its top edge is on the outer boundary.
    /// </summary>
    private static List<Vector2> TraceOuter(int[] labels, int width, int height, int start, int label)
    {
        bool Inside(int x, int y) => x >= 0 && y >= 0 && x < width && y < height && labels[y * width + x] == label;
        int startX = start % width, startY = start / width;
        // Directions: 0 = right, 1 = down, 2 = left, 3 = up, moving along pixel-corner lattice points.
        int[] stepX = [1, 0, -1, 0], stepY = [0, 1, 0, -1];
        int x = startX, y = startY, direction = 0;
        var points = new List<Vector2>();
        int limit = 4 * (width + 1) * (height + 1);
        do
        {
            points.Add(new(x, y));
            x += stepX[direction];
            y += stepY[direction];
            // At corner (x, y) the four surrounding pixels decide the turn; the region must stay on the right.
            bool Ahead(int d) => d switch
            {
                0 => Inside(x, y), // right: pixel below-right of the corner
                1 => Inside(x - 1, y), // down: pixel below-left
                2 => Inside(x - 1, y - 1), // left: pixel above-left
                _ => Inside(x, y - 1), // up: pixel above-right
            };
            bool Left(int d) => d switch
            {
                0 => Inside(x, y - 1),
                1 => Inside(x, y),
                2 => Inside(x - 1, y),
                _ => Inside(x - 1, y - 1),
            };
            if (Left(direction)) direction = (direction + 3) % 4;
            else if (!Ahead(direction)) direction = (direction + 1) % 4;
        }
        while ((x != startX || y != startY || direction != 0) && points.Count < limit);
        return points;
    }

    /// <summary>Ramer–Douglas–Peucker simplification of a closed outline.</summary>
    public static Vector2[] Simplify(IReadOnlyList<Vector2> points, float tolerance)
    {
        if (points.Count < 4) return points.ToArray();
        int far = 0;
        float farthest = -1;
        for (int i = 1; i < points.Count; i++)
        {
            float distance = Vector2.DistanceSquared(points[0], points[i]);
            if (distance > farthest) { farthest = distance; far = i; }
        }
        var keep = new bool[points.Count];
        keep[0] = keep[far] = true;
        void Reduce(int first, int last)
        {
            if (last <= first + 1) return;
            Vector2 a = points[first], b = points[last % points.Count];
            int index = -1;
            float worst = tolerance;
            for (int i = first + 1; i < last; i++)
            {
                float distance = DistanceToSegment(points[i], a, b);
                if (distance > worst) { worst = distance; index = i; }
            }
            if (index < 0) return;
            keep[index] = true;
            Reduce(first, index);
            Reduce(index, last);
        }
        Reduce(0, far);
        Reduce(far, points.Count);
        return points.Where((_, i) => keep[i]).ToArray();
    }

    private static float DistanceToSegment(Vector2 point, Vector2 a, Vector2 b)
    {
        Vector2 segment = b - a;
        float length = segment.LengthSquared();
        if (length == 0) return Vector2.Distance(point, a);
        float t = Math.Clamp(Vector2.Dot(point - a, segment) / length, 0, 1);
        return Vector2.Distance(point, a + segment * t);
    }
}
