using System.Diagnostics;
using System.Text.Json;

namespace InkMotion;

public sealed record ScreenRegion(int X, int Y, int Width, int Height)
{
    public long Right => (long)X + Width;
    public long Bottom => (long)Y + Height;
    public override string ToString() => $"{X}, {Y} · {Width} × {Height}";
}

public sealed record ScreenCapturePlan(ScreenRegion Bounds, IReadOnlyList<ScreenRegion> Regions,
    int Width, int Height, int FrameRate)
{
    public static ScreenCapturePlan Create(IReadOnlyList<ScreenRegion> regions,
        IReadOnlyList<ScreenRegion> displays, int longestSide, int frameRate)
    {
        if (regions.Count is < 1 or > 32 || displays.Count == 0 ||
            longestSide is not (1920 or 3840) || frameRate is not (15 or 24 or 30))
            throw new ArgumentException("Select 1–32 display regions, a 1920/3840-pixel output limit and 15, 24 or 30 fps.");
        var pieces = new List<ScreenRegion>();
        foreach (ScreenRegion region in regions)
        {
            if (region.Width < 2 || region.Height < 2 || region.Right > int.MaxValue || region.Bottom > int.MaxValue)
                throw new ArgumentException("Capture regions must have positive dimensions of at least two physical pixels.");
            bool intersects = false;
            foreach (ScreenRegion display in displays)
            {
                int x = Math.Max(region.X, display.X), y = Math.Max(region.Y, display.Y);
                long right = Math.Min(region.Right, display.Right), bottom = Math.Min(region.Bottom, display.Bottom);
                if (right > x && bottom > y)
                {
                    pieces.Add(new(x, y, checked((int)(right - x)), checked((int)(bottom - y))));
                    intersects = true;
                }
            }
            if (!intersects) throw new ArgumentException("Every selected region must intersect a connected display.");
        }
        int left = pieces.Min(region => region.X), top = pieces.Min(region => region.Y);
        long width = pieces.Max(region => region.Right) - left, height = pieces.Max(region => region.Bottom) - top;
        if (width > 16384 || height > 16384 || width * height > 33_177_600)
            throw new ArgumentException("The capture canvas exceeds 33 megapixels or 16,384 pixels on one side. Select smaller or closer regions.");
        double scale = Math.Min(1, longestSide / (double)Math.Max(width, height));
        return new(new(left, top, (int)width, (int)height), pieces,
            Math.Max(2, (int)(width * scale) / 2 * 2), Math.Max(2, (int)(height * scale) / 2 * 2), frameRate);
    }
}

public sealed class ScreenRecordingMedia
{
    public string FileName { get; set; } = "";
    public string Kind { get; set; } = "Screen";
    public double OffsetSeconds { get; set; }
    public double DurationSeconds { get; set; }
    public bool Finalised { get; set; }
}

public sealed class ScreenRecordingManifest
{
    public int Version { get; set; } = 1;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "Preparing";
    public string? Error { get; set; }
    public ScreenCapturePlan? Plan { get; set; }
    public bool WebcamPictureInPicture { get; set; } = true;
    public List<ScreenRecordingMedia> Media { get; set; } = [];
    public int DroppedFrames { get; set; }
    public List<string> Warnings { get; set; } = [];
}

