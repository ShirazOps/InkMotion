using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace InkMotion;

/// <summary>
/// Imports and exports a documented Advanced SubStation Alpha (v4.00+) subset: styles, Dialogue events,
/// b/i/u/colour/alpha/karaoke/reset/position/alignment/fade overrides and the \N, \n and \h escapes.
/// Other constructs are reported, and vector drawings are skipped with a warning.
/// </summary>
public static class SubtitleAss
{
    private const double LineHeightFactor = 1.2;

    private static readonly string[] OverrideNames =
    [
        "iclip", "xbord", "ybord", "xshad", "yshad", "alpha", "fscx", "fscy", "fade", "move", "clip", "bord",
        "shad", "blur", "pos", "fad", "org", "fsp", "frx", "fry", "frz", "fax", "fay", "pbo",
        "1c", "2c", "3c", "4c", "1a", "2a", "3a", "4a", "fn", "fs", "fe", "fr", "be", "an", "kf", "ko",
        "a", "k", "K", "q", "r", "t", "p", "c", "b", "i", "u", "s",
    ];

    private static readonly Regex AssTime = new(@"^(?<h>\d+):(?<m>[0-5]\d):(?<s>[0-5]\d)[.:](?<cs>\d{2})$",
        RegexOptions.CultureInvariant);

    private sealed class AssStyle
    {
        public string Name { get; set; } = "Default";
        public string FontName { get; set; } = "Arial";
        public double FontSize { get; set; } = 20;
        public uint Primary { get; set; } = 0xFFFFFFFF;
        public uint Secondary { get; set; } = 0xFFFF0000;
        public uint Outline { get; set; } = 0xFF000000;
        public uint Back { get; set; } = 0xFF000000;
        public bool Bold { get; set; }
        public bool Italic { get; set; }
        public bool Underline { get; set; }
        public int BorderStyle { get; set; } = 1;
        public int Alignment { get; set; } = 2;
        public int MarginL { get; set; }
        public int MarginR { get; set; }
        public int MarginV { get; set; }
    }

    private sealed class RunState
    {
        public bool Bold;
        public bool Italic;
        public bool Underline;
        public uint Colour;

        public RunState(AssStyle style)
        {
            Bold = style.Bold;
            Italic = style.Italic;
            Underline = style.Underline;
            Colour = style.Primary;
        }
    }

