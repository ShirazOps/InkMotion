using System.Numerics;
using Xunit;

namespace InkMotion.Tests;

public sealed class InkCompositeMeshTests
{
    private const string Quad = """
        v -1 -1 0
        v 1 -1 0
        v 1 1 0
        v -1 1 0
        vt 0 0
        vt 1 0
        vt 1 1
        vt 0 1
        f -4/-4 -3/-3 -2/-2 -1/-1
        """;

    [Fact]
    public void ObjNegativeIndicesConvexFacesAndMaterialsImport()
    {
        List<InkMeshMaterial> materials = InkCompositeObjImporter.ParseMaterials(
            "newmtl brick\nKd 0.5 0.25 1\nmap_Kd brick.png", out List<string?> maps);
        InkCompositeMesh mesh = InkCompositeObjImporter.Parse(Quad.Replace("f -4", "usemtl brick\nf -4"), "quad.obj", materials);
        Assert.True(mesh.IsValid());
        Assert.Equal(2, mesh.Triangles.Count);
        Assert.Equal(new Vector3(-1, -1, 0), mesh.Triangles[0].A.Position);
        Assert.Equal(new Vector2(1, 1), mesh.Triangles[0].C.UV);
        Assert.Equal(0xFF8040FFu, mesh.Materials[0].Colour);
        Assert.Equal("brick.png", maps[0]);
    }

    [Theory]
    [InlineData("v NaN 0 0\nf 1 1 1")]
    [InlineData("v 0 0 0\nf 0 1 1")]
    [InlineData("v 0 0 0\nf -2 1 1")]
    [InlineData("v 0 0 0\nv 1 0 0\nv 2 0 0\nf 1 2 3")]
    [InlineData("usemtl missing")]
    [InlineData("v 0 0 0\nl 1 1")]
    public void MalformedOrUnsupportedObjIsRejected(string text) =>
        Assert.Throws<InvalidDataException>(() => InkCompositeObjImporter.Parse(text, "bad.obj"));

