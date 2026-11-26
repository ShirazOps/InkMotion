using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InkMotion;

public sealed partial class MainWindow
{
    private InkCharacterRig? _characterRig;
    private EditorProject? _characterRigPreview;
    private double? _characterRigPreviewSeconds;

    private void CharacterBones_Click(object sender, RoutedEventArgs e) => InkSurface.Invalidate();

    private void DrawCharacterBones(CanvasDrawingSession session, float width, float height)
    {
        InkCharacterRig? rig = _characterRigPreview?.CharacterRig ?? _characterRig;
        if (_workspace != "Ink" || IsOriginalPreview || rig is null ||
            (CharacterBonesToggle.IsChecked != true && _characterRigPreview is null)) return;
        double seconds = _characterRigPreviewSeconds ?? _playheadFramePosition / _frameRate;
        Dictionary<string, Matrix3x2> transforms = InkCharacterRigs.Transforms(rig, seconds, width, height);
        foreach (InkRigBone bone in rig.Bones)
        {
            Matrix3x2 transform = rig.Enabled ? transforms[bone.Id] : Matrix3x2.Identity;
            Vector2 start = Vector2.Transform(new((float)bone.X * width, (float)bone.Y * height), transform);
            Vector2 end = Vector2.Transform(new((float)bone.EndX * width, (float)bone.EndY * height), transform);
            session.DrawLine(start, end, Microsoft.UI.Colors.Gold, 3);
            session.FillCircle(start, 5, Microsoft.UI.Colors.Gold);
            session.DrawText(bone.Name, start + new Vector2(7, 7), Microsoft.UI.Colors.Gold);
        }
    }