    public static SubtitleImportResult Import(string content, double frameRate)
    {
        ArgumentNullException.ThrowIfNull(content);
        SubtitleSrt.ValidateFrameRate(frameRate);
        var warnings = new SubtitleWarnings();
        string[] lines = SubtitleSrt.NormaliseLineEndings(content).Split('\n');
        lines[0] = lines[0].TrimStart('\uFEFF');
        if (!lines[0].Trim().Equals("[Script Info]", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The file does not start with an ASS [Script Info] section.");
        }

        string section = string.Empty;
        var info = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var styles = new Dictionary<string, AssStyle>(StringComparer.OrdinalIgnoreCase);
        string[]? styleFormat = null;
        string[]? eventFormat = null;
        var events = new List<(int Line, string[] Fields)>();
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith("!:", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim().ToLowerInvariant();
                switch (section)
                {
                    case "script info":
                    case "v4+ styles":
                    case "events":
                        break;
                    case "v4 styles":
                        throw new InvalidDataException("Only ASS (v4.00+) scripts are supported; SSA [V4 Styles] are not.");
                    case "fonts":
                    case "graphics":
                        warnings.Add("Embedded fonts and graphics are not supported and were ignored.");
                        break;
                    default:
                        warnings.Add($"The [{line[1..^1]}] section is not supported and was ignored.");
                        break;
                }

                continue;
            }

            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }

            string key = line[..colon].Trim();
            string value = line[(colon + 1)..].TrimStart();
            switch (section)
            {
                case "script info":
                    info[key] = value.Trim();
                    break;
                case "v4+ styles" when key.Equals("Format", StringComparison.OrdinalIgnoreCase):
                    styleFormat = value.Split(',').Select(field => field.Trim()).ToArray();
                    break;
                case "v4+ styles" when key.Equals("Style", StringComparison.OrdinalIgnoreCase):
                {
                    if (styleFormat is null)
                    {
                        throw new InvalidDataException($"ASS style at line {index + 1} appears before its Format line.");
                    }

                    AssStyle style = ParseStyle(SplitFields(value, styleFormat.Length), styleFormat, index + 1, warnings);
                    styles[style.Name] = style;
                    break;
                }
                case "events" when key.Equals("Format", StringComparison.OrdinalIgnoreCase):
                    eventFormat = value.Split(',').Select(field => field.Trim()).ToArray();
                    if (!eventFormat[^1].Equals("Text", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("The ASS [Events] Format line must end with Text.");
                    }

                    break;
                case "events" when key.Equals("Dialogue", StringComparison.OrdinalIgnoreCase):
                    if (eventFormat is null)
                    {
                        throw new InvalidDataException($"ASS event at line {index + 1} appears before its Format line.");
                    }

                    events.Add((index + 1, SplitFields(value, eventFormat.Length)));
                    break;
                case "events" when key.Equals("Comment", StringComparison.OrdinalIgnoreCase):
                    break;
                case "events":
                    warnings.Add($"{key} events are not supported and were ignored.");
                    break;
            }
        }

        if (info.TryGetValue("ScriptType", out string? scriptType) &&
            !scriptType.Equals("v4.00+", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Only ASS (v4.00+) scripts are supported, not '{scriptType}'.");
        }

        (double playResX, double playResY) = GetPlayResolution(info);
        bool smartWrapOff = info.TryGetValue("WrapStyle", out string? wrapStyle) && wrapStyle == "2";
        if (eventFormat is null || events.Count == 0)
        {
            throw new InvalidDataException("The subtitle file contains no Dialogue events.");
        }

        var cues = new List<SubtitleCueText>();
        foreach ((int lineNumber, string[] fields) in events)
        {
            string Field(string name)
            {
                int fieldIndex = Array.FindIndex(eventFormat, item => item.Equals(name, StringComparison.OrdinalIgnoreCase));
                return fieldIndex < 0 ? string.Empty : fields[fieldIndex].Trim();
            }

            if (!TryParseTime(Field("Start"), out double start) || !TryParseTime(Field("End"), out double end))
            {
                throw new InvalidDataException($"ASS event at line {lineNumber} has invalid timing.");
            }

            if (end <= start)
            {
                warnings.Add("Events with no duration were skipped.");
                continue;
            }

            string styleName = Field("Style").TrimStart('*');
            if (!styles.TryGetValue(styleName, out AssStyle? style))
            {
                warnings.Add($"Style '{styleName}' is not defined, so the Default style or ASS defaults were used.");
                style = styles.GetValueOrDefault("Default") ?? new AssStyle();
            }

            if (Field("Effect").Length > 0)
            {
                warnings.Add("Event effects (Karaoke, Scroll, Banner) are not supported and were ignored.");
            }

            int marginV = int.TryParse(Field("MarginV"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int eventMarginV) &&
                eventMarginV != 0 ? eventMarginV : style.MarginV;
            SubtitleCueText? cue = ParseEvent(fields[^1], start, end, style, styles, marginV, playResX, playResY,
                smartWrapOff, warnings, lineNumber);
            if (cue is not null)
            {
                cues.Add(cue);
            }
        }

        if (cues.Count == 0)
        {
            throw new InvalidDataException("The subtitle file contains no usable Dialogue events.");
        }

        return new(SubtitleSrt.CreateTracks(cues, frameRate), warnings.ToArray());
    }

    public static SubtitleExportResult Export(IEnumerable<TimelineTrackProject> tracks, double frameRate)
    {
        IReadOnlyList<(double StartSeconds, TimelineClipProject Clip)> cues = SubtitleSrt.CollectCues(tracks, frameRate);
        var warnings = new SubtitleWarnings();
        var styles = new List<(string Key, string Line)>();
        var events = new StringBuilder();
        foreach ((double start, TimelineClipProject clip) in cues)
        {
            string text = SubtitleSrt.NormaliseLineEndings(clip.Text);
            if (text.Contains('{') || text.Contains('}'))
            {
                throw new InvalidDataException($"The subtitle \"{Shorten(text)}\" contains braces, which ASS treats as override tags.");
            }

            if (Regex.IsMatch(text, @"\\[Nnh]"))
            {
                throw new InvalidDataException($"The subtitle \"{Shorten(text)}\" contains \\N, \\n or \\h, which ASS treats as escapes.");
            }

            string fontSize = FormatNumber(Math.Clamp(clip.FontSize * clip.Scale, 1, 4000));
            string key = string.Join("|", clip.FontFamily, fontSize, clip.Colour, clip.SubtitleUpcomingColour, clip.TextBackgroundColour);
            int styleIndex = styles.FindIndex(style => style.Key == key);
            if (styleIndex < 0)
            {
                styleIndex = styles.Count;
                string padding = FormatNumber(Math.Round(clip.FontSize * clip.Scale * 0.12, 1));
                styles.Add((key, string.Join(",",
                    $"Style: S{styleIndex + 1}", clip.FontFamily.Replace(",", string.Empty, StringComparison.Ordinal), fontSize,
                    FormatColour(clip.Colour, true), FormatColour(clip.SubtitleUpcomingColour, true),
                    FormatColour(clip.TextBackgroundColour, true), FormatColour(clip.TextBackgroundColour, true),
                    "0,0,0,0,100,100,0,0,3", padding, "0,5,0,0,0,1")));
                if (clip.FontFamily.Contains(','))
                {
                    warnings.Add("Commas were removed from font names, which ASS cannot store.");
                }
            }

            if (clip.RotationDegrees != 0)
            {
                warnings.Add("Clip rotation is not written to ASS.");
            }

            var line = new StringBuilder();
            line.Append(string.Create(CultureInfo.InvariantCulture,
                $"{{\\pos({FormatNumber(clip.X * 1920)},{FormatNumber(clip.Y * 1080)})"));
            if (clip.FadeInSeconds > 0 || clip.FadeOutSeconds > 0)
            {
                line.Append(string.Create(CultureInfo.InvariantCulture,
                    $"\\fad({Math.Round(clip.FadeInSeconds * 1000)},{Math.Round(clip.FadeOutSeconds * 1000)})"));
            }

            line.Append('}');
            AppendRuns(line, clip, warnings);
            events.Append("Dialogue: 0,").Append(FormatTime(start)).Append(',')
                .Append(FormatTime(start + clip.DurationSeconds)).Append(",S").Append(styleIndex + 1)
                .Append(",,0,0,0,,").Append(line).Append('\n');
        }

        var output = new StringBuilder();
        output.Append("[Script Info]\n; Written by InkMotion\nScriptType: v4.00+\nPlayResX: 1920\nPlayResY: 1080\n")
            .Append("WrapStyle: 0\nScaledBorderAndShadow: yes\n\n[V4+ Styles]\n")
            .Append("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, ")
            .Append("Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, ")
            .Append("MarginL, MarginR, MarginV, Encoding\n");
        foreach ((_, string styleLine) in styles)
        {
            output.Append(styleLine).Append('\n');
        }

        output.Append("\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n")
            .Append(events);
        return new(output.ToString(), warnings.ToArray());
    }

    private static void AppendRuns(StringBuilder line, TimelineClipProject clip, SubtitleWarnings warnings)
    {
        if (!SubtitleRichText.HasRuns(clip))
        {
            line.Append(Escape(clip.Text));
            return;
        }

        List<SubtitleRun> runs = clip.SubtitleRuns;
        bool timed = runs.Any(run => run.RevealSeconds is not null);
        // Karaoke syllables are sequential, so convert reveal times into centisecond boundaries.
        var boundaries = new long?[runs.Count];
        long? previous = null;
        for (int index = 0; index < runs.Count; index++)
        {
            if (runs[index].RevealSeconds is double reveal)
            {
                previous = Math.Max(previous ?? 0, (long)Math.Round(Math.Min(reveal, clip.DurationSeconds) * 100));
                boundaries[index] = previous;
            }
            else if (previous is not null)
            {
                warnings.Add("Words without a reveal time after timed words take the preceding word's time in ASS.");
                boundaries[index] = previous;
            }
        }

        long clipEnd = (long)Math.Round(clip.DurationSeconds * 100);
        int firstTimed = Array.FindIndex(boundaries, boundary => boundary is not null);
        for (int index = 0; index < runs.Count; index++)
        {
            SubtitleRun run = runs[index];
            var tags = new StringBuilder("\\r");
            if (timed && index == firstTimed && boundaries[index] > 0)
            {
                line.Append(string.Create(CultureInfo.InvariantCulture, $"{{\\k{boundaries[index]}}}"));
            }

            if (boundaries[index] is long boundary)
            {
                long next = index + 1 < runs.Count && boundaries[index + 1] is long following ? following : clipEnd;
                tags.Append(string.Create(CultureInfo.InvariantCulture, $"\\k{Math.Max(0, next - boundary)}"));
            }

            if (run.Bold) tags.Append("\\b1");
            if (run.Italic) tags.Append("\\i1");
            if (run.Underline) tags.Append("\\u1");
            if (run.Colour is uint colour && colour != clip.Colour)
            {
                tags.Append("\\c").Append(FormatColour(colour, false)).Append("\\1a&H")
                    .Append((255 - (colour >> 24)).ToString("X2", CultureInfo.InvariantCulture)).Append('&');
            }

            line.Append('{').Append(tags).Append('}').Append(Escape(run.Text));
        }
    }

    private static SubtitleCueText? ParseEvent(string raw, double start, double end, AssStyle style,
        IReadOnlyDictionary<string, AssStyle> styles, int marginV, double playResX, double playResY, bool smartWrapOff,
        SubtitleWarnings warnings, int lineNumber)
    {
        var builder = new SubtitleRunBuilder();
        var state = new RunState(style);
        AssStyle currentStyle = style;
        double? reveal = null;
        double karaokeCursor = 0;
        uint upcoming = style.Secondary;
        bool hasText = false;
        (double X, double Y)? position = null;
        int alignment = style.Alignment;
        bool alignmentSet = false;
        double fadeIn = 0, fadeOut = 0;
        bool fadeSet = false;
        string fontName = style.FontName;
        double fontSize = style.FontSize;
        var text = new StringBuilder();

        void Flush()
        {
            if (text.Length > 0)
            {
                builder.Append(text.ToString(), state.Bold, state.Italic, state.Underline, state.Colour, reveal);
                hasText |= text.ToString().Trim().Length > 0;
                text.Clear();
            }
        }

        int index = 0;
        while (index < raw.Length)
        {
            char current = raw[index];
            if (current == '{')
            {
                int close = raw.IndexOf('}', index + 1);
                if (close < 0)
                {
                    // libass shows an unterminated brace literally.
                    warnings.Add("Unterminated override blocks were kept as literal text.");
                    text.Append(raw[index..]);
                    break;
                }

                Flush();
                string block = raw[(index + 1)..close];
                index = close + 1;
                int tagIndex = block.IndexOf('\\', StringComparison.Ordinal);
                while (tagIndex >= 0 && tagIndex < block.Length)
                {
                    tagIndex++;
                    string? name = OverrideNames.FirstOrDefault(candidate =>
                        string.CompareOrdinal(block, tagIndex, candidate, 0, candidate.Length) == 0);
                    int argumentStart = tagIndex + (name?.Length ?? 0);
                    int argumentEnd;
                    if (argumentStart < block.Length && block[argumentStart] == '(')
                    {
                        int depth = 0;
                        argumentEnd = argumentStart;
                        while (argumentEnd < block.Length)
                        {
                            if (block[argumentEnd] == '(') depth++;
                            if (block[argumentEnd] == ')' && --depth == 0)
                            {
                                argumentEnd++;
                                break;
                            }

                            argumentEnd++;
                        }
                    }
                    else
                    {
                        argumentEnd = block.IndexOf('\\', argumentStart);
                        if (argumentEnd < 0) argumentEnd = block.Length;
                    }

                    string argument = block[argumentStart..argumentEnd].Trim();
                    tagIndex = block.IndexOf('\\', argumentEnd);
                    if (name is null)
                    {
                        if (argument.Length > 0)
                        {
                            warnings.Add($"Unknown override tag \\{argument} was ignored.");
                        }

                        continue;
                    }

                    switch (name)
                    {
                        case "b":
                            state.Bold = argument.Length == 0 ? currentStyle.Bold :
                                int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out int weight) &&
                                (weight == 1 || weight >= 600);
                            break;
                        case "i":
                            state.Italic = argument.Length == 0 ? currentStyle.Italic : argument == "1";
                            break;
                        case "u":
                            state.Underline = argument.Length == 0 ? currentStyle.Underline : argument == "1";
                            break;
                        case "c":
                        case "1c":
                            state.Colour = argument.Length == 0
                                ? (state.Colour & 0xFF000000) | (currentStyle.Primary & 0xFFFFFF)
                                : TryParseColour(argument, out uint colour)
                                    ? (state.Colour & 0xFF000000) | (colour & 0xFFFFFF)
                                    : throw new InvalidDataException($"ASS event at line {lineNumber} has an invalid colour \\{name}{argument}.");
                            break;
                        case "1a":
                        case "alpha":
                            if (name == "alpha")
                            {
                                warnings.Add("\\alpha sets every colour's alpha; only the text alpha was applied.");
                            }

                            state.Colour = argument.Length == 0
                                ? (currentStyle.Primary & 0xFF000000) | (state.Colour & 0xFFFFFF)
                                : TryParseAlpha(argument, out byte alpha)
                                    ? ((uint)alpha << 24) | (state.Colour & 0xFFFFFF)
                                    : throw new InvalidDataException($"ASS event at line {lineNumber} has an invalid alpha \\{name}{argument}.");
                            break;
                        case "2c":
                            if (TryParseColour(argument, out uint secondary))
                            {
                                if (hasText && ((upcoming & 0xFFFFFF) != (secondary & 0xFFFFFF)))
                                {
                                    warnings.Add("Mid-line karaoke colour changes are not supported; the first colour applies to the whole cue.");
                                }
                                else
                                {
                                    upcoming = (upcoming & 0xFF000000) | (secondary & 0xFFFFFF);
                                }
                            }

                            break;
                        case "k":
                        case "K":
                        case "kf":
                        case "ko":
                            if (!double.TryParse(argument, NumberStyles.Float, CultureInfo.InvariantCulture, out double centiseconds) ||
                                !double.IsFinite(centiseconds) || centiseconds < 0)
                            {
                                throw new InvalidDataException($"ASS event at line {lineNumber} has an invalid karaoke duration.");
                            }

                            if (name is "K" or "kf")
                            {
                                warnings.Add("Karaoke sweeps (\\kf, \\K) are shown as an instant change at the start of each syllable.");
                            }
                            else if (name == "ko")
                            {
                                warnings.Add("Karaoke outline effects (\\ko) are shown as an instant colour change.");
                            }

                            reveal = karaokeCursor;
                            karaokeCursor += centiseconds / 100;
                            break;
                        case "r":
                            currentStyle = argument.Length > 0 && styles.TryGetValue(argument, out AssStyle? reset) ? reset : style;
                            if (argument.Length > 0 && !styles.ContainsKey(argument))
                            {
                                warnings.Add($"Style '{argument}' in \\r is not defined, so the event style was used.");
                            }

                            state = new RunState(currentStyle);
                            break;
                        case "pos":
                            if (position is null)
                            {
                                double[] values = ParseArguments(argument);
                                if (values.Length != 2)
                                {
                                    throw new InvalidDataException($"ASS event at line {lineNumber} has an invalid \\pos.");
                                }

                                position = (values[0], values[1]);
                            }

                            break;
                        case "an":
                        case "a":
                            if (!alignmentSet && int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                            {
                                int? converted = name == "an" ? value is >= 1 and <= 9 ? value : null : LegacyAlignment(value);
                                if (converted is int resolved)
                                {
                                    alignment = resolved;
                                    alignmentSet = true;
                                }
                            }

                            break;
                        case "fad":
                            if (!fadeSet)
                            {
                                double[] fades = ParseArguments(argument);
                                if (fades.Length != 2 || fades.Any(fade => fade < 0))
                                {
                                    throw new InvalidDataException($"ASS event at line {lineNumber} has an invalid \\fad.");
                                }

                                (fadeIn, fadeOut, fadeSet) = (fades[0] / 1000, fades[1] / 1000, true);
                            }

                            break;
                        case "fn" when !hasText:
                            fontName = argument.Length > 0 ? argument : currentStyle.FontName;
                            break;
                        case "fs" when !hasText:
                            fontSize = argument.Length > 0 &&
                                double.TryParse(argument, NumberStyles.Float, CultureInfo.InvariantCulture, out double size) &&
                                size > 0 ? size : currentStyle.FontSize;
                            break;
                        case "fn":
                        case "fs":
                            warnings.Add("Font or size changes within a line are not supported; the first font and size apply to the whole cue.");
                            break;
                        case "p":
                            if (argument != "0")
                            {
                                warnings.Add("Vector drawings (\\p) are not supported; events containing them were skipped.");
                                return null;
                            }

                            break;
                        case "s":
                            if (argument == "1")
                            {
                                warnings.Add("Strikeout is not supported and was ignored.");
                            }

                            break;
                        default:
                            warnings.Add($"Override tag \\{name} is not supported and was ignored.");
                            break;
                    }
                }

                continue;
            }

            if (current == '\\' && index + 1 < raw.Length && raw[index + 1] is 'N' or 'n' or 'h')
            {
                char escape = raw[index + 1];
                text.Append(escape == 'N' || (escape == 'n' && smartWrapOff) ? "\n" : escape == 'h' ? "\u00A0" : " ");
                index += 2;
                continue;
            }

            text.Append(current);
            index++;
        }

        Flush();
        if (karaokeCursor > end - start + 0.005)
        {
            warnings.Add("Karaoke timings longer than their event were clamped to the event end.");
        }

        (string cueText, List<SubtitleRun> runs, uint cueColour) = builder.Finish(style.Primary);
        // Leading line breaks or spaces are layout only in ASS.
        int leading = cueText.Length - cueText.TrimStart().Length;
        if (leading > 0)
        {
            cueText = cueText[leading..];
            while (leading > 0 && runs.Count > 0)
            {
                int remove = Math.Min(leading, runs[0].Text.Length);
                runs[0].Text = runs[0].Text[remove..];
                leading -= remove;
                if (runs[0].Text.Length == 0)
                {
                    runs.RemoveAt(0);
                }
            }
        }

        if (string.IsNullOrWhiteSpace(cueText))
        {
            return null;
        }

        if (alignment % 3 != 2)
        {
            warnings.Add("Left- and right-aligned events are shown centred.");
        }

        double scale = TimelineCompositor.TitleReferenceHeight / playResY;
        double clipFontSize = fontSize * scale;
        if (clipFontSize is < VideoTimelineEditing.MinimumFontSize or > VideoTimelineEditing.MaximumFontSize)
        {
            warnings.Add("Font sizes outside the supported range were clamped.");
            clipFontSize = Math.Clamp(clipFontSize, VideoTimelineEditing.MinimumFontSize, VideoTimelineEditing.MaximumFontSize);
        }

        int lineCount = cueText.Count(character => character == '\n') + 1;
        double halfHeight = fontSize * LineHeightFactor * lineCount / 2;
        double x;
        double y;
        if (position is (double posX, double posY))
        {
            x = posX / playResX;
            y = (alignment <= 3 ? posY - halfHeight : alignment >= 7 ? posY + halfHeight : posY) / playResY;
        }
        else
        {
            x = (style.MarginL + (playResX - style.MarginR)) / 2 / playResX;
            y = (alignment <= 3 ? playResY - marginV - halfHeight : alignment >= 7 ? marginV + halfHeight : playResY / 2) / playResY;
        }

        if (x is < 0 or > 1 || y is < 0 or > 1)
        {
            warnings.Add("Positions outside the frame were clamped to its edges.");
        }

        uint background = style.BorderStyle switch
        {
            3 => style.Outline,
            4 => style.Back,
            _ => 0x00000000,
        };
        if (style.BorderStyle is not 3 and not 4)
        {
            warnings.Add("ASS outlines are not supported; text is drawn with InkMotion's drop shadow and no box.");
        }

        return new SubtitleCueText(start, end, cueText)
        {
            Runs = runs.Count > 0 ? runs : null,
            Style = new SubtitleCueStyle
            {
                FontFamily = fontName.Length is > 0 and <= 128 ? fontName : "Segoe UI",
                FontSize = clipFontSize,
                Colour = cueColour,
                TextBackgroundColour = background,
                UpcomingColour = upcoming,
                X = Math.Clamp(x, 0, 1),
                Y = Math.Clamp(y, 0, 1),
                FadeInSeconds = fadeIn,
                FadeOutSeconds = fadeOut,
            },
        };
    }

    private static AssStyle ParseStyle(string[] fields, string[] format, int lineNumber, SubtitleWarnings warnings)
    {
        string Field(string name)
        {
            int index = Array.FindIndex(format, item => item.Equals(name, StringComparison.OrdinalIgnoreCase));
            return index < 0 ? string.Empty : fields[index].Trim();
        }

        bool Flag(string name) => Field(name) is "-1" or "1";

        uint Colour(string name, uint fallback)
        {
            string value = Field(name);
            if (value.Length == 0)
            {
                return fallback;
            }

            return TryParseColour(value, out uint colour)
                ? colour
                : throw new InvalidDataException($"ASS style at line {lineNumber} has an invalid {name}.");
        }

        int Integer(string name, int fallback) =>
            int.TryParse(Field(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;

        double Number(string name, double fallback) =>
            double.TryParse(Field(name), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) &&
            double.IsFinite(value) ? value : fallback;

        var style = new AssStyle
        {
            Name = Field("Name").TrimStart('*') is { Length: > 0 } name ? name : "Default",
            FontName = Field("Fontname") is { Length: > 0 } font ? font : "Arial",
            FontSize = Number("Fontsize", 20),
            Primary = Colour("PrimaryColour", 0xFFFFFFFF),
            Secondary = Colour("SecondaryColour", 0xFFFF0000),
            Outline = Colour("OutlineColour", 0xFF000000),
            Back = Colour("BackColour", 0xFF000000),
            Bold = Flag("Bold"),
            Italic = Flag("Italic"),
            Underline = Flag("Underline"),
            BorderStyle = Integer("BorderStyle", 1),
            Alignment = Integer("Alignment", 2),
            MarginL = Integer("MarginL", 0),
            MarginR = Integer("MarginR", 0),
            MarginV = Integer("MarginV", 0),
        };
        if (style.FontSize <= 0 || style.Alignment is < 1 or > 9)
        {
            throw new InvalidDataException($"ASS style at line {lineNumber} has an invalid font size or alignment.");
        }

        if (Flag("StrikeOut"))
        {
            warnings.Add("Strikeout is not supported and was ignored.");
        }

        if (Number("ScaleX", 100) != 100 || Number("ScaleY", 100) != 100 || Number("Spacing", 0) != 0 ||
            Number("Angle", 0) != 0)
        {
            warnings.Add("Style scaling, spacing and angle are not supported and were ignored.");
        }

        return style;
    }

    private static string[] SplitFields(string value, int count)
    {
        string[] parts = value.Split(',', count);
        if (parts.Length != count)
        {
            throw new InvalidDataException("An ASS line has fewer fields than its Format line.");
        }

        return parts;
    }

    private static (double X, double Y) GetPlayResolution(Dictionary<string, string> info)
    {
        bool hasX = info.TryGetValue("PlayResX", out string? xText) &&
            double.TryParse(xText, NumberStyles.Float, CultureInfo.InvariantCulture, out double x) && x > 0;
        bool hasY = info.TryGetValue("PlayResY", out string? yText) &&
            double.TryParse(yText, NumberStyles.Float, CultureInfo.InvariantCulture, out double y) && y > 0;
        double resX = hasX ? double.Parse(xText!, CultureInfo.InvariantCulture) : 0;
        double resY = hasY ? double.Parse(yText!, CultureInfo.InvariantCulture) : 0;
        // Follow libass defaults for missing play resolutions.
        return (hasX, hasY) switch
        {
            (true, true) => (resX, resY),
            (true, false) => (resX, resX == 1280 ? 1024 : resX * 3 / 4),
            (false, true) => (resY == 1024 ? 1280 : resY * 4 / 3, resY),
            _ => (384, 288),
        };
    }

    private static int? LegacyAlignment(int value) => value switch
    {
        1 or 2 or 3 => value,
        5 or 6 or 7 => value + 2,
        9 or 10 or 11 => value - 5,
        _ => null,
    };

    private static double[] ParseArguments(string argument)
    {
        string inner = argument.Trim();
        if (!inner.StartsWith('(') || !inner.EndsWith(')'))
        {
            return [];
        }

        string[] parts = inner[1..^1].Split(',');
        var values = new double[parts.Length];
        for (int index = 0; index < parts.Length; index++)
        {
            if (!double.TryParse(parts[index].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[index]) ||
                !double.IsFinite(values[index]))
            {
                return [];
            }
        }

        return values;
    }

    /// <summary>Parses &amp;HAABBGGRR&amp;, &amp;HBBGGRR&amp; or decimal ASS colours, where alpha 00 is opaque.</summary>
    internal static bool TryParseColour(string value, out uint colour)
    {
        colour = 0;
        string text = value.Trim().TrimEnd('&');
        uint raw;
        if (text.StartsWith("&H", StringComparison.OrdinalIgnoreCase))
        {
            if (!uint.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out raw))
            {
                return false;
            }
        }
        else if (!uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out raw) &&
                 !(int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int signed) &&
                   (raw = unchecked((uint)signed)) == raw))
        {
            return false;
        }

        uint alpha = 255 - (raw >> 24);
        uint blue = (raw >> 16) & 0xFF;
        uint green = (raw >> 8) & 0xFF;
        uint red = raw & 0xFF;
        colour = (alpha << 24) | (red << 16) | (green << 8) | blue;
        return true;
    }

    private static bool TryParseAlpha(string value, out byte alpha)
    {
        alpha = 0;
        string text = value.Trim().Trim('&');
        if (!text.StartsWith('H') && !text.StartsWith('h') ||
            !uint.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint raw) ||
            raw > 0xFF)
        {
            return false;
        }

        alpha = (byte)(255 - raw);
        return true;
    }

