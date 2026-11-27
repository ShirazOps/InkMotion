using System.Numerics;

namespace InkMotion;

internal static class InkCompositeScene3DRenderer
{
    internal readonly record struct Triangle(Vector3 A, Vector3 B, Vector3 C,
        Vector2 UVA = default, Vector2 UVB = default, Vector2 UVC = default, int Material = 0);
    private readonly record struct ClipVertex(Vector4 Position, Vector3 World, Vector2 UV)
    {
        public static ClipVertex Lerp(ClipVertex a, ClipVertex b, float fraction) => new(
            Vector4.Lerp(a.Position, b.Position, fraction), Vector3.Lerp(a.World, b.World, fraction),
            Vector2.Lerp(a.UV, b.UV, fraction));
    }
    private readonly record struct ScreenVertex(float X, float Y, float Depth, float InverseW,
        Vector3 WorldOverW, Vector2 UVOverW);

    private static readonly Triangle[] Cube = CreateCube();
    private static readonly Triangle[] Sphere = CreateSphere();
    private static readonly Triangle[] Plane =
    [
        new(new(-0.5f, -0.5f, 0), new(0.5f, -0.5f, 0), new(0.5f, 0.5f, 0), new(0, 0), new(1, 0), new(1, 1)),
        new(new(-0.5f, -0.5f, 0), new(0.5f, 0.5f, 0), new(-0.5f, 0.5f, 0), new(0, 0), new(1, 1), new(0, 1)),
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
            Matrix4x4 world = WorldMatrix(item, seconds);
            Triangle[] mesh = GetMesh(item);
            foreach (Triangle triangle in mesh)
            {
                Vector3 a = Vector3.Transform(triangle.A, world);
                Vector3 b = Vector3.Transform(triangle.B, world);
                Vector3 c = Vector3.Transform(triangle.C, world);
                Vector3 cross = Vector3.Cross(b - a, c - a);
                if (cross.LengthSquared() < 1e-12f) continue;

                float diffuse = Math.Max(0, Vector3.Dot(Vector3.Normalize(cross), light));
                float brightness = (float)(scene.AmbientLight + ((1 - scene.AmbientLight) * diffuse));
                InkMeshMaterial? material = item.Shape == InkComposite3DShape.Mesh ? item.Mesh!.Materials[triangle.Material] : null;
                uint colour = MultiplyColour(item.Colour, material?.Colour ?? 0xFFFFFFFF);
                ClipAndRasterise(
                    new(Vector4.Transform(new Vector4(a, 1), viewProjection), a, triangle.UVA),
                    new(Vector4.Transform(new Vector4(b, 1), viewProjection), b, triangle.UVB),
                    new(Vector4.Transform(new Vector4(c, 1), viewProjection), c, triangle.UVC),
                    canvasWidth, canvasHeight, transform, targetWidth, targetHeight,
                    pixels, depth, colour, brightness, item.Texture ?? material?.Texture);
            }
        }

