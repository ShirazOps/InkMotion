using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Graphics.Canvas;

namespace InkMotion;

public sealed partial class MainWindow
{
    private EditorFrame? _textInspectorFrame;
    private InkTextOverlay[] _textInspectorItems = [];
    private bool _updatingTextInspector;
    private bool _isInkTextPicker;
    private bool _isInkTextRangePicker;
    private InkTextOverlay? _rangeAnchorOverlay;
    private (int First, int Last)? _rangeAnchor;

    private void SelectInkTextOnCanvas_Click(object sender, RoutedEventArgs e)
    {
        if (IsOriginalPreview)
        {
            StatusText.Text = "Switch to Modified preview before selecting cel text on the canvas.";
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        ClearInkSelection();
        _isInkTextPicker = true;
        _isInkTextRangePicker = sender is FrameworkElement { Tag: "range" };
        _rangeAnchorOverlay = null;
        _rangeAnchor = null;
        _isInkBucketFill = false;
        _isInkColourSampler = false;
        _isInkLasso = false;
        _isEraser = false;
        _inkShape = null;
        InkSurface.Invalidate();
        StatusText.Text = _isInkTextRangePicker
            ? "Click the first then last character on the same cel text overlay. Reverse selection is allowed; other overlays and drawings are unchanged."
            : "Click a cel text overlay to select it for editing. Topmost visible text wins; drawing layers and timeline titles are excluded.";
    }

    private void SelectInkTextAtPoint(double x, double y)
    {
        PausePlayback();
        FinishActiveEdit();
        bool awaitingEnd = false;
        try
        {
            if (IsOriginalPreview)
            {
                StatusText.Text = "Switch to Modified preview before selecting cel text.";
                return;
            }
            InkTextOverlay? overlay = InkTextEditing.HitTest(InkSurface, CurrentFrame,
                InkSurface.ActualWidth, InkSurface.ActualHeight, x, y);
            if (_isInkTextRangePicker)
            {
                if (_rangeAnchorOverlay is not null && !CurrentFrame.TextOverlays.Contains(_rangeAnchorOverlay))
                {
                    StatusText.Text = "Cel text changed during range selection. Select character range again.";
                    return;
                }
                if (_rangeAnchorOverlay is not null && CurrentFrame.TextOverlays.Contains(_rangeAnchorOverlay))
                {
                    overlay = _rangeAnchorOverlay;
                }
                else
                {
                    overlay = CurrentFrame.TextOverlays.LastOrDefault(text =>
                        InkTextEditing.HitCharacter(InkSurface, text, InkSurface.ActualWidth,
                            InkSurface.ActualHeight, x, y) is not null);
                }
                (int First, int Last)? hit = overlay is null ? null : InkTextEditing.HitCharacter(InkSurface, overlay,
                    InkSurface.ActualWidth, InkSurface.ActualHeight, x, y);
                if (overlay is null || hit is null)
                {
                    StatusText.Text = "No character on the selected cel text at that point. Select character range again to retry.";
                    return;
                }
                if (_rangeAnchor is null)
                {
                    RefreshInkTextSelection();
                    InkTextOverlaySelector.SelectedItem = InkTextOverlaySelector.Items.OfType<ComboBoxItem>()
                        .FirstOrDefault(item => ReferenceEquals(item.Tag, overlay));
                    _rangeAnchorOverlay = overlay;
                    _rangeAnchor = hit;
                    InkTextFirstCharacterBox.Value = hit.Value.First;
                    InkTextLastCharacterBox.Value = hit.Value.Last;
                    awaitingEnd = true;
                    StatusText.Text = "First character selected. Click the last character on this same overlay.";
                    return;
                }
                InkTextFirstCharacterBox.Value = Math.Min(_rangeAnchor.Value.First, hit.Value.First);
                InkTextLastCharacterBox.Value = Math.Max(_rangeAnchor.Value.Last, hit.Value.Last);
                StatusText.Text = "Character range selected. Choose font, size, colour and emphasis, then Style character range. Pen selected.";
                return;
            }
            RefreshInkTextSelection();
            InkTextOverlaySelector.SelectedItem = InkTextOverlaySelector.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => ReferenceEquals(item.Tag, overlay));
            StatusText.Text = overlay is null
                ? "No visible cel text at this point. Pen selected; project unchanged."
                : "Cel text selected. Edit its properties in the inspector and choose Apply to selected text. Pen selected; project unchanged.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            StatusText.Text = $"Could not select cel text: {ex.Message}";
        }
        finally
        {
            _isInkTextPicker = awaitingEnd;
            if (!awaitingEnd)
            {
                _rangeAnchorOverlay = null;
                _rangeAnchor = null;
            }
            InkSurface.Invalidate();
        }
    }

