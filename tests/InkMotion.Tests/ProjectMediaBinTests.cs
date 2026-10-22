using Xunit;

namespace InkMotion.Tests;

public sealed class ProjectMediaBinTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"inkmotion-media-bin-{Guid.NewGuid():N}");

    private string Missing(string name) => Path.Combine(_root, "old-project", name);

    private string CreateFile(string relativePath)
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "candidate");
        return path;
    }

    [Fact]
    public void BuildsUniqueAssetsByKindAndPathAndRetainsLiveUseReferences()
    {
        string path = Missing("shared.mp4");
        var clip1 = new TimelineClipProject { SourcePath = path, SourceDurationSeconds = 4 };
        var clip2 = new TimelineClipProject { SourcePath = Path.Combine(Path.GetDirectoryName(path)!, ".", "shared.mp4"), SourceDurationSeconds = 4 };
        var video = new TimelineTrackProject { Kind = TimelineTrackKind.Video, Clips = [clip1, clip2] };
        var image = new TimelineTrackProject { Kind = TimelineTrackKind.Image,
            Clips = [new() { SourcePath = path }] };
        var audio = new AudioTrackProject { AudioPath = Path.Combine(_root, "music.wav") };
        var project = new EditorProject { VideoTracks = [video, image], AudioTracks = [audio] };
        IReadOnlyList<ProjectMediaAsset> assets = ProjectMediaBin.Build(project.VideoTracks, project.AudioTracks);
        Assert.Equal(3, assets.Count);
        ProjectMediaAsset videoAsset = Assert.Single(assets, asset => asset.Kind == ProjectMediaKind.Video);
        Assert.True(videoAsset.IsMissing);
        Assert.Equal(2, videoAsset.UseCount);
        Assert.Same(clip1, videoAsset.TimelineUses[0].Clip);
        Assert.Same(clip2, videoAsset.TimelineUses[1].Clip);
        Assert.Single(assets, asset => asset.Kind == ProjectMediaKind.Image);
        Assert.Single(assets, asset => asset.Kind == ProjectMediaKind.Audio);
    }

    [Fact]
    public void FindsUniqueExactFilenameRecursivelyAndIgnoresUnsupportedExtensions()
    {
        string directory = Directory.CreateDirectory(Path.Combine(_root, "search")).FullName;
        string candidate = CreateFile(Path.Combine("search", "nested", "scene.mp4"));
        CreateFile(Path.Combine("search", "nested", "unused.txt"));
        var asset = new ProjectMediaAsset(ProjectMediaKind.Video, Missing("scene.mp4"));
        MediaDiscoveryResult result = ProjectMediaBin.FindUniqueMatches([asset], directory, CancellationToken.None);
        Assert.Equal(1, result.ScannedFiles);
        Assert.Equal([Path.GetFullPath(candidate)], result.UniqueMatches);
        Assert.Equal(0, result.AmbiguousMatches);
    }

    [Fact]
    public void NeverSuggestsDifferentNamesOrWrongMediaKinds()
    {
        string directory = Directory.CreateDirectory(Path.Combine(_root, "search")).FullName;
        CreateFile(Path.Combine("search", "scene copy.mp4"));
        CreateFile(Path.Combine("search", "scene.wav"));
        var video = new ProjectMediaAsset(ProjectMediaKind.Video, Missing("scene.mp4"));
        var image = new ProjectMediaAsset(ProjectMediaKind.Image, Missing("scene.png"));
        var audio = new ProjectMediaAsset(ProjectMediaKind.Audio, Missing("music.wav"));
        MediaDiscoveryResult result = ProjectMediaBin.FindUniqueMatches([video, image, audio], directory, CancellationToken.None);
        Assert.Empty(result.UniqueMatches);
        Assert.Equal(0, result.AmbiguousMatches);
    }

    [Fact]
    public void DuplicateNamesAreReportedAsAmbiguousAndNotReturnedAsMatches()
    {
        string directory = Directory.CreateDirectory(Path.Combine(_root, "search")).FullName;
        CreateFile(Path.Combine("search", "one", "Scene.MP4"));
        CreateFile(Path.Combine("search", "two", "scene.mp4"));
        var asset = new ProjectMediaAsset(ProjectMediaKind.Video, Missing("scene.mp4"));
        MediaDiscoveryResult result = ProjectMediaBin.FindUniqueMatches([asset], directory, CancellationToken.None);
        Assert.Empty(result.UniqueMatches);
        Assert.Equal(1, result.AmbiguousMatches);
        Assert.Equal(2, result.ScannedFiles);
    }

    [Fact]
    public void DoesNotSuggestMatchesForAvailableAssets()
    {
        string directory = Directory.CreateDirectory(Path.Combine(_root, "search")).FullName;
        string available = CreateFile(Path.Combine("search", "scene.mp4"));
        var asset = new ProjectMediaAsset(ProjectMediaKind.Video, available);
        MediaDiscoveryResult result = ProjectMediaBin.FindUniqueMatches([asset], directory, CancellationToken.None);
        Assert.Empty(result.UniqueMatches);
    }

    [Fact]
    public void ValidatesSearchFolderAndCancellation()
    {
        Assert.Throws<DirectoryNotFoundException>(() =>
            ProjectMediaBin.FindUniqueMatches([], Path.Combine(_root, "absent"), CancellationToken.None));
        string directory = Directory.CreateDirectory(Path.Combine(_root, "search")).FullName;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            ProjectMediaBin.FindUniqueMatches([], directory, cancellation.Token));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            foreach (string file in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.Delete(file);
            }
            foreach (string directory in Directory.GetDirectories(_root, "*", SearchOption.AllDirectories)
                .OrderByDescending(path => path.Length))
            {
                Directory.Delete(directory);
            }
            Directory.Delete(_root);
        }
    }
}
