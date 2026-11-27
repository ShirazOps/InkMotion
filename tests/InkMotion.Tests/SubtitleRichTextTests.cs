using Xunit;

namespace InkMotion.Tests;

public sealed class SubtitleRichTextTests
{
    private static (TimelineTrackProject Track, TimelineClipProject Clip) CreateTimedSubtitle()
    {
        var clip = new TimelineClipProject { StartFrame = 1, DurationSeconds = 4, Text = "One two three four" };
        clip.SubtitleRuns = SubtitleRichText.SplitWords(clip.Text);
        SubtitleRichText.DistributeEvenly(clip.SubtitleRuns, clip.DurationSeconds);
        clip.SubtitleRuns[1].Bold = true;
        clip.SubtitleRuns[2].Colour = 0xFFFF0000;
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Subtitle, Clips = [clip] };
        return (track, clip);
    }

    [Fact]
    public void SplitWords_KeepsTextAndWhitespaceExactly()
    {
        List<SubtitleRun> runs = SubtitleRichText.SplitWords("  Hello there\nworld ");
        Assert.Equal(["  Hello ", "there\n", "world "], runs.Select(run => run.Text));
        Assert.Equal("  Hello there\nworld ", string.Concat(runs.Select(run => run.Text)));
    }

    [Fact]
    public void GetStyledRanges_UsesUpcomingColourBeforeReveal()
    {
        (_, TimelineClipProject clip) = CreateTimedSubtitle();
        IReadOnlyList<SubtitleStyledRange> early = SubtitleRichText.GetStyledRanges(clip, 1.5);
        Assert.Equal(clip.Colour, early[0].Colour);
        Assert.True(early[1].Bold);
        Assert.Equal(clip.Colour, early[1].Colour);
        Assert.Equal(clip.SubtitleUpcomingColour, early[2].Colour);
        Assert.Equal(4, early[1].Start);
        Assert.Equal(0xFFFF0000u, SubtitleRichText.GetStyledRanges(clip, 2)[2].Colour);
        Assert.Equal(0xFFFF0000u, SubtitleRichText.GetStyledRanges(clip, null)[2].Colour);
    }

    [Fact]
    public void Validate_RejectsMismatchedTextDecreasingTimesAndUnsupportedTracks()
    {
        (TimelineTrackProject track, TimelineClipProject clip) = CreateTimedSubtitle();
        Assert.Null(VideoTimelineEditing.Validate([track], 25));

        clip.SubtitleRuns[3].RevealSeconds = 0.5;
        Assert.NotNull(VideoTimelineEditing.Validate([track], 25));
        SubtitleRichText.DistributeEvenly(clip.SubtitleRuns, 4);

        clip.SubtitleRuns[3].Text = "fourth";
        Assert.NotNull(VideoTimelineEditing.Validate([track], 25));
        clip.SubtitleRuns[3].Text = "four";

        var image = new TimelineTrackProject
        {
            Kind = TimelineTrackKind.Image,
            Clips = [new() { StartFrame = 1, DurationSeconds = 1, SourcePath = "a.png", SubtitleRuns = [new() { Text = "x" }] }],
        };
        Assert.NotNull(VideoTimelineEditing.Validate([image], 25));
    }

    [Fact]
    public void SplitAndTrim_RebaseRevealTimes()
    {
        (TimelineTrackProject track, TimelineClipProject clip) = CreateTimedSubtitle();
        TimelineClipProject right = VideoTimelineEditing.Split(track, clip, 51, 25)!;
        Assert.Equal([0, 1, 2, 2], clip.SubtitleRuns.Select(run => run.RevealSeconds!.Value));
        Assert.Equal([0, 0, 0, 1], right.SubtitleRuns.Select(run => run.RevealSeconds!.Value));
        Assert.Equal(clip.Text, right.Text);
        Assert.Null(VideoTimelineEditing.Validate([track], 25));

        Assert.True(VideoTimelineEditing.TryTrimStart(track, right, 63, 25));
        Assert.Equal([0, 0, 0, 0.52], right.SubtitleRuns.Select(run => Math.Round(run.RevealSeconds!.Value, 2)));
        Assert.Null(VideoTimelineEditing.Validate([track], 25));
    }

    [Fact]
    public void Clone_DeepCopiesRuns()
    {
        (_, TimelineClipProject clip) = CreateTimedSubtitle();
        TimelineClipProject copy = VideoTimelineEditing.Clone(clip);
        copy.SubtitleRuns[0].Bold = true;
        Assert.False(clip.SubtitleRuns[0].Bold);
        Assert.True(SubtitleRichText.RunsEqual(clip.SubtitleRuns, VideoTimelineEditing.Clone(clip).SubtitleRuns));
    }

    [Fact]
    public void ProjectRoundTrip_PreservesRunsAndLegacyProjectsHaveNone()
    {
        (TimelineTrackProject track, TimelineClipProject clip) = CreateTimedSubtitle();
        clip.SubtitleUpcomingColour = 0x80112233;
        var project = new EditorProject { VideoTracks = [track] };
        EditorProject reopened = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        TimelineClipProject restored = reopened.VideoTracks[0].Clips[0];
        Assert.True(SubtitleRichText.RunsEqual(clip.SubtitleRuns, restored.SubtitleRuns));
        Assert.Equal(0x80112233u, restored.SubtitleUpcomingColour);

        string legacy = EditorProjectSerialiser.Serialise(new EditorProject
        {
            VideoTracks = [new() { Kind = TimelineTrackKind.Subtitle, Clips = [new() { StartFrame = 1, DurationSeconds = 1, Text = "Old" }] }],
        }).Replace($"\"Version\": {EditorProject.CurrentVersion}", "\"Version\": 97", StringComparison.Ordinal);
        var node = System.Text.Json.Nodes.JsonNode.Parse(legacy)!;
        node["VideoTracks"]![0]!["Clips"]![0]!.AsObject().Remove("SubtitleRuns");
        node["VideoTracks"]![0]!["Clips"]![0]!.AsObject().Remove("SubtitleUpcomingColour");
        EditorProject migrated = EditorProjectSerialiser.Deserialise(node.ToJsonString());
        Assert.Equal(EditorProject.CurrentVersion, migrated.Version);
        Assert.Empty(migrated.VideoTracks[0].Clips[0].SubtitleRuns);
        Assert.Equal(SubtitleRichText.DefaultUpcomingColour, migrated.VideoTracks[0].Clips[0].SubtitleUpcomingColour);
    }

    [Fact]
    public void CreateTracks_RebasesRunsOntoFrameAlignedStart()
    {
        var cue = new SubtitleCueText(1.01, 3, "Hi there")
        {
            Runs = [new() { Text = "Hi ", RevealSeconds = 0 }, new() { Text = "there", RevealSeconds = 1 }],
        };
        TimelineClipProject clip = SubtitleSrt.CreateTracks([cue], 25)[0].Clips[0];
        Assert.Equal(26, clip.StartFrame);
        Assert.Equal(0.01, clip.SubtitleRuns[0].RevealSeconds!.Value, 6);
        Assert.Equal(1.01, clip.SubtitleRuns[1].RevealSeconds!.Value, 6);
    }

    [Fact]
    public void SpeechCues_WithWordTiming_RevealWordsAtTheirStarts()
    {
        SpeechWord[] words = [new(1, 1.4, "hello"), new(1.5, 2, "world")];
        SubtitleCueText cue = Assert.Single(SpeechSubtitles.BuildCues(words, new SpeechCueOptions { WordTiming = true }));
        Assert.NotNull(cue.Runs);
        Assert.Equal(cue.Text, string.Concat(cue.Runs!.Select(run => run.Text)));
        Assert.Equal([0, 0.5], cue.Runs!.Select(run => Math.Round(run.RevealSeconds!.Value, 3)));
        Assert.Null(Assert.Single(SpeechSubtitles.BuildCues(words)).Runs);
    }

    [Theory]
    [InlineData("#FF0000", 0xFFFF0000u)]
    [InlineData("#80112233", 0x80112233u)]
    public void ParseColour_AcceptsRgbAndArgb(string text, uint expected)
    {
        Assert.True(SubtitleRichText.TryParseColour(text, out uint colour));
        Assert.Equal(expected, colour);
        Assert.True(SubtitleRichText.TryParseColour(SubtitleRichText.FormatColour(colour), out uint again));
        Assert.Equal(expected, again);
        Assert.False(SubtitleRichText.TryParseColour("red", out _));
    }
}
