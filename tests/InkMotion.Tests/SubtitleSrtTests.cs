using Xunit;

namespace InkMotion.Tests;

public sealed class SubtitleSrtTests
{
    [Fact]
    public void ImportAndExport_PreservesMultilineAndOverlappingCues()
    {
        const string input = """
            1
            00:00:00,000 --> 00:00:01,000
            First line
            second line

            2
            00:00:00,480 --> 00:00:01,480
            Overlapping cue

            3
            00:00:01,520 --> 00:00:02,000
            Last cue
            """;

        IReadOnlyList<TimelineTrackProject> tracks = SubtitleSrt.Import(input, 25);
        Assert.Equal(2, tracks.Count);
        Assert.Equal(3, tracks.Sum(track => track.Clips.Count));
        Assert.All(tracks, track => Assert.Equal(TimelineTrackKind.Subtitle, track.Kind));
        Assert.Null(VideoTimelineEditing.Validate(tracks, 25));
        Assert.Equal(2, VideoTimelineEditing.GetVisibleLayers(tracks, 0.6, 25).Count);

        string exported = SubtitleSrt.Export(tracks, 25);
        Assert.Contains("00:00:00,000 --> 00:00:01,000\r\nFirst line\r\nsecond line", exported);
        Assert.Contains("00:00:00,480 --> 00:00:01,480\r\nOverlapping cue", exported);
        Assert.Contains("00:00:01,520 --> 00:00:02,000\r\nLast cue", exported);
        Assert.Equal(3, exported.Split("\r\n\r\n", StringSplitOptions.RemoveEmptyEntries).Length);

        var project = new EditorProject { VideoTracks = tracks.ToList() };
        EditorProject reopened = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        Assert.All(reopened.VideoTracks, track => Assert.Equal(TimelineTrackKind.Subtitle, track.Kind));
    }

    [Theory]
    [InlineData("1\n00:00:02,000 --> 00:00:01,000\nBackwards")]
    [InlineData("1\n00:61:00,000 --> 00:61:01,000\nBad minutes")]
    [InlineData("1\nnot a timestamp\nNo time")]
    [InlineData("1\n00:00:01,000 --> 00:00:02,000\n")]
    public void Import_RejectsMalformedCues(string input)
    {
        Assert.Throws<InvalidDataException>(() => SubtitleSrt.Import(input, 24));
    }

    [Fact]
    public void Export_FormatsHoursBeyondOneDay()
    {
        var track = new TimelineTrackProject
        {
            Kind = TimelineTrackKind.Subtitle,
            Clips = [new() { StartFrame = 2_160_001, DurationSeconds = 1, Text = "Late cue" }],
        };

        string exported = SubtitleSrt.Export([track], 24);

        Assert.Contains("25:00:00,000 --> 25:00:01,000", exported);
    }
}