public static class ScreenRecordingStore
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "InkMotion", "ScreenRecordings");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string CreateDirectory()
    {
        string path = Path.Combine(Root, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    public static void Save(string directory, ScreenRecordingManifest manifest)
    {
        string path = Path.Combine(directory, "recording.json");
        string pending = path + ".new";
        File.WriteAllText(pending, JsonSerializer.Serialize(manifest, Options));
        File.Move(pending, path, true);
    }

    public static ScreenRecordingManifest Read(string directory)
    {
        ScreenRecordingManifest manifest = JsonSerializer.Deserialize<ScreenRecordingManifest>(
            File.ReadAllText(Path.Combine(directory, "recording.json")), Options) ??
            throw new InvalidDataException("The recording recovery manifest is empty.");
        if (manifest.Version != 1 || manifest.Media is null || manifest.Media.Count > 100000 || manifest.Warnings is null)
            throw new InvalidDataException("Unsupported or invalid recording recovery manifest.");
        foreach (ScreenRecordingMedia media in manifest.Media)
        {
            if (media is null || string.IsNullOrWhiteSpace(media.FileName) ||
                Path.GetFileName(media.FileName) != media.FileName || media.FileName.Contains(':') ||
                !double.IsFinite(media.OffsetSeconds) || media.OffsetSeconds < 0 ||
                !double.IsFinite(media.DurationSeconds) || media.DurationSeconds < 0 ||
                media.Kind is not ("Screen" or "Webcam" or "System" or "Microphone"))
                throw new InvalidDataException("Invalid recording media entry.");
        }
        return manifest;
    }

    public static async Task RecoverAsync(string directory, ScreenRecordingManifest manifest)
    {
        foreach (ScreenRecordingMedia media in manifest.Media.Where(media => !media.Finalised))
        {
            string path = Path.Combine(directory, media.FileName);
            try
            {
                if (media.Kind is "System" or "Microphone")
                {
                    using var reader = new NAudio.Wave.WaveFileReader(path);
                    media.DurationSeconds = reader.TotalTime.TotalSeconds;
                }
                else
                {
                    Windows.Storage.StorageFile file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                    var properties = await file.Properties.GetVideoPropertiesAsync();
                    media.DurationSeconds = properties.Duration.TotalSeconds;
                }
                media.Finalised = double.IsFinite(media.DurationSeconds) && media.DurationSeconds > 0;
                if (!media.Finalised) throw new InvalidDataException("No indexed media duration was found.");
                manifest.Warnings.Add($"Recovered the indexed prefix of {media.FileName}; review its ending and sync.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException)
            {
                manifest.Warnings.Add($"{media.FileName} remains incomplete and was not imported: {ex.Message}");
            }
        }
        manifest.Status = "Recovered";
        Save(directory, manifest);
    }

    public static EditorProject BuildProject(string directory, ScreenRecordingManifest manifest)
    {
        // The editor supports 24/30 fps here; 15 fps capture is time-mapped on a 30 fps timeline.
        var project = new EditorProject { FrameRate = manifest.Plan?.FrameRate == 24 ? 24 : 30 };
        var screen = new TimelineTrackProject { Name = "Screen recording", Kind = TimelineTrackKind.Video, IsMuted = true };
        var webcam = new TimelineTrackProject { Name = "Webcam", Kind = TimelineTrackKind.Video, IsMuted = true };
        foreach (ScreenRecordingMedia media in manifest.Media.Where(media => media.Finalised && media.DurationSeconds > 0))
        {
            string path = Path.Combine(directory, media.FileName);
            if (!File.Exists(path)) throw new FileNotFoundException("A recording source is missing.", path);
            int startFrame = checked((int)Math.Round(media.OffsetSeconds * project.FrameRate) + 1);
            if (media.Kind is "Screen" or "Webcam")
            {
                var clip = new TimelineClipProject
                {
                    SourcePath = path, StartFrame = startFrame, SourceOutSeconds = media.DurationSeconds,
                    SourceDurationSeconds = media.DurationSeconds, Volume = 0,
                };
                if (media.Kind == "Webcam" && manifest.WebcamPictureInPicture)
                {
                    clip.Scale = 0.25;
                    clip.X = 0.82;
                    clip.Y = 0.8;
                }
                (media.Kind == "Screen" ? screen : webcam).Clips.Add(clip);
            }
            else
                project.AudioTracks.Add(new() { AudioPath = path, StartFrame = startFrame,
                    SourceOutSeconds = media.DurationSeconds, Title = media.Kind == "System" ? "System audio" : "Microphone" });
        }
        if (screen.Clips.Count == 0) throw new InvalidOperationException("No completed screen segments are available. Incomplete files remain in the recording folder.");
        project.VideoTracks = [screen];
        if (webcam.Clips.Count > 0) project.VideoTracks.Add(webcam);
        return project;
    }

    public static string SaveProject(string directory, ScreenRecordingManifest manifest)
    {
        string path = Path.Combine(directory, "Recording.inkmotion");
        File.WriteAllText(path + ".new", EditorProjectSerialiser.Serialise(BuildProject(directory, manifest)));
        File.Move(path + ".new", path, true);
        return path;
    }

    public static long StorageBytes(string directory) => Directory.EnumerateFiles(directory)
        .Sum(path => new FileInfo(path).Length);

    public static void Delete(string directory)
    {
        string resolved = Path.GetFullPath(directory);
        if (!string.Equals(Path.GetDirectoryName(resolved), Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(resolved, "recording.json")))
            throw new InvalidOperationException("Only an identified recording folder can be deleted.");
        if ((File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0 ||
            Directory.EnumerateDirectories(resolved).Any())
            throw new InvalidOperationException("The recording folder contains unexpected subfolders or links; remove it manually.");
        foreach (string file in Directory.EnumerateFiles(resolved)) File.Delete(file);
        Directory.Delete(resolved);
    }
}

public interface IScreenRecordingFrames : IDisposable
{
    byte[] Capture();
}

public static class ScreenRecordingEncoder
{
    // Finalised segments survive a crash even if the current MP4 has no closing index.
    public static void Record(string directory, ScreenRecordingManifest manifest, Stopwatch clock,
        Func<IScreenRecordingFrames> createFrames, Action checkpoint, CancellationToken stop)
    {
        ScreenCapturePlan plan = manifest.Plan ?? throw new ArgumentException("A capture plan is required.");
        using IScreenRecordingFrames frames = createFrames();
        while (!stop.IsCancellationRequested)
        {
            int index = manifest.Media.Count(media => media.Kind == "Screen");
            var media = new ScreenRecordingMedia { FileName = $"Screen-{index:D5}.mp4", OffsetSeconds =
                Math.Ceiling(clock.Elapsed.TotalSeconds * plan.FrameRate) / plan.FrameRate };
            lock (manifest) { manifest.Media.Add(media); checkpoint(); }
            using var writer = new MediaFoundationVideoWriter(Path.Combine(directory, media.FileName), plan.Width, plan.Height,
                plan.FrameRate, VideoExportCodec.H264, plan.Width >= 3000 ? 20_000_000 : 8_000_000, 0, 0, true);
            int next = 0;
            byte[]? previous = null;
            while (!stop.IsCancellationRequested && next < plan.FrameRate * 5)
            {
                double due = media.OffsetSeconds + next / (double)plan.FrameRate;
                double delay = due - clock.Elapsed.TotalSeconds;
                if (delay > 0 && stop.WaitHandle.WaitOne(TimeSpan.FromSeconds(delay))) break;
                byte[] pixels = frames.Capture();
                int current = Math.Max(next, (int)Math.Floor((clock.Elapsed.TotalSeconds - media.OffsetSeconds) * plan.FrameRate));
                current = Math.Min(plan.FrameRate * 5 - 1, current);
                while (next <= current)
                {
                    byte[] image = next == current || previous is null ? pixels : previous;
                    writer.WriteVideoFrame(image, ExportPlanning.GetFrameTime(next, plan.FrameRate),
                        ExportPlanning.GetFrameTime(next + 1, plan.FrameRate) - ExportPlanning.GetFrameTime(next, plan.FrameRate));
                    if (next < current) manifest.DroppedFrames++;
                    next++;
                }
                previous = pixels;
            }
            if (next > 0)
            {
                writer.Finish();
                lock (manifest)
                {
                    media.DurationSeconds = next / (double)plan.FrameRate;
                    media.Finalised = true;
                    checkpoint();
                }
            }
        }
    }
}
