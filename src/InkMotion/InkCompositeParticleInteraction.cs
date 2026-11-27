using System.Numerics;

namespace InkMotion;

internal static class InkCompositeParticleInteraction
{
    internal readonly record struct Obstacle(Vector2 Centre, float Radius);
    private const double Step = 1d / 30;

    public static List<Obstacle> Project(InkCompositeScene3D scene, float width, float height)
    {
        Matrix4x4 projection = InkCompositeScene3DRenderer.ViewProjection(scene, width, height);
        float focal = 1 / MathF.Tan((float)(scene.FieldOfView * Math.PI / 360));
        var result = new List<Obstacle>();
        foreach (InkComposite3DObject item in scene.Objects.Where(item => item.Visible))
        {
            Vector4 point = Vector4.Transform(new Vector4((float)item.X, (float)item.Y, (float)item.Z, 1), projection);
            if (point.W <= 0.05f || point.Z < 0 || point.Z > point.W) continue;
            float radiusSquared = 0;
            void Include(Vector3 vertex)
            {
                var scaled = new Vector3(vertex.X * (float)item.ScaleX, vertex.Y * (float)item.ScaleY,
                    vertex.Z * (float)item.ScaleZ);
                radiusSquared = Math.Max(radiusSquared, scaled.LengthSquared());
            }
            foreach (InkCompositeScene3DRenderer.Triangle triangle in InkCompositeScene3DRenderer.GetMesh(item))
            {
                Include(triangle.A);
                Include(triangle.B);
                Include(triangle.C);
            }
            float radius = Math.Min(4, MathF.Sqrt(radiusSquared) * focal / (2 * point.W));
            result.Add(new Obstacle(new Vector2(((point.X / point.W) + 1) * width / (2 * height),
                (1 - (point.Y / point.W)) / 2), radius));
        }
        return result;
    }

    public static Vector2 Simulate(InkCompositeParticleSettings settings, Vector2 position, Vector2 velocity,
        double age, IReadOnlyList<Obstacle> obstacles)
    {
        int steps = (int)Math.Floor(age / Step);
        for (int step = 0; step <= steps; step++)
        {
            float dt = (float)Math.Min(Step, age - (step * Step));
            if (dt <= 1e-8f) break;
            Vector2 acceleration = new(0, (float)settings.Gravity);
            foreach (Obstacle obstacle in obstacles)
            {
                Vector2 delta = obstacle.Centre - position;
                float distanceSquared = delta.LengthSquared();
                if (distanceSquared > 1e-10f)
                    acceleration += Vector2.Normalize(delta) * ((float)settings.InteractionForce /
                        Math.Max(0.04f, distanceSquared));
            }
            if (acceleration.LengthSquared() > 400) acceleration = Vector2.Normalize(acceleration) * 20;
            velocity += acceleration * dt;
            if (settings.CollideWithObjects)
                MoveWithCollisions(ref position, ref velocity, dt, (float)settings.Size / 2,
                    (float)settings.Restitution, obstacles);
            else
                position += velocity * dt;
        }
        return position;
    }

    private static void MoveWithCollisions(ref Vector2 position, ref Vector2 velocity, float dt, float particleRadius,
        float restitution, IReadOnlyList<Obstacle> obstacles)
    {
        for (int iteration = 0; iteration < 4 && dt > 1e-7f; iteration++)
        {
            foreach (Obstacle obstacle in obstacles)
            {
                float radius = obstacle.Radius + particleRadius;
                Vector2 relative = position - obstacle.Centre;
                if (relative.LengthSquared() >= radius * radius) continue;
                Vector2 normal = relative.LengthSquared() > 1e-10f ? Vector2.Normalize(relative) : Vector2.UnitX;
                position = obstacle.Centre + (normal * (radius + 0.0001f));
                float inward = Vector2.Dot(velocity, normal);
                if (inward < 0) velocity -= (1 + restitution) * inward * normal;
            }
            float earliest = 1;
            Obstacle hit = default;
            bool found = false;
            Vector2 displacement = velocity * dt;
            float a = displacement.LengthSquared();
            foreach (Obstacle obstacle in obstacles)
            {
                float radius = obstacle.Radius + particleRadius;
                Vector2 relative = position - obstacle.Centre;
                float c = relative.LengthSquared() - (radius * radius);
                if (c < 0) continue;
                if (a < 1e-12f) continue;
                float b = 2 * Vector2.Dot(relative, displacement);
                float discriminant = (b * b) - (4 * a * c);
                if (b >= 0 || discriminant < 0) continue;
                float fraction = (-b - MathF.Sqrt(discriminant)) / (2 * a);
                if (fraction < 0 || fraction > earliest) continue;
                earliest = fraction;
                hit = obstacle;
                found = true;
            }
            if (!found)
            {
                position += velocity * dt;
                return;
            }
            position += velocity * (dt * earliest);
            Vector2 surfaceNormal = Vector2.Normalize(position - hit.Centre);
            velocity -= (1 + restitution) * Vector2.Dot(velocity, surfaceNormal) * surfaceNormal;
            position += surfaceNormal * 0.0001f;
            dt *= 1 - earliest;
        }
    }
}