    private void DrawSelectedInkText(CanvasDrawingSession session, float width, float height)
    {
        if (_workspace != "Ink" || _isPlaying || IsOriginalPreview ||
            !ReferenceEquals(_textInspectorFrame, CurrentFrame) ||
            InkTextOverlaySelector.SelectedItem is not ComboBoxItem { Tag: InkTextOverlay selected } ||
            !CurrentFrame.TextOverlays.Contains(selected))
        {
            return;
        }

        Windows.Foundation.Rect bounds = TimelineCompositor.GetInkTextBounds(session, selected, width, height);
        if (!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0)
        {
            session.DrawRectangle(bounds, ToColour(0xFF64C8FF),
                (float)(1 / Math.Max(0.25, PreviewCanvasTransform.ScaleX)));
        }
        if (TryGetInkTextRange(out int first, out int last))
        {
            foreach (Windows.Foundation.Rect region in InkTextEditing.GetRangeBounds(session, selected,
                width, height, first, last))
            {
                session.FillRectangle(region, ToColour(0x4064C8FF));
            }
        }
    }

    private void RefreshInkTextSelection()
    {
        if (InkTextOverlaySelector is null)
        {
            return;
        }

        if (ReferenceEquals(_textInspectorFrame, CurrentFrame) &&
            _textInspectorItems.SequenceEqual(CurrentFrame.TextOverlays))
        {
            return;
        }

        InkTextOverlay? previous = ReferenceEquals(_textInspectorFrame, CurrentFrame) &&
            InkTextOverlaySelector.SelectedItem is ComboBoxItem { Tag: InkTextOverlay selected } ? selected : null;
        _updatingTextInspector = true;
        _textInspectorFrame = CurrentFrame;
        _textInspectorItems = CurrentFrame.TextOverlays.ToArray();
        InkTextOverlaySelector.Items.Clear();
        for (int index = 0; index < _textInspectorItems.Length; index++)
        {
            InkTextOverlay overlay = _textInspectorItems[index];
            InkTextOverlaySelector.Items.Add(new ComboBoxItem
            {
                Content = $"{index + 1}: {overlay.Text[..Math.Min(overlay.Text.Length, 60)]}",
                Tag = overlay,
            });
        }
        InkTextOverlaySelector.SelectedItem = InkTextOverlaySelector.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => ReferenceEquals(item.Tag, previous));
        _updatingTextInspector = false;
        LoadSelectedInkText();
    }

    private void InkTextOverlay_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingTextInspector)
        {
            LoadSelectedInkText();
        }
    }

    private void LoadSelectedInkText()
    {
        bool valid = ReferenceEquals(_textInspectorFrame, CurrentFrame) &&
            InkTextOverlaySelector.SelectedItem is ComboBoxItem { Tag: InkTextOverlay text } &&
            CurrentFrame.TextOverlays.Contains(text);
        ApplySelectedInkTextButton.IsEnabled = valid;
        RemoveSelectedInkTextButton.IsEnabled = valid;
        InkSelectedTextProperties.IsEnabled = valid;
        InkSurface.Invalidate();
        if (!valid || InkTextOverlaySelector.SelectedItem is not ComboBoxItem { Tag: InkTextOverlay selected })
        {
            return;
        }

        InkTextBox.Text = selected.Text;
        ComboBoxItem? font = InkTextFontSelector.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => Equals(item.Tag, selected.FontFamily));
        if (font is null)
        {
            font = new ComboBoxItem { Content = selected.FontFamily, Tag = selected.FontFamily };
            InkTextFontSelector.Items.Add(font);
        }
        InkTextFontSelector.SelectedItem = font;
        InkTextSizeBox.Value = selected.FontSize;
        InkTextXBox.Value = selected.X * 100;
        InkTextYBox.Value = selected.Y * 100;
        InkSelectedTextColour.Color = ToColour(selected.Colour);
        InkTextFirstCharacterBox.Value = 1;
        InkTextLastCharacterBox.Value = InkTextEditing.CharacterBoundaries(selected.Text).Length - 1;
        InkTextRangeBoldCheckBox.IsChecked = false;
        InkTextRangeItalicCheckBox.IsChecked = false;
        InkTextRangeUnderlineCheckBox.IsChecked = false;
    }

    private void SelectedInkTextAction_Click(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(_textInspectorFrame, CurrentFrame) ||
            InkTextOverlaySelector.SelectedItem is not ComboBoxItem { Tag: InkTextOverlay selected } ||
            !CurrentFrame.TextOverlays.Contains(selected))
        {
            StatusText.Text = "Select a text overlay on the current cel.";
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        bool remove = sender is Button { Tag: "remove" };
        bool changedContent = !remove && selected.Text != InkTextBox.Text;
        try
        {
            var colour = InkSelectedTextColour.Color;
            uint argb = ((uint)colour.A << 24) | ((uint)colour.R << 16) | ((uint)colour.G << 8) | colour.B;
            bool changed = remove ? CurrentFrame.TextOverlays.Remove(selected) :
                InkTextEditing.Update(CurrentFrame, selected, InkTextBox.Text,
                    (InkTextFontSelector.SelectedItem as ComboBoxItem)?.Tag as string ?? "",
                    InkTextSizeBox.Value, InkTextXBox.Value / 100, InkTextYBox.Value / 100, argb);
            if (changed)
            {
                MarkEditChanged();
            }
            CommitEdit();
            if (!remove && InkTextOverlaySelector.SelectedItem is ComboBoxItem item)
            {
                int index = CurrentFrame.TextOverlays.IndexOf(selected);
                item.Content = $"{index + 1}: {selected.Text[..Math.Min(selected.Text.Length, 60)]}";
            }
            RenderCurrentInk();
            if (changedContent)
            {
                LoadSelectedInkText();
            }
            StatusText.Text = changed
                ? remove ? "Selected cel text removed. Undo restores it." : changedContent
                    ? "Cel text content updated; character-range styles reset to its base style. Undo restores content and styles."
                    : "Selected cel text updated. Existing character-range styles still override base properties."
                : "Selected cel text is unchanged.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            CommitEdit();
            StatusText.Text = $"Could not update cel text: {ex.Message}";
        }
    }

    private void InkTextRange_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args) =>
        InkSurface?.Invalidate();

    private bool TryGetInkTextRange(out int first, out int last)
    {
        double a = InkTextFirstCharacterBox?.Value ?? double.NaN;
        double b = InkTextLastCharacterBox?.Value ?? double.NaN;
        first = last = 0;
        if (!double.IsFinite(a) || !double.IsFinite(b) || a != Math.Truncate(a) || b != Math.Truncate(b) ||
            a < 1 || b < a || b > 2048)
        {
            return false;
        }
        first = (int)a;
        last = (int)b;
        return true;
    }

    private void StyleInkTextRange_Click(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(_textInspectorFrame, CurrentFrame) ||
            InkTextOverlaySelector.SelectedItem is not ComboBoxItem { Tag: InkTextOverlay selected } ||
            !CurrentFrame.TextOverlays.Contains(selected) || !TryGetInkTextRange(out int first, out int last))
        {
            StatusText.Text = "Select cel text and enter valid whole character numbers.";
            return;
        }
        if (InkTextBox.Text != selected.Text)
        {
            StatusText.Text = "Apply or discard pending text-content changes before styling a character range.";
            return;
        }
        PausePlayback();
        FinishActiveEdit();
        _isInkTextPicker = false;
        BeginEdit();
        try
        {
            var colour = InkSelectedTextColour.Color;
            var style = new InkTextStyleRange
            {
                FontFamily = (InkTextFontSelector.SelectedItem as ComboBoxItem)?.Tag as string ?? "",
                FontSize = InkTextSizeBox.Value,
                Colour = ((uint)colour.A << 24) | ((uint)colour.R << 16) | ((uint)colour.G << 8) | colour.B,
                Bold = InkTextRangeBoldCheckBox.IsChecked == true,
                Italic = InkTextRangeItalicCheckBox.IsChecked == true,
                Underline = InkTextRangeUnderlineCheckBox.IsChecked == true,
            };
            bool changed = InkTextEditing.ApplyStyle(CurrentFrame, selected, first, last, style,
                clear: sender is FrameworkElement { Tag: "clear" });
            if (changed)
            {
                MarkEditChanged();
            }
            CommitEdit();
            RenderCurrentInk();
            StatusText.Text = changed
                ? "Character-range style updated. Content, position and other overlays are unchanged; undo restores the previous styling."
                : "Character-range styling is unchanged.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException)
        {
            CommitEdit();
            StatusText.Text = $"Could not style character range: {ex.Message}";
        }
    }
}
