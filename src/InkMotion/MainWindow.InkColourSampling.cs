using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace InkMotion;

public sealed partial class MainWindow
{
    private bool _isInkColourSampler;
    private bool _samplePreviewColour;

    private void SelectInkColourSampler_Click(object sender, RoutedEventArgs e)
    {
        bool preview = sender is FrameworkElement { Tag: "Preview" };
        if (preview && IsOriginalPreview)
        {
            StatusText.Text = "Switch to Modified preview before sampling the full preview colour.";
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        ClearInkSelection();
        _isInkColourSampler = true;
        _isInkBucketFill = false;
        _isInkTextPicker = false;
        _samplePreviewColour = preview;
        _isInkLasso = false;
        _isEraser = false;
        _inkShape = null;
        InkSurface.Invalidate();
        StatusText.Text = preview
            ? "Click to sample the modified preview: video, images, titles, ink and the canvas background. Onion skin, guides and selection outlines are excluded."
            : "Click to sample the visible drawing and cel text, including opacity. Media, onion skin, guides and selection outlines are excluded. Transparent pixels leave the brush unchanged.";
    }

    private void SampleInkColourAtCoordinates_Click(object sender, RoutedEventArgs e)
    {
        SampleInkColour(InkSampleXBox.Value / 100 * InkSurface.ActualWidth,
            InkSampleYBox.Value / 100 * InkSurface.ActualHeight,
            preview: sender is FrameworkElement { Tag: "Preview" });
    }

    private void SampleInkColour(double x, double y, bool preview = false)
    {
        PausePlayback();
        FinishActiveEdit();
        _isInkBucketFill = false;
        _isInkTextPicker = false;
        _isInkLasso = false;
        _isEraser = false;
        _inkShape = null;
        try
        {
            if (preview && IsOriginalPreview)
            {
                StatusText.Text = "Switch to Modified preview before sampling the full preview colour.";
                return;
            }

            EditorProject project = CreateProjectSnapshot();
            uint colour;
            if (preview)
            {
                if (PreviewStage.Background is not SolidColorBrush background)
                {
                    throw new InvalidOperationException("The preview background is not a solid colour.");
                }

                Windows.UI.Color value = background.Color;
                uint argb = ((uint)value.A << 24) | ((uint)value.R << 16) | ((uint)value.G << 8) | value.B;
                colour = InkColourSampling.SampleComposite(InkSurface, project, _selectedFrameIndex,
                    InkSurface.ActualWidth, InkSurface.ActualHeight, x, y, argb,
                    (session, width, height) => DrawVideoComposition(CompositionSurface, session,
                        _playheadFramePosition / _frameRate, width, height, requireComplete: true),
                    _playheadFramePosition / _frameRate);
            }
            else
            {
                colour = InkColourSampling.Sample(InkSurface, project, _selectedFrameIndex,
                    InkSurface.ActualWidth, InkSurface.ActualHeight, x, y, _playheadFramePosition / _frameRate);
            }
            if (colour == 0)
            {
                StatusText.Text = "No visible drawing at this point. Brush colour unchanged; video, images, titles and guides are not sampled.";
                return;
            }

            _brushColour = colour;
            InkCustomColourPicker.Color = ToColour(colour);
            StatusText.Text = $"{(preview ? "Preview" : "Drawing")} colour sampled: #{colour:X8} (ARGB). Pen selected; drawing and history are unchanged.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or
            System.Runtime.InteropServices.COMException)
        {
            StatusText.Text = $"Could not sample {(preview ? "preview" : "drawing")} colour: {ex.Message}";
        }
        finally
        {
            _isInkColourSampler = false;
        }
    }
}
