using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace InkMotion;

public sealed partial class MainWindow
{
    private void UpdateInkLayerEffectControls()
    {
        bool restoring = _isRestoringProject;
        _isRestoringProject = true;
        try
        {
            bool editable = CurrentInkLayer.IsVisible && !CurrentInkLayer.IsLocked;
            InkLayerGlowRadiusSlider.Value = CurrentInkLayer.GlowRadius;
            InkLayerGlowIntensitySlider.Value = CurrentInkLayer.GlowIntensity;
            InkLayerGlowRadiusSlider.IsEnabled = editable;
            InkLayerGlowIntensitySlider.IsEnabled = editable;
            InkLayerPixelSizeSlider.Value = CurrentInkLayer.PixelSize;
            InkLayerPixelSizeSlider.IsEnabled = editable;
            int index = _inkLayers.IndexOf(CurrentInkLayer);
            InkLayerClipButton.IsChecked = CurrentInkLayer.ClipToLayerBelow;
            InkLayerClipButton.IsEnabled = index > 0 || CurrentInkLayer.ClipToLayerBelow;
        }
        finally { _isRestoringProject = restoring; }
    }

    private void InkLayerClip_Click(object sender, RoutedEventArgs e)
    {
        bool clip = InkLayerClipButton.IsChecked == true;
        if (_isRestoringProject || _isInitialising || CurrentInkLayer.ClipToLayerBelow == clip)
        {
            return;
        }

        if (clip && _inkLayers.IndexOf(CurrentInkLayer) <= 0)
        {
            InkLayerClipButton.IsChecked = false;
            StatusText.Text = "The bottom layer has no layer beneath it to clip to.";
            return;
        }

        FinishActiveEdit();
        BeginEdit();
        CurrentInkLayer.ClipToLayerBelow = clip;
        MarkEditChanged();
        CommitEdit();
        RenderCurrentInk();
        StatusText.Text = clip
            ? $"{CurrentInkLayer.Name} is clipped to the layer below."
            : $"{CurrentInkLayer.Name} clipping mask released.";
    }

    private void InkLayerEffect_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isRestoringProject || _isInitialising || CurrentInkLayer.IsLocked ||
            sender is not Slider { Tag: string effect } || !double.IsFinite(e.NewValue))
        {
            return;
        }

        BeginEdit();
        switch (effect)
        {
            case "glow-radius":
                CurrentInkLayer.GlowRadius = Math.Clamp(e.NewValue, 0, InkLayerEffects.MaxGlowRadius);
                break;
            case "glow-intensity":
                CurrentInkLayer.GlowIntensity = Math.Clamp(e.NewValue, 0, InkLayerEffects.MaxGlowIntensity);
                break;
            case "pixel-size":
                CurrentInkLayer.PixelSize = e.NewValue < InkLayerEffects.MinPixelSize
                    ? 0
                    : Math.Min(Math.Round(e.NewValue), InkLayerEffects.MaxPixelSize);
                break;
            default:
                return;
        }

        MarkEditChanged();
        RenderCurrentInk();
    }

    private void InkLayerGlowColour_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentInkLayer.IsLocked)
        {
            StatusText.Text = "Unlock the layer before changing its effects.";
            return;
        }

        FinishActiveEdit();
        BeginEdit();
        CurrentInkLayer.GlowColour = _brushColour | 0xFF000000;
        MarkEditChanged();
        CommitEdit();
        RenderCurrentInk();
        StatusText.Text = CurrentInkLayer.GlowRadius > 0
            ? $"{CurrentInkLayer.Name} glow colour set from the brush."
            : $"{CurrentInkLayer.Name} glow colour set from the brush. Increase the glow radius to see it.";
    }
}
