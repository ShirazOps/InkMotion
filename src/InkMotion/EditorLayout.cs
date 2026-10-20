using System.Text.Json;

namespace InkMotion;

public sealed record WorkspaceLayout
{
    public double OptionsWidth { get; init; } = 340;
    public double PreviewFraction { get; init; } = 0.6;
    public bool ShowOptions { get; init; } = true;
    public bool ShowTimeline { get; init; } = true;
    public bool ShowPreview { get; init; } = true;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => double.IsFinite(OptionsWidth) && OptionsWidth is >= 220 and <= 900 &&
        double.IsFinite(PreviewFraction) && PreviewFraction is >= 0.1 and <= 0.9 &&
        (ShowTimeline || ShowPreview);

    public double GetOptionsWidth(double availableWidth) =>
        ShowOptions ? Math.Min(OptionsWidth, Math.Max(0, availableWidth - 10) * 0.65) : 0;

    public double GetPreviewHeight(double availableHeight)
    {
        if (!ShowTimeline) return Math.Max(0, availableHeight);
        if (!ShowPreview) return Math.Min(availableHeight, 48);
        double usable = Math.Max(0, availableHeight - 10);
        return Math.Clamp(usable * PreviewFraction, Math.Min(180, usable / 2), usable - Math.Min(100, usable / 2));
    }
}

public sealed class EditorLayoutPreferences
{
    public int Version { get; set; } = 1;
    public Dictionary<string, WorkspaceLayout> Workspaces { get; set; } = new()
    {
        ["Video"] = new(), ["Audio"] = new(), ["Ink"] = new(), ["Export"] = new(),
    };

    public void Validate()
    {
        if (Version != 1 || Workspaces is null || Workspaces.Count != 4 ||
            new[] { "Video", "Audio", "Ink", "Export" }.Any(key =>
                !Workspaces.TryGetValue(key, out WorkspaceLayout? layout) || layout is null || !layout.IsValid))
        {
            throw new InvalidDataException("Saved editor layout has unsupported or invalid workspace settings.");
        }
    }

    public static EditorLayoutPreferences Load(string path)
    {
        if (!File.Exists(path)) return new();
        EditorLayoutPreferences result = JsonSerializer.Deserialize<EditorLayoutPreferences>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Saved editor layout is empty.");
        result.Validate();
        return result;
    }

    public void Save(string path)
    {
        Validate();
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".layout-{Guid.NewGuid():N}.tmp");
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