    internal static string FormatColour(uint colour, bool withAlpha)
    {
        uint red = (colour >> 16) & 0xFF;
        uint green = (colour >> 8) & 0xFF;
        uint blue = colour & 0xFF;
        uint bgr = (blue << 16) | (green << 8) | red;
        return withAlpha
            ? $"&H{255 - (colour >> 24):X2}{bgr:X6}"
            : $"&H{bgr:X6}&";
    }

    private static string Escape(string text) =>
        SubtitleSrt.NormaliseLineEndings(text).Replace("\n", "\\N", StringComparison.Ordinal)
            .Replace("\u00A0", "\\h", StringComparison.Ordinal);

    private static string FormatNumber(double value) =>
        Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);

    private static string Shorten(string text) =>
        text.Length <= 40 ? text.Replace('\n', ' ') : text[..40].Replace('\n', ' ') + "…";

    internal static bool TryParseTime(string value, out double seconds)
    {
        seconds = 0;
        Match match = AssTime.Match(value);
        if (!match.Success)
        {
            return false;
        }

        seconds = long.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture) * 3600d +
            int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) * 60d +
            int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture) +
            int.Parse(match.Groups["cs"].Value, CultureInfo.InvariantCulture) / 100d;
        return double.IsFinite(seconds);
    }

    internal static string FormatTime(double seconds)
    {
        double total = Math.Round(seconds * 100, MidpointRounding.AwayFromZero);
        if (!double.IsFinite(total) || total < 0 || total > long.MaxValue)
        {
            throw new InvalidDataException("A subtitle clip is outside the supported timeline range.");
        }

        long centiseconds = (long)total;
        return string.Create(CultureInfo.InvariantCulture,
            $"{centiseconds / 360_000}:{centiseconds / 6_000 % 60:00}:{centiseconds / 100 % 60:00}.{centiseconds % 100:00}");
    }
}
