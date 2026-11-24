using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InkMotion;

public sealed partial class MainWindow
{
    private void SelectSmudge_Click(object sender, RoutedEventArgs e)
    {
        FinishActiveEdit();
        _isInkBucketFill = false;
        _isInkColourSampler = false;
        _isInkTextPicker = false;
        _isEraser = false;
        _inkShape = null;
        _isInkLasso = false;
        _inkModifierTool = InkBrushTexture.Smudge;
        StatusText.Text = "Smudge selected. Drag to pull earlier pixels on the active drawing layer. Original paths remain editable; pressure controls radius and strength.";
    }

    private void InkBrushTexture_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitialising) return;
        FinishActiveEdit();
        _inkModifierTool = null;
    }
}
