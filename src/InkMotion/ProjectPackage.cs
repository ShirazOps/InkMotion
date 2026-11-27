using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace InkMotion;

public static class ProjectMediaPaths
{
    private static readonly HashSet<string> MediaKeys =
        ["VideoPath", "AudioPath", "SourcePath", "ProxyPath", "ColourLutPath"];

    public static IEnumerable<(JsonObject Owner, string Key, string Path)> References(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var item in obj.ToArray())
            {
                if (MediaKeys.Contains(item.Key) && item.Value is JsonValue value &&
                    value.TryGetValue<string>(out string? path) && !string.IsNullOrWhiteSpace(path))
                    yield return (obj, item.Key, path);
                else if (item.Value is not null)
                    foreach (var child in References(item.Value)) yield return child;
            }
        }
        else if (node is JsonArray array)
            foreach (JsonNode child in array.OfType<JsonNode>())
                foreach (var reference in References(child)) yield return reference;
    }

    public static EditorProject Resolve(string json, string directory)
    {
        JsonNode node = JsonNode.Parse(json) ?? throw new InvalidDataException("Empty project.");
        foreach (var reference in References(node).ToArray())
            reference.Owner[reference.Key] = Path.GetFullPath(reference.Path, directory);
        return EditorProjectSerialiser.Deserialise(node.ToJsonString());
    }

    public static string Serialise(EditorProject project, string directory)
    {
        JsonNode node = JsonNode.Parse(EditorProjectSerialiser.Serialise(project))!;
        foreach (var reference in References(node).ToArray())
        {
            string path = Path.GetFullPath(reference.Path, directory);
            string relative = Path.GetRelativePath(directory, path);
            reference.Owner[reference.Key] = relative.StartsWith("..", StringComparison.Ordinal) ||
                Path.IsPathRooted(relative) ? path : relative;
        }
        return node.ToJsonString(new() { WriteIndented = true });
    }
}

public static class ProjectPackage
{
    public const string Extension = ".inkpkg";
    private const long MaximumMediaBytes = 1L << 40;
    private const int MaximumJsonBytes = 64 * 1024 * 1024;
    private const int MaximumEntries = 10000;
    private sealed record MediaEntry(string Name, long Length, string Sha256);
    private sealed record Manifest(int Version, List<MediaEntry> Media);