    [Fact]
    public void ImportLimitsAndUnsafePathsAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => InkCompositeObjImporter.Parse(
            Quad + string.Concat(Enumerable.Repeat("\nf 1 2 3", 4096)), "large.obj"));
        string root = Path.GetFullPath(".");
        Assert.Throws<InvalidDataException>(() => InkCompositeObjImporter.ResolveLocalPath(root, "..\\escape.png"));
        Assert.Throws<InvalidDataException>(() => InkCompositeObjImporter.ResolveLocalPath(root, "https://example.com/a.png"));
        Assert.Throws<InvalidDataException>(() => InkCompositeObjImporter.ParseMaterials("newmtl a\nd 0.5", out _));
        Assert.Throws<InvalidDataException>(() => InkCompositeObjImporter.ParseMaterials("newmtl a\nmap_Kd -s 2 2 a.png", out _));
    }

    [Fact]
    public async Task ObjImportLoadsMtlAndTextureFromDisk()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"inkmotion-obj-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string obj = Path.Combine(directory, "mesh.obj");
        string mtl = Path.Combine(directory, "material.mtl");
        string bmp = Path.Combine(directory, "texture.bmp");
        try
        {
            await File.WriteAllTextAsync(obj, "mtllib material.mtl\nusemtl brick\n" + Quad);
            await File.WriteAllTextAsync(mtl, "newmtl brick\nKd 1 1 1\nmap_Kd texture.bmp");
            using (var stream = File.Create(bmp))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)'B'); writer.Write((byte)'M');
                writer.Write(58); writer.Write(0); writer.Write(54);
                writer.Write(40); writer.Write(1); writer.Write(1);
                writer.Write((short)1); writer.Write((short)24);
                writer.Write(0); writer.Write(4); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
                writer.Write(new byte[] { 7, 11, 23, 0 });
            }
            InkCompositeMesh mesh = await InkCompositeObjImporter.ImportAsync(obj);
            InkMeshTexture texture = mesh.Materials[1].Texture!;
            Assert.Equal(1, texture.Width);
            Assert.Equal(1, texture.Height);
            Assert.Equal(new byte[] { 7, 11, 23 }, texture.Pixels[..3]);
            Assert.Equal(1, mesh.Triangles[0].Material);
            await File.WriteAllTextAsync(obj, "v NaN 0 0\n");
            await Assert.ThrowsAsync<InvalidDataException>(() => InkCompositeObjImporter.ImportAsync(obj));
        }
        finally
        {
            File.Delete(obj);
            File.Delete(mtl);
            File.Delete(bmp);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void EmbeddedMeshTextureRoundTripsClonesAndMigrates()
    {
        InkCompositeMesh mesh = InkCompositeObjImporter.Parse(Quad, "quad.obj");
        mesh.Materials[0].Texture = new InkMeshTexture { Width = 1, Height = 1, Pixels = [20, 40, 80, 255] };
        var node = InkCompositeGraphs.CreateNode(InkCompositeNodeKind.Scene3D, "scene", 0, 0);
        node.Scene3D!.Objects = [new() { Shape = InkComposite3DShape.Mesh, Mesh = mesh, Colour = 0xFFFFFFFF }];
        var output = InkCompositeGraphs.CreateNode(InkCompositeNodeKind.Output, "out", 0, 0);
        output.Inputs[0] = "scene";
        var project = new EditorProject { CompositeGraph = new() { Enabled = true, Nodes = [node, output] } };
        EditorProject restored = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        InkCompositeMesh copy = restored.CompositeGraph!.Nodes[0].Scene3D!.Objects[0].Mesh!;
        Assert.Equal(mesh.Triangles, copy.Triangles);
        Assert.Equal(mesh.Materials[0].Texture!.Pixels, copy.Materials[0].Texture!.Pixels);
        InkCompositeMesh cloned = mesh.Clone();
        cloned.Materials[0].Texture!.Pixels[0] = 99;
        Assert.Equal(20, mesh.Materials[0].Texture!.Pixels[0]);
        mesh.Triangles[0] = mesh.Triangles[0] with { Material = 100 };
        Assert.False(mesh.IsValid());
        EditorProject legacy = EditorProjectSerialiser.Deserialise("""{"Version":90}""");
        Assert.Equal(EditorProject.CurrentVersion, legacy.Version);
    }

    [Fact]
    public void TexturedMeshUsesUvOrientationAndOutputScaling()
    {
        InkCompositeMesh mesh = InkCompositeObjImporter.Parse(Quad, "quad.obj");
        mesh.Materials[0].Texture = new InkMeshTexture
        {
            Width = 2, Height = 2,
            Pixels = [0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255],
        };
        var scene = new InkCompositeScene3D
        {
            CameraYaw = 0, CameraPitch = 0, AmbientLight = 1,
            Objects = [new() { Shape = InkComposite3DShape.Mesh, Mesh = mesh, Colour = 0xFFFFFFFF }],
        };
        byte[] preview = InkCompositeScene3DRenderer.Render(scene, 100, 100, 100, 100, Matrix3x2.Identity, 0);
        byte[] export = InkCompositeScene3DRenderer.Render(scene, 200, 200, 100, 100, Matrix3x2.CreateScale(2), 0);
        foreach ((int x, int y, byte r, byte g, byte b) in new[]
        {
            (40, 40, (byte)255, (byte)0, (byte)0), (60, 40, (byte)0, (byte)255, (byte)0),
            (40, 60, (byte)0, (byte)0, (byte)255), (60, 60, (byte)255, (byte)255, (byte)255),
        })
        {
            int offset = ((y * 100) + x) * 4, scaled = (((y * 2) * 200) + (x * 2)) * 4;
            Assert.Equal(new byte[] { b, g, r, 255 }, preview[offset..(offset + 4)]);
            Assert.Equal(preview[offset..(offset + 4)], export[scaled..(scaled + 4)]);
        }
    }
}
