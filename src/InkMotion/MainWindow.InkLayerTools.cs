using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InkMotion;

public sealed partial class MainWindow
{
    private void InkLayerAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string action } || action is not ("rename" or "duplicate" or "remove" or "merge"))
        {
            return;
        }

        string name = InkLayerNameBox.Text.Trim();
        if (action == "rename" && name.Length == 0)
        {
            StatusText.Text = "Enter a non-empty layer name.";
            return;
        }

        if (action == "rename" && name == CurrentInkLayer.Name)
        {
            StatusText.Text = "Layer name is unchanged.";
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        try
        {
            switch (action)
            {
                case "merge":
                    _activeInkLayerId = InkEditing.MergeLayerDown(_inkLayers, _frames, _activeInkLayerId).Id;
                    break;
                case "duplicate":
                    _activeInkLayerId = InkEditing.DuplicateLayer(_inkLayers, _frames, _activeInkLayerId).Id;
                    break;
                case "remove":
                    InkEditing.RemoveLayer(_inkLayers, _frames, _activeInkLayerId);
                    _activeInkLayerId = BaseInkLayerId;
                    break;
                case "rename":
                    CurrentInkLayer.Name = name;
                    break;
            }
        }
        catch (InvalidOperationException ex)
        {
            CommitEdit();
            StatusText.Text = ex.Message;
            return;
        }

        ClearInkSelection();
        MarkEditChanged();
        CommitEdit();
        RefreshInkLayers();
        RefreshTimeline();
        RenderCurrentInk();
        UpdateFrameStatus();
        StatusText.Text = action switch
        {
            "merge" => "Layer merged down across all cels. Generated drawings are now explicit strokes; undo restores both layers. Text is unchanged.",
            "duplicate" => "Layer drawings duplicated across all cels. The independent copy is unlocked; cel-global text is unchanged.",
            "remove" => "Layer and its drawings removed across all cels. Undo restores them; text is unchanged.",
            _ => "Ink layer renamed.",
        };
    }

    private void InkBrushPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitialising || InkBrushPresetSelector.SelectedItem is not string name)
        {
            return;
        }

        InkBrushPreset preset = InkBrushPresets.All.First(preset => preset.Name == name);
        FinishActiveEdit();
        _brushWidth = preset.Width;
        _brushColour = preset.ApplyColour(_brushColour);
        InkWidthSlider.Value = preset.Width;
        InkStabilisationSlider.Value = preset.Stabilisation;
        InkCustomColourPicker.Color = ToColour(_brushColour);
        _isEraser = false;
        _isInkLasso = false;
        _inkShape = null;
        ClearInkSelection();
        InkSurface.Invalidate();
        InkBrushPresetSelector.SelectedIndex = -1;
        StatusText.Text = $"{preset.Name} selected. Round pressure-sensitive brush; colour retained. Width, alpha and stabilisation remain editable.";
    }
}
