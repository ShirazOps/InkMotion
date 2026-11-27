using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace InkMotion;

/// <summary>
/// Imports and exports a documented WebVTT subset: cue timings, b/i/u/c tags, colour classes,
/// ::cue(.class) colour rules, timestamp (karaoke) tags and line/position settings. Other constructs are reported.
/// </summary>
public static class SubtitleWebVtt
{
    private static readonly Regex TimingLine = new(
        @"^(?<start>\S+)[ \t]+-->[ \t]+(?<end>\S+)(?:[ \t]+(?<settings>.*))?$", RegexOptions.CultureInvariant);

    private static readonly Regex Timestamp = new(
        @"^(?:(?<h>\d{2,}):)?(?<m>[0-5]\d):(?<s>[0-5]\d)\.(?<ms>\d{3})$", RegexOptions.CultureInvariant);

    private static readonly Regex StyleRule = new(
        @"::cue\(\s*(?<selector>[^)]*?)\s*\)\s*\{(?<body>[^}]*)\}", RegexOptions.CultureInvariant);

    private static readonly IReadOnlyDictionary<string, uint> NamedColours = new Dictionary<string, uint>(StringComparer.Ordinal)
    {
        ["white"] = 0xFFFFFFFF,
        ["lime"] = 0xFF00FF00,
        ["cyan"] = 0xFF00FFFF,
        ["red"] = 0xFFFF0000,
        ["yellow"] = 0xFFFFFF00,
        ["magenta"] = 0xFFFF00FF,
        ["blue"] = 0xFF0000FF,
        ["black"] = 0xFF000000,
    };

    private sealed record Tag(string Name, bool Bold, bool Italic, bool Underline, uint? Colour, bool IsRubyText);

