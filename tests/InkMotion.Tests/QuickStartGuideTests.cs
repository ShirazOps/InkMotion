using Xunit;

namespace InkMotion.Tests;

public sealed class QuickStartGuideTests
{
    [Fact]
    public void AdvancesOnlyAfterTheExpectedActionAndCompletesAfterExportAcceptance()
    {
        var guide = new QuickStartGuide();
        Assert.False(guide.Observe(QuickStartAction.MediaAdded));
        guide.Start();
        Assert.False(guide.Observe(QuickStartAction.VideoExportSubmitted));
        Assert.Equal(0, guide.StepIndex);
        foreach (QuickStartAction action in new[]
        {
            QuickStartAction.MediaAdded,
            QuickStartAction.InkWorkspaceOpened,
            QuickStartAction.LayerAdded,
            QuickStartAction.PenSelected,
            QuickStartAction.InkDrawn,
            QuickStartAction.PlaybackStarted,
            QuickStartAction.ExportWorkspaceOpened,
        })
        {
            Assert.True(guide.Observe(action));
            Assert.False(guide.Observe(action));
            Assert.False(guide.IsComplete);
        }
        Assert.Equal(QuickStartAction.VideoExportSubmitted, guide.CurrentStep!.Action);
        Assert.True(guide.Observe(QuickStartAction.VideoExportSubmitted));
        Assert.True(guide.IsComplete);
        Assert.Null(guide.CurrentStep);
        Assert.False(guide.Observe(QuickStartAction.VideoExportSubmitted));
    }

    [Fact]
    public void SkippingStopsProgressAndRestartBeginsAtImport()
    {
        var guide = new QuickStartGuide();
        guide.Start();
        guide.Observe(QuickStartAction.MediaAdded);
        guide.Stop();
        Assert.False(guide.Observe(QuickStartAction.InkWorkspaceOpened));
        Assert.Null(guide.CurrentStep);
        guide.Start();
        Assert.False(guide.IsComplete);
        Assert.Equal(QuickStartAction.MediaAdded, guide.CurrentStep!.Action);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemembersDismissalAndCompletionWithoutChangingAProject(bool completed)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"InkMotion-guide-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "quick-start.json");
        try
        {
            Assert.False(QuickStartPreferences.Load(path).WelcomeDismissed);
            var preferences = new QuickStartPreferences { WelcomeDismissed = true, Completed = completed };
            preferences.Save(path);
            QuickStartPreferences reopened = QuickStartPreferences.Load(path);
            Assert.True(reopened.WelcomeDismissed);
            Assert.Equal(completed, reopened.Completed);
            Assert.Single(Directory.GetFiles(directory));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    [Theory]
    [InlineData("null", typeof(InvalidDataException))]
    [InlineData("{", typeof(System.Text.Json.JsonException))]
    public void InvalidPreferencesAreReportedRatherThanTreatedAsFirstLaunch(string content, Type error)
    {
        string path = Path.Combine(Path.GetTempPath(), $"InkMotion-guide-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, content);
            Assert.Throws(error, () => QuickStartPreferences.Load(path));
            Assert.Equal(content, File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }
}
