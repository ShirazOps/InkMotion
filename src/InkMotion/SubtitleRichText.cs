namespace InkMotion;

/// <summary>
/// A word or phrase within a title or subtitle clip. Concatenated run text always equals the clip text.
/// <see cref="RevealSeconds"/> is relative to the clip start; before it, the run uses the clip's upcoming colour.
/// </summary>
public sealed class SubtitleRun
{
    public string Text { get; set; } = string.Empty;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    /// <summary>ARGB colour, or null to use the clip colour.</summary>
    public uint? Colour { get; set; }
    /// <summary>Seconds from the clip start at which the run is spoken or highlighted, or null for always revealed.</summary>
    public double? RevealSeconds { get; set; }

    public SubtitleRun Clone() => new()
    {
        Text = Text,
        Bold = Bold,
        Italic = Italic,
        Underline = Underline,
        Colour = Colour,
        RevealSeconds = RevealSeconds,
    };

    public bool HasSameContent(SubtitleRun other) =>
        Text == other.Text && Bold == other.Bold && Italic == other.Italic && Underline == other.Underline &&
        Colour == other.Colour && RevealSeconds == other.RevealSeconds;
}

public readonly record struct SubtitleStyledRange(int Start, int Length, bool Bold, bool Italic, bool Underline, uint Colour);

public static class SubtitleRichText
{
    public const int MaximumRuns = 512;
    public const uint DefaultUpcomingColour = 0xFFA0A0A0;

    public static bool HasRuns(TimelineClipProject clip) => clip.SubtitleRuns is { Count: > 0 };

    public static bool IsValid(TimelineClipProject clip, TimelineTrackKind kind)
    {
        if (clip.SubtitleRuns is null)
        {
            return false;
        }

        if (clip.SubtitleRuns.Count == 0)
        {
            return true;
        }

        if (kind is not (TimelineTrackKind.Text or TimelineTrackKind.Subtitle) ||
            clip.SubtitleRuns.Count > MaximumRuns ||
            clip.SubtitleRuns.Any(run => run is null || string.IsNullOrEmpty(run.Text)) ||
            !string.Equals(string.Concat(clip.SubtitleRuns.Select(run => run.Text)), clip.Text, StringComparison.Ordinal))
        {
            return false;
        }

        double previous = 0;
        foreach (SubtitleRun run in clip.SubtitleRuns)
        {
            if (run.RevealSeconds is double reveal)
            {
                if (!double.IsFinite(reveal) || reveal < previous ||
                    reveal > VideoTimelineEditing.MaximumStillDurationSeconds)
                {
                    return false;
                }

                previous = reveal;
            }
        }

        return true;
    }

    /// <summary>Splits text into word runs, keeping following whitespace with each word so the text is unchanged.</summary>
    public static List<SubtitleRun> SplitWords(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var runs = new List<SubtitleRun>();
        int index = 0;
        while (index < text.Length)
        {
            int start = index;
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            while (index < text.Length && !char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            runs.Add(new SubtitleRun { Text = text[start..index] });
        }

        if (runs.Count > MaximumRuns)
        {
            throw new InvalidOperationException($"Subtitles support at most {MaximumRuns} word runs.");
        }

        return runs;
    }

    /// <summary>Spreads reveal times evenly from the clip start so the last word appears before the clip ends.</summary>
    public static void DistributeEvenly(IList<SubtitleRun> runs, double durationSeconds)
    {
        ArgumentNullException.ThrowIfNull(runs);
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        }

        for (int index = 0; index < runs.Count; index++)
        {
            runs[index].RevealSeconds = Math.Round(durationSeconds * index / runs.Count, 3);
        }
    }

    public static List<SubtitleRun> CloneRuns(IEnumerable<SubtitleRun> runs) => runs.Select(run => run.Clone()).ToList();

    /// <summary>Parses #RRGGBB or #AARRGGBB into ARGB.</summary>
    public static bool TryParseColour(string? value, out uint colour)
    {
        colour = 0;
        string text = value?.Trim() ?? string.Empty;
        if (!text.StartsWith('#') || (text.Length != 7 && text.Length != 9) ||
            !uint.TryParse(text.AsSpan(1), System.Globalization.NumberStyles.AllowHexSpecifier,
                System.Globalization.CultureInfo.InvariantCulture, out uint parsed))
        {
            return false;
        }

        colour = text.Length == 7 ? 0xFF000000 | parsed : parsed;
        return true;
    }

    public static string FormatColour(uint colour) =>
        (colour >> 24) == 0xFF ? $"#{colour & 0xFFFFFF:X6}" : $"#{colour:X8}";

    public static bool RunsEqual(IReadOnlyList<SubtitleRun> left, IReadOnlyList<SubtitleRun> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => pair.First.HasSameContent(pair.Second));

    /// <summary>Moves reveal times earlier after the clip start is trimmed or split away.</summary>
    public static void ShiftEarlier(TimelineClipProject clip, double seconds)
    {
        foreach (SubtitleRun run in clip.SubtitleRuns)
        {
            if (run.RevealSeconds is double reveal)
            {
                run.RevealSeconds = Math.Max(0, reveal - seconds);
            }
        }
    }

    /// <summary>Keeps reveal times within a shortened clip.</summary>
    public static void ClampToDuration(TimelineClipProject clip, double durationSeconds)
    {
        foreach (SubtitleRun run in clip.SubtitleRuns)
        {
            if (run.RevealSeconds is double reveal && reveal > durationSeconds)
            {
                run.RevealSeconds = Math.Max(0, durationSeconds);
            }
        }
    }

    /// <summary>
    /// Returns styled character ranges for rendering. With no <paramref name="clipSeconds"/>, every run is revealed.
    /// </summary>
    public static IReadOnlyList<SubtitleStyledRange> GetStyledRanges(TimelineClipProject clip, double? clipSeconds)
    {
        var ranges = new List<SubtitleStyledRange>(clip.SubtitleRuns.Count);
        int start = 0;
        foreach (SubtitleRun run in clip.SubtitleRuns)
        {
            bool upcoming = clipSeconds is double seconds && run.RevealSeconds is double reveal && seconds < reveal;
            uint colour = upcoming ? clip.SubtitleUpcomingColour : run.Colour ?? clip.Colour;
            ranges.Add(new(start, run.Text.Length, run.Bold, run.Italic, run.Underline, colour));
            start += run.Text.Length;
        }

        return ranges;
    }
}
