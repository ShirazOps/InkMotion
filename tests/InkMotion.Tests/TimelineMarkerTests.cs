using Xunit;

namespace InkMotion.Tests;

public sealed class TimelineMarkerTests
{
    [Fact]
    public void Navigation_UsesFrameOrderSkipsCurrentAndHandlesDuplicatesAndEdges()
    {
        List<TimelineMarkerProject> markers =
        [
            new() { Frame = 100 }, new() { Frame = 10 }, new() { Frame = 50 }, new() { Frame = 50 },
        ];
        Assert.Equal(50, TimelineMarkers.FindAdjacent(markers, 10, true));
        Assert.Equal(100, TimelineMarkers.FindAdjacent(markers, 50, true));
        Assert.Equal(10, TimelineMarkers.FindAdjacent(markers, 50, false));
        Assert.Null(TimelineMarkers.FindAdjacent(markers, 100, true));
        Assert.Null(TimelineMarkers.FindAdjacent(markers, 10, false));
        Assert.Null(TimelineMarkers.FindAdjacent([], 10, false));
    }

    [Theory]
    [InlineData(1, 24, 60, 1)]
    [InlineData(25, 24, 60, 61)]
    [InlineData(61, 60, 24, 25)]
    [InlineData(2, 24, 60, 3)]
    public void FrameConversion_PreservesTimelineTimeToNearestFrame(int frame, double oldRate, double newRate, int expected)
    {
        int converted = TimelineMarkers.ConvertFrame(frame, oldRate, newRate);
        Assert.Equal(expected, converted);
        Assert.InRange(Math.Abs((frame - 1d) / oldRate - (converted - 1d) / newRate), 0, 0.5 / newRate);
    }

    [Fact]
    public void FrameConversion_RejectsOverflowAndInvalidRates()
    {
        Assert.Throws<InvalidOperationException>(() => TimelineMarkers.ConvertFrame(int.MaxValue, 12, 60));
        Assert.Throws<ArgumentOutOfRangeException>(() => TimelineMarkers.ConvertFrame(0, 24, 60));
        Assert.Throws<ArgumentOutOfRangeException>(() => TimelineMarkers.ConvertFrame(1, double.NaN, 60));
    }

    [Fact]
    public void Markers_MigrateRoundTripAndCloneWithoutAffectingExportLength()
    {
        EditorProject older = EditorProjectSerialiser.Deserialise("""{"Version":13}""");
        Assert.Empty(older.Markers);
        Assert.Equal(EditorProject.CurrentVersion, older.Version);
        var marker = new TimelineMarkerProject { Frame = 2401, Name = "Check lip sync" };
        older.Markers.Add(marker);
        EditorProject reopened = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(older));
        Assert.Equal(marker.Id, reopened.Markers[0].Id);
        Assert.Equal(2401, reopened.Markers[0].Frame);
        Assert.Equal("Check lip sync", reopened.Markers[0].Name);
        TimelineMarkerProject clone = TimelineMarkers.Clone(marker);
        clone.Frame = 1;
        clone.Name = "Independent";
        Assert.Equal(2401, marker.Frame);
        Assert.Equal(0, VideoExporter.GetDurationSeconds(reopened, 0));
    }

    [Theory]
    [InlineData("""{"Version":14,"Markers":null}""")]
    [InlineData("""{"Version":14,"Markers":[null]}""")]
    [InlineData("""{"Version":14,"Markers":[{"Id":"a","Frame":0,"Name":"Bad"}]}""")]
    [InlineData("""{"Version":14,"Markers":[{"Id":"","Name":"Bad"}]}""")]
    [InlineData("""{"Version":14,"Markers":[{"Name":" "}]}""")]
    [InlineData("""{"Version":14,"Markers":[{"Id":"a"},{"Id":"a"}]}""")]
    public void InvalidMarkerData_IsRejected(string json) =>
        Assert.Throws<InvalidDataException>(() => EditorProjectSerialiser.Deserialise(json));
}
