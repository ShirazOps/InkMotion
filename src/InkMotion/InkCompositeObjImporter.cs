using System.Globalization;
using System.Numerics;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace InkMotion;

internal static class InkCompositeObjImporter
{
    private const int MaximumFileBytes = 8 * 1024 * 1024;

    public static async Task<InkCompositeMesh> ImportAsync(string path)
    {
        string root = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string text = await ReadTextAsync(path);
        var materials = new List<InkMeshMaterial> { new() };
        foreach (string line in Lines(text))
        {
            (string command, string value) = Split(line);
            if (command != "mtllib") continue;
            // One library per directive permits filenames containing spaces.
            string library = ResolveLocalPath(root, value);
            List<InkMeshMaterial> imported = ParseMaterials(await ReadTextAsync(library), out List<string?> maps);
            for (int index = 0; index < imported.Count; index++)
            {
                if (maps[index] is string map)
                    imported[index].Texture = await LoadTextureAsync(ResolveLocalPath(root,
                        Path.GetRelativePath(root, Path.Combine(Path.GetDirectoryName(library)!, map))));
                materials.Add(imported[index]);
                if (materials.Count > InkCompositeMesh.MaximumMaterials)
                    throw new InvalidDataException("The OBJ has too many materials (maximum 32 including the default).");
                if (materials.Sum(item => (long)(item.Texture?.Pixels.Length ?? 0)) > InkCompositeMesh.MaximumTextureBytes)
                    throw new InvalidDataException("The OBJ textures exceed the embedded 8 MiB limit.");
            }
        }
        return Parse(text, Path.GetFileName(path), materials);
    }

    internal static InkCompositeMesh Parse(string text, string name, List<InkMeshMaterial>? materials = null)
    {
        var mesh = new InkCompositeMesh { Name = name, Materials = materials ?? [new()] };
        var positions = new List<Vector3>();
        var coordinates = new List<Vector2>();
        int material = 0;
        foreach (string line in Lines(text))
        {
            (string command, string value) = Split(line);
            string[] parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (command)
            {
                case "v":
                    if (parts.Length != 3) throw new InvalidDataException("OBJ vertices must have three coordinates.");
                    positions.Add(new Vector3(Number(parts[0]), Number(parts[1]), Number(parts[2])));
                    if (positions.Count > 16384) throw new InvalidDataException("The OBJ has too many vertices (maximum 16,384).");
                    break;
                case "vt":
                    if (parts.Length is < 2 or > 3) throw new InvalidDataException("OBJ texture coordinates must have two or three values.");
                    coordinates.Add(new Vector2(Number(parts[0]), Number(parts[1])));
                    if (coordinates.Count > 16384) throw new InvalidDataException("The OBJ has too many texture coordinates.");
                    break;
                case "usemtl":
                    material = mesh.Materials.FindIndex(item => item.Name == value);
                    if (material < 0) throw new InvalidDataException($"The OBJ material '{value}' is missing.");
                    break;
                case "f":
                    if (parts.Length is < 3 or > 64) throw new InvalidDataException("OBJ faces must have 3–64 vertices.");
                    var face = new List<InkMeshVertex>();
                    foreach (string part in parts)
                    {
                        string[] indices = part.Split('/');
                        if (indices.Length > 3) throw new InvalidDataException("An OBJ face index is malformed.");
                        Vector3 position = positions[Index(indices[0], positions.Count)];
                        bool hasUV = indices.Length >= 2 && indices[1].Length > 0;
                        if (!hasUV && mesh.Materials[material].Texture is not null)
                            throw new InvalidDataException("A textured OBJ face is missing texture coordinates.");
                        Vector2 uv = hasUV ? coordinates[Index(indices[1], coordinates.Count)] : Vector2.Zero;
                        face.Add(new InkMeshVertex(position.X, position.Y, position.Z, uv.X, uv.Y));
                    }
                    ValidateFace(face);
                    for (int index = 1; index < face.Count - 1; index++)
                    {
                        mesh.Triangles.Add(new InkMeshTriangle(face[0], face[index], face[index + 1], material));
                        if (mesh.Triangles.Count > InkCompositeMesh.MaximumTriangles)
                            throw new InvalidDataException("The OBJ exceeds the 4,096 triangle limit. Simplify it before importing.");
                    }
                    break;
                case "l" or "p" or "curv" or "surf":
                    throw new InvalidDataException("Only OBJ polygon meshes are supported, not lines, points or curves.");
            }
        }
        if (!mesh.IsValid()) throw new InvalidDataException("The OBJ contains empty, degenerate or out-of-range mesh data.");
        return mesh;
    }

