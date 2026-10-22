using Xunit;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using System.Numerics;

namespace InkMotion.Tests;

public sealed class InkTextEditingTests
{
    [Fact]
    public void CanvasHitTestSelectsTopmostVisibleOverlayWithoutChangingTheProject()
    {
        using var device = new CanvasDevice(true);
        var first = new InkTextOverlay { Text = "Sample", FontSize = 48 };
        var top = new InkTextOverlay { Text = "Sample", FontSize = 48, Colour = 0x80FF0000 };
        var transparent = new InkTextOverlay { Text = "Sample", FontSize = 48, Colour = 0 };
        var project = new EditorProject { Frames = [new() { TextOverlays = [first, top, transparent] }] };
        string before = EditorProjectSerialiser.Serialise(project);
        Windows.Foundation.Rect bounds = TimelineCompositor.GetInkTextBounds(device, top, 400, 200);
        double x = bounds.X + bounds.Width / 2;
        double y = bounds.Y + bounds.Height / 2;

        Assert.Same(top, InkTextEditing.HitTest(device, project.Frames[0], 400, 200, x, y));
        Assert.Null(InkTextEditing.HitTest(device, project.Frames[0], 400, 200, 1, 1));
        Assert.Equal(before, EditorProjectSerialiser.Serialise(project));
        project.Frames[0].TextOverlays.Remove(top);
        Assert.Same(first, InkTextEditing.HitTest(device, project.Frames[0], 400, 200, x, y));
    }

