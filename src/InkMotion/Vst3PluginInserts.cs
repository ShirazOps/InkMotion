using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.Wave;

namespace InkMotion;

public sealed class AudioVst3PluginProject
{
    public string Id { get; set; } = $"vst3-{Guid.NewGuid():N}"[..16];
    public string ModulePath { get; set; } = string.Empty;
    public string ClassId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public Dictionary<uint, double> ParameterValues { get; set; } = [];

    public static AudioVst3PluginProject Clone(AudioVst3PluginProject plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        return new AudioVst3PluginProject
        {
            Id = plugin.Id,
            ModulePath = plugin.ModulePath,
            ClassId = plugin.ClassId,
            Name = plugin.Name,
            Enabled = plugin.Enabled,
            ParameterValues = plugin.ParameterValues is null
                ? []
                : new Dictionary<uint, double>(plugin.ParameterValues),
        };
    }

    public Vst3PluginDescription GetDescription() =>
        new(ModulePath, ClassId, Name, string.Empty, string.Empty);
}

public static class AudioVst3Plugins
{
    public static bool IsValid(IReadOnlyList<AudioVst3PluginProject>? plugins) =>
        plugins is not null &&
        plugins.All(plugin => plugin is not null &&
            !string.IsNullOrWhiteSpace(plugin.Id) &&
            !string.IsNullOrWhiteSpace(plugin.ModulePath) &&
            !string.IsNullOrWhiteSpace(plugin.ClassId) &&
            plugin.ParameterValues is not null &&
            plugin.ParameterValues.Values.All(value => double.IsFinite(value) && value is >= 0 and <= 1)) &&
        plugins.Select(plugin => plugin.Id).Distinct(StringComparer.Ordinal).Count() == plugins.Count;
}

public static class Vst3PluginTrustStore
{
    private static readonly object Sync = new();
    private static HashSet<string>? _trustedPlugins;

    public static bool IsTrusted(Vst3PluginDescription plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        lock (Sync)
        {
            return GetTrustedPlugins().Contains(GetKey(plugin));
        }
    }

    public static void Trust(Vst3PluginDescription plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        lock (Sync)
        {
            HashSet<string> trusted = GetTrustedPlugins();
            if (trusted.Add(GetKey(plugin)))
            {
                string path = GetStorePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string temporaryPath = path + ".new";
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(trusted.Order(StringComparer.Ordinal)));
                File.Move(temporaryPath, path, overwrite: true);
            }
        }
    }

    private static HashSet<string> GetTrustedPlugins()
    {
        if (_trustedPlugins is not null)
        {
            return _trustedPlugins;
        }

        string path = GetStorePath();
        _trustedPlugins = File.Exists(path)
            ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = false,
            }) ?? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        return _trustedPlugins;
    }

    private static string GetStorePath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "InkMotion", "vst3-trusted-plugins.json");

    private static string GetKey(Vst3PluginDescription plugin) =>
        $"{Path.GetFullPath(plugin.ModulePath).ToUpperInvariant()}|{plugin.ClassId.ToUpperInvariant()}";
}

public sealed class Vst3InsertChainSampleProvider : ISampleProvider, IDisposable
{
    private readonly ISampleProvider _source;
    private readonly object _sync = new();
    private readonly List<Vst3HostedSampleProvider?> _hosts = [];
    private IReadOnlyList<AudioVst3PluginProject> _settings = [];
    private ISampleProvider _currentSource;
    private string? _creationError;
    private readonly bool _preservePluginTails;
    private bool _disposed;

    public Vst3InsertChainSampleProvider(
        ISampleProvider source,
        IReadOnlyList<AudioVst3PluginProject>? plugins,
        bool preservePluginTails = false)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _currentSource = source;
        _preservePluginTails = preservePluginTails;
        Update(plugins);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public double TailDurationSeconds
    {
        get
        {
            lock (_sync)
            {
                return _hosts.Where(host => host is not null).Sum(host => host!.TailDurationSeconds);
            }
        }
    }

    public string? FailureMessage
    {
        get
        {
            lock (_sync)
            {
                var failures = _hosts
                    .Select(host => host?.FailureMessage)
                    .OfType<string>()
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (!string.IsNullOrWhiteSpace(_creationError))
                {
                    failures.Insert(0, _creationError);
                }

                return failures.Count == 0 ? null : string.Join(Environment.NewLine, failures);
            }
        }
    }

