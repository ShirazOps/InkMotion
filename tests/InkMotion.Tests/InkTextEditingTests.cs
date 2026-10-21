using Xunit;

namespace InkMotion.Tests;

public sealed class InkTextEditingTests
{
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
