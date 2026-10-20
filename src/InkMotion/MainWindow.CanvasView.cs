using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace InkMotion;

public sealed partial class MainWindow
{
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
