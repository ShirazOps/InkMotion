using System.Numerics;

namespace InkMotion;

/// <summary>
/// A planar projective transform (homography) mapping (x, y) to
/// ((Ax + By + C) / w, (Dx + Ey + F) / w), where w = Gx + Hy + 1.
/// </summary>
public readonly record struct InkHomography(double A, double B, double C, double D, double E, double F, double G, double H)
{
    public static InkHomography Identity => new(1, 0, 0, 0, 1, 0, 0, 0);

    public bool TryApply(double x, double y, out double resultX, out double resultY)
    {
        double w = G * x + H * y + 1;
        resultX = resultY = 0;
        if (!double.IsFinite(w) || w <= 1e-6) return false;
        resultX = (A * x + B * y + C) / w;
        resultY = (D * x + E * y + F) / w;
        return double.IsFinite(resultX) && double.IsFinite(resultY);
    }

    public Vector2 Apply(Vector2 point) =>
        TryApply(point.X, point.Y, out double x, out double y) ? new((float)x, (float)y) : new(float.NaN, float.NaN);

    /// <summary>Row-vector 4x4 form for Direct2D's 3D transform, which divides by the projected w.</summary>
    public Matrix4x4 ToMatrix4x4() => new(
        (float)A, (float)D, 0, (float)G,
        (float)B, (float)E, 0, (float)H,
        0, 0, 1, 0,
        (float)C, (float)F, 0, 1);

    /// <summary>Least-squares homography from four or more correspondences, using normalised coordinates.</summary>
    public static bool TryFit(IReadOnlyList<(Vector2 From, Vector2 To)> pairs, out InkHomography homography)
    {
        homography = Identity;
        if (pairs.Count < 4) return false;
        (double fromX, double fromY, double fromScale) = Normalisation(pairs.Select(pair => pair.From));
        (double toX, double toY, double toScale) = Normalisation(pairs.Select(pair => pair.To));
        if (fromScale <= 0 || toScale <= 0) return false;
        var normal = new double[8, 8];
        var rhs = new double[8];
        Span<double> row = stackalloc double[8];
        foreach ((Vector2 from, Vector2 to) in pairs)
        {
            double x = (from.X - fromX) * fromScale, y = (from.Y - fromY) * fromScale;
            double u = (to.X - toX) * toScale, v = (to.Y - toY) * toScale;
            Accumulate(normal, rhs, row, [x, y, 1, 0, 0, 0, -x * u, -y * u], u);
            Accumulate(normal, rhs, row, [0, 0, 0, x, y, 1, -x * v, -y * v], v);
        }
        if (!Solve(normal, rhs, out double[] h)) return false;
        // Denormalise: H = T_to^-1 * Hn * T_from.
        double[,] hn = { { h[0], h[1], h[2] }, { h[3], h[4], h[5] }, { h[6], h[7], 1 } };
        double[,] tFrom = { { fromScale, 0, -fromX * fromScale }, { 0, fromScale, -fromY * fromScale }, { 0, 0, 1 } };
        double[,] tToInverse = { { 1 / toScale, 0, toX }, { 0, 1 / toScale, toY }, { 0, 0, 1 } };
        double[,] result = Multiply(tToInverse, Multiply(hn, tFrom));
        double scale = result[2, 2];
        if (!double.IsFinite(scale) || Math.Abs(scale) < 1e-12) return false;
        homography = new(result[0, 0] / scale, result[0, 1] / scale, result[0, 2] / scale,
            result[1, 0] / scale, result[1, 1] / scale, result[1, 2] / scale,
            result[2, 0] / scale, result[2, 1] / scale);
        return homography.IsFinite;
    }

    public bool IsFinite =>
        double.IsFinite(A) && double.IsFinite(B) && double.IsFinite(C) && double.IsFinite(D) &&
        double.IsFinite(E) && double.IsFinite(F) && double.IsFinite(G) && double.IsFinite(H);

    /// <summary>True when the four points form a convex quadrilateral with a consistent winding and usable area.</summary>
    public static bool IsConvexQuad(IReadOnlyList<Vector2> corners, double minimumArea = 1)
    {
        if (corners.Count != 4 || corners.Any(corner => !float.IsFinite(corner.X) || !float.IsFinite(corner.Y)))
            return false;
        int sign = 0;
        for (int i = 0; i < 4; i++)
        {
            Vector2 a = corners[i], b = corners[(i + 1) % 4], c = corners[(i + 2) % 4];
            double cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            int current = Math.Sign(cross);
            if (current == 0 || sign != 0 && current != sign) return false;
            sign = current;
        }
        return Math.Abs(QuadArea(corners)) >= minimumArea;
    }

    public static double QuadArea(IReadOnlyList<Vector2> corners)
    {
        double area = 0;
        for (int i = 0; i < corners.Count; i++)
        {
            Vector2 a = corners[i], b = corners[(i + 1) % corners.Count];
            area += a.X * b.Y - b.X * a.Y;
        }
        return area / 2;
    }

    private static (double X, double Y, double Scale) Normalisation(IEnumerable<Vector2> points)
    {
        Vector2[] values = points.ToArray();
        double x = values.Average(point => point.X), y = values.Average(point => point.Y);
        double distance = values.Average(point => Math.Sqrt((point.X - x) * (point.X - x) + (point.Y - y) * (point.Y - y)));
        return (x, y, distance > 1e-9 ? Math.Sqrt(2) / distance : 0);
    }

    private static void Accumulate(double[,] normal, double[] rhs, Span<double> row, double[] values, double target)
    {
        values.CopyTo(row);
        for (int i = 0; i < 8; i++)
        {
            rhs[i] += row[i] * target;
            for (int j = 0; j < 8; j++) normal[i, j] += row[i] * row[j];
        }
    }

    private static bool Solve(double[,] matrix, double[] rhs, out double[] solution)
    {
        int n = rhs.Length;
        var a = (double[,])matrix.Clone();
        var b = (double[])rhs.Clone();
        solution = new double[n];
        for (int column = 0; column < n; column++)
        {
            int pivot = column;
            for (int row = column + 1; row < n; row++)
                if (Math.Abs(a[row, column]) > Math.Abs(a[pivot, column])) pivot = row;
            if (Math.Abs(a[pivot, column]) < 1e-10) return false;
            if (pivot != column)
            {
                for (int k = 0; k < n; k++) (a[column, k], a[pivot, k]) = (a[pivot, k], a[column, k]);
                (b[column], b[pivot]) = (b[pivot], b[column]);
            }
            for (int row = column + 1; row < n; row++)
            {
                double factor = a[row, column] / a[column, column];
                for (int k = column; k < n; k++) a[row, k] -= factor * a[column, k];
                b[row] -= factor * b[column];
            }
        }
        for (int row = n - 1; row >= 0; row--)
        {
            double sum = b[row];
            for (int k = row + 1; k < n; k++) sum -= a[row, k] * solution[k];
            solution[row] = sum / a[row, row];
        }
        return solution.All(double.IsFinite);
    }

    private static double[,] Multiply(double[,] left, double[,] right)
    {
        var result = new double[3, 3];
        for (int i = 0; i < 3; i++)
        for (int j = 0; j < 3; j++)
        for (int k = 0; k < 3; k++)
            result[i, j] += left[i, k] * right[k, j];
        return result;
    }
}
