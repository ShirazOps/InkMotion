using System.Text.Json;

namespace InkMotion;

public enum QuickStartAction
{
    MediaAdded,
    InkWorkspaceOpened,
    LayerAdded,
    PenSelected,
    InkDrawn,
    PlaybackStarted,
    ExportWorkspaceOpened,
    VideoExportSubmitted,
}

public sealed record QuickStartStep(QuickStartAction Action, string Workspace, string Title, string Instruction);

public sealed class QuickStartGuide
{
    public static IReadOnlyList<QuickStartStep> Steps { get; } = Array.AsReadOnly(new[]
    {
        new QuickStartStep(QuickStartAction.MediaAdded, "Video", "Add your media",
            "Click Add video and choose a short video from your computer. It appears on the timeline. You can use Add image instead. Cancelling the file picker keeps you on this step."),
        new QuickStartStep(QuickStartAction.InkWorkspaceOpened, "Video", "Open Ink Editor",
            "Click Ink Editor at the top of the window. This workspace contains the drawing tools and ink layers."),
        new QuickStartStep(QuickStartAction.LayerAdded, "Ink", "Create an ink layer",
            "Click Add ink layer in the options panel. The new, independent layer becomes selected; drawings on other layers are unchanged."),
        new QuickStartStep(QuickStartAction.PenSelected, "Ink", "Choose the pen",
            "Click Pen on the toolbar to select freehand drawing. Mouse, touch and pressure-sensitive pen input are supported."),
        new QuickStartStep(QuickStartAction.InkDrawn, "Ink", "Draw on the canvas",
            "Press and drag on the highlighted canvas, then release to finish your stroke. Your drawing belongs to the selected layer and cel. It is not motion-tracked automatically."),
        new QuickStartStep(QuickStartAction.PlaybackStarted, "Ink", "Preview your work",
            "Click Play to preview the timeline with your artwork. A single drawing is a cel, not automatic animation; add cels or use Follow object later for motion."),
        new QuickStartStep(QuickStartAction.ExportWorkspaceOpened, "Ink", "Open Export",
            "Click Export at the top of the window. Resolution, frame rate and codec settings are separate from the saved drawing."),
        new QuickStartStep(QuickStartAction.VideoExportSubmitted, "Export", "Export your video",
            "Review the export settings, then click Export video and choose a destination. The guide advances when the export is accepted, not when you open or cancel the picker. Watch Export jobs for the final result."),
    });

    public bool IsActive { get; private set; }
    public int StepIndex { get; private set; }
    public bool IsComplete => IsActive && StepIndex == Steps.Count;
    public QuickStartStep? CurrentStep => IsActive && !IsComplete ? Steps[StepIndex] : null;

    public void Start()
    {
        StepIndex = 0;
        IsActive = true;
    }

    public bool Observe(QuickStartAction action)
    {
        if (CurrentStep?.Action != action) return false;
        StepIndex++;
        return true;
    }

    public void Stop() => IsActive = false;
}

public sealed class QuickStartPreferences
{
    public bool WelcomeDismissed { get; set; }
    public bool Completed { get; set; }

    public static QuickStartPreferences Load(string path)
    {
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize<QuickStartPreferences>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Saved beginner guide preferences are empty.");
    }

    public void Save(string path)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".quick-start-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, this);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