        return pixels;
    }

    private static void ClipAndRasterise(
        ClipVertex a,
        ClipVertex b,
        ClipVertex c,
        float canvasWidth,
        float canvasHeight,
        Matrix3x2 transform,
        int targetWidth,
        int targetHeight,
        byte[] pixels,
        float[] depth,
        uint colour,
        float brightness,
        InkMeshTexture? texture)
    {
        Span<ClipVertex> input = stackalloc ClipVertex[16];
        Span<ClipVertex> output = stackalloc ClipVertex[16];
        input[0] = a;
        input[1] = b;
        input[2] = c;
        int count = 3;
        for (int plane = 0; plane < 6; plane++)
        {
            int nextCount = 0;
            ClipVertex previous = input[count - 1];
            float previousDistance = ClipDistance(previous.Position, plane);
            for (int index = 0; index < count; index++)
            {
                ClipVertex current = input[index];
                float currentDistance = ClipDistance(current.Position, plane);
                bool previousInside = previousDistance >= 0;
                bool currentInside = currentDistance >= 0;
                if (previousInside != currentInside)
                {
                    float fraction = previousDistance / (previousDistance - currentDistance);
                    output[nextCount++] = ClipVertex.Lerp(previous, current, fraction);
                }
                if (currentInside) output[nextCount++] = current;
                previous = current;
                previousDistance = currentDistance;
            }

            if (nextCount < 3) return;
            Span<ClipVertex> swap = input;
            input = output;
            output = swap;
            count = nextCount;
        }

        ScreenVertex first = Project(input[0], canvasWidth, canvasHeight, transform);
        for (int index = 1; index < count - 1; index++)
        {
            Rasterise(first, Project(input[index], canvasWidth, canvasHeight, transform),
                Project(input[index + 1], canvasWidth, canvasHeight, transform),
                targetWidth, targetHeight, pixels, depth, colour, brightness, texture);
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

    private static ScreenVertex Project(ClipVertex vertex, float width, float height, Matrix3x2 transform)
    {
        Vector4 point = vertex.Position;
        float inverseW = 1 / point.W;
        var position = new Vector2(
            ((point.X * inverseW) + 1) * width * 0.5f,
            (1 - (point.Y * inverseW)) * height * 0.5f);
        position = Vector2.Transform(position, transform);
        return new ScreenVertex(position.X, position.Y, point.Z * inverseW, inverseW,
            vertex.World * inverseW, vertex.UV * inverseW);
    }

    private static void Rasterise(
        ScreenVertex a,
        ScreenVertex b,
        ScreenVertex c,
        int width,
        int height,
        byte[] pixels,
        float[] depth,
        uint colour,
        float brightness,
        InkMeshTexture? texture)
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
                        uint sampled = colour;
                        if (texture is not null)
                        {
                            float reciprocal = (weightA * a.InverseW) + (weightB * b.InverseW) + (weightC * c.InverseW);
                            Vector2 uv = ((weightA * a.UVOverW) + (weightB * b.UVOverW) + (weightC * c.UVOverW)) / reciprocal;
                            sampled = MultiplyColour(colour, SampleTexture(texture, uv));
                        }
                        pixels[offset] = Shade((byte)sampled, brightness);
                        pixels[offset + 1] = Shade((byte)(sampled >> 8), brightness);
                        pixels[offset + 2] = Shade((byte)(sampled >> 16), brightness);
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

    internal static Matrix4x4 WorldMatrix(InkComposite3DObject item, double seconds) =>
        Matrix4x4.CreateScale((float)item.ScaleX, (float)item.ScaleY, (float)item.ScaleZ) *
        Matrix4x4.CreateRotationX(AnimatedAngle(item.RotationX, item.SpinX, seconds)) *
        Matrix4x4.CreateRotationY(AnimatedAngle(item.RotationY, item.SpinY, seconds)) *
        Matrix4x4.CreateRotationZ(AnimatedAngle(item.RotationZ, item.SpinZ, seconds)) *
        Matrix4x4.CreateTranslation((float)item.X, (float)item.Y, (float)item.Z);

    internal static Triangle[] GetMesh(InkComposite3DObject item) => item.Shape switch
    {
        InkComposite3DShape.Cube => Cube,
        InkComposite3DShape.Sphere => Sphere,
        InkComposite3DShape.Plane => Plane,
        InkComposite3DShape.Mesh => item.Mesh!.Triangles.Select(triangle => new Triangle(
            triangle.A.Position, triangle.B.Position, triangle.C.Position, triangle.A.UV,
            triangle.B.UV, triangle.C.UV, triangle.Material)).ToArray(),
        _ => throw new InvalidOperationException("The 3D scene contains an unknown shape."),
    };

    private static uint MultiplyColour(uint a, uint b) => 0xFF000000 |
        ((uint)((((a >> 16) & 255) * ((b >> 16) & 255) + 127) / 255) << 16) |
        ((uint)((((a >> 8) & 255) * ((b >> 8) & 255) + 127) / 255) << 8) |
        (uint)(((a & 255) * (b & 255) + 127) / 255);

    private static uint SampleTexture(InkMeshTexture texture, Vector2 uv)
    {
        float u = uv.X - MathF.Floor(uv.X), v = uv.Y - MathF.Floor(uv.Y);
        int x = Math.Clamp((int)(u * texture.Width), 0, texture.Width - 1);
        int y = Math.Clamp((int)((1 - v) * texture.Height), 0, texture.Height - 1);
        int offset = ((y * texture.Width) + x) * 4;
        return 0xFF000000 | ((uint)texture.Pixels[offset + 2] << 16) |
            ((uint)texture.Pixels[offset + 1] << 8) | texture.Pixels[offset];
    }

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
            triangles.Add(new Triangle(a, b, c, new(0, 0), new(1, 0), new(1, 1)));
            triangles.Add(new Triangle(a, c, d, new(0, 0), new(1, 1), new(0, 1)));
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
            Vector2 UV(Vector3 point) => new(
                (MathF.Atan2(point.Z, point.X) / (2 * MathF.PI)) + 0.5f,
                (MathF.Asin(Math.Clamp(point.Y * 2, -1, 1)) / MathF.PI) + 0.5f);
            Vector2 uva = UV(a), uvb = UV(b), uvc = UV(c);
            if (Math.Max(uva.X, Math.Max(uvb.X, uvc.X)) - Math.Min(uva.X, Math.Min(uvb.X, uvc.X)) > 0.5f)
            {
                if (uva.X < 0.5f) uva.X += 1;
                if (uvb.X < 0.5f) uvb.X += 1;
                if (uvc.X < 0.5f) uvc.X += 1;
            }
            triangles.Add(new Triangle(a, b, c, uva, uvb, uvc));
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
