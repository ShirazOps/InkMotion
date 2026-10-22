using Microsoft.Graphics.Canvas;
using Xunit;

namespace InkMotion.Tests;

public sealed class InkTextStyleTests
{
    private static InkTextStyleRange Style(uint colour = 0xFFFF0000) => new()
    {
        FontFamily = "Arial", FontSize = 48, Colour = colour, Bold = true, Italic = true, Underline = true,
    };

    [Fact]
    public void OverlappingStylesSplitExistingRangesAndResetRestoresBaseProperties()
    {
        var overlay = new InkTextOverlay { Text = "abcdefghij" };
        var frame = new EditorFrame { TextOverlays = [overlay] };
        Assert.True(InkTextEditing.ApplyStyle(frame, overlay, 2, 8, Style()));
        Assert.True(InkTextEditing.ApplyStyle(frame, overlay, 4, 6, Style(0xFF00FF00)));
        Assert.Equal(new[] { (1, 2), (3, 3), (6, 2) }, overlay.StyleRanges.Select(r => (r.Start, r.Length)));
        Assert.Equal(new uint[] { 0xFFFF0000, 0xFF00FF00, 0xFFFF0000 }, overlay.StyleRanges.Select(r => r.Colour));
        Assert.False(InkTextEditing.ApplyStyle(frame, overlay, 4, 6, Style(0xFF00FF00)));
        Assert.True(InkTextEditing.ApplyStyle(frame, overlay, 3, 7, Style(), clear: true));
        Assert.Equal(new[] { (1, 1), (7, 1) }, overlay.StyleRanges.Select(r => (r.Start, r.Length)));
        Assert.True(InkTextEditing.ApplyStyle(frame, overlay, 1, 10, Style(), clear: true));
        Assert.Empty(overlay.StyleRanges);
        Assert.False(InkTextEditing.ApplyStyle(frame, overlay, 1, 10, Style(), clear: true));
    }

    [Fact]
    public void CharacterNumbersRespectCombiningMarksEmojiAndJoinedSequences()
    {
        var overlay = new InkTextOverlay { Text = "Ae\u0301\U0001F600\U0001F469\u200D\U0001F4BBB" };
        var frame = new EditorFrame { TextOverlays = [overlay] };
        Assert.Equal(6, InkTextEditing.CharacterBoundaries(overlay.Text).Length);
        InkTextEditing.ApplyStyle(frame, overlay, 2, 4, Style());
        InkTextStyleRange range = Assert.Single(overlay.StyleRanges);
        Assert.Equal("e\u0301\U0001F600\U0001F469\u200D\U0001F4BB", overlay.Text.Substring(range.Start, range.Length));
        Assert.Equal(1, range.Start);
        InkTextEditing.ValidateStyles(overlay);
        range.Start++;
        range.Length--;
        Assert.Throws<InvalidDataException>(() => InkTextEditing.ValidateStyles(overlay));
    }

    [Fact]
    public void StylesCloneIndependentlyAndSurviveProjectSnapshots()
    {
        var overlay = new InkTextOverlay { Text = "Styled text" };
        var project = new EditorProject { Frames = [new() { TextOverlays = [overlay] }] };
        string before = EditorProjectSerialiser.Serialise(project);
        InkTextEditing.ApplyStyle(project.Frames[0], overlay, 1, 6, Style());
        EditorFrame clone = InkEditing.Clone(project.Frames[0]);
        clone.TextOverlays[0].StyleRanges[0].FontSize = 100;
        Assert.Equal(48, overlay.StyleRanges[0].FontSize);
        EditorProject restored = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        InkTextStyleRange range = Assert.Single(restored.Frames[0].TextOverlays[0].StyleRanges);
        Assert.Equal("Arial", range.FontFamily);
        Assert.True(range.Bold && range.Italic && range.Underline);
        Assert.Equal(0xFFFF0000u, range.Colour);
        Assert.Empty(EditorProjectSerialiser.Deserialise(before).Frames[0].TextOverlays[0].StyleRanges);
        Assert.True(InkTextEditing.Update(project.Frames[0], overlay, overlay.Text, "Georgia", 64, 0.2, 0.4, 0xFF00FF00));
        Assert.Single(overlay.StyleRanges);
        Assert.True(InkTextEditing.Update(project.Frames[0], overlay, "New text", "Georgia", 64, 0.2, 0.4, 0xFF00FF00));
        Assert.Empty(overlay.StyleRanges);
    }

