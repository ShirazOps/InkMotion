using Microsoft.Graphics.Canvas;
using System.Numerics;
using Windows.UI;

namespace InkMotion;

internal static class InkCompositeParticleRenderer
{
    public static void Draw(CanvasDrawingSession session, InkCompositeParticleSettings settings,
        float width, float height, double seconds, InkCompositeScene3D? scene = null)
    {
        if (!InkCompositeParticles.IsValid(settings))
            throw new ArgumentException("The particle emitter has invalid settings.", nameof(settings));
        if (!double.IsFinite(seconds) || seconds * settings.Rate >= 9007199254740991d)
            throw new ArgumentOutOfRangeException(nameof(seconds), "The particle time must be finite and within the supported range.");
        if (seconds < 0) return;

        List<InkCompositeParticleInteraction.Obstacle>? obstacles = scene is not null &&
            (settings.CollideWithObjects || settings.InteractionForce != 0)
            ? InkCompositeParticleInteraction.Project(scene, width, height) : null;
        long last = (long)Math.Floor(seconds * settings.Rate);
        long first = Math.Max(0, (long)Math.Floor((seconds - settings.Lifetime) * settings.Rate) + 1);
        for (long index = first; index <= last; index++)
        {
            double age = seconds - (index / (double)settings.Rate);
            byte alpha = (byte)Math.Clamp((int)Math.Round(255 * (1 - (age / settings.Lifetime))), 0, 255);
            if (alpha == 0) continue;

            double angle = (settings.Direction + ((RandomFraction(index, settings.Seed) - 0.5) * settings.Spread))
                * Math.PI / 180;
            double distance = settings.Speed * height * age;
            float x = (float)((settings.EmitterX * width) + (Math.Sin(angle) * distance));
            float y = (float)((settings.EmitterY * height) - (Math.Cos(angle) * distance) +
                (0.5 * settings.Gravity * height * age * age));
            if (obstacles is { Count: > 0 })
            {
                var start = new Vector2((float)(settings.EmitterX * width / height), (float)settings.EmitterY);
                var velocity = new Vector2((float)(Math.Sin(angle) * settings.Speed),
                    (float)(-Math.Cos(angle) * settings.Speed));
                Vector2 position = InkCompositeParticleInteraction.Simulate(settings, start, velocity, age, obstacles);
                x = position.X * height;
                y = position.Y * height;
            }
            var colour = Color.FromArgb(alpha, (byte)(settings.Colour >> 16),
                (byte)(settings.Colour >> 8), (byte)settings.Colour);
            session.FillCircle(x, y, (float)(settings.Size * height / 2), colour);
        }
    }

    private static double RandomFraction(long index, int seed)
    {
        unchecked
        {
            ulong value = (ulong)index + ((ulong)(uint)seed * 0x9E3779B97F4A7C15UL);
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            value ^= value >> 31;
            return (value >> 11) * (1d / 9007199254740992d);
        }
    }
}
