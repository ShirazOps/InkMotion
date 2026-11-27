using System.Numerics;
using Xunit;

namespace InkMotion.Tests;

public sealed class InkCompositeShadowTests
{
    [Fact]
    public void DirectionalShadowsRespectCastingAndReceiving()
    {
        var receiver = new InkComposite3DObject
        {
            Shape = InkComposite3DShape.Plane, ScaleX = 4, ScaleY = 4, Colour = 0xFFFFFFFF,
            CastsShadow = false,
        };
        var caster = new InkComposite3DObject
        {
            Shape = InkComposite3DShape.Plane, Z = 0.6, Colour = 0xFFFFFFFF,
        };
        var scene = new InkCompositeScene3D
        {
            Objects = [receiver, caster], CameraYaw = 0, CameraPitch = 0,
            LightYaw = 45, LightPitch = 0, AmbientLight = 0.2, Shadows = true, ShadowStrength = 1,
        };
        byte[] shadowed = Render(scene);
        caster.CastsShadow = false;
        byte[] unshadowed = Render(scene);
        Assert.True(Enumerable.Range(0, shadowed.Length / 4).Any(index =>
            shadowed[(index * 4) + 3] == 255 && shadowed[index * 4] + 30 < unshadowed[index * 4]));
        caster.CastsShadow = true;
        receiver.ReceivesShadow = false;
        Assert.Equal(unshadowed, Render(scene));
        receiver.ReceivesShadow = true;
        scene.Shadows = false;
        Assert.Equal(unshadowed, Render(scene));
    }

    [Fact]
    public void ShadowSettingsCloneAndLegacyScenesRemainUnshadowed()
    {
        var scene = new InkCompositeScene3D { Shadows = true, ShadowStrength = 0.4 };
        scene.Objects[0].CastsShadow = false;
        scene.Objects[0].ReceivesShadow = false;
        InkCompositeScene3D copy = scene.Clone();
        Assert.True(copy.Shadows);
        Assert.Equal(0.4, copy.ShadowStrength);
        Assert.False(copy.Objects[0].CastsShadow);
        Assert.False(copy.Objects[0].ReceivesShadow);
        var node = InkCompositeGraphs.CreateNode(InkCompositeNodeKind.Scene3D, "scene", 0, 0);
        node.Scene3D = scene;
        var output = InkCompositeGraphs.CreateNode(InkCompositeNodeKind.Output, "output", 0, 0);
        output.Inputs[0] = node.Id;
        var project = new EditorProject { CompositeGraph = new() { Enabled = true, Nodes = [node, output] } };
        InkCompositeScene3D restored = EditorProjectSerialiser.Deserialise(
            EditorProjectSerialiser.Serialise(project)).CompositeGraph!.Nodes[0].Scene3D!;
        Assert.True(restored.Shadows);
        Assert.Equal(0.4, restored.ShadowStrength);
        Assert.False(restored.Objects[0].CastsShadow);
        Assert.False(restored.Objects[0].ReceivesShadow);
        EditorProject oldProject = EditorProjectSerialiser.Deserialise("""{"Version":91}""");
        Assert.Equal(EditorProject.CurrentVersion, oldProject.Version);
        Assert.False(new InkCompositeScene3D().Shadows);
    }

    private static byte[] Render(InkCompositeScene3D scene) =>
        InkCompositeScene3DRenderer.Render(scene, 100, 100, 100, 100, Matrix3x2.Identity, 0);
}
