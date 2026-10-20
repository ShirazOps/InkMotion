using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace InkMotion;

public sealed partial class MainWindow
{
    private int _inkGridDivisions = 8;

    private void InkGuides_Click(object sender, RoutedEventArgs e) => InkSurface.Invalidate();

    private void InkGridDivisions_Changed(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (_isInitialising)
        {
            return;
        }

        if (!double.IsFinite(e.NewValue) || e.NewValue != Math.Truncate(e.NewValue) || e.NewValue is < 2 or > 64)
        {
            sender.Value = _inkGridDivisions;
            StatusText.Text = "Use a whole number from 2 to 64 for the canvas grid divisions.";
            return;
        }

        _inkGridDivisions = (int)e.NewValue;
        InkSurface.Invalidate();
    }

    private void DrawInkGuides(Microsoft.Graphics.Canvas.CanvasDrawingSession session, float width, float height)
    {
        if (_workspace != "Ink" || _isPlaying || IsOriginalPreview || width <= 0 || height <= 0)
        {
            return;
        }

        float thickness = (float)(1 / Math.Max(0.25, PreviewCanvasTransform.ScaleX));
        foreach (InkGuideLine guide in InkGuideLayout.Create(InkGridToggle?.IsChecked == true,
            _inkGridDivisions, InkCentreGuidesToggle?.IsChecked == true))
        {
            session.DrawLine((float)guide.X1 * width, (float)guide.Y1 * height,
                (float)guide.X2 * width, (float)guide.Y2 * height,
                ToColour(guide.IsCentre ? 0x99FFB454u : 0x557F9AAAu), thickness);
        }
    }

    private void PreviewViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        PreviewViewport.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
        UpdateInkCanvasView();
    }

    private void InkCanvasZoom_Changed(object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) => UpdateInkCanvasView();

    private void InkCanvasView_Changed(NumberBox sender, NumberBoxValueChangedEventArgs e) => UpdateInkCanvasView();

    private void UpdateInkCanvasView()
    {
        if (_isInitialising || PreviewCanvasTransform is null || InkCanvasZoomSlider is null ||
            InkCanvasRotationBox is null || InkCanvasPanXBox is null || InkCanvasPanYBox is null)
        {
            return;
        }

        bool enabled = _workspace == "Ink";
        double zoom = InkCanvasZoomSlider.Value / 100;
        double rotation = enabled ? InkCanvasRotationBox.Value : 0;
        double x = enabled ? InkCanvasPanXBox.Value : 0;
        double y = enabled ? InkCanvasPanYBox.Value : 0;
        if (!double.IsFinite(rotation) || !double.IsFinite(x) || !double.IsFinite(y))
        {
            return;
        }

        FinishActiveEdit();
        PreviewCanvasTransform.ScaleX = PreviewCanvasTransform.ScaleY = enabled ? zoom : 1;
        PreviewCanvasTransform.Rotation = enabled ? rotation : 0;
        PreviewCanvasTransform.TranslateX = enabled ? PreviewViewport.ActualWidth * x / 100 : 0;
        PreviewCanvasTransform.TranslateY = enabled ? PreviewViewport.ActualHeight * y / 100 : 0;
        InkSurface.Invalidate();
    }

    private void ResetInkCanvasView_Click(object sender, RoutedEventArgs e)
    {
        InkCanvasZoomSlider.Value = 100;
        InkCanvasRotationBox.Value = 0;
        InkCanvasPanXBox.Value = 0;
        InkCanvasPanYBox.Value = 0;
        UpdateInkCanvasView();
        StatusText.Text = "Canvas view reset. Drawings and export framing are unchanged.";
    }
}
