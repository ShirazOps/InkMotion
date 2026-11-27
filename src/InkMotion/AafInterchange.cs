using System.Text.Json;
using NAudio.Wave;

namespace InkMotion;

public sealed record AafAudioCut(string Path, int Start, int In, int Length);
public sealed record AafAudioTimeline(int FrameRate, List<AafAudioCut> Clips);

public static class AafInterchange
{
    public static AafAudioTimeline FromProject(EditorProject project)
    {
        EditorProjectValidation.Validate(project);
        if (project.VideoTracks.Any(t => t.Clips.Count > 0) || project.Frames.Any(f =>
            f.Strokes.Count > 0 || f.LayerStrokes.Values.Any(s => s.Count > 0) || f.TextOverlays.Count > 0))
            throw new InvalidDataException("AAF interchange is audio-only. Remove picture/artwork from a project copy, or use a package.");
        PictureInterchange.RequireDefaults(project, new EditorProject(),
            ["Version", "FrameRate", "AudioTracks", "SelectedFrameIndex", "AudioPath", "AudioVolume", "AudioMuted",
             "VideoTracks", "Frames", "InkLayers", "ActiveInkLayerId", "CanvasSize"],
            "AAF audio export");
        int rate = PictureInterchange.Rate(project.FrameRate);
        var cuts = new List<AafAudioCut>();
        foreach (var track in project.AudioTracks.Where(t => !string.IsNullOrWhiteSpace(t.AudioPath)))
        {
            PictureInterchange.RequireDefaults(track, new AudioTrackProject(),
                ["AudioPath", "StartFrame", "SourceInSeconds", "SourceOutSeconds",
                 "Title", "Artist", "Album", "Genre", "Comments", "Year", "TagsInitialized"],
                $"Audio clip '{Path.GetFileName(track.AudioPath)}'");
            string path = Path.GetFullPath(track.AudioPath ?? throw new InvalidDataException("AAF audio needs linked WAV media."));
            using var wave = new WaveFileReader(path);
            if (!path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || wave.WaveFormat.Channels != 1 ||
                wave.WaveFormat.Encoding != WaveFormatEncoding.Pcm)
                throw new InvalidDataException("AAF supports linked mono PCM WAV files only. Render other audio to mono PCM WAV first.");
            int sourceIn = PictureInterchange.Frames(track.SourceInSeconds, rate);
            int sourceOut = PictureInterchange.Frames(track.SourceOutSeconds ?? wave.TotalTime.TotalSeconds, rate);
            if ((double)sourceOut / rate > wave.TotalTime.TotalSeconds + 0.000001)
                throw new InvalidDataException("An AAF source trim exceeds its WAV file.");
            cuts.Add(new(path, track.StartFrame - 1, sourceIn, sourceOut - sourceIn));
        }
        var result = new AafAudioTimeline(rate, cuts);
        Validate(result);
        return result;
    }

    public static EditorProject ToProject(AafAudioTimeline timeline)
    {
        Validate(timeline);
        var project = new EditorProject { FrameRate = timeline.FrameRate };
        project.AudioTracks = timeline.Clips.Select(c => new AudioTrackProject
        {
            AudioPath = c.Path, StartFrame = c.Start + 1,
            SourceInSeconds = (double)c.In / timeline.FrameRate,
            SourceOutSeconds = (double)(c.In + c.Length) / timeline.FrameRate,
        }).ToList();
        EditorProjectValidation.Validate(project);
        return project;
    }

    public static void Validate(AafAudioTimeline timeline)
    {
        PictureInterchange.Rate(timeline.FrameRate);
        if (timeline.Clips is null || timeline.Clips.Count is < 1 or > 10000)
            throw new InvalidDataException("AAF needs between 1 and 10,000 audio clips.");
        foreach (var clip in timeline.Clips)
            if (clip is null || string.IsNullOrWhiteSpace(clip.Path) || !Path.IsPathFullyQualified(clip.Path) ||
                !clip.Path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || clip.Start < 0 ||
                clip.In < 0 || clip.Length <= 0 ||
                (long)clip.Start + clip.Length > timeline.FrameRate * 86400L ||
                (long)clip.In + clip.Length > int.MaxValue)
                throw new InvalidDataException("Invalid AAF audio path or frame range (maximum 24 hours).");
    }

    public static void Export(AafAudioTimeline timeline, string outputPath, string python, CancellationToken token)
    {
        Validate(timeline);
        outputPath = Path.GetFullPath(outputPath);
        if (!outputPath.EndsWith(".aaf", StringComparison.OrdinalIgnoreCase) ||
            timeline.Clips.Any(c => c.Path.Equals(outputPath, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Choose an .aaf output that does not overwrite linked media.");
        string temporary = outputPath + $".{Guid.NewGuid():N}.tmp";
        string directory = NewWorkDirectory();
        try
        {
            string input = Path.Combine(directory, "input.json");
            File.WriteAllText(input, JsonSerializer.Serialize(timeline));
            Run(python, "export", input, temporary, token);
            using (var stream = File.OpenRead(temporary))
            {
                Span<byte> header = stackalloc byte[8];
                stream.ReadExactly(header);
                if (!header.SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }))
                    throw new InvalidDataException("The AAF bridge did not produce a binary structured-storage file.");
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, outputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            Directory.Delete(directory, recursive: true);
        }
    }

    public static EditorProject Import(string inputPath, string python, CancellationToken token)
    {
        if (new FileInfo(inputPath).Length > 256 * 1024 * 1024)
            throw new InvalidDataException("Linked-media AAF import is limited to 256 MiB; embedded essence is not supported.");
        string directory = NewWorkDirectory();
        try
        {
            string output = Path.Combine(directory, "output.json");
            Run(python, "import", Path.GetFullPath(inputPath), output, token);
            if (new FileInfo(output).Length > 64 * 1024 * 1024)
                throw new InvalidDataException("AAF bridge output exceeds 64 MiB.");
            var timeline = JsonSerializer.Deserialize<AafAudioTimeline>(File.ReadAllText(output))
                ?? throw new InvalidDataException("The AAF bridge returned an empty timeline.");
            return ToProject(timeline);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string NewWorkDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"InkMotion-AAF-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void Run(string python, string mode, string input, string output, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(python) || !File.Exists(python) ||
            !Path.GetFileName(python).Equals("python.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Select python.exe from a trusted Python 3 environment with pyaaf2 installed. InkMotion does not install or download it.");
        string bridge = Path.Combine(AppContext.BaseDirectory, "Tools", "aaf_bridge.py");
        LocalColourImport.RunTool(python, ["-I", "-B", bridge, mode, input, output], token);
    }
}
