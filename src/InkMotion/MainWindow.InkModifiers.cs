using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InkMotion;

public sealed partial class MainWindow
{
    private void SelectSmudge_Click(object sender, RoutedEventArgs e)
        => SelectInkModifier(InkBrushTexture.Smudge);

    private void SelectBlurDrawing_Click(object sender, RoutedEventArgs e)
        => SelectInkModifier(InkBrushTexture.Blur);

    private void SelectInkModifier(InkBrushTexture tool)
    {
        FinishActiveEdit();
        _isInkBucketFill = false;
        _isInkColourSampler = false;
        _isInkTextPicker = false;
        _isEraser = false;
        _inkShape = null;
        _isInkLasso = false;
        _inkModifierTool = tool;
        StatusText.Text = tool == InkBrushTexture.Smudge
            ? "Smudge selected. Drag to pull earlier pixels on the active drawing layer. Original paths remain editable; pressure controls radius and strength."
            : "Blur drawing selected. Tap or drag to soften earlier pixels on the active drawing layer. Original paths remain editable; pressure controls brush coverage.";
    }

    private void InkBrushTexture_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitialising) return;
        FinishActiveEdit();
        _inkModifierTool = null;
    }
}
