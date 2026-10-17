using System.Text.Json;

namespace InkMotion;

public sealed class EditorProject
{
    public int Version { get; set; } = 1;
    public string? VideoPath { get; set; }
    public int SelectedFrameIndex { get; set; }
    public List<EditorFrame> Frames { get; set; } = [new()];
}

public sealed class EditorFrame
{
    public int Number { get; set; } = 1;
    public bool IsKeyframe { get; set; }
    public List<List<InkPoint>> Strokes { get; set; } = [];
}

public sealed class InkPoint
{
    public double X { get; set; }
    public double Y { get; set; }
}

public static class EditorProjectSerializer
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Serialize(EditorProject project)
    {
        Validate(project);
        return JsonSerializer.Serialize(project, Options);
    }

    public static EditorProject Deserialize(string json)
    {
        EditorProject project = JsonSerializer.Deserialize<EditorProject>(json)
            ?? throw new InvalidDataException("The project file is empty.");
        Validate(project);
        return project;
    }

    private static void Validate(EditorProject project)
    {
        if (project.Version != 1)
        {
            throw new InvalidDataException($"Project version {project.Version} is not supported.");
        }

        if (project.Frames is null || project.Frames.Count == 0)
        {
            throw new InvalidDataException("A project must contain at least one frame.");
        }

        if (project.SelectedFrameIndex < 0 || project.SelectedFrameIndex >= project.Frames.Count)
        {
            throw new InvalidDataException("The selected frame index is outside the project timeline.");
        }

        int expectedFrameNumber = 1;
        foreach (EditorFrame? frame in project.Frames)
        {
            if (frame is null || frame.Number != expectedFrameNumber++ || frame.Strokes is null)
            {
                throw new InvalidDataException("The project timeline must contain valid, sequentially numbered frames.");
            }

            foreach (List<InkPoint>? stroke in frame.Strokes)
            {
                if (stroke is null || stroke.Any(point =>
                    point is null ||
                    !double.IsFinite(point.X) ||
                    !double.IsFinite(point.Y) ||
                    point.X is < 0 or > 1 ||
                    point.Y is < 0 or > 1))
                {
                    throw new InvalidDataException("The project contains invalid ink coordinates.");
                }
            }
        }
    }
}
