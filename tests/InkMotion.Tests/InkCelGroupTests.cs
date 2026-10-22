using Xunit;

namespace InkMotion.Tests;

public sealed class InkCelGroupTests
{
    private static List<EditorFrame> Frames() =>
        Enumerable.Range(1, 6).Select(number => new EditorFrame
        {
            Number = number, IsKeyframe = true, ExposureFrames = number,
            Strokes = [[new() { X = number / 10d, Y = 0.5 }]],
        }).ToList();

    [Fact]
    public void GroupingLabelsContiguousRunsWithoutChangingDrawingsOrTiming()
    {
        List<EditorFrame> frames = Frames();
        Assert.True(InkCelGroups.Assign(frames, 0, 2, "Walk"));
        Assert.False(InkCelGroups.Assign(frames, 0, 2, "Walk"));
        Assert.True(InkCelGroups.Assign(frames, 4, 5, "Walk"));
        Assert.Equal(new[] { new InkCelGroupRange("Walk", 0, 2), new InkCelGroupRange("Walk", 4, 5) },
            InkCelGroups.GetRanges(frames));
        Assert.True(InkCelGroups.Assign(frames, 3, 3, "Walk"));
        Assert.Equal(new InkCelGroupRange("Walk", 0, 5), Assert.Single(InkCelGroups.GetRanges(frames)));
        Assert.True(InkCelGroups.Assign(frames, 1, 4, ""));
        Assert.Equal(new[] { new InkCelGroupRange("Walk", 0, 0), new InkCelGroupRange("Walk", 5, 5) },
            InkCelGroups.GetRanges(frames));
        Assert.Equal(21, InkTimeline.GetTotalExposureFrames(frames));
        Assert.Equal(0.3, frames[2].Strokes[0][0].X);
    }

    [Fact]
    public void LabelsFollowCelsThroughRangeEditsAndSaveLoad()
    {
        var project = new EditorProject { Frames = Frames() };
        InkCelGroups.Assign(project.Frames, 1, 3, "Walk");
        string before = EditorProjectSerialiser.Serialise(project);
        InkEditing.MoveCelRange(project.Frames, 1, 3, 0);
        Assert.Equal(new InkCelGroupRange("Walk", 0, 2), Assert.Single(InkCelGroups.GetRanges(project.Frames)));
        List<EditorFrame> clipboard = InkEditing.CopyCelRange(project.Frames, 0, 2);
        InkEditing.PasteCelRange(project.Frames, clipboard, project.Frames.Count);
        Assert.Equal(2, InkCelGroups.GetRanges(project.Frames).Count);
        EditorProject restored = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        Assert.Equal(InkCelGroups.GetRanges(project.Frames), InkCelGroups.GetRanges(restored.Frames));
        Assert.Equal(new InkCelGroupRange("Walk", 1, 3),
            Assert.Single(InkCelGroups.GetRanges(EditorProjectSerialiser.Deserialise(before).Frames)));
        InkEditing.DeleteCelRange(project.Frames, 0, 2);
        Assert.Equal(new InkCelGroupRange("Walk", 3, 5), Assert.Single(InkCelGroups.GetRanges(project.Frames)));
    }

    [Theory]
    [InlineData(-1, 2, "Walk")]
    [InlineData(2, 1, "Walk")]
    [InlineData(0, 6, "Walk")]
    [InlineData(0, 2, " Walk")]
    [InlineData(0, 2, "Walk\n")]
    public void InvalidGroupingIsAtomic(int first, int last, string name)
    {
        List<EditorFrame> frames = Frames();
        Assert.Throws<ArgumentException>(() => InkCelGroups.Assign(frames, first, last, name));
        Assert.All(frames, frame => Assert.Equal("", frame.GroupName));
    }

    [Fact]
    public void VersionThirtyNineMigratesToUngroupedCelsAndInvalidSavedNamesFail()
    {
        EditorProject project = EditorProjectSerialiser.Deserialise(
            """{"Version":39,"Frames":[{"Number":1}],"SelectedFrameIndex":0}""");
        Assert.Equal(EditorProject.CurrentVersion, project.Version);
        Assert.Empty(project.Frames[0].GroupName);
        project.Frames[0].GroupName = new string('x', 81);
        Assert.Throws<InvalidDataException>(() => EditorProjectSerialiser.Serialise(project));
    }
}