    public static void Create(EditorProject project, string outputPath, string baseDirectory)
    {
        outputPath = Path.GetFullPath(outputPath);
        if (!string.Equals(Path.GetExtension(outputPath), Extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose an .inkpkg package file.");
        if (CompoundTimelines.Projects(project).SelectMany(p => p.AudioTracks)
            .Any(track => track.Vst3Plugins.Count > 0))
            throw new InvalidDataException("Packages cannot redistribute VST3 plug-ins. Remove the inserts from a project copy or render them first.");

        JsonNode node = JsonNode.Parse(EditorProjectSerialiser.Serialise(project))!;
        var references = ProjectMediaPaths.References(node).ToArray();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in references)
        {
            string source = Path.GetFullPath(reference.Path, baseDirectory);
            if (source.Equals(outputPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The package would overwrite linked media.");
            if (!File.Exists(source)) throw new FileNotFoundException("Linked media is missing; relink it before packaging.", source);
            if (!names.ContainsKey(source))
            {
                string extension = Path.GetExtension(source);
                if (!Regex.IsMatch(extension, @"^\.[A-Za-z0-9]{1,10}$")) extension = ".bin";
                names.Add(source, $"media/{names.Count + 1:D6}{extension.ToLowerInvariant()}");
            }
            reference.Owner[reference.Key] = names[source];
        }
        if (names.Count + 2 > MaximumEntries) throw new InvalidDataException("The package contains too many linked files.");
        string temporary = outputPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var manifest = new Manifest(1, []);
            long total = 0;
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                foreach (var pair in names)
                {
                    using var input = File.OpenRead(pair.Key);
                    total = checked(total + input.Length);
                    if (total > MaximumMediaBytes) throw new InvalidDataException("Package media exceeds 1 TiB.");
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    using Stream output = zip.CreateEntry(pair.Value, CompressionLevel.NoCompression).Open();
                    long length = Copy(input, output, input.Length, hash);
                    manifest.Media.Add(new(pair.Value, length, Convert.ToHexString(hash.GetHashAndReset())));
                }
                WriteJson(zip, "project.inkmotion", node.ToJsonString());
                WriteJson(zip, "manifest.json", JsonSerializer.Serialize(manifest));
            }
            File.Move(temporary, outputPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // Nothing is extracted using an archive-supplied filesystem path.
    public static string Extract(string packagePath, string parentDirectory)
    {
        using var zip = ZipFile.OpenRead(packagePath);
        if (zip.Entries.Count is < 2 or > MaximumEntries)
            throw new InvalidDataException("Invalid package entry count.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in zip.Entries)
            if (!entries.TryAdd(entry.FullName, entry))
                throw new InvalidDataException("Duplicate package entry.");
        if (!entries.TryGetValue("manifest.json", out var manifestEntry) ||
            !entries.TryGetValue("project.inkmotion", out var projectEntry))
            throw new InvalidDataException("The package is incomplete.");
        Manifest manifest = JsonSerializer.Deserialize<Manifest>(ReadJson(manifestEntry))
            ?? throw new InvalidDataException("Empty package manifest.");
        if (manifest.Version != 1 || manifest.Media is null || manifest.Media.Count + 2 != entries.Count)
            throw new InvalidDataException("Unsupported or incomplete package manifest.");
        var media = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var item in manifest.Media)
        {
            if (item is null || !Regex.IsMatch(item.Name ?? "", @"^media/[0-9]{6}\.[a-z0-9]{1,10}$") ||
                !media.Add(item.Name!) || item.Length < 0 || item.Length > MaximumMediaBytes ||
                !Regex.IsMatch(item.Sha256 ?? "", "^[0-9A-F]{64}$") ||
                !entries.TryGetValue(item.Name!, out var entry) || entry.Length != item.Length)
                throw new InvalidDataException("Invalid or missing packaged media.");
            total = checked(total + item.Length);
            if (total > MaximumMediaBytes) throw new InvalidDataException("Package media exceeds 1 TiB.");
        }
        JsonNode node = JsonNode.Parse(ReadJson(projectEntry)) ?? throw new InvalidDataException("Empty packaged project.");
        foreach (var reference in ProjectMediaPaths.References(node))
            if (!media.Contains(reference.Path))
                throw new InvalidDataException("The packaged project references media outside its manifest.");
        EditorProject project = EditorProjectSerialiser.Deserialise(node.ToJsonString());
        if (CompoundTimelines.Projects(project).SelectMany(p => p.AudioTracks).Any(t => t.Vst3Plugins.Count > 0))
            throw new InvalidDataException("Packaged projects cannot load executable plug-ins.");

        string directory = Path.Combine(Path.GetFullPath(parentDirectory), $"InkMotion-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "media"));
            foreach (var item in manifest.Media)
            {
                string outputPath = Path.Combine(directory, "media", item.Name["media/".Length..]);
                using Stream input = entries[item.Name].Open();
                using var output = new FileStream(outputPath, FileMode.CreateNew);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                Copy(input, output, item.Length, hash);
                if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(item.Sha256, StringComparison.Ordinal))
                    throw new InvalidDataException($"Packaged media failed its checksum: {item.Name}");
            }
            string projectPath = Path.Combine(directory, "project.inkmotion");
            File.WriteAllText(projectPath, node.ToJsonString(), Encoding.UTF8);
            return projectPath;
        }
        catch
        {
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    private static long Copy(Stream input, Stream output, long expected, IncrementalHash hash)
    {
        byte[] buffer = new byte[81920];
        long length = 0;
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            length = checked(length + read);
            if (length > expected) throw new InvalidDataException("Packaged media changed length.");
            hash.AppendData(buffer, 0, read);
            output.Write(buffer, 0, read);
        }
        if (length != expected) throw new InvalidDataException("Packaged media is truncated.");
        return length;
    }

    private static string ReadJson(ZipArchiveEntry entry)
    {
        if (entry.Length > MaximumJsonBytes) throw new InvalidDataException("Package metadata is too large.");
        using Stream input = entry.Open();
        using var output = new MemoryStream();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Copy(input, output, entry.Length, hash);
        return Encoding.UTF8.GetString(output.ToArray()).TrimStart('\uFEFF');
    }

    private static void WriteJson(ZipArchive zip, string name, string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes)
            throw new InvalidDataException("Package metadata is too large.");
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(json);
    }
}