    public void Update(IReadOnlyList<AudioVst3PluginProject>? plugins)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AudioVst3PluginProject[] next = (plugins ?? []).Select(AudioVst3PluginProject.Clone).ToArray();
        lock (_sync)
        {
            if (SameStructure(_settings, next))
            {
                UpdateParameters(next);
                _settings = next;
                return;
            }

            DisposeHosts();
            _creationError = null;
            _currentSource = _source;
            _settings = next;
            foreach (AudioVst3PluginProject plugin in next)
            {
                Vst3HostedSampleProvider? host = null;
                if (plugin.Enabled)
                {
                    if (!Vst3PluginTrustStore.IsTrusted(plugin.GetDescription()))
                    {
                        _creationError = JoinFailure(_creationError,
                            $"{plugin.Name}: this plug-in has not been approved on this device. Review it in the insert controls before enabling it.");
                        _hosts.Add(null);
                        continue;
                    }

                    try
                    {
                        host = Vst3Host.CreateEffect(
                            _currentSource,
                            plugin.GetDescription(),
                            plugin.ParameterValues,
                            preserveTail: _preservePluginTails);
                        if (host.HasInfiniteTail)
                        {
                            _creationError = JoinFailure(_creationError,
                                $"{plugin.Name}: the plug-in reports an infinite tail; no finite export tail can be inferred.");
                        }

                        _currentSource = host;
                    }
                    catch (Exception exception) when (IsHostFailure(exception))
                    {
                        _creationError = JoinFailure(_creationError,
                            $"{plugin.Name}: the VST3 insert could not be loaded: {exception.Message}");
                    }
                }

                _hosts.Add(host);
            }
        }
    }

    public IReadOnlyList<Vst3Parameter> GetParameters(string pluginId)
    {
        lock (_sync)
        {
            int index = _settings.ToList().FindIndex(plugin => plugin.Id == pluginId);
            if (index < 0)
            {
                throw new InvalidOperationException("The selected VST3 insert is not active.");
            }

            if (_hosts[index] is Vst3HostedSampleProvider host)
            {
                return host.GetParameters();
            }

            return Vst3Host.GetParameters(_settings[index].GetDescription());
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            return _currentSource.Read(buffer, offset, count);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_sync)
        {
            DisposeHosts();
            _currentSource = _source;
        }
    }

    private void UpdateParameters(IReadOnlyList<AudioVst3PluginProject> next)
    {
        for (int index = 0; index < next.Count; index++)
        {
            if (_hosts[index] is not Vst3HostedSampleProvider host)
            {
                continue;
            }

            Dictionary<uint, double> oldValues = _settings[index].ParameterValues;
            foreach ((uint id, double value) in next[index].ParameterValues)
            {
                if (!oldValues.TryGetValue(id, out double previous) || previous != value)
                {
                    try
                    {
                        host.SetParameter(id, value);
                    }
                    catch (Exception exception) when (IsHostFailure(exception))
                    {
                        _creationError = JoinFailure(_creationError,
                            $"{next[index].Name}: the VST3 parameter update failed: {exception.Message}");
                    }
                }
            }
        }
    }

    private void DisposeHosts()
    {
        foreach (Vst3HostedSampleProvider? host in _hosts)
        {
            host?.Dispose();
        }

        _hosts.Clear();
    }

    private static bool SameStructure(
        IReadOnlyList<AudioVst3PluginProject> first,
        IReadOnlyList<AudioVst3PluginProject> second) =>
        first.Count == second.Count &&
        first.Zip(second).All(pair =>
            pair.First.Id == pair.Second.Id &&
            pair.First.ModulePath == pair.Second.ModulePath &&
            pair.First.ClassId == pair.Second.ClassId &&
            pair.First.Enabled == pair.Second.Enabled);

    private static bool IsHostFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidOperationException or
            NotSupportedException or ArgumentException or TimeoutException or OperationCanceledException or
            Win32Exception or COMException;

    private static string JoinFailure(string? first, string next) =>
        string.IsNullOrWhiteSpace(first) ? next : $"{first}{Environment.NewLine}{next}";
}

internal sealed class SilenceSampleProvider : ISampleProvider
{
    public SilenceSampleProvider(int sampleRate, int channels) =>
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);
        return count;
    }
}
