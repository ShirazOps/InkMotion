using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Display;
using Windows.Foundation;

namespace InkMotion;

public sealed partial class MainWindow
{
    private int _inkGridDivisions = 8;
    private List<InkPoint>? _activeSymmetricStroke;

    private void ApplyProjectCanvas_Click(object sender, RoutedEventArgs e)
    {
        double width = ProjectCanvasWidthBox.Value;
        double height = ProjectCanvasHeightBox.Value;
        if (!double.IsFinite(width) || !double.IsFinite(height) ||
            width != Math.Truncate(width) || height != Math.Truncate(height) ||
            width is < 1 or > 8192 || height is < 1 or > 8192)
        {
            StatusText.Text = "Use whole canvas dimensions from 1 to 8,192 pixels.";
            return;
        }

        var size = new ProjectCanvasSize((int)width, (int)height);
        try { size.Validate(); }
        catch (InvalidDataException error) { StatusText.Text = error.Message; return; }
        SetProjectCanvas(size);
    }

    private void AutomaticProjectCanvas_Click(object sender, RoutedEventArgs e) => SetProjectCanvas(null);

    private void SetProjectCanvas(ProjectCanvasSize? size)
    {
        if (_projectCanvasSize == size)
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        _projectCanvasSize = size;
        MarkEditChanged();
        FinishActiveEdit();
        UpdateProjectCanvasControls();
        RenderCurrentInk();
        StatusText.Text = "Project canvas updated. Normalised artwork positions and export presets are unchanged.";
    }

    private void ApplyProjectColour_Click(object sender, RoutedEventArgs e)
    {
        if (_compoundContexts.Count > 0)
        {
            StatusText.Text = "Compound timelines inherit colour settings from the parent project.";
            return;
        }
        string? tag = (ProjectColourSpaceSelector.SelectedItem as ComboBoxItem)?.Tag as string;
        ProjectColourSettings? colour = null;
        if (tag is not (null or "Sdr"))
        {
            double reference = ProjectReferenceWhiteBox.Value;
            double peak = ProjectPeakNitsBox.Value;
            colour = new ProjectColourSettings(Enum.Parse<ProjectColourSpace>(tag),
                double.IsFinite(reference) ? reference : double.NaN, double.IsFinite(peak) ? peak : double.NaN);
            try { colour.Validate(); }
            catch (InvalidDataException error) { StatusText.Text = error.Message; return; }
        }

        if (_projectColour == colour)
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        _projectColour = colour;
        foreach (EditorProject nested in _videoTracks.SelectMany(track => track.Clips)
            .Select(clip => clip.NestedProject).OfType<EditorProject>()
            .SelectMany(CompoundTimelines.Projects))
        {
            nested.Colour = colour;
        }
        MarkEditChanged();
        FinishActiveEdit();
        UpdateProjectColourControls();
        RenderCurrentInk();
        CompositionSurface.Invalidate();
        StatusText.Text = colour is null
            ? "Project colour set to SDR Rec.709."
            : $"Project colour set to {colour.DisplayName}. HDR export needs the FFmpeg backend with HEVC or AV1.";
    }

    private void UpdateProjectColourControls()
    {
        ProjectColourSpaceSelector.SelectedIndex = _projectColour?.Space switch
        {
            ProjectColourSpace.Hdr10Pq => 1,
            ProjectColourSpace.Hlg => 2,
            _ => 0,
        };
        ProjectReferenceWhiteBox.Value = _projectColour?.ReferenceWhiteNits ?? 203;
        ProjectPeakNitsBox.Value = _projectColour?.PeakNits ?? 1000;
        ProjectColourReadout.Text = _projectColour is { } colour
            ? $"{colour.DisplayName}: SDR media maps to {colour.ReferenceWhiteNits:0} nits and highlights are kept up to {colour.PeakNits:0} nits in 10-bit HEVC/AV1 export through FFmpeg. " +
              $"HDR10 PQ through libx265 carries static metadata and can include a locally generated, frame-aligned HDR10+ sidecar; InkMotion does not generate it. Dolby Vision remains unsupported. {GetHdrPreviewReadout()}"
            : "SDR Rec.709: the existing 8-bit pipeline. Choose HDR10 PQ or HLG for a float, Rec.2020 colour-managed export.";
        UpdateHdrPreviewSurfaceVisibility();
    }

