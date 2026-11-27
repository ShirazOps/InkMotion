using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace InkMotion;

public sealed record PictureMediaInfo(double FrameRate, int Width, int Height);
public sealed record PictureCut(string Path, int Start, int In, int Length, int SourceDuration);
public sealed record PictureTrack(string Name, List<PictureCut> Cuts);
public sealed record PictureTimeline(int FrameRate, int Width, int Height, List<PictureTrack> Tracks);

public static class PictureInterchange
{
    private static readonly int[] Rates = [24, 30, 60];

    public static PictureTimeline FromProject(EditorProject project, IReadOnlyDictionary<string, PictureMediaInfo> media)
    {
        EditorProjectValidation.Validate(project);
        int rate = Rate(project.FrameRate);
        if (project.AudioTracks.Count > 0 || project.AudioPath is not null || project.Colour is not null ||
            project.VideoMulticamGroups.Count > 0 || project.Markers.Count > 0 || project.CompositeGraph is not null ||
            project.CharacterRig is not null || project.AudioBuses.Count > 0 ||
            project.Frames.Any(frame => frame.Strokes.Count > 0 || frame.LayerStrokes.Values.Any(s => s.Count > 0) ||
                frame.TextOverlays.Count > 0))
            throw new InvalidDataException("Picture interchange cannot carry audio tracks, artwork, titles, markers, colour management, buses or multicamera edits. Use a package or a simplified project copy.");
        var tracks = new List<PictureTrack>();
        int width = project.CanvasSize?.Width ?? 0, height = project.CanvasSize?.Height ?? 0;
        foreach (var track in project.VideoTracks.Where(t => t.Clips.Count > 0))
        {
            if (track.Kind != TimelineTrackKind.Video || !track.IsVisible || track.AudioBusId is not null)
                throw new InvalidDataException($"Track '{track.Name}' is not a visible, unrouted video track. Compounds, images, titles and subtitles are not supported.");
            var cuts = new List<PictureCut>();
            foreach (var clip in track.Clips.OrderBy(c => c.StartFrame))
            {
                RequireDefaults(clip, new TimelineClipProject(),
                    ["Id", "StartFrame", "SourcePath", "SourceInSeconds", "SourceOutSeconds", "SourceDurationSeconds",
                     "Volume", "ProxyPath", "UseProxy", "ProxySourceLength", "ProxySourceLastWriteUtcTicks"],
                    $"Clip '{Path.GetFileName(clip.SourcePath)}'");
                string path = Path.GetFullPath(clip.SourcePath!);
                if (!media.TryGetValue(path, out var info) || Math.Abs(info.FrameRate - rate) > 0.001)
                    throw new InvalidDataException($"'{Path.GetFileName(path)}' must have the same integral frame rate ({rate}) as the timeline. Mixed/fractional rates are not supported.");
                if (width == 0) { width = info.Width; height = info.Height; }
                if (info.Width != width || info.Height != height)
                    throw new InvalidDataException("Picture interchange requires matching source and canvas dimensions; scaling is not exported.");
                int sourceIn = Frames(clip.SourceInSeconds, rate);
                int sourceOut = Frames(clip.SourceOutSeconds ?? clip.SourceDurationSeconds, rate);
                int duration = Frames(clip.SourceDurationSeconds, rate);
                cuts.Add(new(path, clip.StartFrame - 1, sourceIn, sourceOut - sourceIn, duration));
            }
            tracks.Add(new(track.Name, cuts));
        }
        var result = new PictureTimeline(rate, width, height, tracks);
        Validate(result);
        return result;
    }

    internal static void RequireDefaults<T>(T actual, T baseline, string[] allowed, string label)
    {
        var left = (JsonObject)JsonSerializer.SerializeToNode(actual)!;
        var right = (JsonObject)JsonSerializer.SerializeToNode(baseline)!;
        foreach (var key in allowed) { left.Remove(key); right.Remove(key); }
        string[] changed = left.Where(p => !JsonNode.DeepEquals(p.Value, right[p.Key])).Select(p => p.Key).ToArray();
        if (changed.Length > 0)
            throw new InvalidDataException($"{label} has unsupported settings: {string.Join(", ", changed)}. Use a package or render/simplify the edit first.");
    }

