using Xunit;

namespace InkMotion.Tests;

public sealed class EditorLayoutTests
{
    [Fact]
    public void DefaultsKeepAllPanelsVisibleAndIndependentPerWorkspace()
    {
        var preferences = new EditorLayoutPreferences();
        preferences.Validate();
        Assert.Equal(4, preferences.Workspaces.Count);
        Assert.All(preferences.Workspaces.Values, layout =>
        {
            Assert.Equal(340, layout.OptionsWidth);
            Assert.Equal(0.6, layout.PreviewFraction);
            Assert.True(layout.ShowPreview && layout.ShowTimeline && layout.ShowOptions);
        });
        preferences.Workspaces["Ink"] = new() { ShowTimeline = false, OptionsWidth = 500 };
        Assert.True(preferences.Workspaces["Video"].ShowTimeline);
        Assert.Equal(340, preferences.Workspaces["Audio"].OptionsWidth);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(180, 120)]
    [InlineData(800, 500)]
    [InlineData(1920, 1080)]
    public void SizingFitsSmallAndLargeWindowsWithoutLosingPreferences(double width, double height)
    {
        var layout = new WorkspaceLayout { OptionsWidth = 900, PreviewFraction = 0.9 };
        double options = layout.GetOptionsWidth(width);
        Assert.InRange(options, 0, width);
        if (width > 10) Assert.True(width - options - 10 >= (width - 10) * 0.35 - 1e-8);
        double preview = layout.GetPreviewHeight(height);
        Assert.InRange(preview, 0, height);
        if (height >= 290)
        {
            Assert.True(preview >= 180);
            Assert.True(height - 10 - preview >= 100);
        }
        Assert.Equal(900, layout.OptionsWidth);
        Assert.Equal(0.9, layout.PreviewFraction);
    }

    [Fact]
    public void CollapsedPanelsReleaseSpaceAndTransportRemainsForWaveformPlayback()
    {
        var layout = new WorkspaceLayout();
        Assert.Equal(0, (layout with { ShowOptions = false }).GetOptionsWidth(1200));
        Assert.Equal(800, (layout with { ShowTimeline = false }).GetPreviewHeight(800));
        Assert.Equal(48, (layout with { ShowPreview = false }).GetPreviewHeight(800));
        Assert.Equal(20, (layout with { ShowPreview = false }).GetPreviewHeight(20));
        Assert.False((layout with { ShowPreview = false, ShowTimeline = false }).IsValid);
    }

    [Fact]
    public void LayoutRoundTripsAllWorkspaceSizesAndVisibilityAndResetOverwritesSavedChoices()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"inkmotion-layout-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "layout.json");
        try
        {
            Assert.Equal(new WorkspaceLayout(), EditorLayoutPreferences.Load(path).Workspaces["Ink"]);
            var preferences = new EditorLayoutPreferences();
            preferences.Workspaces["Video"] = new() { OptionsWidth = 420, PreviewFraction = 0.75, ShowOptions = false };
            preferences.Workspaces["Ink"] = new() { ShowTimeline = false, OptionsWidth = 550 };
            preferences.Workspaces["Audio"] = new() { ShowPreview = false, PreviewFraction = 0.2 };
            preferences.Save(path);
            EditorLayoutPreferences reopened = EditorLayoutPreferences.Load(path);
            foreach (string workspace in preferences.Workspaces.Keys)
            {
                Assert.Equal(preferences.Workspaces[workspace], reopened.Workspaces[workspace]);
            }
            new EditorLayoutPreferences().Save(path);
            Assert.All(EditorLayoutPreferences.Load(path).Workspaces.Values, layout => Assert.Equal(new WorkspaceLayout(), layout));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    [Theory]
    [InlineData("width-low")]
    [InlineData("width-high")]
    [InlineData("width-nan")]
    [InlineData("fraction-low")]
    [InlineData("fraction-high")]
    [InlineData("fraction-infinite")]
    [InlineData("both-hidden")]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("null")]
    [InlineData("version")]
    public void RejectsInvalidLayoutsBeforeReplacingAValidSavedFile(string reason)
    {
        string path = Path.Combine(Path.GetTempPath(), $"inkmotion-layout-{Guid.NewGuid():N}.json");
        try
        {
            var preferences = new EditorLayoutPreferences();
            preferences.Save(path);
            string before = File.ReadAllText(path);
            preferences.Workspaces["Video"] = reason switch
            {
                "width-low" => new() { OptionsWidth = 219 },
                "width-high" => new() { OptionsWidth = 901 },
                "width-nan" => new() { OptionsWidth = double.NaN },
                "fraction-low" => new() { PreviewFraction = 0 },
                "fraction-high" => new() { PreviewFraction = 1 },
                "fraction-infinite" => new() { PreviewFraction = double.PositiveInfinity },
                "both-hidden" => new() { ShowTimeline = false, ShowPreview = false },
                "null" => null!,
                _ => new(),
            };
            if (reason == "version") preferences.Version = 2;
            if (reason == "missing") preferences.Workspaces.Remove("Ink");
            if (reason == "extra") preferences.Workspaces["Unknown"] = new();
            Assert.Throws<InvalidDataException>(() => preferences.Save(path));
            Assert.Equal(before, File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("""{"Version":999}""")]
    [InlineData("""{"Workspaces":{}}""")]
    [InlineData("""{"Workspaces":null}""")]
    public void InvalidSavedLayoutsAreReportedInsteadOfSilentlyAccepted(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"inkmotion-layout-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, content);
            Assert.Throws<InvalidDataException>(() => EditorLayoutPreferences.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MalformedJsonAndBlockedSaveReportErrorsWithoutLeavingTemporaryFiles()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"inkmotion-layout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "layout.json");
        try
        {
            File.WriteAllText(path, "{");
            Assert.Throws<System.Text.Json.JsonException>(() => EditorLayoutPreferences.Load(path));
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Exception? error = Record.Exception(() => new EditorLayoutPreferences().Save(path));
                Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString() ?? "Expected a blocked save to report an error.");
            }
            Assert.Equal("{", File.ReadAllText(path));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }
}
