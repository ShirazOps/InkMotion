using System.Numerics;
using System.Text.Json.Serialization;

namespace InkMotion;

public readonly record struct InkMeshVertex(float X, float Y, float Z, float U = 0, float V = 0)
{
    [JsonIgnore]
    public Vector3 Position => new(X, Y, Z);
    [JsonIgnore]
    public Vector2 UV => new(U, V);
}

public sealed record InkMeshTriangle(InkMeshVertex A, InkMeshVertex B, InkMeshVertex C, int Material = 0);

public sealed class InkMeshTexture
{
    public int Width { get; set; }
    public int Height { get; set; }
    public byte[] Pixels { get; set; } = [];

    public InkMeshTexture Clone() => new() { Width = Width, Height = Height, Pixels = [.. Pixels] };

    public bool IsValid() => Width is >= 1 and <= 512 && Height is >= 1 and <= 512 &&
        Pixels is not null && Pixels.Length == Width * Height * 4;
}

public sealed class InkMeshMaterial
{
    public string Name { get; set; } = "";
    public uint Colour { get; set; } = 0xFFFFFFFF;
    public InkMeshTexture? Texture { get; set; }

    public InkMeshMaterial Clone() => new() { Name = Name, Colour = Colour, Texture = Texture?.Clone() };
}

public sealed class InkCompositeMesh
{
    public const int MaximumTriangles = 4096;
    public const int MaximumMaterials = 32;
    public const int MaximumTextureBytes = 8 * 1024 * 1024;
    public string Name { get; set; } = "";
    public List<InkMeshTriangle> Triangles { get; set; } = [];
    public List<InkMeshMaterial> Materials { get; set; } = [new()];

    public InkCompositeMesh Clone() => new()
    {
        Name = Name, Triangles = [.. Triangles], Materials = Materials.Select(item => item.Clone()).ToList(),
    };

    public bool IsValid() => Name is not null && Name.Length <= 256 &&
        Triangles is { Count: > 0 and <= MaximumTriangles } &&
        Materials is { Count: > 0 and <= MaximumMaterials } &&
        Materials.All(item => item is not null && item.Name is not null && item.Name.Length <= 256 &&
            (item.Colour >> 24) == 255 && (item.Texture is null || item.Texture.IsValid())) &&
        Materials.Sum(item => (long)(item.Texture?.Pixels.Length ?? 0)) <= MaximumTextureBytes &&
        Triangles.All(item => item is not null && item.Material >= 0 && item.Material < Materials.Count &&
            ValidVertex(item.A) && ValidVertex(item.B) && ValidVertex(item.C) &&
            Vector3.Cross(item.B.Position - item.A.Position, item.C.Position - item.A.Position).LengthSquared() > 1e-12f);

    private static bool ValidVertex(InkMeshVertex vertex) =>
        float.IsFinite(vertex.X) && float.IsFinite(vertex.Y) && float.IsFinite(vertex.Z) &&
        Math.Abs(vertex.X) <= 10000 && Math.Abs(vertex.Y) <= 10000 && Math.Abs(vertex.Z) <= 10000 &&
        float.IsFinite(vertex.U) && float.IsFinite(vertex.V) &&
        Math.Abs(vertex.U) <= 10000 && Math.Abs(vertex.V) <= 10000;
}
