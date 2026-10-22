using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using System.Globalization;
using Windows.Foundation;

namespace InkMotion;

public static class InkTextEditing
{
    public static InkTextOverlay? HitTest(ICanvasResourceCreator device, EditorFrame frame,
        double width, double height, double x, double y)
    {
        InkColourSampling.GetPixel(x, y, width, height);
        return frame.TextOverlays.LastOrDefault(overlay =>
            HasVisibleText(overlay) &&
            TimelineCompositor.GetInkTextBounds(device, overlay, (float)width, (float)height)
                .Contains(new Windows.Foundation.Point(x, y)));
    }

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

        if (selected.Text != text)
        {
            selected.StyleRanges.Clear();
        }
        selected.Text = text;
        selected.FontFamily = font;
        selected.FontSize = size;
        selected.X = x;
        selected.Y = y;
        selected.Colour = colour;
        return true;
    }

    public static bool HasVisibleText(InkTextOverlay overlay) =>
        HasVisibleCharacters(overlay, 0, overlay.Text.Length);

    private static bool HasVisibleCharacters(InkTextOverlay overlay, int start, int length)
    {
        int end = start + length;
        int covered = 0;
        foreach (InkTextStyleRange range in overlay.StyleRanges)
        {
            int overlap = Math.Max(0, Math.Min(end, range.Start + range.Length) - Math.Max(start, range.Start));
            if (overlap > 0 && (range.Colour >> 24) != 0)
            {
                return true;
            }
            covered += overlap;
        }
        return covered < length && (overlay.Colour >> 24) != 0;
    }

    public static int[] CharacterBoundaries(string text) =>
        [.. StringInfo.ParseCombiningCharacters(text), text.Length];

    public static void ValidateStyles(InkTextOverlay overlay)
    {
        if (overlay.StyleRanges is null || overlay.StyleRanges.Count > overlay.Text.Length)
        {
            throw new InvalidDataException("Cel text has an invalid style-range list.");
        }
        HashSet<int> boundaries = CharacterBoundaries(overlay.Text).ToHashSet();
        int previousEnd = 0;
        foreach (InkTextStyleRange? range in overlay.StyleRanges)
        {
            if (range is null || range.Start < previousEnd || range.Length < 1 ||
                range.Start > overlay.Text.Length - range.Length ||
                !boundaries.Contains(range.Start) || !boundaries.Contains(range.Start + range.Length) ||
                string.IsNullOrWhiteSpace(range.FontFamily) || range.FontFamily.Length > 128 ||
                !double.IsFinite(range.FontSize) || range.FontSize is < 6 or > 512)
            {
                throw new InvalidDataException("Cel text style ranges must be ordered, non-overlapping and aligned to complete characters, with valid fonts and sizes.");
            }
            previousEnd = range.Start + range.Length;
        }
    }

    public static InkTextStyleRange CopyStyle(InkTextStyleRange style, int start, int length) => new()
    {
        Start = start, Length = length, FontFamily = style.FontFamily, FontSize = style.FontSize,
        Colour = style.Colour, Bold = style.Bold, Italic = style.Italic, Underline = style.Underline,
    };

    public static bool ApplyStyle(EditorFrame frame, InkTextOverlay overlay, int firstCharacter, int lastCharacter,
        InkTextStyleRange style, bool clear = false)
    {
        if (!frame.TextOverlays.Contains(overlay))
        {
            throw new InvalidOperationException("Select cel text before applying character styles.");
        }
        int[] boundaries = CharacterBoundaries(overlay.Text);
        if (firstCharacter < 1 || lastCharacter < firstCharacter || lastCharacter >= boundaries.Length)
        {
            throw new ArgumentException("Choose an inclusive range of existing character numbers, starting at 1.");
        }
        int start = boundaries[firstCharacter - 1];
        int end = boundaries[lastCharacter];
        var replacement = new List<InkTextStyleRange>();
        foreach (InkTextStyleRange range in overlay.StyleRanges)
        {
            int rangeEnd = range.Start + range.Length;
            if (rangeEnd <= start || range.Start >= end)
            {
                replacement.Add(CopyStyle(range, range.Start, range.Length));
                continue;
            }
            if (range.Start < start)
            {
                replacement.Add(CopyStyle(range, range.Start, start - range.Start));
            }
            if (rangeEnd > end)
            {
                replacement.Add(CopyStyle(range, end, rangeEnd - end));
            }
        }
        if (!clear)
        {
            replacement.Add(CopyStyle(style, start, end - start));
        }
        replacement.Sort((a, b) => a.Start.CompareTo(b.Start));
        ValidateStyles(new InkTextOverlay { Text = overlay.Text, StyleRanges = replacement });
        bool Same(InkTextStyleRange a, InkTextStyleRange b) =>
            a.Start == b.Start && a.Length == b.Length && a.FontFamily == b.FontFamily &&
            a.FontSize == b.FontSize && a.Colour == b.Colour && a.Bold == b.Bold &&
            a.Italic == b.Italic && a.Underline == b.Underline;
        if (replacement.Count == overlay.StyleRanges.Count &&
            replacement.Zip(overlay.StyleRanges).All(pair => Same(pair.First, pair.Second)))
        {
            return false;
        }
        overlay.StyleRanges = replacement;
        return true;
    }

    public static (int First, int Last)? HitCharacter(ICanvasResourceCreator device, InkTextOverlay overlay,
        double width, double height, double x, double y)
    {
        InkColourSampling.GetPixel(x, y, width, height);
        using CanvasTextLayout layout = TimelineCompositor.CreateInkTextLayout(device, overlay, (float)overlay.FontSize);
        if (!layout.HitTest((float)(x - overlay.X * width), (float)(y - overlay.Y * height),
            out CanvasTextLayoutRegion region))
        {
            return null;
        }
        if (!HasVisibleCharacters(overlay, region.CharacterIndex, region.CharacterCount))
        {
            return null;
        }
        int[] boundaries = CharacterBoundaries(overlay.Text);
        int first = Array.FindLastIndex(boundaries, boundary => boundary <= region.CharacterIndex);
        int last = Array.FindIndex(boundaries, boundary => boundary >= region.CharacterIndex + region.CharacterCount);
        return first >= 0 && last > first ? (first + 1, last) : null;
    }

    public static IReadOnlyList<Rect> GetRangeBounds(ICanvasResourceCreator device, InkTextOverlay overlay,
        float width, float height, int first, int last)
    {
        int[] boundaries = CharacterBoundaries(overlay.Text);
        if (first < 1 || last < first || last >= boundaries.Length)
        {
            return [];
        }
        using CanvasTextLayout layout = TimelineCompositor.CreateInkTextLayout(device, overlay, (float)overlay.FontSize);
        return layout.GetCharacterRegions(boundaries[first - 1], boundaries[last] - boundaries[first - 1])
            .Select(region => new Rect(region.LayoutBounds.X + overlay.X * width,
                region.LayoutBounds.Y + overlay.Y * height, region.LayoutBounds.Width, region.LayoutBounds.Height)).ToArray();
    }
}