    public static EditorProject ToProject(PictureTimeline timeline)
    {
        Validate(timeline);
        var project = new EditorProject { FrameRate = timeline.FrameRate };
        if (timeline.Width > 0) project.CanvasSize = new(timeline.Width, timeline.Height);
        foreach (var track in timeline.Tracks)
            project.VideoTracks.Add(new()
            {
                Name = track.Name,
                Clips = track.Cuts.Select(c => new TimelineClipProject
                {
                    SourcePath = c.Path, StartFrame = checked(c.Start + 1),
                    SourceInSeconds = (double)c.In / timeline.FrameRate,
                    SourceOutSeconds = (double)(c.In + c.Length) / timeline.FrameRate,
                    SourceDurationSeconds = (double)c.SourceDuration / timeline.FrameRate,
                    Volume = 0,
                }).ToList(),
            });
        EditorProjectValidation.Validate(project);
        return project;
    }

    public static string WriteXml(PictureTimeline timeline)
    {
        Validate(timeline);
        if (timeline.Width == 0) throw new InvalidDataException("XML export needs known canvas dimensions.");
        int id = 0;
        XElement rate() => new("rate", new XElement("timebase", timeline.FrameRate), new XElement("ntsc", "FALSE"));
        XElement characteristics() => new("samplecharacteristics", rate(),
            new XElement("width", timeline.Width), new XElement("height", timeline.Height),
            new XElement("anamorphic", "FALSE"), new XElement("pixelaspectratio", "square"),
            new XElement("fielddominance", "none"));
        var video = new XElement("video", new XElement("format", characteristics()));
        foreach (var track in timeline.Tracks)
        {
            var element = new XElement("track");
            foreach (var cut in track.Cuts)
            {
                string name = Path.GetFileName(cut.Path);
                int index = ++id;
                element.Add(new XElement("clipitem", new XAttribute("id", $"clip-{index}"),
                    new XElement("name", name), new XElement("enabled", "TRUE"), rate(),
                    new XElement("duration", cut.SourceDuration), new XElement("start", cut.Start),
                    new XElement("end", cut.Start + cut.Length), new XElement("in", cut.In),
                    new XElement("out", cut.In + cut.Length),
                    new XElement("file", new XAttribute("id", $"file-{index}"),
                        new XElement("name", name), new XElement("pathurl", new Uri(cut.Path).AbsoluteUri),
                        rate(), new XElement("duration", cut.SourceDuration),
                        new XElement("media", new XElement("video", characteristics())))));
            }
            element.Add(new XElement("enabled", "TRUE"), new XElement("locked", "FALSE"));
            video.Add(element);
        }
        int end = timeline.Tracks.SelectMany(t => t.Cuts).Max(c => c.Start + c.Length);
        return new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement("xmeml", new XAttribute("version", "5"),
                new XElement("sequence", new XAttribute("id", "inkmotion-sequence"),
                    new XElement("name", "InkMotion picture edit"), new XElement("duration", end),
                    rate(), new XElement("timecode", rate(), new XElement("frame", 0),
                        new XElement("displayformat", "NDF")),
                    new XElement("media", video)))).ToString();
    }

    public static PictureTimeline ReadXml(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 * 1024,
        });
        XDocument document = XDocument.Load(reader);
        XElement root = document.Root ?? throw new InvalidDataException("Empty XML.");
        if (root.Name != "xmeml" || (string?)root.Attribute("version") is not ("4" or "5"))
            throw new InvalidDataException("Only a single FCP 7 xmeml version 4/5 sequence is supported, not FCPXML.");
        XElement container = root;
        if (root.Element("project") is { } project)
        {
            RejectUnknown(root, ["project"]);
            RejectUnknown(project, ["name", "children"]);
            container = project.Element("children") ?? throw new InvalidDataException("Missing project children.");
        }
        RejectUnknown(container, ["sequence"]);
        if (container.Elements("sequence").Count() != 1)
            throw new InvalidDataException("Choose exactly one XML sequence.");
        XElement sequence = container.Element("sequence")!;
        RejectUnknown(sequence, ["name", "duration", "rate", "timecode", "media", "uuid"]);
        int fps = ReadRate(sequence.Element("rate"));
        if (sequence.Element("timecode") is { } timecode &&
            (Number(timecode, "frame", 0) != 0 || (string?)timecode.Element("displayformat") is not (null or "NDF")))
            throw new InvalidDataException("Only zero-origin, non-drop-frame sequences are supported.");
        XElement media = sequence.Element("media") ?? throw new InvalidDataException("Missing sequence media.");
        RejectUnknown(media, ["video", "audio"]);
        if (media.Element("audio") is { } audio && audio.Descendants("track").Any())
            throw new InvalidDataException("XML audio tracks are not supported by picture interchange.");
        XElement video = media.Element("video") ?? throw new InvalidDataException("Missing picture tracks.");
        RejectUnknown(video, ["format", "track"]);
        XElement format = video.Element("format")?.Element("samplecharacteristics")
            ?? throw new InvalidDataException("Missing sequence picture dimensions.");
        int width = Number(format, "width"), height = Number(format, "height");
        CheckGeometry(format, width, height);
        var files = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var file in sequence.Descendants("file").Where(e => e.Element("pathurl") is not null))
        {
            string key = (string?)file.Attribute("id") ?? throw new InvalidDataException("Missing file ID.");
            if (!files.TryAdd(key, file) && !XNode.DeepEquals(files[key], file))
                throw new InvalidDataException("Conflicting XML file definitions.");
        }
        var tracks = new List<PictureTrack>();
        foreach (var track in video.Elements("track"))
        {
            RejectUnknown(track, ["clipitem", "enabled", "locked"]);
            if ((string?)track.Element("enabled") is string enabled && enabled != "TRUE")
                throw new InvalidDataException("Disabled tracks are not supported.");
            var cuts = new List<PictureCut>();
            foreach (var clip in track.Elements("clipitem"))
            {
                RejectUnknown(clip, ["name", "enabled", "duration", "rate", "start", "end", "in", "out", "file",
                    "masterclipid", "alphatype", "pixelaspectratio", "anamorphic", "sourcetrack", "labels", "logginginfo"]);
                if ((string?)clip.Element("enabled") is string clipEnabled && clipEnabled != "TRUE")
                    throw new InvalidDataException("Disabled clips are not supported.");
                if ((string?)clip.Attribute("frameBlend") is not (null or "FALSE"))
                    throw new InvalidDataException("Frame blending is not supported.");
                if (ReadRate(clip.Element("rate")) != fps)
                    throw new InvalidDataException("Mixed source frame rates are not supported.");
                XElement file = clip.Element("file") ?? throw new InvalidDataException("Missing source file.");
                if (file.Element("pathurl") is null &&
                    !files.TryGetValue((string?)file.Attribute("id") ?? "", out file!))
                    throw new InvalidDataException("Unresolved source file ID.");
                if (ReadRate(file.Element("rate")) != fps)
                    throw new InvalidDataException("Mixed source frame rates are not supported.");
                if (file.Element("timecode") is { } sourceTimecode && Number(sourceTimecode, "frame", 0) != 0)
                    throw new InvalidDataException("Non-zero source timecode origins are not supported.");
                XElement geometry = file.Element("media")?.Element("video")?.Element("samplecharacteristics")
                    ?? throw new InvalidDataException("Missing source picture dimensions.");
                CheckGeometry(geometry, width, height);
                string path = (string?)file.Element("pathurl") ?? "";
                if (!Uri.TryCreate(path, UriKind.Absolute, out var uri) || !uri.IsFile || uri.IsUnc ||
                    !Path.IsPathFullyQualified(uri.LocalPath))
                    throw new InvalidDataException("XML media must use an absolute local file URL for this Windows machine. Relink foreign paths before importing.");
                int start = Number(clip, "start"), end = Number(clip, "end");
                int sourceIn = Number(clip, "in"), sourceOut = Number(clip, "out");
                if ((long)end - start != (long)sourceOut - sourceIn)
                    throw new InvalidDataException("Retimed clips are not supported.");
                cuts.Add(new(uri.LocalPath, start, sourceIn, end - start, Number(file, "duration")));
            }
            if (cuts.Count > 0) tracks.Add(new($"Video {tracks.Count + 1}", cuts));
        }
        var result = new PictureTimeline(fps, width, height, tracks);
        Validate(result);
        if (Number(sequence, "duration") != result.Tracks.SelectMany(t => t.Cuts).Max(c => c.Start + c.Length))
            throw new InvalidDataException("Sequence duration does not match the picture edit; trailing empty duration is not supported.");
        return result;
    }

    public static string WriteEdl(PictureTimeline timeline)
    {
        Validate(timeline);
        if (timeline.Tracks.Count != 1 || timeline.FrameRate > 30 || timeline.Tracks[0].Cuts.Count > 999)
            throw new InvalidDataException("CMX 3600 supports one picture track, up to 999 cuts, at 24 or 30 fps NDF.");
        var output = new StringBuilder("TITLE: InkMotion picture edit\nFCM: NON-DROP FRAME\n\n");
        int index = 0;
        foreach (var cut in timeline.Tracks[0].Cuts)
        {
            if (cut.Path.Any(c => c is '\r' or '\n') || cut.Path.Any(c => c > 127))
                throw new InvalidDataException("CMX 3600 media paths must be single-line ASCII. Use XML for Unicode paths.");
            string tc(int frame) => Timecode(frame, timeline.FrameRate);
            output.AppendLine($"{++index:D3}  AX       V     C        {tc(cut.In)} {tc(cut.In + cut.Length)} {tc(cut.Start)} {tc(cut.Start + cut.Length)}");
            output.AppendLine($"* FROM CLIP NAME: {Path.GetFileName(cut.Path)}");
            output.AppendLine($"* FROM FILE: {cut.Path}");
            output.AppendLine();
        }
        return output.ToString();
    }

    public static PictureTimeline ReadEdl(string text, int frameRate, string baseDirectory)
    {
        Rate(frameRate);
        if (frameRate > 30) throw new InvalidDataException("EDL requires 24 or 30 fps.");
        var cuts = new List<PictureCut>();
        string? path = null;
        int[]? pending = null;
        bool fcm = false;
        void finish()
        {
            if (pending is null) return;
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidDataException("Every EDL event needs a FROM FILE or FROM CLIP NAME comment; reel-only media cannot be resolved.");
            if (pending[1] - pending[0] != pending[3] - pending[2])
                throw new InvalidDataException("EDL source and record durations differ.");
            cuts.Add(new(Path.GetFullPath(path, baseDirectory), pending[2], pending[0], pending[1] - pending[0], pending[1]));
            pending = null;
            path = null;
        }
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line.StartsWith("TITLE:", StringComparison.Ordinal)) continue;
            if (line.StartsWith("FCM:", StringComparison.Ordinal))
            {
                if (line != "FCM: NON-DROP FRAME" || fcm) throw new InvalidDataException("Only one NON-DROP FRAME header is supported.");
                fcm = true;
                continue;
            }
            if (line.StartsWith("* FROM FILE:", StringComparison.Ordinal) || line.StartsWith("* FROM CLIP:", StringComparison.Ordinal))
            {
                if (pending is null) throw new InvalidDataException("Media comment without an EDL event.");
                path = line[(line.IndexOf(':') + 1)..].Trim();
                continue;
            }
            if (line.StartsWith("* FROM CLIP NAME:", StringComparison.Ordinal))
            {
                if (pending is null) throw new InvalidDataException("Clip comment without an EDL event.");
                path ??= line["* FROM CLIP NAME:".Length..].Trim();
                continue;
            }
            if (line.StartsWith("* OTIO TRUNCATED REEL NAME FROM:", StringComparison.Ordinal)) continue;
            if (line.StartsWith('*'))
                throw new InvalidDataException($"Unsupported EDL comment (possibly an effect): {line}");
            finish();
            Match match = Regex.Match(line, @"^\d{3}\s+.{1,8}?\s+V\s+C\s+(\d{2}:\d{2}:\d{2}:\d{2})\s+(\d{2}:\d{2}:\d{2}:\d{2})\s+(\d{2}:\d{2}:\d{2}:\d{2})\s+(\d{2}:\d{2}:\d{2}:\d{2})$");
            if (!match.Success) throw new InvalidDataException($"Unsupported EDL event; only V cuts are supported: {line}");
            pending = Enumerable.Range(1, 4).Select(i => ParseTimecode(match.Groups[i].Value, frameRate)).ToArray();
        }
        finish();
        var result = new PictureTimeline(frameRate, 0, 0, [new("Video 1", cuts)]);
        Validate(result);
        return result;
    }

    public static void Validate(PictureTimeline timeline)
    {
        Rate(timeline.FrameRate);
        if (timeline.Width != 0 || timeline.Height != 0)
            new ProjectCanvasSize(timeline.Width, timeline.Height).Validate();
        if (timeline.Tracks.Count == 0 || timeline.Tracks.All(t => t.Cuts.Count == 0))
            throw new InvalidDataException("The interchange contains no picture cuts.");
        foreach (var track in timeline.Tracks)
        {
            long end = 0;
            foreach (var cut in track.Cuts)
            {
                if (!Path.IsPathFullyQualified(cut.Path) || cut.Start < end || cut.In < 0 || cut.Length <= 0 ||
                    (long)cut.In + cut.Length > cut.SourceDuration ||
                    (long)cut.Start + cut.Length > timeline.FrameRate * 86400L)
                    throw new InvalidDataException("Invalid, overlapping or out-of-range picture cuts (maximum 24 hours).");
                end = (long)cut.Start + cut.Length;
            }
        }
    }

    internal static int Rate(double rate)
    {
        if (!Rates.Contains((int)rate) || rate != (int)rate)
            throw new InvalidDataException("Interchange supports integral 24, 30 or 60 fps, not fractional/drop-frame or other project rates.");
        return (int)rate;
    }

    internal static int Frames(double seconds, int rate)
    {
        double frames = seconds * rate;
        if (!double.IsFinite(frames) || frames < 0 || frames > int.MaxValue || Math.Abs(frames - Math.Round(frames)) > 0.00001)
            throw new InvalidDataException("Source trims/durations must align exactly to timeline frames; subframe rounding is not performed.");
        return checked((int)Math.Round(frames));
    }

    private static void RejectUnknown(XElement parent, string[] allowed)
    {
        var unsupported = parent.Elements().Where(e => !allowed.Contains(e.Name.ToString())).Select(e => e.Name).ToArray();
        if (unsupported.Length > 0) throw new InvalidDataException($"Unsupported XML {parent.Name} content: {string.Join(", ", unsupported.Select(n => n.ToString()))}.");
        if (parent.Elements().GroupBy(e => e.Name).Any(g => g.Count() > 1 && g.Key != "track" && g.Key != "clipitem" &&
            (g.Key != "rate" || g.Any(e => !XNode.DeepEquals(e, g.First())))))
            throw new InvalidDataException($"Duplicate XML fields in {parent.Name}.");
    }

    private static void CheckGeometry(XElement element, int width, int height)
    {
        if (Number(element, "width") != width || Number(element, "height") != height ||
            (string?)element.Element("anamorphic") is not (null or "FALSE") ||
            (string?)element.Element("pixelaspectratio") is not (null or "square") ||
            (string?)element.Element("fielddominance") is not (null or "none"))
            throw new InvalidDataException("Only matching progressive square-pixel picture dimensions are supported.");
    }

    private static int ReadRate(XElement? element)
    {
        if (element is null || (string?)element.Element("ntsc") is not (null or "FALSE"))
            throw new InvalidDataException("Missing or fractional/NTSC frame rate.");
        return Rate(Number(element, "timebase"));
    }

    private static int Number(XElement element, string name, int? fallback = null) =>
        element.Element(name) is null && fallback.HasValue ? fallback.Value :
        int.TryParse((string?)element.Element(name), NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            ? value : throw new InvalidDataException($"Invalid XML {name}.");

    private static string Timecode(int frame, int rate)
    {
        if (frame >= rate * 86400L) throw new InvalidDataException("EDL timecodes must be shorter than 24 hours.");
        return $"{frame / (rate * 3600):D2}:{frame / (rate * 60) % 60:D2}:{frame / rate % 60:D2}:{frame % rate:D2}";
    }

    private static int ParseTimecode(string value, int rate)
    {
        int[] p = value.Split(':').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        if (p[0] >= 24 || p[1] >= 60 || p[2] >= 60 || p[3] >= rate)
            throw new InvalidDataException($"Invalid EDL timecode: {value}");
        return ((p[0] * 60 + p[1]) * 60 + p[2]) * rate + p[3];
    }
}
