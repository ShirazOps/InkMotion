namespace InkMotion;

public static class InkTextEditing
{
    public static bool Update(EditorFrame frame, InkTextOverlay selected, string text, string font,
        double size, double x, double y, uint colour)
    {
        if (!frame.TextOverlays.Contains(selected))
        {
            throw new InvalidOperationException("Select a text overlay on the current cel.");
        }
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2048 ||
            string.IsNullOrWhiteSpace(font) || font.Length > 128 ||
            !double.IsFinite(size) || size is < 6 or > 512 ||
            !double.IsFinite(x) || x is < 0 or > 1 || !double.IsFinite(y) || y is < 0 or > 1)
        {
            throw new ArgumentException("Use non-empty text, a valid font, size 6-512 and positions within the canvas.");
        }
        if (selected.Text == text && selected.FontFamily == font && selected.FontSize == size &&
            selected.X == x && selected.Y == y && selected.Colour == colour)
        {
            return false;
        }

        selected.Text = text;
        selected.FontFamily = font;
        selected.FontSize = size;
        selected.X = x;
        selected.Y = y;
        selected.Colour = colour;
        return true;
    }
}
