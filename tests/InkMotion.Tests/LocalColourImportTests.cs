using Microsoft.Graphics.Canvas;
using Xunit;

namespace InkMotion.Tests;

public sealed class OcioImportFactAttribute : FactAttribute
{
    public OcioImportFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("INKMOTION_TEST_OIIOTOOL")))
            Skip = "Optional real ACES import: set INKMOTION_TEST_OIIOTOOL to OCIO-enabled oiiotool.exe.";
    }
}

public sealed class LocalColourImportTests
{
    [Fact]
    public void ReportsMissingToolsAndRejectsUnsupportedInterpretations()
    {
        Assert.Throws<FileNotFoundException>(() => LocalColourImport.PrepareAces("source.exr",
            @"C:\missing-inkmotion-tool.exe", Path.GetTempPath(), "ACEScg", CancellationToken.None));
        Assert.Throws<ArgumentException>(() => LocalColourImport.PrepareAces("source.exr",
            @"C:\missing-inkmotion-tool.exe", Path.GetTempPath(), "sRGB", CancellationToken.None));
        Assert.Throws<InvalidDataException>(() => LocalColourImport.PrepareAces("source.png",
            @"C:\missing-inkmotion-tool.exe", Path.GetTempPath(), "ACEScg", CancellationToken.None));
    }

    [OcioImportFact]
    public async Task RealAcesImportProducesDisplayPixelsAndRemainsUsableWithoutTool()
    {
        string tool = Environment.GetEnvironmentVariable("INKMOTION_TEST_OIIOTOOL")!;
        string directory = Path.Combine(Path.GetTempPath(), $"inkmotion-aces-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "source with spaces.exr");
        try
        {
            LocalColourImport.RunTool(tool, ["--pattern", "constant:color=0.18,0.18,0.18", "4x4", "3",
                "-d", "float", "-o", source], CancellationToken.None);
            byte[] original = File.ReadAllBytes(source);
            PreparedColourStill prepared = LocalColourImport.PrepareAces(source, tool, directory, "ACEScg", CancellationToken.None);
            Assert.Equal(original, File.ReadAllBytes(source));
            Assert.Contains("ACES 1.3", prepared.Description);
            using var device = new CanvasDevice(true);
            using var bitmap = await CanvasBitmap.LoadAsync(device, prepared.Path);
            byte[] pixels = bitmap.GetPixelBytes();
            Assert.All(pixels.Where((_, i) => i % 4 != 3), value => Assert.InRange(value, 89, 93));
            var clip = new TimelineClipProject { SourcePath = prepared.Path, ColourSourceDescription = prepared.Description };
            var project = new EditorProject { VideoTracks = [new() { Kind = TimelineTrackKind.Image, Clips = [clip] }] };
            var restored = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
            Assert.Equal(prepared.Description, restored.VideoTracks[0].Clips[0].ColourSourceDescription);
            Assert.Equal(prepared.Description, VideoTimelineEditing.Clone(clip).ColourSourceDescription);
            using var target = new CanvasRenderTarget(device, 4, 4, 96);
            using (var drawing = target.CreateDrawingSession())
                TimelineCompositor.DrawFitted(drawing, bitmap, bitmap.Bounds, 4, 4, 1, 0.5, 0.5, 1,
                    colourGrade: restored.VideoTracks[0].Clips[0]);
            Assert.Equal(pixels, target.GetPixelBytes());
            Assert.Equal(2, Directory.GetFiles(directory).Length);
        }
        finally
        {
            foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    [OcioImportFact]
    public async Task RealAces2065ImportPreservesPremultipliedAlphaAndHonoursCancellation()
    {
        string tool = Environment.GetEnvironmentVariable("INKMOTION_TEST_OIIOTOOL")!;
        string directory = Path.Combine(Path.GetTempPath(), $"inkmotion-aces-alpha-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "alpha.exr");
        try
        {
            LocalColourImport.RunTool(tool, ["--pattern", "constant:color=0.09,0.09,0.09,0.5", "4x4", "4",
                "-d", "float", "-o", source], CancellationToken.None);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() =>
                LocalColourImport.PrepareAces(source, tool, directory, "ACES2065-1", cancelled.Token));
            Assert.Single(Directory.GetFiles(directory));
            PreparedColourStill prepared = LocalColourImport.PrepareAces(source, tool, directory, "ACES2065-1", CancellationToken.None);
            using var device = new CanvasDevice(true);
            using var bitmap = await CanvasBitmap.LoadAsync(device, prepared.Path);
            byte[] pixels = bitmap.GetPixelBytes();
            Assert.InRange(pixels[3], 127, 128);
            Assert.InRange(pixels[2], 44, 47);
        }
        finally
        {
            foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    [OcioImportFact]
    public void RealToolReportsAbsentConfigAndImportRejectsDisguisedNonExr()
    {
        string tool = Environment.GetEnvironmentVariable("INKMOTION_TEST_OIIOTOOL")!;
        string directory = Path.Combine(Path.GetTempPath(), $"inkmotion-aces-error-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "not-an-exr.exr");
        try
        {
            File.WriteAllBytes(source, new byte[32]);
            Assert.Throws<InvalidDataException>(() =>
                LocalColourImport.PrepareAces(source, tool, directory, "ACEScg", CancellationToken.None));
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
                LocalColourImport.RunTool(tool, ["--colorconfig", Path.Combine(directory, "missing-config.ocio"),
                    "--pattern", "constant:color=0.18,0.18,0.18", "4x4", "3",
                    "--ociodisplay:from=ACEScg", LocalColourImport.AcesDisplay, LocalColourImport.AcesView,
                    "-o", Path.Combine(directory, "should-not-exist.png")], CancellationToken.None));
            Assert.Contains("failed", error.Message);
        }
        finally
        {
            foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }
}
