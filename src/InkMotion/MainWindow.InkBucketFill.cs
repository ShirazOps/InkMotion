using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace InkMotion;

public sealed partial class MainWindow
{
    private bool _isInkBucketFill;

    private void SelectInkBucketFill_Click(object sender, RoutedEventArgs e)
    {
        if (IsOriginalPreview)
        {
            StatusText.Text = "Switch to Modified preview before filling ink.";
            return;
        }
        PausePlayback();
        FinishActiveEdit();
        ClearInkSelection();
        _isInkBucketFill = true;
        _isInkTextPicker = false;
        _isInkColourSampler = false;
        _isInkLasso = false;
        _isEraser = false;
        _inkShape = null;
        InkSurface.Invalidate();
        StatusText.Text = "Click an empty enclosed area on the active ink layer. Open boundaries reaching the canvas edge are rejected; other layers, text and media are ignored.";
    }

    private void FillInkAtCoordinates_Click(object sender, RoutedEventArgs e) =>
        FillInkAtPoint(new Point(InkFillXBox.Value / 100 * InkSurface.ActualWidth,
            InkFillYBox.Value / 100 * InkSurface.ActualHeight));

    private void FillInkAtPoint(Point point)
    {
        PausePlayback();
        FinishActiveEdit();
        _isInkTextPicker = false;
        _isInkColourSampler = false;
        _isInkLasso = false;
        _isEraser = false;
        _inkShape = null;
        try
        {
            if (IsOriginalPreview)
            {
                StatusText.Text = "Switch to Modified preview before filling ink.";
                return;
            }
            if (!EnsureInkLayerEditable())
            {
                return;
            }
            if (!CurrentInkLayer.IsVisible || CurrentInkLayer.Opacity <= 0)
            {
                StatusText.Text = "Show the active ink layer and give it non-zero opacity before filling.";
                return;
            }

            List<InkPoint> fill = InkBucketFill.Create(InkSurface,
                GetDisplayedLayerStrokes(_selectedFrameIndex, _activeInkLayerId),
                InkSurface.ActualWidth, InkSurface.ActualHeight, point.X, point.Y, _brushColour);
            ClearInkSelection();
            BeginEdit();
            PromoteInterpolatedFrameForEditing();
            CurrentStrokes.Insert(0, fill);
            MarkEditChanged();
            CommitEdit();
            RenderCurrentInk();
            StatusText.Text = "Enclosed area filled beneath the active layer's strokes. Undo restores the drawing. Pen selected.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
            System.Runtime.InteropServices.COMException)
        {
            StatusText.Text = $"Could not fill ink: {ex.Message}";
        }
        finally
        {
            _isInkBucketFill = false;
        }
    }
}