    internal static List<InkMeshMaterial> ParseMaterials(string text, out List<string?> maps)
    {
        var materials = new List<InkMeshMaterial>();
        maps = [];
        foreach (string line in Lines(text))
        {
            (string command, string value) = Split(line);
            if (command == "newmtl")
            {
                if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || materials.Any(item => item.Name == value))
                    throw new InvalidDataException("MTL material names must be unique and at most 256 characters.");
                materials.Add(new InkMeshMaterial { Name = value });
                maps.Add(null);
                if (materials.Count >= InkCompositeMesh.MaximumMaterials)
                    throw new InvalidDataException("The MTL has too many materials.");
            }
            else if (command is "Kd" or "map_Kd" or "d" or "Tr")
            {
                if (materials.Count == 0) throw new InvalidDataException("An MTL property has no material.");
                if (command == "Kd")
                {
                    string[] parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 3) throw new InvalidDataException("MTL diffuse colour requires three values.");
                    uint Channel(string part)
                    {
                        float channel = Number(part);
                        if (channel < 0 || channel > 1) throw new InvalidDataException("MTL diffuse colours must be between 0 and 1.");
                        return (uint)Math.Round(channel * 255);
                    }
                    materials[^1].Colour = 0xFF000000 | (Channel(parts[0]) << 16) | (Channel(parts[1]) << 8) | Channel(parts[2]);
                }
                else if (command == "map_Kd")
                {
                    if (value.StartsWith('-')) throw new InvalidDataException("MTL map options are unsupported; use a plain map_Kd filename.");
                    maps[^1] = value;
                }
                else if (Number(value) != (command == "d" ? 1 : 0))
                    throw new InvalidDataException("Transparent MTL materials are unsupported; use opaque materials.");
            }
        }
        return materials;
    }

    public static async Task<InkMeshTexture> LoadTextureAsync(string path)
    {
        if (new FileInfo(path).Length > MaximumFileBytes)
            throw new InvalidDataException("A texture file exceeds the 8 MiB import limit.");
        if (!new[] { ".png", ".jpg", ".jpeg", ".bmp" }.Contains(Path.GetExtension(path).ToLowerInvariant()))
            throw new InvalidDataException("Diffuse textures must be PNG, JPEG or BMP files.");
        StorageFile file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenReadAsync();
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        if ((long)decoder.PixelWidth * decoder.PixelHeight > 33_500_000)
            throw new InvalidDataException("A texture exceeds the 33.5 megapixel decode limit.");
        double scale = Math.Min(1, 512d / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        uint width = Math.Max(1, (uint)Math.Round(decoder.PixelWidth * scale));
        uint height = Math.Max(1, (uint)Math.Round(decoder.PixelHeight * scale));
        PixelDataProvider data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            new BitmapTransform { ScaledWidth = width, ScaledHeight = height, InterpolationMode = BitmapInterpolationMode.Linear },
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb);
        return new InkMeshTexture { Width = (int)width, Height = (int)height, Pixels = data.DetachPixelData() };
    }

    internal static string ResolveLocalPath(string root, string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || Path.IsPathRooted(reference) || reference.Contains(':'))
            throw new InvalidDataException("OBJ assets must use local relative filenames.");
        string path = Path.GetFullPath(Path.Combine(root, reference));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("OBJ assets must be inside the OBJ folder or its subfolders.");
        return path;
    }

    private static async Task<string> ReadTextAsync(string path)
    {
        if (new FileInfo(path).Length > MaximumFileBytes)
            throw new InvalidDataException("An OBJ or MTL file exceeds the 8 MiB import limit.");
        return await File.ReadAllTextAsync(path);
    }

    private static IEnumerable<string> Lines(string text) => text.Split('\n')
        .Select(line => line.Split('#')[0].Trim()).Where(line => line.Length > 0);

    private static (string, string) Split(string line)
    {
        int index = line.IndexOfAny([' ', '\t']);
        return index < 0 ? (line, "") : (line[..index], line[(index + 1)..].Trim());
    }

    private static float Number(string text)
    {
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || !float.IsFinite(value))
            throw new InvalidDataException("OBJ/MTL numbers must be finite decimal values.");
        return value;
    }

    private static int Index(string text, int count)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value == 0)
            throw new InvalidDataException("OBJ indices must be non-zero integers.");
        long index = value > 0 ? (long)value - 1 : (long)count + value;
        if (index < 0 || index >= count) throw new InvalidDataException("An OBJ index is out of range.");
        return (int)index;
    }

    private static void ValidateFace(List<InkMeshVertex> face)
    {
        Vector3 normal = Vector3.Cross(face[1].Position - face[0].Position, face[2].Position - face[0].Position);
        if (normal.LengthSquared() < 1e-12f) throw new InvalidDataException("An OBJ face is degenerate.");
        normal = Vector3.Normalize(normal);
        for (int index = 0; index < face.Count; index++)
        {
            Vector3 a = face[index].Position, b = face[(index + 1) % face.Count].Position,
                c = face[(index + 2) % face.Count].Position;
            if (Math.Abs(Vector3.Dot(a - face[0].Position, normal)) > 0.001f ||
                Vector3.Dot(Vector3.Cross(b - a, c - b), normal) <= 1e-8f)
                throw new InvalidDataException("OBJ faces must be planar and convex. Triangulate concave faces before importing.");
        }
    }
}
