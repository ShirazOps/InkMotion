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
    public static IReadOnlyList<string> ImportExtensions { get; } = new[] { ".srt", ".vtt", ".ass", ".ssa" };

    public static SubtitleImportResult Import(string extension, string content, double frameRate) =>
        NormaliseExtension(extension) switch
        {
            ".srt" => new(SubtitleSrt.Import(content, frameRate), []),
            ".vtt" => SubtitleWebVtt.Import(content, frameRate),
            ".ass" => SubtitleAss.Import(content, frameRate),
            ".ssa" => throw new InvalidDataException("SubStation Alpha (.ssa) files are not supported; convert them to ASS (v4.00+) first."),
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
            case ".ass":
                return SubtitleAss.Export(trackArray, frameRate);
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
/// <summary>Builds word runs from formatted text, merging adjacent text with identical formatting.</summary>
internal sealed class SubtitleRunBuilder
{
    private readonly List<SubtitleRun> _runs = [];

    public void Append(string text, bool bold, bool italic, bool underline, uint? colour, double? reveal)
    {
        if (text.Length == 0)
        {
            return;
        }

        SubtitleRun? last = _runs.Count > 0 ? _runs[^1] : null;
        if (last is not null && last.Bold == bold && last.Italic == italic && last.Underline == underline &&
            last.Colour == colour && last.RevealSeconds == reveal)
        {
            last.Text += text;
        }
        else
        {
            _runs.Add(new SubtitleRun
            {
                Text = text, Bold = bold, Italic = italic, Underline = underline, Colour = colour, RevealSeconds = reveal,
            });
        }
    }

    /// <summary>
    /// Trims trailing whitespace, makes the colour covering most characters the clip colour (null runs meaning
    /// <paramref name="defaultColour"/>) and drops runs when they carry no formatting or timing.
    /// </summary>
    public (string Text, List<SubtitleRun> Runs, uint Colour) Finish(uint defaultColour)
    {
        while (_runs.Count > 0)
        {
            _runs[^1].Text = _runs[^1].Text.TrimEnd();
            if (_runs[^1].Text.Length > 0)
            {
                break;
            }

            _runs.RemoveAt(_runs.Count - 1);
        }

        foreach (SubtitleRun run in _runs)
        {
            run.Colour ??= defaultColour;
        }

        uint colour = _runs.GroupBy(run => run.Colour!.Value)
            .Select(group => (Colour: group.Key, Length: group.Sum(run => run.Text.Length)))
            .OrderByDescending(group => group.Length)
            .Select(group => group.Colour)
            .DefaultIfEmpty(defaultColour)
            .First();
        foreach (SubtitleRun run in _runs)
        {
            if (run.Colour == colour)
            {
                run.Colour = null;
            }
        }

        string text = string.Concat(_runs.Select(run => run.Text));
        var runs = new List<SubtitleRun>();
        foreach (SubtitleRun run in _runs)
        {
            SubtitleRun? last = runs.Count > 0 ? runs[^1] : null;
            if (last is not null && last.Bold == run.Bold && last.Italic == run.Italic && last.Underline == run.Underline &&
                last.Colour == run.Colour && last.RevealSeconds == run.RevealSeconds)
            {
                last.Text += run.Text;
            }
            else
            {
                runs.Add(run);
            }
        }

        if (runs.All(run => !run.Bold && !run.Italic && !run.Underline && run.Colour is null && run.RevealSeconds is null))
        {
            runs.Clear();
        }

        return (text, runs, colour);
    }
}
