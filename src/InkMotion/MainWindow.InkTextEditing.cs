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
        _isInkColourSampler = false;
        _isInkLasso = false;
        _isEraser = false;
        _inkShape = null;
        InkSurface.Invalidate();
        StatusText.Text = "Click a cel text overlay to select it for editing. Topmost visible text wins; drawing layers and timeline titles are excluded.";
    }

    private void SelectInkTextAtPoint(double x, double y)
    {
        PausePlayback();
        FinishActiveEdit();
        try
        {
            InkTextOverlay? overlay = InkTextEditing.HitTest(InkSurface, CurrentFrame,
                InkSurface.ActualWidth, InkSurface.ActualHeight, x, y);
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
            _isInkTextPicker = false;
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
            StatusText.Text = changed
                ? remove ? "Selected cel text removed. Undo restores it." : "Selected cel text updated. Other overlays and drawings are unchanged."
                : "Selected cel text is unchanged.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            CommitEdit();
            StatusText.Text = $"Could not update cel text: {ex.Message}";
        }
    }
}