    private string GetHdrPreviewReadout() => _hdrPreviewIssue ??
        (_hdrDisplayActive
            ? "HDR display preview is active using scRGB half-float output."
            : "This display is not in HDR mode; preview remains tone-mapped to SDR.");

    private void InitialiseHdrPreviewDisplayInformation()
    {
        try
        {
            _hdrDisplayInformation = DisplayInformation.CreateForWindowId(AppWindow.Id);
            _hdrDisplayInformation.AdvancedColorInfoChanged += HdrDisplayInformation_AdvancedColorInfoChanged;
            UpdateHdrPreviewDisplayState();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or
            ArgumentException or InvalidOperationException)
        {
            _hdrPreviewIssue = $"HDR display capabilities could not be read: {ex.Message}";
            UpdateProjectColourControls();
        }
    }

    private void HdrDisplayInformation_AdvancedColorInfoChanged(DisplayInformation sender, object args) =>
        UpdateHdrPreviewDisplayState();

    private void UpdateHdrPreviewDisplayState()
    {
        bool active;
        try
        {
            active = _hdrDisplayInformation?.GetAdvancedColorInfo().CurrentAdvancedColorKind ==
                DisplayAdvancedColorKind.HighDynamicRange;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or
            ArgumentException or InvalidOperationException)
        {
            _hdrDisplayActive = false;
            _hdrPreviewIssue = $"HDR display capabilities could not be read: {ex.Message}";
            UpdateProjectColourControls();
            CompositionSurface.Invalidate();
            return;
        }

        if (_hdrDisplayActive == active && _hdrPreviewIssue is null)
        {
            return;
        }

        _hdrDisplayActive = active;
        _hdrPreviewIssue = null;
        UpdateProjectColourControls();
        CompositionSurface.Invalidate();
    }