    [Theory]
    [InlineData(0.5, 0.5, 48, "Segoe UI", "Sample")]
    [InlineData(0.2, 0.8, 24, "Arial", "Text\nTwo lines")]
    [InlineData(0, 0, 72, "Georgia", "Edge")]
    public void SharedTextLayoutPreservesExistingRenderingAndMatchesDrawBounds(
        double x, double y, double size, string font, string text)
    {
        using var device = new CanvasDevice(true);
        using var previous = new CanvasRenderTarget(device, 400, 200, 96);
        using var current = new CanvasRenderTarget(device, 400, 200, 96);
        var overlay = new InkTextOverlay { Text = text, X = x, Y = y, FontSize = size, FontFamily = font };
        using (CanvasDrawingSession session = previous.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Transparent);
            using var format = new CanvasTextFormat
            {
                FontFamily = font,
                FontSize = (float)size,
                HorizontalAlignment = CanvasHorizontalAlignment.Center,
                VerticalAlignment = CanvasVerticalAlignment.Center,
            };
            session.DrawText(text, new Vector2((float)(x * 400), (float)(y * 200)), Microsoft.UI.Colors.White, format);
        }
        using (CanvasDrawingSession session = current.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Transparent);
            TimelineCompositor.DrawInkText(session, overlay, 1, 400, 200, 1);
        }

        Assert.Equal(previous.GetPixelBytes(), current.GetPixelBytes());
        Windows.Foundation.Rect bounds = TimelineCompositor.GetInkTextBounds(device, overlay, 400, 200);
        byte[] pixels = current.GetPixelBytes();
        int visible = 0;
        for (int row = 0; row < 200; row++)
        {
            for (int column = 0; column < 400; column++)
            {
                if (pixels[(row * 400 + column) * 4 + 3] > 0)
                {
                    visible++;
                    Assert.InRange(column, Math.Floor(bounds.Left) - 1, Math.Ceiling(bounds.Right) + 1);
                    Assert.InRange(row, Math.Floor(bounds.Top) - 1, Math.Ceiling(bounds.Bottom) + 1);
                }
            }
        }

        Assert.True(visible > 0);
    }

    [Fact]
    public void CanvasTextSelectionRejectsInvalidCoordinatesAndIgnoresEmptyText()
    {
        using var device = new CanvasDevice(true);
        var frame = new EditorFrame { TextOverlays = [new() { Text = " " }] };
        Assert.Null(InkTextEditing.HitTest(device, frame, 400, 200, 200, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            InkTextEditing.HitTest(device, frame, 400, 200, double.NaN, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            InkTextEditing.HitTest(device, frame, 400, 200, 401, 100));
    }

    [Fact]
    public void UpdatesOnlyTheSelectedOverlayAndPersistsAllFields()
    {
        var first = new InkTextOverlay { Text = "First" };
        var second = new InkTextOverlay { Text = "Second" };
        var project = new EditorProject { Frames = [new() { TextOverlays = [first, second] }] };
        string before = EditorProjectSerialiser.Serialise(project);
        Assert.True(InkTextEditing.Update(project.Frames[0], first, "Changed", "Georgia", 96, 0.2, 0.8, 0x80123456));
        Assert.Equal("Second", second.Text);
        EditorProject saved = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        InkTextOverlay text = saved.Frames[0].TextOverlays[0];
        Assert.Equal("Changed", text.Text);
        Assert.Equal("Georgia", text.FontFamily);
        Assert.Equal(96, text.FontSize);
        Assert.Equal(0.2, text.X);
        Assert.Equal(0.8, text.Y);
        Assert.Equal(0x80123456u, text.Colour);
        Assert.Equal("First", EditorProjectSerialiser.Deserialise(before).Frames[0].TextOverlays[0].Text);
    }

    [Fact]
    public void RejectsStaleSelectionAndNoOpEdits()
    {
        var selected = new InkTextOverlay { Text = "Existing" };
        var frame = new EditorFrame { TextOverlays = [selected] };
        Assert.False(InkTextEditing.Update(frame, selected, "Existing", "Segoe UI", 48, 0.5, 0.5, 0xFFFFFFFF));
        Assert.Throws<InvalidOperationException>(() =>
            InkTextEditing.Update(new(), selected, "New", "Arial", 48, 0.5, 0.5, 0xFFFFFFFF));
        Assert.Equal("Existing", selected.Text);
    }

    [Theory]
    [InlineData("", "Arial", 48, 0.5, 0.5)]
    [InlineData("New", "", 48, 0.5, 0.5)]
    [InlineData("New", "Arial", 5, 0.5, 0.5)]
    [InlineData("New", "Arial", 513, 0.5, 0.5)]
    [InlineData("New", "Arial", double.NaN, 0.5, 0.5)]
    [InlineData("New", "Arial", 48, -0.1, 0.5)]
    [InlineData("New", "Arial", 48, 0.5, 1.1)]
    [InlineData("New", "Arial", 48, double.PositiveInfinity, 0.5)]
    public void InvalidEditsDoNotMutateTheOverlay(string content, string font, double size, double x, double y)
    {
        var selected = new InkTextOverlay { Text = "Existing" };
        var project = new EditorProject { Frames = [new() { TextOverlays = [selected] }] };
        string before = EditorProjectSerialiser.Serialise(project);
        Assert.Throws<ArgumentException>(() =>
            InkTextEditing.Update(project.Frames[0], selected, content, font, size, x, y, 0));
        Assert.Equal(before, EditorProjectSerialiser.Serialise(project));
    }

    [Fact]
    public void SupportsCanvasEdgesAndMaximumContentAndRejectsOversizeContent()
    {
        var selected = new InkTextOverlay { Text = "Existing" };
        var frame = new EditorFrame { TextOverlays = [selected] };
        Assert.True(InkTextEditing.Update(frame, selected, new string('a', 2048), new string('f', 128), 512, 0, 1, 0));
        Assert.Equal(0u, selected.Colour);
        Assert.Throws<ArgumentException>(() =>
            InkTextEditing.Update(frame, selected, new string('a', 2049), "Arial", 48, 0, 1, 0));
        Assert.Throws<ArgumentException>(() =>
            InkTextEditing.Update(frame, selected, "Text", new string('f', 129), 48, 0, 1, 0));
    }
}
