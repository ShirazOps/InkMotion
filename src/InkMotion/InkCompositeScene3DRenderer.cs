using System.Numerics;

namespace InkMotion;

internal static class InkCompositeScene3DRenderer
{
    private readonly record struct Triangle(Vector3 A, Vector3 B, Vector3 C);
    private readonly record struct ScreenVertex(float X, float Y, float Depth);

    private static readonly Triangle[] Cube = CreateCube();
    private static readonly Triangle[] Sphere = CreateSphere();
    private static readonly Triangle[] Plane =
    [
        new(new(-0.5f, -0.5f, 0), new(0.5f, -0.5f, 0), new(0.5f, 0.5f, 0)),
        new(new(-0.5f, -0.5f, 0), new(0.5f, 0.5f, 0), new(-0.5f, 0.5f, 0)),
    ];

    public static byte[] Render(
        InkCompositeScene3D scene,
        int targetWidth,
        int targetHeight,
        float canvasWidth,
        float canvasHeight,
        Matrix3x2 transform,
        double seconds)
    {
        if (!InkCompositeScenes3D.IsValid(scene))
            throw new ArgumentException("The 3D scene has invalid settings.", nameof(scene));
        if (targetWidth <= 0 || targetHeight <= 0 || !float.IsFinite(canvasWidth) ||
            !float.IsFinite(canvasHeight) || canvasWidth <= 0 || canvasHeight <= 0 || !double.IsFinite(seconds))
            throw new ArgumentOutOfRangeException(nameof(targetWidth), "The 3D rendering dimensions and time must be valid.");

        int pixelCount = checked(targetWidth * targetHeight);
        byte[] pixels = new byte[checked(pixelCount * 4)];
        float[] depth = new float[pixelCount];
        Array.Fill(depth, float.PositiveInfinity);

        Vector3 camera = Direction(scene.CameraYaw, scene.CameraPitch) * (float)scene.CameraDistance;
        Matrix4x4 view = Matrix4x4.CreateLookAt(camera, Vector3.Zero, Vector3.UnitY);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
            Radians(scene.FieldOfView), canvasWidth / canvasHeight, 0.05f, 400);
        Matrix4x4 viewProjection = view * projection;
        Vector3 light = Direction(scene.LightYaw, scene.LightPitch);

        foreach (InkComposite3DObject item in scene.Objects.Where(item => item.Visible))
        {
            Matrix4x4 world = Matrix4x4.CreateScale((float)item.ScaleX, (float)item.ScaleY, (float)item.ScaleZ) *
                Matrix4x4.CreateRotationX(AnimatedAngle(item.RotationX, item.SpinX, seconds)) *
                Matrix4x4.CreateRotationY(AnimatedAngle(item.RotationY, item.SpinY, seconds)) *
                Matrix4x4.CreateRotationZ(AnimatedAngle(item.RotationZ, item.SpinZ, seconds)) *
                Matrix4x4.CreateTranslation((float)item.X, (float)item.Y, (float)item.Z);
            Triangle[] mesh = item.Shape switch
            {
                InkComposite3DShape.Cube => Cube,
                InkComposite3DShape.Sphere => Sphere,
                InkComposite3DShape.Plane => Plane,
                _ => throw new InvalidOperationException("The 3D scene contains an unknown shape."),
            };
            foreach (Triangle triangle in mesh)
            {
                Vector3 a = Vector3.Transform(triangle.A, world);
                Vector3 b = Vector3.Transform(triangle.B, world);
                Vector3 c = Vector3.Transform(triangle.C, world);
                Vector3 cross = Vector3.Cross(b - a, c - a);
                if (cross.LengthSquared() < 1e-12f) continue;

                float diffuse = Math.Max(0, Vector3.Dot(Vector3.Normalize(cross), light));
                float brightness = (float)(scene.AmbientLight + ((1 - scene.AmbientLight) * diffuse));
                byte blue = Shade((byte)item.Colour, brightness);
                byte green = Shade((byte)(item.Colour >> 8), brightness);
                byte red = Shade((byte)(item.Colour >> 16), brightness);
                ClipAndRasterise(
                    Vector4.Transform(new Vector4(a, 1), viewProjection),
                    Vector4.Transform(new Vector4(b, 1), viewProjection),
                    Vector4.Transform(new Vector4(c, 1), viewProjection),
                    canvasWidth, canvasHeight, transform, targetWidth, targetHeight,
                    pixels, depth, blue, green, red);
            }
        }