    private void UpdateHdrPreviewSurfaceVisibility()
    {
        bool visible = _projectColour is not null && _hdrDisplayActive && _workspace == "Video" &&
            !IsOriginalPreview && _hdrPreviewIssue is null;
        HdrPreviewSurface.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible && _hdrPreviewSwapChain is not null)
        {
            _hdrPreviewSwapChain.Dispose();
            _hdrPreviewSwapChain = null;
            HdrPreviewSurface.SwapChain = null;
        }
    }

    private void UpdateProjectCanvasControls()
    {
        ProjectCanvasWidthBox.Value = _projectCanvasSize?.Width ?? 1920;
        ProjectCanvasHeightBox.Value = _projectCanvasSize?.Height ?? 1080;
        ProjectCanvasReadout.Text = _projectCanvasSize is { } size
            ? $"Saved canvas: {size.Width:N0} x {size.Height:N0} pixels. Export fits this aspect ratio; unused output space is black in video and transparent in drawing exports."
            : "Automatic canvas: legacy viewer and output framing. Apply dimensions to save a fixed project aspect ratio, independently of export resolution.";
        UpdateProjectCanvasLayout();
        UpdateInkRulerReadout();
    }

    private void UpdateProjectCanvasLayout()
    {
        if (_projectCanvasSize is { } size && PreviewViewport.ActualWidth > 24 && PreviewViewport.ActualHeight > 24)
        {
            Rect fit = size.Fit(PreviewViewport.ActualWidth - 24, PreviewViewport.ActualHeight - 24);
            PreviewStage.Width = fit.Width + 24;
            PreviewStage.Height = fit.Height + 24;
            PreviewStage.HorizontalAlignment = HorizontalAlignment.Center;
            PreviewStage.VerticalAlignment = VerticalAlignment.Center;
        }
        else
        {
            PreviewStage.Width = double.NaN;
            PreviewStage.Height = double.NaN;
            PreviewStage.HorizontalAlignment = HorizontalAlignment.Stretch;
            PreviewStage.VerticalAlignment = VerticalAlignment.Stretch;
        }

        UpdateProjectCanvasClip();
    }

    private void ProjectCanvasContent_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateProjectCanvasClip();

    private void UpdateProjectCanvasClip() => ProjectCanvasContent.Clip = _projectCanvasSize is null ? null :
        new RectangleGeometry { Rect = new Rect(0, 0, ProjectCanvasContent.ActualWidth, ProjectCanvasContent.ActualHeight) };

    private void InkGuides_Click(object sender, RoutedEventArgs e)
    {
        CompositionSurface.Invalidate();
        InkSurface.Invalidate();
    }

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
        CompositionSurface.Invalidate();
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

        if (InkSymmetryToggle?.IsChecked == true)
        {
            session.DrawLine(width / 2, 0, width / 2, height, ToColour(0xCC71D6C1u), thickness);
        }
    }

    private void UpdateInkRulerReadout()
    {
        if (InkRulerReadout is not null && TryGetInkRulerDimensions(out int width, out int height))
        {
            InkRulerReadout.Text = _projectCanvasSize is not null
                ? $"Ruler calibration: {width:N0} x {height:N0} project pixels."
                : $"Ruler calibration: {width:N0} x {height:N0} output pixels. Change resolution on Export.";
        }
    }

    private bool TryGetInkRulerDimensions(out int width, out int height)
    {
        if (_projectCanvasSize is { } size)
        {
            width = size.Width;
            height = size.Height;
            return true;
        }

        bool valid = TryGetExportProfile(24, out VideoExportProfile profile);
        width = valid ? profile.Width : 0;
        height = valid ? profile.Height : 0;
        return valid;
    }

    private void DrawInkRulers(CanvasDrawingSession session, float width, float height)
    {
        if (_workspace != "Ink" || _isPlaying || IsOriginalPreview ||
            InkRulersToggle?.IsChecked != true || width <= 0 || height <= 0 ||
            !TryGetInkRulerDimensions(out int pixelWidth, out int pixelHeight))
        {
            return;
        }

        double zoom = PreviewCanvasTransform.ScaleX;
        float unit = (float)(1 / zoom);
        float band = 20 * unit;
        using var format = new CanvasTextFormat { FontSize = 10 * unit, WordWrapping = CanvasWordWrapping.NoWrap };
        Windows.UI.Color colour = ToColour(0xFFDDE5EC);
        session.FillRectangle(0, 0, width, band, ToColour(0xDD15202B));
        session.FillRectangle(0, 0, band * 2, height, ToColour(0xDD15202B));
        foreach (InkRulerTick tick in InkGuideLayout.CreateRuler(width, pixelWidth, zoom))
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

        foreach (InkRulerTick tick in InkGuideLayout.CreateRuler(height, pixelHeight, zoom))
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
        UpdateProjectCanvasLayout();
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

    private (double X, double Y) GetInkCanvasPosition(Point point, bool snapToGrid = true)
    {
        double x = Math.Clamp(point.X / InkSurface.ActualWidth, 0, 1);
        double y = Math.Clamp(point.Y / InkSurface.ActualHeight, 0, 1);
        if (snapToGrid && InkGridSnapToggle?.IsChecked == true)
        {
            x = InkStrokeEditing.SnapToGrid(x, _inkGridDivisions);
            y = InkStrokeEditing.SnapToGrid(y, _inkGridDivisions);
        }

        return (x, y);
    }

    private void SyncActiveSymmetricStroke()
    {
        if (_activeStroke is null || _activeSymmetricStroke is null)
        {
            return;
        }

        _activeSymmetricStroke.Clear();
        _activeSymmetricStroke.AddRange(_activeStroke.Select(InkStrokeEditing.ReflectAcrossVerticalAxis));
    }
}
