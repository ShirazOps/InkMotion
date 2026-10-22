using Microsoft.Graphics.Canvas;

namespace InkMotion;

public static class InkBucketFill
{
    public const int MaximumDimension = 1024;
    public const int MaximumBoundaryPoints = 65536;
    private const byte BoundaryAlpha = 8;

    public static List<InkPoint> Create(ICanvasResourceCreator device,
        IReadOnlyList<List<InkPoint>> strokes, double canvasWidth, double canvasHeight,
        double x, double y, uint colour)
    {
        InkColourSampling.GetPixel(x, y, canvasWidth, canvasHeight);
        if ((colour >> 24) == 0)
        {
            throw new InvalidOperationException("Choose a fill colour with non-zero opacity.");
        }

        double scale = Math.Min(1, MaximumDimension / Math.Max(canvasWidth, canvasHeight));
        int width = Math.Max(1, (int)Math.Round(canvasWidth * scale));
        int height = Math.Max(1, (int)Math.Round(canvasHeight * scale));
        using var target = new CanvasRenderTarget(device, width, height, 96);
        using (CanvasDrawingSession session = target.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Transparent);
            foreach (List<InkPoint> stroke in strokes)
            {
                if (stroke.Count > 0)
                {
                    TimelineCompositor.DrawInkStroke(session, stroke,
                        TimelineCompositor.ToColour(stroke[0].BrushColour), width, height, (float)scale);
                }
            }
        }

        byte[] pixels = target.GetPixelBytes();
        var blocked = new bool[width * height];
        for (int index = 0; index < blocked.Length; index++)
        {
            blocked[index] = pixels[index * 4 + 3] > BoundaryAlpha;
        }

        return CreateFromMask(blocked, width, height,
            Math.Min(width - 1, (int)(x / canvasWidth * width)),
            Math.Min(height - 1, (int)(y / canvasHeight * height)), colour);
    }

    public static List<InkPoint> CreateFromMask(bool[] blocked, int width, int height,
        int x, int y, uint colour)
    {
        if (width < 1 || height < 1 || width > MaximumDimension || height > MaximumDimension ||
            blocked.Length != width * height || x < 0 || x >= width || y < 0 || y >= height ||
            (colour >> 24) == 0)
        {
            throw new ArgumentException("Bucket fill needs a bounded mask, an interior seed and a visible colour.");
        }
        int seed = y * width + x;
        if (blocked[seed])
        {
            throw new InvalidOperationException("Click an empty enclosed area, not an existing stroke or fill.");
        }

        var region = new bool[blocked.Length];
        var queue = new int[blocked.Length];
        int read = 0;
        int count = 1;
        queue[0] = seed;
        region[seed] = true;
        void Visit(int next)
        {
            if (!blocked[next] && !region[next])
            {
                region[next] = true;
                queue[count++] = next;
            }
        }
        while (read < count)
        {
            int current = queue[read++];
            int px = current % width;
            int py = current / width;
            if (px == 0 || py == 0 || px == width - 1 || py == height - 1)
            {
                throw new InvalidOperationException("The area reaches the canvas edge. Close the boundary on the active layer before filling.");
            }
            Visit(current - 1);
            Visit(current + 1);
            Visit(current - width);
            Visit(current + width);
        }

        int stride = width + 1;
        var edges = new HashSet<(int Vertex, int Direction)>();
        for (int index = 0; index < count; index++)
        {
            int current = queue[index];
            int px = current % width;
            int py = current / width;
            int topLeft = py * stride + px;
            if (!region[current - width]) edges.Add((topLeft, 0));
            if (!region[current + 1]) edges.Add((topLeft + 1, 1));
            if (!region[current + width]) edges.Add((topLeft + stride + 1, 2));
            if (!region[current - 1]) edges.Add((topLeft + stride, 3));
            if (edges.Count > MaximumBoundaryPoints * 4)
            {
                throw new InvalidOperationException("This area is too complex to fill safely. Simplify the active layer's boundary and retry.");
            }
        }

        var loops = new List<List<int>>();
        int boundaryPoints = 0;
        int[] offsets = [1, stride, -1, -stride];
        int[] turns = [1, 0, 3, 2];
        foreach (var first in edges.ToArray())
        {
            if (!edges.Contains(first))
            {
                continue;
            }
            var edge = first;
            var loop = new List<int>();
            int previousDirection = -1;
            do
            {
                if (!edges.Remove(edge))
                {
                    throw new InvalidOperationException("The fill boundary could not be traced. Try a simpler closed area.");
                }
                if (edge.Direction != previousDirection)
                {
                    loop.Add(edge.Vertex);
                }
                previousDirection = edge.Direction;
                int nextVertex = edge.Vertex + offsets[edge.Direction];
                bool found = false;
                // Keep the region on the right when two contours meet at a grid corner.
                foreach (int turn in turns)
                {
                    int direction = (edge.Direction + turn) % 4;
                    var candidate = (Vertex: nextVertex, Direction: direction);
                    if (candidate == first || edges.Contains(candidate))
                    {
                        edge = candidate;
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    throw new InvalidOperationException("The fill boundary is incomplete. Close the area and retry.");
                }
            } while (edge != first);

            boundaryPoints += loop.Count + 3;
            if (boundaryPoints > MaximumBoundaryPoints)
            {
                throw new InvalidOperationException("This area is too complex to fill safely. Simplify the active layer's boundary and retry.");
            }
            loops.Add(loop);
        }

        InkPoint Point(int vertex) => new()
        {
            X = (double)(vertex % stride) / width,
            Y = (double)(vertex / stride) / height,
            BrushColour = colour,
            IsFilled = true,
        };
        var result = new List<InkPoint>();
        // Doubled bridges join outer and hole contours into one even-odd polygon.
        int anchor = loops[0][0];
        foreach (List<int> loop in loops)
        {
            result.AddRange(loop.Select(Point));
            result.Add(Point(loop[0]));
            result.Add(Point(anchor));
        }
        return result;
    }
}
