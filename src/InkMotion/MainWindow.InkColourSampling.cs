using Microsoft.UI.Xaml;

namespace InkMotion;

public sealed partial class MainWindow
{
    private bool _isInkColourSampler;

    private void SelectInkColourSampler_Click(object sender, RoutedEventArgs e)
    {
        PausePlayback();
        FinishActiveEdit();
        ClearInkSelection();
        _isInkColourSampler = true;
        _isInkLasso = false;
        _isEraser = false;
        _inkShape = null;
        InkSurface.Invalidate();
        StatusText.Text = "Click to sample the visible drawing and cel text, including opacity. Media, onion skin, guides and selection outlines are excluded. Transparent pixels leave the brush unchanged.";
    }

    private void SampleInkColourAtCoordinates_Click(object sender, RoutedEventArgs e)
    {
        SampleInkColour(InkSampleXBox.Value / 100 * InkSurface.ActualWidth,
            InkSampleYBox.Value / 100 * InkSurface.ActualHeight);
    }

    private void SampleInkColour(double x, double y)
    {
        PausePlayback();
        FinishActiveEdit();
        _isInkLasso = false;
        _isEraser = false;
        _inkShape = null;
        try
        {
            uint colour = InkColourSampling.Sample(InkSurface, CreateProjectSnapshot(), _selectedFrameIndex,
                InkSurface.ActualWidth, InkSurface.ActualHeight, x, y);
            if (colour == 0)
            {
                StatusText.Text = "No visible drawing at this point. Brush colour unchanged; video, images, titles and guides are not sampled.";
                return;
            }

            _brushColour = colour;
            InkCustomColourPicker.Color = ToColour(colour);
            StatusText.Text = $"Drawing colour sampled: #{colour:X8} (ARGB). Pen selected; drawing and history are unchanged.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
            System.Runtime.InteropServices.COMException)
        {
            StatusText.Text = $"Could not sample drawing colour: {ex.Message}";
        }
        finally
        {
            _isInkColourSampler = false;
        }
    }
}
