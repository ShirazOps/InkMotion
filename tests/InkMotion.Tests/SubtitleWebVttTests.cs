using Xunit;

namespace InkMotion.Tests;

public sealed class SubtitleWebVttTests
{
    [Fact]
    public void Import_ParsesTimingsTagsEntitiesAndSettings()
    {
        const string content = "\uFEFFWEBVTT - sample\nKind: captions\n\nNOTE a comment\nspanning lines\n\n" +
            "intro\n00:01.000 --> 00:03.500 line:10%,center position:25%,center align:center\n" +
            "Hello <b>bold</b> &amp; <i>it</i>\n<u>under</u> <c.yellow>yellow</c>\n\n" +
            "01:00:00.000 --> 01:00:01.000\nplain &lt;text&gt;\n";

        SubtitleImportResult result = SubtitleWebVtt.Import(content, 25);

        Assert.Empty(result.Warnings);
        TimelineClipProject[] clips = result.Tracks.SelectMany(track => track.Clips).OrderBy(clip => clip.StartFrame).ToArray();
        Assert.Equal(2, clips.Length);
        TimelineClipProject first = clips[0];
        Assert.Equal(26, first.StartFrame);
        Assert.Equal(2.52, first.DurationSeconds, 6);
        Assert.Equal("Hello bold & it\nunder yellow", first.Text);
        Assert.Equal(0.25, first.X, 6);
        Assert.Equal(0.10, first.Y, 6);
        Assert.Contains(first.SubtitleRuns, run => run.Text == "bold" && run.Bold);
        Assert.Contains(first.SubtitleRuns, run => run.Text == "it" && run.Italic);
        Assert.Contains(first.SubtitleRuns, run => run.Text == "under" && run.Underline);
        Assert.Contains(first.SubtitleRuns, run => run.Text == "yellow" && run.Colour == 0xFFFFFF00);
        Assert.Equal("plain <text>", clips[1].Text);
        Assert.Empty(clips[1].SubtitleRuns);
        Assert.Equal(0.5, clips[1].X);
        Assert.Equal(0.88, clips[1].Y);
    }

    [Fact]
    public void Import_MapsTimestampTagsStyleClassesAndFutureColour()
    {
        const string content = "WEBVTT\n\nSTYLE\n::cue(.orange) { color: #ff8000; }\n::cue(:future) { color: rgba(128, 128, 128, 0.5); }\n\n" +
            "00:00:02.000 --> 00:00:04.000\nOne <00:00:02.500><c.orange>two</c> <00:00:03.000>three\n";

        SubtitleImportResult result = SubtitleWebVtt.Import(content, 50);

        Assert.Empty(result.Warnings);
        TimelineClipProject clip = Assert.Single(Assert.Single(result.Tracks).Clips);
        Assert.Equal("One two three", clip.Text);
        Assert.Equal(0x80808080u, clip.SubtitleUpcomingColour);
        Assert.Collection(clip.SubtitleRuns,
            run => { Assert.Equal("One ", run.Text); Assert.Null(run.RevealSeconds); },
            run => { Assert.Equal("two", run.Text); Assert.Equal(0xFFFF8000, run.Colour); Assert.Equal(0.5, run.RevealSeconds!.Value, 6); },
            run => { Assert.Equal(" ", run.Text); Assert.Equal(0.5, run.RevealSeconds!.Value, 6); },
            run => { Assert.Equal("three", run.Text); Assert.Equal(1.0, run.RevealSeconds!.Value, 6); });
    }

