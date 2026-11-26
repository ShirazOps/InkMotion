namespace InkMotion;

public static class CompoundTimelines
{
    public const int MaximumDepth = 4;
    public const double MaximumDurationSeconds = 24 * 60 * 60;

    public static EditorProject Clone(EditorProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return new EditorProject
        {
            Version = project.Version,
            FrameRate = project.FrameRate,
            CanvasSize = project.CanvasSize,
            Colour = project.Colour,
            VideoPath = project.VideoPath,
            VideoTracks = project.VideoTracks.Select(VideoTimelineEditing.Clone).ToList(),
            VideoMulticamGroups = project.VideoMulticamGroups.Select(VideoMulticamEditing.Clone).ToList(),
            Markers = project.Markers.Select(TimelineMarkers.Clone).ToList(),
            AudioPath = project.AudioPath,
            AudioVolume = project.AudioVolume,
            AudioMuted = project.AudioMuted,
            AudioTracks = project.AudioTracks.Select(AudioClipEditing.Clone).ToList(),
            AudioMixLayout = project.AudioMixLayout,
            AudioBuses = project.AudioBuses.Select(AudioBuses.Clone).ToList(),
            InkLayers = project.InkLayers.Select(InkEditing.CloneLayer).ToList(),
            ActiveInkLayerId = project.ActiveInkLayerId,
            CompositeGraph = project.CompositeGraph is null ? null : InkCompositeGraphs.Clone(project.CompositeGraph),
            SelectedFrameIndex = project.SelectedFrameIndex,
            Frames = project.Frames.Select(frame => InkEditing.Clone(frame, preserveDrawingLink: true)).ToList(),
        };
    }

    public static IEnumerable<EditorProject> Projects(EditorProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var pending = new Stack<EditorProject>();
        var visited = new HashSet<EditorProject>(ReferenceEqualityComparer.Instance);
        pending.Push(project);
        while (pending.TryPop(out EditorProject? current))
        {
            if (!visited.Add(current))
            {
                throw new InvalidDataException("Compound timelines cannot contain cycles or share mutable projects.");
            }

            yield return current;
            foreach (EditorProject nested in current.VideoTracks.SelectMany(track => track.Clips)
                .Select(clip => clip.NestedProject).OfType<EditorProject>())
            {
                pending.Push(nested);
            }
        }
    }

    public static IEnumerable<string> ReferencedPaths(EditorProject project) =>
        Projects(project).SelectMany(current =>
            current.AudioTracks.Select(track => track.AudioPath)
                .Concat(current.VideoTracks.SelectMany(track => track.Clips)
                    .SelectMany(clip => new[] { clip.SourcePath, clip.ProxyPath, clip.ColourLutPath })))
            .OfType<string>()
            .Where(path => !string.IsNullOrWhiteSpace(path));

    public static int Depth(EditorProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return project.VideoTracks.SelectMany(track => track.Clips)
            .Select(clip => clip.NestedProject is null ? 0 : 1 + Depth(clip.NestedProject))
            .DefaultIfEmpty(0)
            .Max();
    }

    public static double GetAudioGain(TimelineClipProject clip, double elapsedSeconds)
    {
        ArgumentNullException.ThrowIfNull(clip);
        double remaining = clip.DurationSeconds - elapsedSeconds;
        if (elapsedSeconds < 0 || remaining <= 0)
        {
            return 0;
        }

        double gain = clip.Volume;
        if (clip.FadeInSeconds > 0 && elapsedSeconds < clip.FadeInSeconds)
        {
            gain *= elapsedSeconds / clip.FadeInSeconds;
        }
        if (clip.FadeOutSeconds > 0 && remaining < clip.FadeOutSeconds)
        {
            gain *= remaining / clip.FadeOutSeconds;
        }
        return gain;
    }

    public static string? TryCreateTrack(
        EditorProject project, string trackId, int ancestorDepth, out TimelineTrackProject? compoundTrack)
    {
        ArgumentNullException.ThrowIfNull(project);
        compoundTrack = null;
        TimelineTrackProject? track = project.VideoTracks.FirstOrDefault(item => item.Id == trackId);
        if (track is null || track.IsLocked || track.Clips.Count == 0)
        {
            return "Select an unlocked track containing at least one clip.";
        }

        if (project.VideoMulticamGroups.Any(group => group.Angles.Any(angle => angle.TrackId == track.Id)))
        {
            return "Remove this track from its multicamera group before creating a compound.";
        }

        var clipIds = track.Clips.Select(clip => clip.Id).ToHashSet(StringComparer.Ordinal);
        bool hasDependentTracking =
            track.Clips.Any(clip => clip.ObjectTracking is not null) ||
            project.VideoTracks.SelectMany(item => item.Clips)
                .Any(clip => clip.ObjectTracking is { } tracking && clipIds.Contains(tracking.SourceClipId)) ||
            project.InkLayers.Any(layer =>
                (layer.ObjectTracking is { } tracking && clipIds.Contains(tracking.SourceClipId)) ||
                (layer.AutoMatte is { } matte && clipIds.Contains(matte.SourceClipId))) ||
            project.Frames.SelectMany(frame => frame.TextOverlays)
                .Any(text => text.ObjectTracking is { } tracking && clipIds.Contains(tracking.SourceClipId));
        if (hasDependentTracking ||
            project.AudioTracks.Any(audio => audio.LinkedVideoClipId is string id && clipIds.Contains(id)))
        {
            return "Unlink audio and clear tracking or automatic mattes that reference this track before creating a compound.";
        }

        int firstFrame = track.Clips.Min(clip => clip.StartFrame);
        double start = VideoTimelineEditing.FrameToSeconds(firstFrame, project.FrameRate);
        double end = track.Clips.Max(clip => VideoTimelineEditing.GetEndSeconds(clip, track.Kind, project.FrameRate));
        double duration = end - start;
        if (track.Clips.Any(clip => VideoTimelineEditing.GetDurationSeconds(clip, track.Kind) <= 0) ||
            !double.IsFinite(duration) || duration < 1 / project.FrameRate - 1e-8 ||
            duration > MaximumDurationSeconds)
        {
            return "Every clip needs a known duration; the compound must span at least one frame and no more than 24 hours.";
        }

        TimelineTrackProject innerTrack = VideoTimelineEditing.Clone(track);
        innerTrack.IsVisible = true;
        innerTrack.IsLocked = false;
        innerTrack.IsMuted = false;
        innerTrack.AudioBusId = null;
        foreach (TimelineClipProject clip in innerTrack.Clips)
        {
            clip.StartFrame -= firstFrame - 1;
        }

        var nested = new EditorProject
        {
            FrameRate = project.FrameRate,
            CanvasSize = project.CanvasSize,
            Colour = project.Colour,
            AudioMixLayout = project.AudioMixLayout,
            VideoTracks = [innerTrack],
        };
        if (ancestorDepth + 1 + Depth(nested) > MaximumDepth)
        {
            return $"Compound timelines can be nested at most {MaximumDepth} levels deep.";
        }

        compoundTrack = new TimelineTrackProject
        {
            Id = track.Id,
            Kind = TimelineTrackKind.Compound,
            Name = track.Name,
            IsVisible = track.IsVisible,
            IsMuted = track.IsMuted,
            AudioBusId = track.AudioBusId,
            Clips =
            [
                new TimelineClipProject
                {
                    StartFrame = firstFrame,
                    SourceDurationSeconds = duration,
                    DurationSeconds = duration,
                    NestedName = track.Name,
                    NestedProject = nested,
                },
            ],
        };
        return null;
    }
}