    public static SubtitleImportResult Import(string content, double frameRate)
    {
        ArgumentNullException.ThrowIfNull(content);
        SubtitleSrt.ValidateFrameRate(frameRate);
        var warnings = new SubtitleWarnings();
        string[] lines = SubtitleSrt.NormaliseLineEndings(content).Split('\n');
        lines[0] = lines[0].TrimStart('\uFEFF');
        if (!(lines[0] == "WEBVTT" || lines[0].StartsWith("WEBVTT ", StringComparison.Ordinal) ||
              lines[0].StartsWith("WEBVTT\t", StringComparison.Ordinal)))
        {
            throw new InvalidDataException("The file does not start with a WEBVTT header.");
        }

        var classColours = new Dictionary<string, uint>(NamedColours, StringComparer.Ordinal);
        uint upcomingColour = SubtitleRichText.DefaultUpcomingColour;
        var cues = new List<SubtitleCueText>();
        int index = 1;
        while (index < lines.Length && lines[index].Length > 0)
        {
            index++;
        }

        while (index < lines.Length)
        {
            while (index < lines.Length && string.IsNullOrWhiteSpace(lines[index]))
            {
                index++;
            }

            if (index >= lines.Length)
            {
                break;
            }

            int blockLine = index + 1;
            int blockStart = index;
            while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]))
            {
                index++;
            }

            string[] block = lines[blockStart..index];
            string first = block[0];
            if (IsBlockKeyword(first, "NOTE"))
            {
                continue;
            }

            if (IsBlockKeyword(first, "STYLE") && !first.Contains("-->", StringComparison.Ordinal))
            {
                if (cues.Count > 0)
                {
                    warnings.Add("STYLE blocks after the first cue are not allowed in WebVTT and were ignored.");
                }
                else
                {
                    ParseStyle(string.Join("\n", block[1..]), classColours, ref upcomingColour, warnings);
                }

                continue;
            }

            if (IsBlockKeyword(first, "REGION") && !first.Contains("-->", StringComparison.Ordinal))
            {
                warnings.Add("REGION blocks are not supported; region placement was ignored.");
                continue;
            }

            int timingIndex = first.Contains("-->", StringComparison.Ordinal) ? 0 : 1;
            if (timingIndex >= block.Length)
            {
                throw new InvalidDataException($"WebVTT block at line {blockLine} has no cue timing.");
            }

            Match match = TimingLine.Match(block[timingIndex].Trim());
            if (!match.Success ||
                !TryParseTime(match.Groups["start"].Value, out double start) ||
                !TryParseTime(match.Groups["end"].Value, out double end) || end <= start)
            {
                throw new InvalidDataException($"WebVTT cue at line {blockLine + timingIndex} has invalid timing.");
            }

            string rawText = string.Join("\n", block[(timingIndex + 1)..]);
            (string text, List<SubtitleRun> runs, uint? colour) =
                ParseCueText(rawText, start, end, classColours, warnings, blockLine);
            if (string.IsNullOrWhiteSpace(text))
            {
                warnings.Add("Cues without text were skipped.");
                continue;
            }

            (double x, double y) = ParseSettings(match.Groups["settings"].Value, warnings);
            cues.Add(new SubtitleCueText(start, end, text)
            {
                Runs = runs.Count > 0 ? runs : null,
                Style = new SubtitleCueStyle
                {
                    Colour = colour ?? 0xFFFFFFFF,
                    UpcomingColour = upcomingColour,
                    X = x,
                    Y = y,
                },
            });
        }

        if (cues.Count == 0)
        {
            throw new InvalidDataException("The subtitle file contains no cues.");
        }

        return new(SubtitleSrt.CreateTracks(cues, frameRate), warnings.ToArray());
    }

    public static SubtitleExportResult Export(IEnumerable<TimelineTrackProject> tracks, double frameRate)
    {
        IReadOnlyList<(double StartSeconds, TimelineClipProject Clip)> cues = SubtitleSrt.CollectCues(tracks, frameRate);
        var warnings = new SubtitleWarnings();
        var customClasses = new SortedDictionary<string, uint>(StringComparer.Ordinal);
        uint[] upcomingColours = cues.Where(cue => cue.Clip.SubtitleRuns.Any(run => run.RevealSeconds is not null))
            .Select(cue => cue.Clip.SubtitleUpcomingColour).Distinct().ToArray();
        if (upcomingColours.Length > 1)
        {
            warnings.Add("WebVTT has one upcoming-word colour per file, so the first clip's colour was used.");
        }

        var body = new StringBuilder();
        foreach ((double start, TimelineClipProject clip) in cues)
        {
            string text = SubtitleSrt.NormaliseLineEndings(clip.Text);
            if (text.Split('\n').Any(string.IsNullOrWhiteSpace))
            {
                throw new InvalidDataException($"The subtitle \"{Shorten(text)}\" contains a blank line, which would end a WebVTT cue.");
            }

            double end = start + clip.DurationSeconds;
            body.Append(FormatTime(start)).Append(" --> ").Append(FormatTime(end));
            if (clip.X != 0.5 || clip.Y != 0.88)
            {
                double x = Math.Clamp(clip.X, 0, 1);
                double y = Math.Clamp(clip.Y, 0, 1);
                if (x != clip.X || y != clip.Y)
                {
                    warnings.Add("Positions outside the frame were clamped to its edges.");
                }

                body.Append(string.Create(CultureInfo.InvariantCulture,
                    $" position:{x * 100:0.###}%,center line:{y * 100:0.###}%,center align:center"));
            }

            body.Append('\n');
            if (clip.FontFamily != "Segoe UI" || clip.FontSize != 48 || clip.TextBackgroundColour != 0xB0000000 ||
                clip.FadeInSeconds != 0 || clip.FadeOutSeconds != 0)
            {
                warnings.Add("WebVTT does not store font, size, background or fades per cue, so those were not written.");
            }

            List<SubtitleRun> runs = SubtitleRichText.HasRuns(clip)
                ? clip.SubtitleRuns
                : [new SubtitleRun { Text = clip.Text }];
            bool timed = false;
            foreach (SubtitleRun run in runs)
            {
                if (run.RevealSeconds is double reveal && reveal > 0)
                {
                    if (reveal >= clip.DurationSeconds)
                    {
                        warnings.Add("Words revealed after their cue ends were written at the cue's final millisecond.");
                        reveal = Math.Max(0.001, clip.DurationSeconds - 0.001);
                    }

                    body.Append('<').Append(FormatTime(start + reveal)).Append('>');
                    timed = true;
                }
                else if (run.RevealSeconds is null && timed)
                {
                    warnings.Add("Words without a reveal time after timed words take the preceding word's time in WebVTT.");
                }

                uint colour = run.Colour ?? clip.Colour;
                string? colourClass = colour == 0xFFFFFFFF ? null : GetColourClass(colour, customClasses);
                if (colourClass is not null)
                {
                    body.Append("<c.").Append(colourClass).Append('>');
                }

                if (run.Bold) body.Append("<b>");
                if (run.Italic) body.Append("<i>");
                if (run.Underline) body.Append("<u>");
                body.Append(Escape(SubtitleSrt.NormaliseLineEndings(run.Text)));
                if (run.Underline) body.Append("</u>");
                if (run.Italic) body.Append("</i>");
                if (run.Bold) body.Append("</b>");
                if (colourClass is not null)
                {
                    body.Append("</c>");
                }
            }

            body.Append("\n\n");
        }

        var output = new StringBuilder("WEBVTT\n\n");
        bool writeFuture = upcomingColours.Length > 0 && upcomingColours[0] != SubtitleRichText.DefaultUpcomingColour;
        if (customClasses.Count > 0 || writeFuture)
        {
            output.Append("STYLE\n");
            foreach ((string name, uint colour) in customClasses)
            {
                output.Append("::cue(.").Append(name).Append(") { color: ").Append(FormatCssColour(colour)).Append("; }\n");
            }

            if (writeFuture)
            {
                output.Append("::cue(:future) { color: ").Append(FormatCssColour(upcomingColours[0])).Append("; }\n");
            }

            output.Append('\n');
        }

        return new(output.Append(body).ToString(), warnings.ToArray());
    }

    private static bool IsBlockKeyword(string line, string keyword) =>
        line.StartsWith(keyword, StringComparison.Ordinal) &&
        (line.Length == keyword.Length || line[keyword.Length] is ' ' or '\t');

    private static void ParseStyle(string css, Dictionary<string, uint> classColours, ref uint upcomingColour,
        SubtitleWarnings warnings)
    {
        string withoutComments = Regex.Replace(css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        foreach (Match rule in StyleRule.Matches(withoutComments))
        {
            string selector = rule.Groups["selector"].Value;
            uint? colour = null;
            foreach (string declaration in rule.Groups["body"].Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = declaration.IndexOf(':', StringComparison.Ordinal);
                string property = colon < 0 ? declaration.Trim() : declaration[..colon].Trim();
                if (property.Length == 0)
                {
                    continue;
                }

                if (property.Equals("color", StringComparison.OrdinalIgnoreCase) && colon >= 0 &&
                    TryParseCssColour(declaration[(colon + 1)..], out uint parsed))
                {
                    colour = parsed;
                }
                else
                {
                    warnings.Add($"Only the color property is supported in WebVTT STYLE rules; '{property}' was ignored.");
                }
            }

            if (colour is null)
            {
                continue;
            }

            if (selector == ":future")
            {
                upcomingColour = colour.Value;
            }
            else if (Regex.IsMatch(selector, @"^\.[A-Za-z0-9_-]+$"))
            {
                classColours[selector[1..]] = colour.Value;
            }
            else
            {
                warnings.Add($"Only ::cue(.class) and ::cue(:future) rules are supported; '::cue({selector})' was ignored.");
            }
        }

        if (!string.IsNullOrWhiteSpace(StyleRule.Replace(withoutComments, string.Empty)))
        {
            warnings.Add("STYLE rules other than ::cue(...) were ignored.");
        }
    }

    private static (double X, double Y) ParseSettings(string settings, SubtitleWarnings warnings)
    {
        double x = 0.5;
        double y = 0.88;
        foreach (string setting in settings.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = setting.IndexOf(':', StringComparison.Ordinal);
            string name = colon < 0 ? setting : setting[..colon];
            string value = colon < 0 ? string.Empty : setting[(colon + 1)..];
            switch (name)
            {
                case "line":
                case "position":
                {
                    string[] parts = value.Split(',');
                    if (!TryParsePercentage(parts[0], out double fraction))
                    {
                        warnings.Add("Only percentage line and position settings are supported; line numbers and 'auto' use the default position.");
                        break;
                    }

                    if (parts.Length > 1 && parts[1] is not ("center" or "middle"))
                    {
                        warnings.Add("Line and position alignments other than centre are applied to the caption centre, so placement is approximate.");
                    }
                    else if (parts.Length == 1 && name == "line")
                    {
                        warnings.Add("WebVTT line percentages are applied to the caption centre, so placement is approximate.");
                    }

                    if (name == "line")
                    {
                        y = fraction;
                    }
                    else
                    {
                        x = fraction;
                    }

                    break;
                }
                case "align" when value is "center" or "middle":
                    break;
                case "align":
                    warnings.Add($"Cue alignment '{value}' is not supported; text is centred.");
                    break;
                case "size":
                    warnings.Add("Cue size settings were ignored.");
                    break;
                case "vertical":
                    warnings.Add("Vertical cue text is not supported and is shown horizontally.");
                    break;
                case "region":
                    warnings.Add("REGION blocks are not supported; region placement was ignored.");
                    break;
                default:
                    warnings.Add($"Unknown cue setting '{name}' was ignored.");
                    break;
            }
        }

        return (x, y);
    }

    private static (string Text, List<SubtitleRun> Runs, uint? Colour) ParseCueText(string raw, double start, double end,
        IReadOnlyDictionary<string, uint> classColours, SubtitleWarnings warnings, int blockLine)
    {
        var runs = new List<SubtitleRun>();
        var stack = new List<Tag>();
        double? reveal = null;

        void Append(string value)
        {
            bool bold = stack.Any(tag => tag.Bold);
            bool italic = stack.Any(tag => tag.Italic);
            bool underline = stack.Any(tag => tag.Underline);
            uint? colour = stack.LastOrDefault(tag => tag.Colour is not null)?.Colour;
            SubtitleRun? last = runs.Count > 0 ? runs[^1] : null;
            if (last is not null && last.Bold == bold && last.Italic == italic && last.Underline == underline &&
                last.Colour == colour && last.RevealSeconds == reveal)
            {
                last.Text += value;
            }
            else
            {
                runs.Add(new SubtitleRun
                {
                    Text = value, Bold = bold, Italic = italic, Underline = underline, Colour = colour, RevealSeconds = reveal,
                });
            }
        }

        int index = 0;
        while (index < raw.Length)
        {
            char current = raw[index];
            if (current == '<')
            {
                int close = raw.IndexOf('>', index + 1);
                if (close < 0)
                {
                    throw new InvalidDataException($"WebVTT cue at line {blockLine} has an unterminated tag.");
                }

                string tag = raw[(index + 1)..close];
                index = close + 1;
                if (tag.StartsWith('/'))
                {
                    string name = tag[1..].Trim();
                    int open = stack.FindLastIndex(item => item.Name == name);
                    if (open >= 0)
                    {
                        if (stack[open].IsRubyText)
                        {
                            Append(")");
                        }

                        stack.RemoveRange(open, stack.Count - open);
                    }

                    continue;
                }

                if (tag.Length > 0 && char.IsAsciiDigit(tag[0]))
                {
                    if (!TryParseTime(tag, out double time) || time < start || time > end ||
                        time - start < (reveal ?? 0))
                    {
                        throw new InvalidDataException($"WebVTT cue at line {blockLine} has an invalid timestamp tag <{tag}>.");
                    }

                    reveal = time - start;
                    continue;
                }

                int nameEnd = 0;
                while (nameEnd < tag.Length && tag[nameEnd] is not ('.' or ' ' or '\t'))
                {
                    nameEnd++;
                }

                string tagName = tag[..nameEnd];
                int annotationStart = tag.IndexOfAny([' ', '\t'], nameEnd);
                string classText = annotationStart < 0 ? tag[nameEnd..] : tag[nameEnd..annotationStart];
                string annotation = annotationStart < 0 ? string.Empty : tag[(annotationStart + 1)..].Trim();
                uint? colour = null;
                foreach (string className in classText.Split('.', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (classColours.TryGetValue(className, out uint classColour))
                    {
                        colour = classColour;
                    }
                    else if (className.StartsWith("bg_", StringComparison.Ordinal))
                    {
                        warnings.Add("Background colour classes are not supported; the cue background is used.");
                    }
                    else
                    {
                        warnings.Add($"Unknown cue class '{className}' was ignored.");
                    }
                }

                switch (tagName)
                {
                    case "b":
                        stack.Add(new(tagName, true, false, false, colour, false));
                        break;
                    case "i":
                        stack.Add(new(tagName, false, true, false, colour, false));
                        break;
                    case "u":
                        stack.Add(new(tagName, false, false, true, colour, false));
                        break;
                    case "c":
                        stack.Add(new(tagName, false, false, false, colour, false));
                        break;
                    case "v":
                        stack.Add(new(tagName, false, false, false, colour, false));
                        if (annotation.Length > 0)
                        {
                            warnings.Add("Voice spans were imported as 'Name: ' speaker prefixes.");
                            Append(annotation + ": ");
                        }

                        break;
                    case "lang":
                        warnings.Add("Language spans were imported as plain text; the language was not kept.");
                        stack.Add(new(tagName, false, false, false, colour, false));
                        break;
                    case "ruby":
                        stack.Add(new(tagName, false, false, false, colour, false));
                        break;
                    case "rt":
                        warnings.Add("Ruby annotations were placed in brackets after their base text.");
                        stack.Add(new(tagName, false, false, false, colour, true));
                        Append("(");
                        break;
                    default:
                        warnings.Add($"Unsupported tag <{tagName}> was ignored; its text was kept.");
                        break;
                }

                continue;
            }

            if (current == '&')
            {
                int semicolon = raw.IndexOf(';', index + 1);
                string? decoded = semicolon > index && semicolon - index <= 10
                    ? DecodeEntity(raw[(index + 1)..semicolon])
                    : null;
                if (decoded is not null)
                {
                    Append(decoded);
                    index = semicolon + 1;
                    continue;
                }

                warnings.Add("Unknown character references were kept as literal text.");
            }

            Append(current.ToString());
            index++;
        }

        // Trailing whitespace is not meaningful in a cue and is trimmed, as for SubRip.
        while (runs.Count > 0)
        {
            runs[^1].Text = runs[^1].Text.TrimEnd();
            if (runs[^1].Text.Length > 0)
            {
                break;
            }

            runs.RemoveAt(runs.Count - 1);
        }

        string text = string.Concat(runs.Select(run => run.Text));
        // The colour covering most characters becomes the clip colour, so exported cue-wide colours round-trip.
        uint? sharedColour = runs.GroupBy(run => run.Colour)
            .Select(group => (Colour: group.Key, Length: group.Sum(run => run.Text.Length)))
            .OrderByDescending(group => group.Length)
            .FirstOrDefault().Colour;
        if (sharedColour is not null)
        {
            foreach (SubtitleRun run in runs)
            {
                run.Colour = run.Colour == sharedColour ? null : run.Colour ?? 0xFFFFFFFF;
            }
        }

        if (runs.All(run => !run.Bold && !run.Italic && !run.Underline && run.Colour is null && run.RevealSeconds is null))
        {
            runs.Clear();
        }

        return (text, runs, sharedColour);
    }

    private static string? DecodeEntity(string name)
    {
        switch (name)
        {
            case "amp": return "&";
            case "lt": return "<";
            case "gt": return ">";
            case "nbsp": return "\u00A0";
            case "lrm": return "\u200E";
            case "rlm": return "\u200F";
            case "quot": return "\"";
            case "apos": return "'";
        }

        if (name.StartsWith('#'))
        {
            bool hex = name.Length > 1 && name[1] is 'x' or 'X';
            string digits = hex ? name[2..] : name[1..];
            if (int.TryParse(digits, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None,
                    CultureInfo.InvariantCulture, out int codePoint) &&
                codePoint is > 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF))
            {
                return char.ConvertFromUtf32(codePoint);
            }
        }

        return null;
    }

    private static bool TryParsePercentage(string value, out double fraction)
    {
        fraction = 0;
        return value.EndsWith('%') &&
            double.TryParse(value[..^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double percent) &&
            percent is >= 0 and <= 100 &&
            (fraction = percent / 100) >= 0;
    }

    internal static bool TryParseCssColour(string value, out uint colour)
    {
        colour = 0;
        string text = value.Trim().ToLowerInvariant();
        if (NamedColours.TryGetValue(text, out colour))
        {
            return true;
        }

        if (text.StartsWith('#') && uint.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out uint hex))
        {
            switch (text.Length)
            {
                case 4:
                    uint r = (hex >> 8) & 0xF, g = (hex >> 4) & 0xF, b = hex & 0xF;
                    colour = 0xFF000000 | (r * 0x11 << 16) | (g * 0x11 << 8) | b * 0x11;
                    return true;
                case 7:
                    colour = 0xFF000000 | hex;
                    return true;
                case 9:
                    colour = (hex << 24) | (hex >> 8);
                    return true;
            }
        }

        Match rgb = Regex.Match(text,
            @"^rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*(?:,\s*([01]?(?:\.\d+)?)\s*)?\)$");
        if (rgb.Success)
        {
            int[] channels = [.. Enumerable.Range(1, 3).Select(group => int.Parse(rgb.Groups[group].Value, CultureInfo.InvariantCulture))];
            double alpha = rgb.Groups[4].Success && rgb.Groups[4].Value.Length > 0
                ? double.Parse(rgb.Groups[4].Value, CultureInfo.InvariantCulture)
                : 1;
            if (channels.All(channel => channel <= 255) && alpha is >= 0 and <= 1)
            {
                colour = ((uint)Math.Round(alpha * 255) << 24) | ((uint)channels[0] << 16) | ((uint)channels[1] << 8) | (uint)channels[2];
                return true;
            }
        }

        return false;
    }

    private static string FormatCssColour(uint colour) =>
        (colour >> 24) == 0xFF
            ? $"#{colour & 0xFFFFFF:x6}"
            : $"#{colour & 0xFFFFFF:x6}{colour >> 24:x2}";

    private static string GetColourClass(uint colour, SortedDictionary<string, uint> customClasses)
    {
        foreach ((string name, uint named) in NamedColours)
        {
            if (named == colour)
            {
                return name;
            }
        }

        string custom = (colour >> 24) == 0xFF ? $"c{colour & 0xFFFFFF:x6}" : $"c{colour:x8}";
        customClasses[custom] = colour;
        return custom;
    }

    private static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    private static string Shorten(string text) => text.Length <= 40 ? text.Replace('\n', ' ') : text[..40].Replace('\n', ' ') + "…";

    internal static bool TryParseTime(string value, out double seconds)
    {
        seconds = 0;
        Match match = Timestamp.Match(value);
        if (!match.Success)
        {
            return false;
        }

        long hours = match.Groups["h"].Success
            ? long.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture)
            : 0;
        seconds = hours * 3600d + int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) * 60d +
            int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture) +
            int.Parse(match.Groups["ms"].Value, CultureInfo.InvariantCulture) / 1000d;
        return double.IsFinite(seconds);
    }

    internal static string FormatTime(double seconds)
    {
        double total = Math.Round(seconds * 1000, MidpointRounding.AwayFromZero);
        if (!double.IsFinite(total) || total < 0 || total > long.MaxValue)
        {
            throw new InvalidDataException("A subtitle clip is outside the supported timeline range.");
        }

        long milliseconds = (long)total;
        return string.Create(CultureInfo.InvariantCulture,
            $"{milliseconds / 3_600_000:00}:{milliseconds / 60_000 % 60:00}:{milliseconds / 1_000 % 60:00}.{milliseconds % 1_000:000}");
    }
}