    [Fact]
    public void InvalidAndStaleRangeUpdatesAreAtomic()
    {
        var overlay = new InkTextOverlay { Text = "Sample" };
        var project = new EditorProject { Frames = [new() { TextOverlays = [overlay] }] };
        InkTextEditing.ApplyStyle(project.Frames[0], overlay, 1, 6, Style());
        string before = EditorProjectSerialiser.Serialise(project);
        Assert.Throws<ArgumentException>(() => InkTextEditing.ApplyStyle(project.Frames[0], overlay, 0, 4, Style()));
        Assert.Throws<ArgumentException>(() => InkTextEditing.ApplyStyle(project.Frames[0], overlay, 4, 3, Style()));
        Assert.Throws<ArgumentException>(() => InkTextEditing.ApplyStyle(project.Frames[0], overlay, 1, 7, Style()));
        var invalid = Style();
        invalid.FontSize = double.NaN;
        Assert.Throws<InvalidDataException>(() => InkTextEditing.ApplyStyle(project.Frames[0], overlay, 2, 3, invalid));
        Assert.Throws<InvalidOperationException>(() => InkTextEditing.ApplyStyle(new(), overlay, 1, 2, Style()));
        Assert.Equal(before, EditorProjectSerialiser.Serialise(project));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    [InlineData(0, int.MaxValue)]
    [InlineData(5, 2)]
    public void SavedMalformedStyleRangesAreRejected(int start, int length)
    {
        var overlay = new InkTextOverlay { Text = "Sample", StyleRanges = [new() { Start = start, Length = length }] };
        var project = new EditorProject { Frames = [new() { TextOverlays = [overlay] }] };
        Assert.Throws<InvalidDataException>(() => EditorProjectSerialiser.Serialise(project));
    }

    [Fact]
    public void VersionThirtyEightTextMigratesWithoutStyleChanges()
    {
        EditorProject project = EditorProjectSerialiser.Deserialise(
            """{"Version":38,"Frames":[{"Number":1,"TextOverlays":[{"Text":"Sample"}]}],"SelectedFrameIndex":0}""");
        Assert.Equal(EditorProject.CurrentVersion, project.Version);
        Assert.Empty(project.Frames[0].TextOverlays[0].StyleRanges);
    }

    [Fact]
    public void SharedLayoutUsesRangeFontSizeEmphasisAndAlphaWithoutChangingOtherCharacters()
    {
        using var device = new CanvasDevice(true);
        var overlay = new InkTextOverlay { Text = "MMMM", Colour = 0xFFFFFFFF };
        var frame = new EditorFrame { TextOverlays = [overlay] };
        InkTextStyleRange style = Style(0x80FF0000);
        style.FontSize = 72;
        InkTextEditing.ApplyStyle(frame, overlay, 2, 3, style);
        using var layout = TimelineCompositor.CreateInkTextLayout(device, overlay, 48, 0.5);
        Assert.Equal(48, layout.GetFontSize(0));
        Assert.Equal(72, layout.GetFontSize(1));
        Assert.Equal(700, layout.GetFontWeight(1).Weight);
        Assert.True(layout.GetUnderline(1));
        Assert.Equal(Windows.UI.Text.FontStyle.Italic, layout.GetFontStyle(1));
        using var target = new CanvasRenderTarget(device, 400, 200, 96);
        using (CanvasDrawingSession session = target.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Transparent);
            TimelineCompositor.DrawInkText(session, overlay, 0.5, 400, 200, 1);
        }
        byte[] pixels = target.GetPixelBytes();
        var redAlphas = Enumerable.Range(0, pixels.Length / 4)
            .Where(index => pixels[index * 4 + 2] > 0 && pixels[index * 4] == 0)
            .Select(index => pixels[index * 4 + 3]).ToArray();
        Assert.NotEmpty(redAlphas);
        Assert.InRange(redAlphas.Max(), (byte)63, (byte)64);
        Assert.Contains(Enumerable.Range(0, pixels.Length / 4), index => pixels[index * 4] > 100);
    }

