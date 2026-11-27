using Xunit;

namespace InkMotion.Tests;

public sealed class SubtitleAssTests
{
    private const string StyleFormat =
        "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, " +
        "Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, " +
        "MarginV, Encoding\n";

    private const string EventFormat = "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n";

    private const string Sample =
        "\uFEFF[Script Info]\n; comment\nScriptType: v4.00+\nPlayResX: 1920\nPlayResY: 1080\n\n[V4+ Styles]\n" + StyleFormat +
        "Style: Default,Arial,54,&H00FFFFFF,&H0000FFFF,&H00000000,&H80000000,0,0,0,0,100,100,0,0,3,2,0,2,10,10,40,1\n\n" +
        "[Events]\n" + EventFormat +
        "Comment: 0,0:00:00.00,0:00:09.00,Default,,0,0,0,,hidden\n" +
        "Dialogue: 0,0:00:01.00,0:00:04.00,Default,Ana,0,0,0,,{\\k50}One\\N{\\k100\\b1}two{\\k50\\c&H0000FF&}!\n" +
        "Dialogue: 0,0:00:05.00,0:00:06.00,Default,,0,0,0,,{\\an8\\pos(960,100)\\fad(200,300)}{note}Top\\hcomma, kept\n";

    [Fact]
    public void Import_MapsStylesKaraokeOverridesAndEscapes()
    {
        SubtitleImportResult result = SubtitleAss.Import(Sample, 25);

        Assert.Empty(result.Warnings);
        TimelineClipProject[] clips = result.Tracks.SelectMany(track => track.Clips).OrderBy(clip => clip.StartFrame).ToArray();
        Assert.Equal(2, clips.Length);
        TimelineClipProject first = clips[0];
        Assert.Equal(26, first.StartFrame);
        Assert.Equal(3, first.DurationSeconds, 6);
        Assert.Equal("One\ntwo!", first.Text);
        Assert.Equal("Arial", first.FontFamily);
        Assert.Equal(54, first.FontSize, 6);
        Assert.Equal(0xFFFFFFFFu, first.Colour);
        Assert.Equal(0xFFFFFF00u, first.SubtitleUpcomingColour);
        Assert.Equal(0xFF000000u, first.TextBackgroundColour);
        Assert.Equal(0.5, first.X, 6);
        Assert.Equal((1080 - 40 - 54 * 1.2) / 1080, first.Y, 6);
        Assert.Collection(first.SubtitleRuns,
            run => { Assert.Equal("One\n", run.Text); Assert.False(run.Bold); Assert.Equal(0, run.RevealSeconds!.Value, 6); },
            run => { Assert.Equal("two", run.Text); Assert.True(run.Bold); Assert.Null(run.Colour); Assert.Equal(0.5, run.RevealSeconds!.Value, 6); },
            run => { Assert.Equal("!", run.Text); Assert.True(run.Bold); Assert.Equal(0xFFFF0000u, run.Colour); Assert.Equal(1.5, run.RevealSeconds!.Value, 6); });

        TimelineClipProject second = clips[1];
        Assert.Equal("Top\u00A0comma, kept", second.Text);
        Assert.Empty(second.SubtitleRuns);
        Assert.Equal(0.5, second.X, 6);
        Assert.Equal((100 + 54 * 1.2 / 2) / 1080, second.Y, 6);
        Assert.Equal(0.2, second.FadeInSeconds, 6);
        Assert.Equal(0.3, second.FadeOutSeconds, 6);
    }

