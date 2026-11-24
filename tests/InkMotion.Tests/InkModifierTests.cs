using Microsoft.Graphics.Canvas;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Xunit;

namespace InkMotion.Tests;

public sealed class InkModifierTests
{
    private static EditorProject Project() => new()
    {
        Frames = [new()
        {
            Strokes = [
                InkStrokeEditing.CreateShape(InkShape.Rectangle, 0.1, 0.2, 0.5, 0.8,
                    1, 0xFFFF0000, filled: true),
            ],
        }],
    };

    private static List<InkPoint> SmudgeStroke() =>
    [
        new() { X = 0.45, Y = 0.5, BrushWidth = 12, Pressure = 1, BrushTexture = InkBrushTexture.Smudge },
        new() { X = 0.75, Y = 0.5, BrushWidth = 12, Pressure = 1, BrushTexture = InkBrushTexture.Smudge },
    ];

    private static byte[] Render(CanvasDevice device, EditorProject project)
    {
        using var target = new CanvasRenderTarget(device, 100, 80, 96);
        using (CanvasDrawingSession session = target.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Transparent);
            if (!TimelineCompositor.HasNonNormalBlendModes(project))
                TimelineCompositor.DrawInkFrame(session, project, 0, 100, 80, 1);
        }
        if (TimelineCompositor.HasNonNormalBlendModes(project))
        {
            using CanvasRenderTarget composite = TimelineCompositor.CompositeInkFrame(
                device, target, 100, 80, project, 0, 100, 80, 1);
            return composite.GetPixelBytes();
        }
        return target.GetPixelBytes();
    }

    [Fact]
    public void SmudgePullsExistingColourAndLeavesOriginalPathsEditable()
    {
        using var device = new CanvasDevice(true);
        EditorProject project = Project();
        byte[] before = Render(device, project);
        string original = EditorProjectSerialiser.Serialise(project);
        project.Frames[0].Strokes.Add(SmudgeStroke());
        byte[] after = Render(device, project);
        int outside = (40 * 100 + 54) * 4;
        Assert.Equal(0, before[outside + 3]);
        Assert.True(after[outside + 3] > 0);
        Assert.True(after[outside + 2] > 0);
        Assert.Equal(0, after[outside]);
        Assert.Equal(before.Take(20 * 100 * 4), after.Take(20 * 100 * 4));
        EditorProject restored = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        Assert.Equal(after, Render(device, restored));
        project.Frames[0].Strokes.RemoveAt(1);
        Assert.Equal(original, EditorProjectSerialiser.Serialise(project));
        Assert.Equal(before, Render(device, project));
    }

    [Fact]
    public void SmudgeSamplesOnlyEarlierPixelsOnItsOwnLayerAndStationaryTapsDoNothing()
    {
        using var device = new CanvasDevice(true);
        EditorProject project = Project();
        byte[] before = Render(device, project);
        project.InkLayers.Add(new() { Id = "upper" });
        project.Frames[0].LayerStrokes["upper"] = [SmudgeStroke()];
        Assert.Equal(before, Render(device, project));
        project.Frames[0].Strokes.Insert(0, SmudgeStroke());
        Assert.Equal(before, Render(device, project));
        project.Frames[0].Strokes.Add([SmudgeStroke()[0]]);
        Assert.Equal(before, Render(device, project));
    }

    [Fact]
    public void SmudgedColourSamplingMatchesLayerRendering()
    {
        using var device = new CanvasDevice(true);
        EditorProject project = Project();
        project.Frames[0].Strokes.Add(SmudgeStroke());
        byte[] pixels = Render(device, project);
        for (int x = 45; x < 70; x += 5)
        {
            int offset = (40 * 100 + x) * 4;
            Assert.Equal(InkColourSampling.FromPremultipliedBgra(
                pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3]),
                InkColourSampling.Sample(device, project, 0, 100, 80, x, 40));
        }
    }

    [Fact]
    public void MergeRejectsUpperSmudgeWithoutChangingTheProject()
    {
        EditorProject project = Project();
        project.InkLayers.Add(new() { Id = "upper" });
        project.Frames[0].LayerStrokes["upper"] = [SmudgeStroke()];
        string before = EditorProjectSerialiser.Serialise(project);
        Assert.Throws<InvalidOperationException>(() =>
            InkEditing.MergeLayerDown(project.InkLayers, project.Frames, "upper"));
        Assert.Equal(before, EditorProjectSerialiser.Serialise(project));
    }

    [Theory]
    [InlineData(InkLayerBlendMode.Normal)]
    [InlineData(InkLayerBlendMode.Screen)]
    public async Task SmudgePngExportMatchesPreviewReplay(InkLayerBlendMode blendMode)
    {
        using var device = new CanvasDevice(true);
        EditorProject project = Project();
        project.InkLayers[0].BlendMode = blendMode;
        project.Frames[0].Strokes.Add(SmudgeStroke());
        string root = Path.Combine(Path.GetTempPath(), $"InkMotion smudge {Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string output = Path.Combine(root, "sequence");
        try
        {
            await PngSequenceExporter.ExportAsync(project,
                new(VideoExportProfile.Create(100, 80, 24), 80), output, null, CancellationToken.None);
            StorageFile frame = await StorageFile.GetFileFromPathAsync(Path.Combine(output, "frame_000001.png"));
            using IRandomAccessStream stream = await frame.OpenAsync(FileAccessMode.Read);
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
            byte[] pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage)).DetachPixelData();
            byte[] preview = Render(device, project);
            Assert.Equal(preview.Length, pixels.Length);
            for (int index = 0; index < pixels.Length; index++)
                Assert.InRange(Math.Abs(preview[index] - pixels[index]), 0, index % 4 == 3 ? 0 : 1);
        }
        finally
        {
            foreach (string file in Directory.Exists(output) ? Directory.GetFiles(output) : [])
                File.Delete(file);
            if (Directory.Exists(output)) Directory.Delete(output);
            Directory.Delete(root);
        }
    }
}