    [Fact]
    public void CanvasHitTestingAndHighlightBoundsUseStyledGlyphClusters()
    {
        using var device = new CanvasDevice(true);
        var overlay = new InkTextOverlay { Text = "ABCDE" };
        var frame = new EditorFrame { TextOverlays = [overlay] };
        InkTextEditing.ApplyStyle(frame, overlay, 2, 4, Style());
        foreach (Windows.Foundation.Rect region in InkTextEditing.GetRangeBounds(device, overlay, 400, 200, 2, 2))
        {
            var hit = InkTextEditing.HitCharacter(device, overlay, 400, 200,
                region.X + region.Width / 2, region.Y + region.Height / 2);
            Assert.NotNull(hit);
            Assert.Equal((2, 2), hit.Value);
        }
        Assert.Empty(InkTextEditing.GetRangeBounds(device, overlay, 400, 200, 0, 9));
        Assert.Null(InkTextEditing.HitCharacter(device, overlay, 400, 200, 0, 0));
        InkTextEditing.ApplyStyle(frame, overlay, 1, 5, Style(0));
        Assert.False(InkTextEditing.HasVisibleText(overlay));
        InkTextEditing.ApplyStyle(frame, overlay, 2, 2, Style());
        overlay.Colour = 0;
        Assert.True(InkTextEditing.HasVisibleText(overlay));
    }

    [Fact]
    public void StyledCharactersMatchColourSamplingAndRetainTheirColourInVideoExport()
    {
        if (!VideoExportTests.HasH264Encoder)
        {
            return;
        }
        string output = Path.Combine(Path.GetTempPath(), $"inkmotion-text-ranges-{Guid.NewGuid():N}.mp4");
        try
        {
            using var device = new CanvasDevice(true);
            var overlay = new InkTextOverlay { Text = "MMMM", FontSize = 100 };
            var project = new EditorProject { Frames = [new() { IsKeyframe = true, ExposureFrames = 6, TextOverlays = [overlay] }] };
            InkTextStyleRange style = Style();
            style.FontSize = 100;
            InkTextEditing.ApplyStyle(project.Frames[0], overlay, 2, 3, style);
            using var target = new CanvasRenderTarget(device, 400, 200, 96);
            using (CanvasDrawingSession session = target.CreateDrawingSession())
            {
                session.Clear(Microsoft.UI.Colors.Transparent);
                TimelineCompositor.DrawInkFrame(session, project, 0, 400, 200, 1);
            }
            byte[] pixels = target.GetPixelBytes();
            int index = Enumerable.Range(0, pixels.Length / 4)
                .First(i => pixels[i * 4 + 2] == 255 && pixels[i * 4] == 0 && pixels[i * 4 + 3] == 255);
            Assert.Equal(0xFFFF0000u, InkColourSampling.Sample(device, project, 0, 400, 200, index % 400, index / 400));
            VideoExporter.Export(new(project, VideoExportProfile.Create(ExportResolution.P720, 24),
                VideoExportCodec.H264, output, 400, PreferHardware: false), null, CancellationToken.None);
            using var reader = new MediaFoundationVideoReader(output);
            Assert.True(reader.TryGetFrame(0));
            Assert.Contains(Enumerable.Range(0, reader.Pixels.Length / 4),
                i => reader.Pixels[i * 4 + 2] > 180 && reader.Pixels[i * 4] < 60 && reader.Pixels[i * 4 + 1] < 60);
        }
        finally
        {
            File.Delete(output);
        }
    }
}
