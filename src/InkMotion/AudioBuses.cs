namespace InkMotion;

/// <summary>
/// A submix bus: audio tracks and video tracks routed to it are summed in the project channel layout, processed by the
/// bus effect chain and gain, and then fed to its parent bus or the master mix. Unrouted sources feed the master directly.
/// </summary>
public sealed class AudioBusProject
{
    public string Id { get; set; } = AudioBuses.NewId();
    public string Name { get; set; } = "Bus 1";
    public string? ParentBusId { get; set; }
    public double GainDecibels { get; set; }
    public bool Muted { get; set; }
    public bool Solo { get; set; }
    public AudioEffectSettings Effects { get; set; } = new();
    public List<AudioVst3PluginProject> Vst3Plugins { get; set; } = [];
}

public static class AudioBuses
{
    public const double MinimumGainDecibels = -60;
    public const double MaximumGainDecibels = 12;
    public const double MinimumSendGainDecibels = -60;
    public const double MaximumSendGainDecibels = 12;

    public static string NewId() => $"bus-{Guid.NewGuid():N}"[..16];

    public static AudioAuxSendProject Clone(AudioAuxSendProject send)
    {
        ArgumentNullException.ThrowIfNull(send);
        return new AudioAuxSendProject { Id = send.Id, BusId = send.BusId, GainDecibels = send.GainDecibels };
    }

    public static AudioBusProject? Find(IReadOnlyList<AudioBusProject>? buses, string? busId) =>
        busId is null || buses is null ? null : buses.FirstOrDefault(bus => string.Equals(bus.Id, busId, StringComparison.Ordinal));

    public static bool AnySolo(IReadOnlyList<AudioBusProject>? buses) => buses?.Any(bus => bus.Solo) == true;

    /// <summary>
    /// Whether a source routed to <paramref name="busId"/> (null for the master) can be heard: a muted bus is silent and,
    /// while any bus is soloed, only sources routed to a soloed bus are heard.
    /// </summary>
    public static bool IsAudible(IReadOnlyList<AudioBusProject>? buses, string? busId)
    {
        var route = new List<AudioBusProject>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        AudioBusProject? bus = Find(buses, busId);
        while (bus is not null)
        {
            if (bus.Muted || !visited.Add(bus.Id))
            {
                return false;
            }

            route.Add(bus);
            bus = Find(buses, bus.ParentBusId);
        }

        return !AnySolo(buses) || route.Any(item => item.Solo);
    }

    public static bool WouldCreateCycle(
        IReadOnlyList<AudioBusProject> buses, string busId, string? parentBusId)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        AudioBusProject? parent = Find(buses, parentBusId);
        while (parent is not null)
        {
            if (parent.Id == busId || !visited.Add(parent.Id))
            {
                return true;
            }

            parent = Find(buses, parent.ParentBusId);
        }