    private async void CharacterRig_Click(object sender, RoutedEventArgs e)
    {
        PausePlayback();
        FinishActiveEdit();
        ClearInkSelection();
        EditorProject draft = CreateProjectSnapshot();
        draft.CharacterRig ??= new();
        InkCharacterRig rig = draft.CharacterRig;
        var panel = new StackPanel { Spacing = 8, Width = 520 };
        var restPanel = new StackPanel { Spacing = 6 };
        var posePanel = new StackPanel { Spacing = 6 };
        var bindingPanel = new StackPanel { Spacing = 6 };
        StackPanel actionPanel = restPanel;
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var enabled = new CheckBox { Content = "Preview animated rig (turn off to edit original artwork)", IsChecked = rig.Enabled };
        var bones = new ComboBox { Header = "Bone hierarchy", HorizontalAlignment = HorizontalAlignment.Stretch };
        var name = new TextBox { Header = "Bone name" };
        var parent = new ComboBox { Header = "Parent bone", HorizontalAlignment = HorizontalAlignment.Stretch };
        NumberBox Number(string header, double value, double minimum, double maximum) => new()
        {
            Header = header, Value = value, Minimum = minimum, Maximum = maximum,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        var x = Number("Rest start X (0-1)", 0.5, 0, 1);
        var y = Number("Rest start Y (0-1)", 0.5, 0, 1);
        var endX = Number("Rest end X (0-1)", 0.5, 0, 1);
        var endY = Number("Rest end Y (0-1)", 0.7, 0, 1);
        var frame = Number("Pose at timeline frame (1-based)", _playheadFramePosition + 1, 1, 86400 * _frameRate + 1);
        var rotation = Number("Local rotation (degrees)", 0, -3600, 3600);
        var offsetX = Number("Local X offset (canvas fraction)", 0, -10, 10);
        var offsetY = Number("Local Y offset (canvas fraction)", 0, -10, 10);
        var stretch = Number("Length multiplier", 1, 0.01, 100);
        var easing = new ComboBox { Header = "Interpolation to next pose", ItemsSource = Enum.GetValues<InkInterpolationMode>(), SelectedIndex = 0 };
        var keys = new ComboBox { Header = "Saved poses (choose to inspect)", HorizontalAlignment = HorizontalAlignment.Stretch };
        var layer = new ComboBox { Header = "Artwork layer", ItemsSource = draft.InkLayers,
            DisplayMemberPath = "Name", SelectedItem = draft.InkLayers.First(item => item.Id == _activeInkLayerId) };
        var mode = new ComboBox { Header = "Artwork binding", ItemsSource = new[] { "Unbound", "Cut-out: selected bone", "Weighted: distance to influenced bones" }, SelectedIndex = 0 };
        var influence = Number("Selected bone influence multiplier (0 removes it)", 1, 0, 100);
        var influenceList = new TextBlock { TextWrapping = TextWrapping.Wrap };
        InkRigBone? SelectedBone() => (bones.SelectedItem as ComboBoxItem)?.Tag as InkRigBone;
        double Seconds() => (frame.Value - 1) / _frameRate;
        void Preview()
        {
            _characterRigPreview = draft;
            _characterRigPreviewSeconds = Seconds();
            RenderCurrentInk();
        }
        void ReadBinding()
        {
            if (layer.SelectedItem is not InkLayerProject artwork) return;
            mode.SelectedIndex = artwork.RigBinding is null ? 0 : artwork.RigBinding.Mode == InkRigBindingMode.CutOut ? 1 : 2;
            influence.Value = SelectedBone() is { } bone && artwork.RigBinding?.Influences.TryGetValue(bone.Id, out double weight) == true ? weight : 0;
            influenceList.Text = artwork.RigBinding is null ? "No binding." :
                $"Bound to {rig.Bones.First(item => item.Id == artwork.RigBinding.BoneId).Name}. " +
                string.Join("; ", artwork.RigBinding.Influences.Select(pair =>
                    $"{rig.Bones.First(item => item.Id == pair.Key).Name}: {pair.Value:0.###}"));
        }
        void ReadPose()
        {
            if (SelectedBone() is not { } bone || !double.IsFinite(frame.Value)) return;
            InkRigPose pose = InkCharacterRigs.Evaluate(bone, Seconds());
            rotation.Value = pose.Rotation; offsetX.Value = pose.X; offsetY.Value = pose.Y; stretch.Value = pose.Stretch;
            easing.SelectedItem = pose.Interpolation;
            Preview();
        }
        void RefreshBones(string? selectedId)
        {
            bones.Items.Clear();
            foreach (InkRigBone bone in rig.Bones)
                bones.Items.Add(new ComboBoxItem { Content = $"{bone.Name} {(bone.ParentId is null ? "(root)" : $"← {rig.Bones.First(item => item.Id == bone.ParentId).Name}")}", Tag = bone });
            bones.SelectedItem = bones.Items.OfType<ComboBoxItem>().FirstOrDefault(item => ((InkRigBone)item.Tag).Id == selectedId)
                ?? bones.Items.FirstOrDefault();
            ReadBinding();
            Preview();
        }
        void ReadBone()
        {
            if (SelectedBone() is not { } bone) return;
            name.Text = bone.Name; x.Value = bone.X; y.Value = bone.Y; endX.Value = bone.EndX; endY.Value = bone.EndY;
            parent.Items.Clear();
            parent.Items.Add(new ComboBoxItem { Content = "None (root)" });
            foreach (InkRigBone candidate in rig.Bones.Where(item => item.Id != bone.Id))
                parent.Items.Add(new ComboBoxItem { Content = candidate.Name, Tag = candidate.Id });
            parent.SelectedItem = parent.Items.OfType<ComboBoxItem>().First(item => (string?)item.Tag == bone.ParentId);
            keys.Items.Clear();
            foreach (InkRigPose key in bone.Keys)
                keys.Items.Add(new ComboBoxItem { Content = $"Frame {key.Seconds * _frameRate + 1:0.###} · {key.Rotation:0.#}°", Tag = key });
            ReadPose(); ReadBinding();
        }
        void Action(string label, Action action)
        {
            var button = new Button { Content = label };
            button.Click += (_, _) => action();
            actionPanel.Children.Add(button);
        }
        panel.Children.Add(new TextBlock
        {
            Text = "Build the rest hierarchy over your drawings. Child poses inherit their parent's movement. Apply edits below to preview them, then Save rig to keep the entire change, or Cancel to discard it. Extend cel exposures to cover your animation.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(enabled); panel.Children.Add(bones);
        var tabs = new Pivot();
        tabs.Items.Add(new PivotItem { Header = "Rest hierarchy", Content = restPanel });
        tabs.Items.Add(new PivotItem { Header = "Pose keys", Content = posePanel });
        tabs.Items.Add(new PivotItem { Header = "Artwork", Content = bindingPanel });
        panel.Children.Add(tabs);
        Action("Add bone", () =>
        {
            if (rig.Bones.Count == 64) { message.Text = "A rig supports at most 64 bones."; return; }
            var bone = new InkRigBone { Name = $"Bone {rig.Bones.Count + 1}", ParentId = SelectedBone()?.Id };
            rig.Bones.Add(bone); RefreshBones(bone.Id);
        });
        restPanel.Children.Add(name); restPanel.Children.Add(parent);
        var rest = new Grid { ColumnSpacing = 8, RowSpacing = 4 };
        rest.ColumnDefinitions.Add(new()); rest.ColumnDefinitions.Add(new());
        rest.RowDefinitions.Add(new()); rest.RowDefinitions.Add(new());
        rest.Children.Add(x); rest.Children.Add(y); Grid.SetColumn(y, 1);
        rest.Children.Add(endX); Grid.SetRow(endX, 1);
        rest.Children.Add(endY); Grid.SetRow(endY, 1); Grid.SetColumn(endY, 1);
        restPanel.Children.Add(rest);
        Action("Apply rest bone / parent", () =>
        {
            if (SelectedBone() is not { } bone) { message.Text = "Add or select a bone."; return; }
            InkCharacterRig candidate = InkCharacterRigs.Clone(rig);
            InkRigBone edited = candidate.Bones.First(item => item.Id == bone.Id);
            edited.Name = name.Text.Trim(); edited.ParentId = (parent.SelectedItem as ComboBoxItem)?.Tag as string;
            edited.X = x.Value; edited.Y = y.Value; edited.EndX = endX.Value; edited.EndY = endY.Value;
            if (InkCharacterRigs.Validate(candidate, draft.InkLayers) is string error) { message.Text = error; return; }
            rig.Bones = candidate.Bones; RefreshBones(edited.Id);
            message.Text = "Rest hierarchy updated. Existing pose keys remain relative to the rest joints.";
        });
        Action("Remove selected bone", () =>
        {
            if (SelectedBone() is not { } bone) return;
            if (rig.Bones.Any(item => item.ParentId == bone.Id) || draft.InkLayers.Any(item =>
                item.RigBinding?.BoneId == bone.Id || item.RigBinding?.Influences.ContainsKey(bone.Id) == true))
            { message.Text = "Reparent child bones and remove artwork bindings/influences before deleting this bone."; return; }
            rig.Bones.Remove(bone); RefreshBones(null);
        });
        actionPanel = posePanel;
        var poseGrid = new Grid { ColumnSpacing = 8, RowSpacing = 4 };
        poseGrid.ColumnDefinitions.Add(new()); poseGrid.ColumnDefinitions.Add(new());
        for (int row = 0; row < 3; row++) poseGrid.RowDefinitions.Add(new());
        FrameworkElement[] poseFields = [frame, keys, rotation, offsetX, offsetY, stretch];
        for (int index = 0; index < poseFields.Length; index++)
        {
            Grid.SetRow(poseFields[index], index / 2); Grid.SetColumn(poseFields[index], index % 2);
            poseGrid.Children.Add(poseFields[index]);
        }
        posePanel.Children.Add(poseGrid); posePanel.Children.Add(easing);
        Action("Set pose key at frame", () =>
        {
            if (SelectedBone() is not { } bone) { message.Text = "Select a bone."; return; }
            if (!double.IsFinite(frame.Value) || frame.Value != Math.Truncate(frame.Value))
            { message.Text = "Choose a whole timeline frame."; return; }
            var pose = new InkRigPose(Seconds(), rotation.Value, offsetX.Value, offsetY.Value, stretch.Value,
                (InkInterpolationMode)easing.SelectedItem);
            InkCharacterRig candidate = InkCharacterRigs.Clone(rig);
            InkCharacterRigs.SetPose(candidate.Bones.First(item => item.Id == bone.Id), pose);
            if (InkCharacterRigs.Validate(candidate, draft.InkLayers) is string error) { message.Text = error; return; }
            rig.Bones = candidate.Bones; RefreshBones(bone.Id);
            message.Text = $"Pose key saved at frame {frame.Value:0}. Add another key to animate between them.";
        });
        Action("Remove pose key at frame", () =>
        {
            if (SelectedBone() is not { } bone) return;
            bone.Keys.RemoveAll(key => Math.Abs(key.Seconds - Seconds()) < 0.000001);
            ReadBone(); Preview();
        });
        actionPanel = bindingPanel;
        bindingPanel.Children.Add(layer); bindingPanel.Children.Add(mode); bindingPanel.Children.Add(influence);
        bindingPanel.Children.Add(influenceList);
        Action("Apply layer binding / selected influence", () =>
        {
            if (layer.SelectedItem is not InkLayerProject artwork || artwork.IsLocked)
            { message.Text = "Select an unlocked artwork layer."; return; }
            if (mode.SelectedIndex == 0) { artwork.RigBinding = null; ReadBinding(); Preview(); return; }
            if (SelectedBone() is not { } bone) { message.Text = "Select a bone."; return; }
            InkRigBinding binding = artwork.RigBinding is null ? new() : InkCharacterRigs.Clone(artwork.RigBinding);
            binding.Mode = mode.SelectedIndex == 1 ? InkRigBindingMode.CutOut : InkRigBindingMode.Weighted;
            binding.BoneId = bone.Id;
            if (!double.IsFinite(influence.Value)) { message.Text = "Enter a finite influence multiplier."; return; }
            if (influence.Value > 0) binding.Influences[bone.Id] = influence.Value;
            else binding.Influences.Remove(bone.Id);
            if (binding.Mode == InkRigBindingMode.Weighted && binding.Influences.Count == 0)
            { message.Text = "Weighted artwork needs at least one positive influence. Set this bone's multiplier above zero."; return; }
            artwork.RigBinding = binding;
            ReadBinding(); Preview();
            message.Text = "Binding updated. Weighted points blend influenced bones by distance to their rest segments; adjust each bone's multiplier to tune the bend.";
        });
        panel.Children.Add(message);
        bones.SelectionChanged += (_, _) => ReadBone();
        layer.SelectionChanged += (_, _) => ReadBinding();
        frame.ValueChanged += (_, _) => ReadPose();
        keys.SelectionChanged += (_, _) => { if (keys.SelectedItem is ComboBoxItem { Tag: InkRigPose key }) frame.Value = Math.Round(key.Seconds * _frameRate) + 1; };
        enabled.Click += (_, _) => { rig.Enabled = enabled.IsChecked == true; Preview(); };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot, Title = "Character rig: cut-out and weighted artwork",
            Content = panel,
            PrimaryButtonText = "Save rig", CloseButtonText = "Cancel",
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (InkCharacterRigs.Validate(rig, draft.InkLayers) is string error)
            { args.Cancel = true; message.Text = error; }
        };
        RefreshBones(null);
        ContentDialogResult result;
        try { result = await dialog.ShowAsync(); }
        finally { _characterRigPreview = null; _characterRigPreviewSeconds = null; RenderCurrentInk(); }
        if (result != ContentDialogResult.Primary) return;
        BeginEdit();
        _characterRig = InkCharacterRigs.Clone(rig);
        for (int index = 0; index < _inkLayers.Count; index++)
            _inkLayers[index].RigBinding = draft.InkLayers[index].RigBinding is { } binding ? InkCharacterRigs.Clone(binding) : null;
        MarkEditChanged(); CommitEdit(); RenderCurrentInk();
        StatusText.Text = "Character rig saved. Pose keys follow the shared timeline; preview and export use the same deformation. Turn animated rig preview off before editing original drawings.";
    }
}
