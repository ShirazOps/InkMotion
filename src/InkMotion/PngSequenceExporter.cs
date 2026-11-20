using Microsoft.Graphics.Canvas;
using Windows.Storage;

namespace InkMotion;

public sealed record PngSequenceExportSettings(VideoExportProfile Profile, double InkReferenceHeight)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Profile);
        if (!double.IsFinite(InkReferenceHeight) || InkReferenceHeight < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(InkReferenceHeight));
        }

        VideoExportProfile.Create(Profile.Width, Profile.Height, Profile.FrameRate);
    }
}

public sealed record PngSequenceExportResult(
    string OutputDirectory,
    int FrameCount,
    double DurationSeconds,
    IReadOnlyList<string> Warnings);

public static class PngSequenceExporter
{
    public static async Task<PngSequenceExportResult> ExportAsync(
        EditorProject project,
        PngSequenceExportSettings settings,
        string outputDirectory,
        IProgress<VideoExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (!ExportPlanning.HasInk(project))
        {
            throw new InvalidOperationException("Nothing to export: add ink or cel text first.");
        }

        double duration = ExportPlanning.GetInkDurationSeconds(project);
        int frameCount = ExportPlanning.GetFrameCount(duration, settings.Profile.FrameRate);
        if (frameCount == 0)
        {
            throw new InvalidOperationException("Nothing to export: add ink or cel text first.");
        }

        string fullOutputDirectory = Path.GetFullPath(outputDirectory);
        string parentPath = Path.GetDirectoryName(fullOutputDirectory) ??
            throw new ArgumentException("Choose a PNG sequence folder inside an existing directory.", nameof(outputDirectory));
        string folderName = Path.GetFileName(fullOutputDirectory);
        if (string.IsNullOrWhiteSpace(folderName))
        {
            throw new ArgumentException("Choose a named PNG sequence folder.", nameof(outputDirectory));
        }

        if (Directory.Exists(fullOutputDirectory) || File.Exists(fullOutputDirectory))
        {
            throw new InvalidOperationException("Choose a PNG sequence folder that does not already exist; existing folders are never overwritten.");
        }

        string stagingDirectory = Path.Combine(parentPath, $".{folderName}.inkmotion-{Guid.NewGuid():N}.tmp");
        bool outputFolderCreated = false;
        int createdFrameCount = 0;
        try
        {
            StorageFolder parent = await StorageFolder.GetFolderFromPathAsync(parentPath);
            StorageFolder output = await parent.CreateFolderAsync(Path.GetFileName(stagingDirectory),
                CreationCollisionOption.FailIfExists);
            outputFolderCreated = true;

            var request = new VideoExportRequest(project, settings.Profile, VideoExportCodec.H264,
                fullOutputDirectory, settings.InkReferenceHeight, PreferHardware: false);
            using var device = new CanvasDevice();
            var warnings = new List<string>();
            using (var renderer = new VideoExporter.FrameRenderer(device, request, warnings, inkOnly: true))
            {
                for (int frame = 0; frame < frameCount; frame++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string fileName = $"frame_{frame + 1:D6}.png";
                    StorageFile file = await output.CreateFileAsync(fileName, CreationCollisionOption.FailIfExists);
                    createdFrameCount++;
                    await renderer.SaveInkFrameAsync(frame, file);
                    if (frame % 5 == 0 || frame == frameCount - 1)
                    {
                        progress?.Report(new VideoExportProgress(frame + 1, frameCount, "Writing PNG sequence"));
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(stagingDirectory, fullOutputDirectory);
            outputFolderCreated = false;
            return new PngSequenceExportResult(fullOutputDirectory, frameCount, duration, warnings);
        }
        catch (Exception ex)
        {
            List<string> cleanupFailures = CleanupPartialOutput(stagingDirectory, createdFrameCount, outputFolderCreated);
            if (cleanupFailures.Count > 0)
            {
                throw new IOException(
                    $"PNG sequence export failed, and partial output cleanup was incomplete: {string.Join("; ", cleanupFailures)}",
                    ex);
            }

            throw;
        }
    }

    private static List<string> CleanupPartialOutput(
        string outputDirectory,
        int createdFrameCount,
        bool outputFolderCreated)
    {
        var failures = new List<string>();
        for (int frame = 1; frame <= createdFrameCount; frame++)
        {
            string file = Path.Combine(outputDirectory, $"frame_{frame:D6}.png");
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        if (outputFolderCreated)
        {
            try
            {
                Directory.Delete(outputDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{Path.GetFileName(outputDirectory)}: {ex.Message}");
            }
        }

        return failures;
    }
}
