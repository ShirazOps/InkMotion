namespace InkMotion;

public sealed record SubtitleImportResult(IReadOnlyList<TimelineTrackProject> Tracks, IReadOnlyList<string> Warnings);

public sealed record SubtitleExportResult(string Content, IReadOnlyList<string> Warnings);

/// <summary>Collects distinct, ordered warnings about constructs that could not be represented exactly.</summary>
internal sealed class SubtitleWarnings
{
    private readonly List<string> _warnings = [];

    public void Add(string warning)
    {
        if (!_warnings.Contains(warning, StringComparer.Ordinal))
        {
            _warnings.Add(warning);
        }
    }

    public IReadOnlyList<string> ToArray() => _warnings.ToArray();
}

/// <summary>Chooses the subtitle format from the file extension.</summary>
public static class SubtitleFormats
{
    public static IReadOnlyList<string> ImportExtensions { get; } = [".srt", ".vtt"];

    public static SubtitleImportResult Import(string extension, string content, double frameRate) =>
        NormaliseExtension(extension) switch
        {
            ".srt" => new(SubtitleSrt.Import(content, frameRate), []),
            ".vtt" => SubtitleWebVtt.Import(content, frameRate),
            _ => throw new InvalidDataException($"Unsupported subtitle file type '{extension}'."),
        };

    public static SubtitleExportResult Export(string extension, IEnumerable<TimelineTrackProject> tracks, double frameRate)
    {
        TimelineTrackProject[] trackArray = tracks.ToArray();
        switch (NormaliseExtension(extension))
        {
            case ".srt":
                var warnings = new SubtitleWarnings();
                foreach ((_, TimelineClipProject clip) in SubtitleSrt.CollectCues(trackArray, frameRate))
                {
                    if (SubtitleRichText.HasRuns(clip))
                    {
                        warnings.Add("SubRip keeps plain text only, so word timing and styling were not written.");
                    }

                    if (HasNonDefaultStyle(clip))
                    {
                        warnings.Add("SubRip does not store font, colour, position or fades, so cue styling was not written.");
                    }
                }

                return new(SubtitleSrt.Export(trackArray, frameRate), warnings.ToArray());
            case ".vtt":
                return SubtitleWebVtt.Export(trackArray, frameRate);
            default:
                throw new InvalidDataException($"Unsupported subtitle file type '{extension}'.");
        }
    }

    internal static bool HasNonDefaultStyle(TimelineClipProject clip)
    {
        var style = new SubtitleCueStyle();
        return clip.FontFamily != style.FontFamily || clip.FontSize != style.FontSize || clip.Colour != style.Colour ||
            clip.TextBackgroundColour != style.TextBackgroundColour || clip.X != style.X || clip.Y != style.Y ||
            clip.FadeInSeconds != 0 || clip.FadeOutSeconds != 0;
    }

    private static string NormaliseExtension(string extension) =>
        (extension ?? string.Empty).Trim().ToLowerInvariant();
}
