using System.IO.Compression;
using System.Text.Json.Nodes;
using Xunit;

namespace InkMotion.Tests;

public sealed class ProjectPackageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"inkmotion-package-test-{Guid.NewGuid():N}");

    public ProjectPackageTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Package_ReopensMovedMediaWithoutMutatingProjectOrCollidingNames()
    {
        string a = Path.Combine(_root, "a");
        string b = Path.Combine(_root, "b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        File.WriteAllText(Path.Combine(a, "clip.mp4"), "first");
        File.WriteAllText(Path.Combine(b, "clip.mp4"), "second");
        var project = new EditorProject
        {
            VideoTracks = [new() { Clips = [
                new() { SourcePath = Path.Combine(a, "clip.mp4"), SourceDurationSeconds = 4 },
                new() { SourcePath = Path.Combine(b, "clip.mp4"), SourceDurationSeconds = 4, StartFrame = 97 }
            ] }],
            AudioTracks = [new() { AudioPath = Path.Combine(a, "clip.mp4") }],
        };
        string before = EditorProjectSerialiser.Serialise(project);
        string package = Path.Combine(_root, "test.inkpkg");
        ProjectPackage.Create(project, package, _root);
        string extracted = ProjectPackage.Extract(package, _root);
        string moved = Path.Combine(_root, "moved");
        Directory.Move(Path.GetDirectoryName(extracted)!, moved);
        EditorProject restored = ProjectMediaPaths.Resolve(File.ReadAllText(Path.Combine(moved, "project.inkmotion")), moved);
        Assert.Equal("first", File.ReadAllText(restored.VideoTracks[0].Clips[0].SourcePath!));
        Assert.Equal("second", File.ReadAllText(restored.VideoTracks[0].Clips[1].SourcePath!));
        Assert.Equal(restored.AudioTracks[0].AudioPath, restored.VideoTracks[0].Clips[0].SourcePath);
        Assert.Equal(before, EditorProjectSerialiser.Serialise(project));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(moved, "media")).Length);
    }

    [Theory]
    [InlineData("../outside.wav")]
    [InlineData("media/../../outside.wav")]
    [InlineData("C:\\outside.wav")]
    [InlineData("\\\\server\\share\\outside.wav")]
    public void ExternalProjectReferences_AreRejectedBeforeExtraction(string path)
    {
        string package = Path.Combine(_root, "bad.inkpkg");
        using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            Write(zip, "manifest.json", """{"Version":1,"Media":[]}""");
            Write(zip, "project.inkmotion", new JsonObject
            {
                ["Version"] = EditorProject.CurrentVersion, ["AudioPath"] = path,
            }.ToJsonString());
        }
        Assert.Throws<InvalidDataException>(() => ProjectPackage.Extract(package, _root));
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public void MissingMedia_DoesNotReplaceExistingOutput()
    {
        string output = Path.Combine(_root, "project.inkpkg");
        File.WriteAllText(output, "untouched");
        var project = new EditorProject { AudioTracks = [new() { AudioPath = Path.Combine(_root, "missing.wav") }] };
        Assert.Throws<FileNotFoundException>(() => ProjectPackage.Create(project, output, _root));
        Assert.Equal("untouched", File.ReadAllText(output));
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("traversal")]
    [InlineData("future")]
    public void InvalidArchives_LeaveExistingFilesUntouched(string failure)
    {
        string media = Path.Combine(_root, "audio.wav");
        File.WriteAllText(media, "original");
        string package = Path.Combine(_root, "project.inkpkg");
        ProjectPackage.Create(new() { AudioTracks = [new() { AudioPath = media }] }, package, _root);
        using (var zip = ZipFile.Open(package, ZipArchiveMode.Update))
        {
            if (failure == "checksum") { zip.GetEntry("media/000001.wav")!.Delete(); Write(zip, "media/000001.wav", "modified"); }
            if (failure == "missing") zip.GetEntry("media/000001.wav")!.Delete();
            if (failure == "duplicate") Write(zip, "PROJECT.inkmotion", "{}");
            if (failure == "traversal") Write(zip, "../audio.wav", "evil");
            if (failure == "future") { zip.GetEntry("manifest.json")!.Delete(); Write(zip, "manifest.json", """{"Version":99,"Media":[]}"""); }
        }
        Assert.Throws<InvalidDataException>(() => ProjectPackage.Extract(package, _root));
        Assert.Equal("original", File.ReadAllText(media));
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public void NestedCompoundsAndLuts_AreIncluded()
    {
        string source = Path.Combine(_root, "clip.mp4");
        string lut = Path.Combine(_root, "grade.cube");
        File.WriteAllText(source, "picture");
        File.WriteAllText(lut, "lut");
        var child = new EditorProject { VideoTracks = [new() { Clips = [new()
            { SourcePath = source, ColourLutPath = lut, SourceDurationSeconds = 1 }] }] };
        var project = new EditorProject { VideoTracks = [new() { Kind = TimelineTrackKind.Compound, Clips = [new()
            { NestedProject = child, NestedName = "Child", SourceDurationSeconds = 1, DurationSeconds = 1 }] }] };
        string package = Path.Combine(_root, "nested.inkpkg");
        ProjectPackage.Create(project, package, _root);
        string extracted = ProjectPackage.Extract(package, _root);
        EditorProject restored = ProjectMediaPaths.Resolve(File.ReadAllText(extracted), Path.GetDirectoryName(extracted)!);
        var clip = restored.VideoTracks[0].Clips[0].NestedProject!.VideoTracks[0].Clips[0];
        Assert.Equal("lut", File.ReadAllText(clip.ColourLutPath!));
        Assert.Equal("picture", File.ReadAllText(clip.SourcePath!));
    }

    private static void Write(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(content);
    }
}
