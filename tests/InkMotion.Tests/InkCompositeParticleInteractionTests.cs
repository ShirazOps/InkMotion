using System.Numerics;
using Xunit;

namespace InkMotion.Tests;

public sealed class InkCompositeParticleInteractionTests
{
    [Fact]
    public void ForcesAttractOrRepelAndRemainDeterministic()
    {
        var obstacles = new[] { new InkCompositeParticleInteraction.Obstacle(new Vector2(1, 0), 0.1f) };
        var settings = new InkCompositeParticleSettings { Gravity = 0, InteractionForce = 0.1 };
        Vector2 attracted = InkCompositeParticleInteraction.Simulate(settings, Vector2.Zero, Vector2.Zero, 1, obstacles);
        Assert.True(attracted.X > 0);
        Assert.Equal(attracted, InkCompositeParticleInteraction.Simulate(settings, Vector2.Zero, Vector2.Zero, 1, obstacles));
        settings.InteractionForce = -0.1;
        Vector2 repelled = InkCompositeParticleInteraction.Simulate(settings, Vector2.Zero, Vector2.Zero, 1, obstacles);
        Assert.True(repelled.X < 0);
    }

    [Fact]
    public void SweptCollisionBouncesEvenWhenMovingQuickly()
    {
        var obstacles = new[] { new InkCompositeParticleInteraction.Obstacle(new Vector2(1, 0), 0.1f) };
        var settings = new InkCompositeParticleSettings { Gravity = 0, CollideWithObjects = true, Restitution = 1 };
        Vector2 bounced = InkCompositeParticleInteraction.Simulate(settings, Vector2.Zero, new Vector2(2, 0), 1, obstacles);
        Assert.True(bounced.X < 0.8f, $"Unexpected position: {bounced}");
        settings.CollideWithObjects = false;
        Vector2 unimpeded = InkCompositeParticleInteraction.Simulate(settings, Vector2.Zero, new Vector2(2, 0), 1, obstacles);
        Assert.InRange(unimpeded.X, 1.99f, 2.01f);
    }

    [Fact]
    public void SceneProjectionUsesCameraAndIgnoresInvisibleObjects()
    {
        var scene = new InkCompositeScene3D
        {
            CameraYaw = 0, CameraPitch = 0,
            Objects = [new() { Shape = InkComposite3DShape.Cube }, new() { Visible = false }],
        };
        List<InkCompositeParticleInteraction.Obstacle> obstacles = InkCompositeParticleInteraction.Project(scene, 200, 100);
        InkCompositeParticleInteraction.Obstacle obstacle = Assert.Single(obstacles);
        Assert.InRange(obstacle.Centre.X, 0.999f, 1.001f);
        Assert.InRange(obstacle.Centre.Y, 0.499f, 0.501f);
        Assert.True(obstacle.Radius > 0);
    }

    [Fact]
    public void InteractionScenePersistsAndRejectsMissingOrWrongNode()
    {
        var particle = InkCompositeGraphs.CreateNode(InkCompositeNodeKind.Particles, "particles", 0, 0);
        particle.Particles!.InteractionSceneId = "scene";
        particle.Particles.InteractionForce = -0.4;
        particle.Particles.CollideWithObjects = true;
        var scene = InkCompositeGraphs.CreateNode(InkCompositeNodeKind.Scene3D, "scene", 0, 0);
        var output = InkCompositeGraphs.CreateNode(InkCompositeNodeKind.Output, "output", 0, 0);
        output.Inputs[0] = particle.Id;
        var project = new EditorProject { CompositeGraph = new() { Enabled = true, Nodes = [particle, scene, output] } };
        Assert.Null(InkCompositeGraphs.Validate(project.CompositeGraph));
        EditorProject restored = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        InkCompositeParticleSettings saved = restored.CompositeGraph!.Nodes[0].Particles!;
        Assert.Equal("scene", saved.InteractionSceneId);
        Assert.Equal(-0.4, saved.InteractionForce);
        Assert.True(saved.CollideWithObjects);
        project.CompositeGraph.Nodes.Remove(scene);
        Assert.NotNull(InkCompositeGraphs.Validate(project.CompositeGraph));
        project.CompositeGraph.Nodes.Add(InkCompositeGraphs.CreateNode(InkCompositeNodeKind.Picture, "scene", 0, 0));
        Assert.NotNull(InkCompositeGraphs.Validate(project.CompositeGraph));
        EditorProject oldProject = EditorProjectSerialiser.Deserialise("""{"Version":92}""");
        Assert.Equal(EditorProject.CurrentVersion, oldProject.Version);
        Assert.Null(new InkCompositeParticleSettings().InteractionSceneId);
    }
}
