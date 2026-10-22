namespace InkMotion;

public enum ProjectMediaKind
{
    Video,
    Image,
    Audio,
}

public sealed class ProjectMediaAsset(ProjectMediaKind kind, string path)
{
    public ProjectMediaKind Kind { get; } = kind;
    public string Path { get; } = path;
    public List<TimelineClipLocation> TimelineUses { get; } = [];
    public List<AudioTrackProject> AudioUses { get; } = [];
    public bool IsMissing => !File.Exists(Path);
    public string DisplayName => $"{(IsMissing ? "MISSING" : "Available")} · {Kind} · {System.IO.Path.GetFileName(Path)} · {UseCount} use(s)";
    public int UseCount => TimelineUses.Count + AudioUses.Count;
}

public sealed record MediaDiscoveryResult(IReadOnlyList<string> UniqueMatches, int AmbiguousMatches, int ScannedFiles);

public static class ProjectMediaBin
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".m4a", ".aac", ".wma", ".flac",
    };

    public static IReadOnlyList<ProjectMediaAsset> Build(EditorProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return Build(project.VideoTracks, project.AudioTracks);
    }

    public static IReadOnlyList<ProjectMediaAsset> Build(
        IReadOnlyList<TimelineTrackProject> videoTracks, IReadOnlyList<AudioTrackProject> audioTracks)
    {
        ArgumentNullException.ThrowIfNull(videoTracks);
        ArgumentNullException.ThrowIfNull(audioTracks);
        var assets = new Dictionary<(ProjectMediaKind Kind, string Path), ProjectMediaAsset>(
            new MediaKeyComparer());
        foreach (TimelineTrackProject track in videoTracks.Where(track =>
            track.Kind is TimelineTrackKind.Video or TimelineTrackKind.Image))
        {
            ProjectMediaKind kind = track.Kind == TimelineTrackKind.Video ? ProjectMediaKind.Video : ProjectMediaKind.Image;
            foreach (TimelineClipProject clip in track.Clips.Where(clip => !string.IsNullOrWhiteSpace(clip.SourcePath)))
            {
                var location = new TimelineClipLocation(track, clip);
                Get(kind, clip.SourcePath!).TimelineUses.Add(location);
            }
        }
        foreach (AudioTrackProject track in audioTracks.Where(track => !string.IsNullOrWhiteSpace(track.AudioPath)))
        {
            Get(ProjectMediaKind.Audio, track.AudioPath!).AudioUses.Add(track);
        }
        return assets.Values.OrderBy(asset => asset.Kind).ThenBy(asset => asset.Path, StringComparer.OrdinalIgnoreCase).ToArray();

        ProjectMediaAsset Get(ProjectMediaKind kind, string path)
        {
            var key = (kind, Path.GetFullPath(path));
            if (!assets.TryGetValue(key, out ProjectMediaAsset? asset))
            {
                asset = new(kind, key.Item2);
                assets.Add(key, asset);
            }
            return asset;
        }
    }

    public static MediaDiscoveryResult FindUniqueMatches(
        IEnumerable<ProjectMediaAsset> assets, string folder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assets);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException("Choose an available folder to search for missing media.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        HashSet<string> extensions = AudioExtensions
            .Concat(VideoFileTypes.SupportedExtensions)
            .Concat(VideoTimelineEditing.ImageExtensions)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        int scanned = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (string path in Directory.EnumerateFiles(folder, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!extensions.Contains(Path.GetExtension(path)))
            {
                continue;
            }
            scanned++;
            string name = Path.GetFileName(path);
            if (!files.TryGetValue(name, out List<string>? matches))
            {
                matches = [];
                files.Add(name, matches);
            }
            matches.Add(Path.GetFullPath(path));
        }

        var unique = new List<string>();
        int ambiguous = 0;
        foreach (ProjectMediaAsset asset in assets.Where(asset => asset.IsMissing))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!files.TryGetValue(Path.GetFileName(asset.Path), out List<string>? matches))
            {
                continue;
            }
            string[] compatible = matches.Where(path => IsCompatible(asset.Kind, Path.GetExtension(path))).ToArray();
            if (compatible.Length == 1)
            {
                unique.Add(compatible[0]);
            }
            else if (compatible.Length > 1)
            {
                ambiguous++;
            }
        }
        return new(unique.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(), ambiguous, scanned);
    }

    private static bool IsCompatible(ProjectMediaKind kind, string extension) => kind switch
    {
        ProjectMediaKind.Audio => AudioExtensions.Contains(extension),
        ProjectMediaKind.Image => VideoTimelineEditing.ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase),
        ProjectMediaKind.Video => VideoFileTypes.SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase),
        _ => false,
    };

    private sealed class MediaKeyComparer : IEqualityComparer<(ProjectMediaKind Kind, string Path)>
    {
        public bool Equals((ProjectMediaKind Kind, string Path) x, (ProjectMediaKind Kind, string Path) y) =>
            x.Kind == y.Kind && StringComparer.OrdinalIgnoreCase.Equals(x.Path, y.Path);

        public int GetHashCode((ProjectMediaKind Kind, string Path) value) =>
            HashCode.Combine(value.Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(value.Path));
    }
}