    [Fact]
    public void Import_ReportsUnsupportedConstructsAndSkipsDrawings()
    {
        const string content = "[Script Info]\nScriptType: v4.00+\n\n[V4+ Styles]\n" + StyleFormat +
            "Style: Default,Arial,20,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,1,120,100,0,0,1,2,0,1,10,10,10,1\n\n" +
            "[Events]\n" + EventFormat +
            "Dialogue: 0,0:00:00.00,0:00:02.00,Default,,0,0,0,Scroll up;10;100,{\\blur2\\fs999\\zz}Big\n" +
            "Dialogue: 0,0:00:02.00,0:00:03.00,Default,,0,0,0,,{\\p1}m 0 0 l 10 10{\\p0}\n" +
            "Picture: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,image.png\n\n[Fonts]\nfontname: a.ttf\n";

        SubtitleImportResult result = SubtitleAss.Import(content, 25);

        TimelineClipProject clip = Assert.Single(Assert.Single(result.Tracks).Clips);
        Assert.Equal("Big", clip.Text);
        Assert.Equal(VideoTimelineEditing.MaximumFontSize, clip.FontSize);
        Assert.Equal(0u, clip.TextBackgroundColour);
        string[] expected =
        [
            "Strikeout", "scaling", "Event effects", "\\blur", "Unknown override tag \\zz", "Font sizes outside",
            "Vector drawings", "Picture events", "Embedded fonts", "Left- and right-aligned", "outlines",
        ];
        foreach (string fragment in expected)
        {
            Assert.Contains(result.Warnings, warning => warning.Contains(fragment, StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("[Script Info]\nScriptType: v4.00\n\n[V4 Styles]\n")]
    [InlineData("[Script Info]\nScriptType: v4.00\n\n[Events]\n" + EventFormat + "Dialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,x\n")]
    [InlineData("[Script Info]\n\n[Events]\n" + EventFormat + "Dialogue: 0,0:00:00.0,0:00:01.00,Default,,0,0,0,,x\n")]
    [InlineData("[Script Info]\n\n[Events]\n" + EventFormat + "Dialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,{\\pos(1)}x\n")]
    [InlineData("[Script Info]\n\n[Events]\n" + EventFormat)]
    [InlineData("WEBVTT\n\n00:00.000 --> 00:01.000\nx\n")]
    public void Import_RejectsUnsupportedOrInvalidFiles(string content)
    {
        Assert.Throws<InvalidDataException>(() => SubtitleAss.Import(content, 25));
    }

    [Fact]
    public void Import_RejectsSsaExtension()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => SubtitleFormats.Import(".ssa", "[Script Info]", 25));
        Assert.Contains("SubStation Alpha", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_RoundTripsTimingStylingAndPosition()
    {
        IReadOnlyList<TimelineTrackProject> tracks = SubtitleAss.Import(Sample, 25).Tracks;

        SubtitleExportResult exported = SubtitleFormats.Export(".ass", tracks, 25);

        Assert.Empty(exported.Warnings);
        Assert.Contains("PlayResY: 1080", exported.Content, StringComparison.Ordinal);
        Assert.Contains(@"{\r\k50}One\N{\r\k100\b1}two{\r\k150\b1\c&H0000FF&\1a&H00&}!", exported.Content, StringComparison.Ordinal);
        Assert.Contains(@"\fad(200,300)}Top\hcomma, kept", exported.Content, StringComparison.Ordinal);

        SubtitleImportResult reimported = SubtitleFormats.Import(".ass", exported.Content, 25);
        Assert.Empty(reimported.Warnings);
        TimelineClipProject[] original = tracks.SelectMany(track => track.Clips).OrderBy(clip => clip.StartFrame).ToArray();
        TimelineClipProject[] roundTrip = reimported.Tracks.SelectMany(track => track.Clips).OrderBy(clip => clip.StartFrame).ToArray();
        Assert.Equal(original.Length, roundTrip.Length);
        for (int index = 0; index < original.Length; index++)
        {
            Assert.Equal(original[index].StartFrame, roundTrip[index].StartFrame);
            Assert.Equal(original[index].DurationSeconds, roundTrip[index].DurationSeconds, 6);
            Assert.Equal(original[index].Text, roundTrip[index].Text);
            Assert.Equal(original[index].FontSize, roundTrip[index].FontSize, 3);
            Assert.Equal(original[index].Colour, roundTrip[index].Colour);
            Assert.Equal(original[index].SubtitleUpcomingColour, roundTrip[index].SubtitleUpcomingColour);
            Assert.Equal(original[index].TextBackgroundColour, roundTrip[index].TextBackgroundColour);
            Assert.Equal(original[index].X, roundTrip[index].X, 3);
            Assert.Equal(original[index].Y, roundTrip[index].Y, 3);
            Assert.Equal(original[index].FadeInSeconds, roundTrip[index].FadeInSeconds, 6);
            Assert.Equal(original[index].FadeOutSeconds, roundTrip[index].FadeOutSeconds, 6);
            Assert.True(SubtitleRichText.RunsEqual(original[index].SubtitleRuns, roundTrip[index].SubtitleRuns));
        }
    }

    [Theory]
    [InlineData("a {b}")]
    [InlineData(@"path C:\New")]
    public void Export_RejectsTextAssWouldReinterpret(string text)
    {
        IReadOnlyList<TimelineTrackProject> tracks = SubtitleSrt.CreateTracks([new SubtitleCueText(1, 2, text)], 25);

        Assert.Throws<InvalidDataException>(() => SubtitleFormats.Export(".ass", tracks, 25));
    }
}
