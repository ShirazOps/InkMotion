using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
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

    private void UpdateInkRulerReadout()
    {
        if (InkRulerReadout is not null &&
            ExportResolutionSelector?.SelectedItem is ComboBoxItem { Tag: string name } &&
            Enum.TryParse(name, out ExportResolution resolution))
        {
            VideoExportProfile profile = VideoExportProfile.Create(resolution, 24);
            InkRulerReadout.Text = $"Ruler calibration: {profile.Width:N0} x {profile.Height:N0} output pixels. Change resolution on Export.";
        }
    }

    private void DrawInkRulers(CanvasDrawingSession session, float width, float height)
    {
        if (_workspace != "Ink" || _isPlaying || IsOriginalPreview ||
            InkRulersToggle?.IsChecked != true || width <= 0 || height <= 0 ||
            ExportResolutionSelector.SelectedItem is not ComboBoxItem { Tag: string name } ||
            !Enum.TryParse(name, out ExportResolution resolution))
        {
            return;
        }

        VideoExportProfile profile = VideoExportProfile.Create(resolution, 24);
        double zoom = PreviewCanvasTransform.ScaleX;
        float unit = (float)(1 / zoom);
        float band = 20 * unit;
        using var format = new CanvasTextFormat { FontSize = 10 * unit, WordWrapping = CanvasWordWrapping.NoWrap };
        Windows.UI.Color colour = ToColour(0xFFDDE5EC);
        session.FillRectangle(0, 0, width, band, ToColour(0xDD15202B));
        session.FillRectangle(0, 0, band * 2, height, ToColour(0xDD15202B));
        foreach (InkRulerTick tick in InkGuideLayout.CreateRuler(width, profile.Width, zoom))
        {
            float x = (float)tick.Position * width;
            float length = (tick.IsMajor ? 7 : 3) * unit;
            session.DrawLine(x, band - length, x, band, colour, unit);
            if (tick.IsMajor)
            {
                session.DrawText(tick.Pixel.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Math.Clamp(x + 2 * unit, 0, Math.Max(0, width - 36 * unit)), 0, colour, format);
            }
        }

        foreach (InkRulerTick tick in InkGuideLayout.CreateRuler(height, profile.Height, zoom))
        {
            float y = (float)tick.Position * height;
            float length = (tick.IsMajor ? 7 : 3) * unit;
            session.DrawLine(band * 2 - length, y, band * 2, y, colour, unit);
            if (tick.IsMajor && tick.Pixel > 0)
            {
                session.DrawText(tick.Pixel.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    0, Math.Clamp(y + unit, band, Math.Max(band, height - 12 * unit)), colour, format);
            }
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
        UpdateInkRulerReadout();
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