        return pixels;
    }

    private static void ClipAndRasterise(
        Vector4 a,
        Vector4 b,
        Vector4 c,
        float canvasWidth,
        float canvasHeight,
        Matrix3x2 transform,
        int targetWidth,
        int targetHeight,
        byte[] pixels,
        float[] depth,
        byte blue,
        byte green,
        byte red)
    {
        Span<Vector4> input = stackalloc Vector4[16];
        Span<Vector4> output = stackalloc Vector4[16];
        input[0] = a;
        input[1] = b;
        input[2] = c;
        int count = 3;
        for (int plane = 0; plane < 6; plane++)
        {
            int nextCount = 0;
            Vector4 previous = input[count - 1];
            float previousDistance = ClipDistance(previous, plane);
            for (int index = 0; index < count; index++)
            {
                Vector4 current = input[index];
                float currentDistance = ClipDistance(current, plane);
                bool previousInside = previousDistance >= 0;
                bool currentInside = currentDistance >= 0;
                if (previousInside != currentInside)
                {
                    float fraction = previousDistance / (previousDistance - currentDistance);
                    output[nextCount++] = Vector4.Lerp(previous, current, fraction);
                }
                if (currentInside) output[nextCount++] = current;
                previous = current;
                previousDistance = currentDistance;
            }

            if (nextCount < 3) return;
            Span<Vector4> swap = input;
            input = output;
            output = swap;
            count = nextCount;
        }

        ScreenVertex first = Project(input[0], canvasWidth, canvasHeight, transform);
        for (int index = 1; index < count - 1; index++)
        {
            Rasterise(first, Project(input[index], canvasWidth, canvasHeight, transform),
                Project(input[index + 1], canvasWidth, canvasHeight, transform),
                targetWidth, targetHeight, pixels, depth, blue, green, red);
        }
    }

    private static float ClipDistance(Vector4 point, int plane) => plane switch
    {
        0 => point.W + point.X,
        1 => point.W - point.X,
        2 => point.W + point.Y,
        3 => point.W - point.Y,
        4 => point.Z,
        5 => point.W - point.Z,
        _ => throw new ArgumentOutOfRangeException(nameof(plane)),
    };

    private static ScreenVertex Project(Vector4 point, float width, float height, Matrix3x2 transform)
    {
        float inverseW = 1 / point.W;
        var position = new Vector2(
            ((point.X * inverseW) + 1) * width * 0.5f,
            (1 - (point.Y * inverseW)) * height * 0.5f);
        position = Vector2.Transform(position, transform);
        return new ScreenVertex(position.X, position.Y, point.Z * inverseW);
    }

    private static void Rasterise(
        ScreenVertex a,
        ScreenVertex b,
        ScreenVertex c,
        int width,
        int height,
        byte[] pixels,
        float[] depth,
        byte blue,
        byte green,
        byte red)
    {
        float area = Edge(a, b, c.X, c.Y);
        if (Math.Abs(area) < 1e-8f) return;
        if (area < 0)
        {
            (b, c) = (c, b);
            area = -area;
        }

        int left = Math.Max(0, (int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X))));
        int top = Math.Max(0, (int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y))));
        int right = Math.Min(width - 1, (int)Math.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))) - 1);
        int bottom = Math.Min(height - 1, (int)Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))) - 1);
        if (left > right || top > bottom) return;

        float rowA = Edge(b, c, left + 0.5f, top + 0.5f);
        float rowB = Edge(c, a, left + 0.5f, top + 0.5f);
        float rowC = Edge(a, b, left + 0.5f, top + 0.5f);
        for (int y = top; y <= bottom; y++)
        {
            float weightA = rowA, weightB = rowB, weightC = rowC;
            for (int x = left; x <= right; x++)
            {
                if (weightA >= 0 && weightB >= 0 && weightC >= 0)
                {
                    float z = ((weightA * a.Depth) + (weightB * b.Depth) + (weightC * c.Depth)) / area;
                    int pixel = (y * width) + x;
                    if (z < depth[pixel])
                    {
                        depth[pixel] = z;
                        int offset = pixel * 4;
                        pixels[offset] = blue;
                        pixels[offset + 1] = green;
                        pixels[offset + 2] = red;
                        pixels[offset + 3] = 255;
                    }
                }

                weightA += c.Y - b.Y;
                weightB += a.Y - c.Y;
                weightC += b.Y - a.Y;
            }

            rowA -= c.X - b.X;
            rowB -= a.X - c.X;
            rowC -= b.X - a.X;
        }
    }

    private static float Edge(ScreenVertex a, ScreenVertex b, float x, float y) =>
        ((x - a.X) * (b.Y - a.Y)) - ((y - a.Y) * (b.X - a.X));

    private static byte Shade(byte channel, float brightness) =>
        (byte)Math.Clamp((int)Math.Round(channel * brightness), 0, 255);

    private static float Radians(double degrees) => (float)(degrees * Math.PI / 180);

    private static float AnimatedAngle(double angle, double spin, double seconds) =>
        Radians(Math.IEEERemainder(angle + (spin * seconds), 360));

    private static Vector3 Direction(double yaw, double pitch)
    {
        float y = Radians(yaw);
        float p = Radians(pitch);
        return new Vector3(MathF.Sin(y) * MathF.Cos(p), MathF.Sin(p), MathF.Cos(y) * MathF.Cos(p));
    }

    private static Triangle[] CreateCube()
    {
        var triangles = new List<Triangle>();
        void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            triangles.Add(new Triangle(a, b, c));
            triangles.Add(new Triangle(a, c, d));
        }

        Face(new(-0.5f, -0.5f, 0.5f), new(0.5f, -0.5f, 0.5f), new(0.5f, 0.5f, 0.5f), new(-0.5f, 0.5f, 0.5f));
        Face(new(0.5f, -0.5f, -0.5f), new(-0.5f, -0.5f, -0.5f), new(-0.5f, 0.5f, -0.5f), new(0.5f, 0.5f, -0.5f));
        Face(new(0.5f, -0.5f, 0.5f), new(0.5f, -0.5f, -0.5f), new(0.5f, 0.5f, -0.5f), new(0.5f, 0.5f, 0.5f));
        Face(new(-0.5f, -0.5f, -0.5f), new(-0.5f, -0.5f, 0.5f), new(-0.5f, 0.5f, 0.5f), new(-0.5f, 0.5f, -0.5f));
        Face(new(-0.5f, 0.5f, 0.5f), new(0.5f, 0.5f, 0.5f), new(0.5f, 0.5f, -0.5f), new(-0.5f, 0.5f, -0.5f));
        Face(new(-0.5f, -0.5f, -0.5f), new(0.5f, -0.5f, -0.5f), new(0.5f, -0.5f, 0.5f), new(-0.5f, -0.5f, 0.5f));
        return triangles.ToArray();
    }

    private static Triangle[] CreateSphere()
    {
        const int latitudeBands = 8;
        const int longitudeBands = 16;
        var triangles = new List<Triangle>();
        Vector3 Point(int latitude, int longitude)
        {
            double pitch = (-Math.PI / 2) + (Math.PI * latitude / latitudeBands);
            double yaw = 2 * Math.PI * longitude / longitudeBands;
            return new Vector3(
                (float)(0.5 * Math.Cos(pitch) * Math.Cos(yaw)),
                (float)(0.5 * Math.Sin(pitch)),
                (float)(0.5 * Math.Cos(pitch) * Math.Sin(yaw)));
        }

        void Add(Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 cross = Vector3.Cross(b - a, c - a);
            if (cross.LengthSquared() < 1e-12f) return;
            if (Vector3.Dot(cross, a + b + c) < 0) (b, c) = (c, b);
            triangles.Add(new Triangle(a, b, c));
        }

        for (int latitude = 0; latitude < latitudeBands; latitude++)
        {
            for (int longitude = 0; longitude < longitudeBands; longitude++)
            {
                Vector3 a = Point(latitude, longitude);
                Vector3 b = Point(latitude + 1, longitude);
                Vector3 c = Point(latitude + 1, longitude + 1);
                Vector3 d = Point(latitude, longitude + 1);
                Add(a, b, c);
                Add(a, c, d);
            }
        }
        return triangles.ToArray();
    }
}
