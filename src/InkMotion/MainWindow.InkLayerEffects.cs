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
            bool belowIsHoldout = index > 0 && _inkLayers[index - 1].IsHoldout;
            bool clippedAbove = index + 1 < _inkLayers.Count && _inkLayers[index + 1].ClipToLayerBelow;
            InkLayerClipButton.IsChecked = CurrentInkLayer.ClipToLayerBelow;
            InkLayerClipButton.IsEnabled = CurrentInkLayer.ClipToLayerBelow ||
                (index > 0 && !CurrentInkLayer.IsHoldout && !belowIsHoldout);
            InkLayerHoldoutButton.IsChecked = CurrentInkLayer.IsHoldout;
            InkLayerHoldoutButton.IsEnabled = CurrentInkLayer.IsHoldout ||
                (!CurrentInkLayer.ClipToLayerBelow && !clippedAbove);
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

        if (clip && (CurrentInkLayer.IsHoldout || _inkLayers[_inkLayers.IndexOf(CurrentInkLayer) - 1].IsHoldout))
        {
            InkLayerClipButton.IsChecked = false;
            StatusText.Text = "Occlusion mattes cannot be clipped or used as a clipping base.";
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

    private void InkLayerHoldout_Click(object sender, RoutedEventArgs e)
    {
        bool holdout = InkLayerHoldoutButton.IsChecked == true;
        if (_isRestoringProject || _isInitialising || CurrentInkLayer.IsHoldout == holdout)
        {
            return;
        }

        int index = _inkLayers.IndexOf(CurrentInkLayer);
        if (holdout && (CurrentInkLayer.ClipToLayerBelow ||
            (index + 1 < _inkLayers.Count && _inkLayers[index + 1].ClipToLayerBelow)))
        {
            InkLayerHoldoutButton.IsChecked = false;
            StatusText.Text = "Release clipping on this layer and on any layer clipped to it before making it an occlusion matte.";
            return;
        }

        FinishActiveEdit();
        BeginEdit();
        CurrentInkLayer.IsHoldout = holdout;
        MarkEditChanged();
        CommitEdit();
        RenderCurrentInk();
        UpdateInkLayerEffectControls();
        StatusText.Text = holdout
            ? $"{CurrentInkLayer.Name} is an occlusion matte. Paint over the foreground object to hide the ink layers beneath it there."
            : $"{CurrentInkLayer.Name} is a normal drawing layer again.";
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
