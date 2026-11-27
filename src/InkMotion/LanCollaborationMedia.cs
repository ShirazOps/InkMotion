using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace InkMotion;

internal sealed record LanProjectSnapshot(string Json, IReadOnlySet<string> Assets);

internal sealed class LanMediaStore
{
    internal const long MaximumMediaBytes = 1L << 40;
    internal const int MaximumProjectBytes = 64 * 1024 * 1024;
    private const int MaximumAssets = 10000;
    private static readonly Regex AssetPattern = new("^[0-9A-F]{64}\\.[a-z0-9]{1,10}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ExtensionPattern = new("^\\.[a-z0-9]{1,10}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly SemaphoreSlim _importGate = new(1, 1);
    private readonly Dictionary<string, LocalAsset> _localAssets = new(StringComparer.OrdinalIgnoreCase);
    private long _usedBytes;
    private int _assetCount;

    private sealed record LocalAsset(long Length, DateTime LastWriteTimeUtc, string Id);

    internal LanMediaStore(string directory)
    {
        DirectoryPath = Path.GetFullPath(directory);
        Directory.CreateDirectory(DirectoryPath);
        foreach (string file in Directory.EnumerateFiles(DirectoryPath))
        {
            if (IsAssetId(Path.GetFileName(file)))
            {
                _usedBytes = checked(_usedBytes + new FileInfo(file).Length);
                _assetCount++;
            }
        }
        if (_usedBytes > MaximumMediaBytes || _assetCount > MaximumAssets)
            throw new InvalidDataException("The collaboration media store exceeds its size or file-count limit.");
    }

    internal string DirectoryPath { get; }

    internal static bool IsAssetId(string? id) => id is not null && AssetPattern.IsMatch(id);

    internal string PathFor(string id)
    {
        if (!IsAssetId(id))
            throw new InvalidDataException("The collaboration project contains an invalid media identifier.");
        return Path.Combine(DirectoryPath, id);
    }

    internal bool Contains(string id) => File.Exists(PathFor(id));

    internal async Task<LanProjectSnapshot> PrepareAsync(
        EditorProject project, string baseDirectory, CancellationToken cancellationToken)
    {
        ValidatePlugins(project);
        string json = EditorProjectSerialiser.Serialise(project);
        JsonNode node = JsonNode.Parse(json) ?? throw new InvalidDataException("The project is empty.");
        var assets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in ProjectMediaPaths.References(node).ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = Path.GetFullPath(reference.Path, baseDirectory);
            string id = await ImportLocalAsync(path, cancellationToken);
            reference.Owner[reference.Key] = id;
            assets.Add(id);
            if (assets.Count > MaximumAssets)
                throw new InvalidDataException("The collaboration project has too many linked files.");
        }

        string wireJson = node.ToJsonString();
        ValidateWireProject(wireJson, Contains);
        return new(wireJson, assets);
    }

    internal async Task<EditorProject> ResolveAsync(
        string json, Func<string, CancellationToken, Task> download, CancellationToken cancellationToken)
    {
        IReadOnlySet<string> assets = ValidateWireProject(json);
        foreach (string id in assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Contains(id))
                await download(id, cancellationToken);
            if (!Contains(id))
                throw new InvalidDataException("A linked collaboration file was not received.");
        }