    [Fact]
    public void Import_ReportsUnsupportedConstructsWithoutDroppingText()
    {
        const string content = "WEBVTT\n\nREGION\nid:r1\n\n" +
            "00:00.000 --> 00:02.000 vertical:rl size:50% region:r1 align:start line:3\n" +
            "<v Ana>Hi <lang en>there</lang> <ruby>漢<rt>kan</rt></ruby> <c.bg_red.custom>x</c> &bogus;\n";

        SubtitleImportResult result = SubtitleWebVtt.Import(content, 25);

        TimelineClipProject clip = Assert.Single(Assert.Single(result.Tracks).Clips);
        Assert.Equal("Ana: Hi there 漢(kan) x &bogus;", clip.Text);
        Assert.Contains(result.Warnings, warning => warning.Contains("REGION", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("Vertical", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("size", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("alignment 'start'", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("line numbers", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("Voice", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("Language", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("Ruby", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("Background colour", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("'custom'", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("character references", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("00:00.000 --> 00:01.000\ntext")]
    [InlineData("WEBVTT\n\n00:02.000 --> 00:01.000\ntext")]
    [InlineData("WEBVTT\n\n00:00.000 --> 00:02.000\nA <00:00:03.000>late")]
    [InlineData("WEBVTT\n\n00:00.000 --> 00:02.000\nA <00:00:01.500>b <00:00:01.000>c")]
    [InlineData("WEBVTT\n\n00:00.000 --> 00:02.000\nA <b")]
    [InlineData("WEBVTT\n\nNOTE only")]
    public void Import_RejectsInvalidFiles(string content)
    {
        Assert.Throws<InvalidDataException>(() => SubtitleWebVtt.Import(content, 25));
    }

    [Fact]
    public void Export_RoundTripsRunsColoursTimingAndPosition()
    {
        var clip = new TimelineClipProject
        {
            StartFrame = 51,
            DurationSeconds = 3,
            Text = "Fish & <chips>\nsecond",
            FontFamily = "Segoe UI",
            FontSize = 48,
            Colour = 0xFF00FFFF,
            TextBackgroundColour = 0xB0000000,
            SubtitleUpcomingColour = 0xFF404040,
            X = 0.3,
            Y = 0.2,
        };
        clip.SubtitleRuns =
        [
            new SubtitleRun { Text = "Fish ", Bold = true },
            new SubtitleRun { Text = "& ", Italic = true, RevealSeconds = 1 },
            new SubtitleRun { Text = "<chips>", Underline = true, Colour = 0xFFFF8000, RevealSeconds = 1.5 },
            new SubtitleRun { Text = "\nsecond", RevealSeconds = 2 },
        ];
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Subtitle, Clips = [clip] };

        SubtitleExportResult exported = SubtitleWebVtt.Export([track], 25);

        Assert.Empty(exported.Warnings);
        Assert.StartsWith("WEBVTT\n\nSTYLE\n::cue(.cff8000) { color: #ff8000; }\n::cue(:future) { color: #404040; }\n", exported.Content);
        Assert.Contains("00:00:02.000 --> 00:00:05.000 position:30%,center line:20%,center align:center", exported.Content);
        Assert.Contains("&lt;chips&gt;", exported.Content);
        Assert.Contains("<00:00:03.500>", exported.Content);

        SubtitleImportResult imported = SubtitleWebVtt.Import(exported.Content, 25);
        Assert.Empty(imported.Warnings);
        TimelineClipProject result = Assert.Single(Assert.Single(imported.Tracks).Clips);
        Assert.Equal(clip.StartFrame, result.StartFrame);
        Assert.Equal(clip.DurationSeconds, result.DurationSeconds, 6);
        Assert.Equal(clip.Text, result.Text);
        Assert.Equal(clip.Colour, result.Colour);
        Assert.Equal(clip.SubtitleUpcomingColour, result.SubtitleUpcomingColour);
        Assert.Equal(clip.X, result.X, 6);
        Assert.Equal(clip.Y, result.Y, 6);
        Assert.True(SubtitleRichText.RunsEqual(clip.SubtitleRuns, result.SubtitleRuns));
    }

    [Fact]
    public void Export_ReportsUnrepresentableStylingAndRejectsBlankLines()
    {
        var styled = new TimelineClipProject
        {
            StartFrame = 1, DurationSeconds = 1, Text = "Styled", FontFamily = "Arial", FontSize = 48,
            Colour = 0xFFFFFFFF, TextBackgroundColour = 0xB0000000, X = 0.5, Y = 0.88, FadeInSeconds = 0.2,
        };
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Subtitle, Clips = [styled] };
        SubtitleExportResult exported = SubtitleWebVtt.Export([track], 25);
        Assert.Contains(exported.Warnings, warning => warning.Contains("font", StringComparison.Ordinal));

        styled.Text = "First\n\nThird";
        Assert.Throws<InvalidDataException>(() => SubtitleWebVtt.Export([track], 25));
    }

    [Fact]
    public void Formats_DispatchByExtensionAndReportSrtLoss()
    {
        var clip = new TimelineClipProject
        {
            StartFrame = 1, DurationSeconds = 1, Text = "Hi there", FontFamily = "Segoe UI", FontSize = 48,
            Colour = 0xFFFFFFFF, TextBackgroundColour = 0xB0000000, X = 0.5, Y = 0.88,
            SubtitleRuns = [new SubtitleRun { Text = "Hi ", Bold = true }, new SubtitleRun { Text = "there" }],
        };
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Subtitle, Clips = [clip] };

        SubtitleExportResult srt = SubtitleFormats.Export(".SRT", [track], 25);
        Assert.Contains(srt.Warnings, warning => warning.Contains("word timing", StringComparison.Ordinal));
        Assert.Empty(SubtitleFormats.Import(".srt", srt.Content, 25).Warnings);
        Assert.Contains("<b>Hi </b>", SubtitleFormats.Export(".vtt", [track], 25).Content);
        Assert.Throws<InvalidDataException>(() => SubtitleFormats.Import(".txt", "x", 25));
    }
}