        return false;
    }

    public static AudioBusProject Clone(AudioBusProject bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        return new AudioBusProject
        {
            Id = bus.Id,
            Name = bus.Name,
            ParentBusId = bus.ParentBusId,
            GainDecibels = bus.GainDecibels,
            Muted = bus.Muted,
            Solo = bus.Solo,
            Effects = bus.Effects,
            Vst3Plugins = bus.Vst3Plugins?.Select(AudioVst3PluginProject.Clone).ToList() ?? [],
        };
    }

    /// <summary>Returns "Bus N" using the lowest number not already taken.</summary>
    public static string CreateName(IReadOnlyCollection<AudioBusProject> buses)
    {
        ArgumentNullException.ThrowIfNull(buses);
        int number = buses.Count + 1;
        while (buses.Any(bus => string.Equals(bus.Name, $"Bus {number}", StringComparison.OrdinalIgnoreCase)))
        {
            number++;
        }

        return $"Bus {number}";
    }

    public static float GetLinearGain(AudioBusProject bus) => (float)Math.Pow(10, bus.GainDecibels / 20);

    /// <summary>Spectral noise reduction needs a per-source profile, so it is not available on buses.</summary>
    public static bool HasValidSettings(AudioBusProject? bus) =>
        bus is not null &&
        !string.IsNullOrWhiteSpace(bus.Id) &&
        !string.IsNullOrWhiteSpace(bus.Name) &&
        double.IsFinite(bus.GainDecibels) &&
        bus.GainDecibels is >= MinimumGainDecibels and <= MaximumGainDecibels &&
        bus.Effects is not null &&
        bus.Effects.IsValid &&
        AudioVst3Plugins.IsValid(bus.Vst3Plugins) &&
        !bus.Effects.NoiseReductionEnabled;

    public static string? Validate(EditorProject project)
    {
        if (project.AudioBuses is null || project.AudioBuses.Any(bus => !HasValidSettings(bus)))
        {
            return "Audio buses must have an id, a name, gain between -60 and +12 dB and valid effects without noise reduction.";
        }

        var ids = project.AudioBuses.Select(bus => bus.Id).ToHashSet(StringComparer.Ordinal);
        if (ids.Count != project.AudioBuses.Count)
        {
            return "Audio bus ids must be unique.";
        }

        bool danglingBusParent = project.AudioBuses.Any(bus =>
            bus.ParentBusId is not null && !ids.Contains(bus.ParentBusId));
        if (danglingBusParent)
        {
            return "Audio buses must route to an existing parent bus or the master.";
        }

        if (project.AudioBuses.Any(bus => WouldCreateCycle(project.AudioBuses, bus.Id, bus.ParentBusId)))
        {
            return "Audio bus parent routes must not contain a cycle.";
        }

        if (project.AudioTracks.Any(track => !AudioVst3Plugins.IsValid(track.Vst3Plugins)))
        {
            return "Audio track VST3 inserts must have unique ids, module paths, class ids and valid parameter values.";
        }

        if (project.AudioTracks.Any(track => track.AuxSends is null ||
            track.AuxSends.Any(send => send is null || string.IsNullOrWhiteSpace(send.Id) ||
                string.IsNullOrWhiteSpace(send.BusId) || !ids.Contains(send.BusId) ||
                !double.IsFinite(send.GainDecibels) ||
                send.GainDecibels is < MinimumSendGainDecibels or > MaximumSendGainDecibels) ||
            track.AuxSends.Select(send => send.Id).Distinct(StringComparer.Ordinal).Count() != track.AuxSends.Count))
        {
            return "Auxiliary sends must have unique ids, target an existing bus and have gain between -60 and +12 dB.";
        }

        bool dangling =
            project.AudioTracks.Any(track => track.BusId is not null && !ids.Contains(track.BusId)) ||
            (project.VideoTracks ?? []).Any(track => track.AudioBusId is not null &&
                (track.Kind != TimelineTrackKind.Video || !ids.Contains(track.AudioBusId)));
        return dangling ? "Audio and video tracks must be routed to an existing audio bus or the master." : null;
    }

    /// <summary>Rehomes sources, child buses and auxiliary sends before removing a bus.</summary>
    public static void ClearRoutes(EditorProject project, string busId)
    {
        ArgumentNullException.ThrowIfNull(project);
        ClearRoutes(project.AudioTracks, project.VideoTracks ?? [], busId);
        ReparentChildren(project.AudioBuses, busId, Find(project.AudioBuses, busId)?.ParentBusId);
        ReparentSends(project.AudioTracks, busId, Find(project.AudioBuses, busId)?.ParentBusId);
    }

    public static void ClearRoutes(IEnumerable<AudioTrackProject> audioTracks, IEnumerable<TimelineTrackProject> videoTracks, string busId)
    {
        foreach (AudioTrackProject track in audioTracks.Where(track => track.BusId == busId))
        {
            track.BusId = null;
        }

        foreach (TimelineTrackProject track in videoTracks.Where(track => track.AudioBusId == busId))
        {
            track.AudioBusId = null;
        }
    }

    public static void ReparentChildren(IEnumerable<AudioBusProject> buses, string busId, string? parentBusId)
    {
        foreach (AudioBusProject child in buses.Where(bus => bus.ParentBusId == busId))
        {
            child.ParentBusId = parentBusId;
        }
    }

    public static void ReparentSends(IEnumerable<AudioTrackProject> tracks, string busId, string? parentBusId)
    {
        foreach (AudioTrackProject track in tracks)
        {
            AudioAuxSendProject[] matching = (track.AuxSends ?? []).Where(send => send.BusId == busId).ToArray();
            if (parentBusId is null)
            {
                foreach (AudioAuxSendProject send in matching)
                {
                    track.AuxSends?.Remove(send);
                }
            }
            else
            {
                foreach (AudioAuxSendProject send in matching)
                {
                    send.BusId = parentBusId;
                }
            }
        }
    }
}