        JsonNode node = JsonNode.Parse(json) ?? throw new InvalidDataException("The collaboration project is empty.");
        foreach (var reference in ProjectMediaPaths.References(node).ToArray())
            reference.Owner[reference.Key] = PathFor(reference.Path);
        return EditorProjectSerialiser.Deserialise(node.ToJsonString());
    }

    internal static IReadOnlySet<string> ValidateWireProject(string json, Func<string, bool>? contains = null)
    {
        if (string.IsNullOrEmpty(json) || Encoding.UTF8.GetByteCount(json) > MaximumProjectBytes)
            throw new InvalidDataException("The collaboration project is empty or exceeds 64 MiB.");
        JsonNode node = JsonNode.Parse(json) ?? throw new InvalidDataException("The collaboration project is empty.");
        if (node["Version"]?.GetValue<int>() != EditorProject.CurrentVersion)
            throw new InvalidDataException("Collaborators must use the same supported project version.");
        var assets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in ProjectMediaPaths.References(node))
        {
            if (!IsAssetId(reference.Path) || (contains is not null && !contains(reference.Path)))
                throw new InvalidDataException("The collaboration project references invalid or unavailable media.");
            assets.Add(reference.Path);
            if (assets.Count > MaximumAssets)
                throw new InvalidDataException("The collaboration project has too many linked files.");
        }
        EditorProject project = EditorProjectSerialiser.Deserialise(json);
        ValidatePlugins(project);
        return assets;
    }

    private static void ValidatePlugins(EditorProject project)
    {
        if (CompoundTimelines.Projects(project).Any(current =>
            current.AudioTracks.Any(track => track.Vst3Plugins.Count > 0) ||
            current.AudioBuses.Any(bus => bus.Vst3Plugins.Count > 0)))
            throw new InvalidDataException(
                "Collaboration cannot transfer VST3 inserts. Remove them from a project copy or render them first.");
    }

    private async Task<string> ImportLocalAsync(string source, CancellationToken cancellationToken)
    {
        await _importGate.WaitAsync(cancellationToken);
        try
        {
            var info = new FileInfo(source);
            if (!info.Exists)
                throw new FileNotFoundException("Linked media is missing; relink it before collaborating.", source);
            if (info.Length > MaximumMediaBytes)
                throw new InvalidDataException("A collaboration file exceeds 1 TiB.");
            long originalLength = info.Length;
            DateTime originalWriteTime = info.LastWriteTimeUtc;

            if (_localAssets.TryGetValue(source, out LocalAsset? cached) &&
                cached.Length == info.Length && cached.LastWriteTimeUtc == info.LastWriteTimeUtc && Contains(cached.Id))
                return cached.Id;

            string extension = Path.GetExtension(source).ToLowerInvariant();
            if (!ExtensionPattern.IsMatch(extension)) extension = ".bin";
            string temporary = Path.Combine(DirectoryPath, $".{Guid.NewGuid():N}.part");
            try
            {
                using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                long length = input.Length;
                if (length != originalLength)
                    throw new InvalidDataException("Linked media changed while it was copied.");
                Reserve(length);
                using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                string hash = await CopyAndHashAsync(input, output, length, cancellationToken);
                if (await input.ReadAsync(new byte[1], cancellationToken) != 0)
                    throw new InvalidDataException("Linked media changed while it was copied.");
                await output.FlushAsync(cancellationToken);
                output.Close();
                info.Refresh();
                if (!info.Exists || info.Length != length || info.LastWriteTimeUtc != originalWriteTime)
                    throw new InvalidDataException("Linked media changed while it was copied.");
                string id = hash + extension;
                CommitTemporary(temporary, id, length);
                _localAssets[source] = new(length, info.LastWriteTimeUtc, id);
                return id;
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        finally
        {
            _importGate.Release();
        }
    }

    internal async Task ReceiveAsync(
        string id, long length, Stream input, CancellationToken cancellationToken, Action? progress = null)
    {
        if (!IsAssetId(id) || length < 0 || length > MaximumMediaBytes)
            throw new InvalidDataException("Invalid collaboration media metadata.");
        await _importGate.WaitAsync(cancellationToken);
        try
        {
            if (Contains(id)) return;
            Reserve(length);
            string temporary = Path.Combine(DirectoryPath, $".{Guid.NewGuid():N}.part");
            try
            {
                using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                string hash = await CopyAndHashAsync(input, output, length, cancellationToken, progress);
                if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(hash), Convert.FromHexString(id[..64])))
                    throw new InvalidDataException("Collaboration media failed its SHA-256 checksum.");
                await output.FlushAsync(cancellationToken);
                output.Close();
                CommitTemporary(temporary, id, length);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        finally
        {
            _importGate.Release();
        }
    }

    private void Reserve(long length)
    {
        if (length < 0 || length > MaximumMediaBytes - _usedBytes)
            throw new InvalidDataException("Collaboration media exceeds 1 TiB.");
    }

    private void CommitTemporary(string temporary, string id, long length)
    {
        string destination = PathFor(id);
        if (File.Exists(destination))
        {
            if (new FileInfo(destination).Length != length)
                throw new InvalidDataException("A cached collaboration file has an invalid length.");
            File.Delete(temporary);
            return;
        }
        if (_assetCount >= MaximumAssets)
            throw new InvalidDataException("The collaboration media store has too many files.");
        File.Move(temporary, destination);
        _usedBytes = checked(_usedBytes + length);
        _assetCount++;
    }

    private static async Task<string> CopyAndHashAsync(
        Stream input, Stream output, long length, CancellationToken cancellationToken, Action? progress = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        long remaining = length;
        while (remaining > 0)
        {
            int read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
            if (read == 0) throw new EndOfStreamException("Collaboration media was truncated.");
            progress?.Invoke();
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            progress?.Invoke();
            remaining -= read;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
