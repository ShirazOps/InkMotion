using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InkMotion;

public sealed partial class MainWindow
{
    private void RefreshInkCelGroups()
    {
        if (InkCelGroupSelector is null)
        {
            return;
        }
        InkCelGroupRange? previous = (InkCelGroupSelector.SelectedItem as ComboBoxItem)?.Tag as InkCelGroupRange;
        InkCelGroupSelector.Items.Clear();
        foreach (InkCelGroupRange range in InkCelGroups.GetRanges(_frames))
        {
            var item = new ComboBoxItem
            {
                Content = $"{range.Name} ({range.First + 1}-{range.Last + 1})",
                Tag = range,
            };
            InkCelGroupSelector.Items.Add(item);
            if (range == previous)
            {
                InkCelGroupSelector.SelectedItem = item;
            }
        }
    }

    private void SelectInkCelGroup_Click(object sender, RoutedEventArgs e)
    {
        if (InkCelGroupSelector.SelectedItem is not ComboBoxItem { Tag: InkCelGroupRange range } ||
            !InkCelGroups.GetRanges(_frames).Contains(range))
        {
            StatusText.Text = "Choose an existing cel group.";
            return;
        }
        PausePlayback();
        FinishActiveEdit();
        InkCycleFirstBox.Value = range.First + 1;
        InkCycleLastBox.Value = range.Last + 1;
        InkCelGroupNameBox.Text = range.Name;
        SelectFrame(range.First);
        StatusText.Text = $"Group '{range.Name}' loaded into the cel-range controls. Copy/move/delete/repeat still require keyframes at both ends.";
    }

    private void AssignInkCelGroup_Click(object sender, RoutedEventArgs e)
    {
        double first = InkCycleFirstBox.Value;
        double last = InkCycleLastBox.Value;
        bool clear = sender is FrameworkElement { Tag: "clear" };
        string name = clear ? "" : InkCelGroupNameBox.Text.Trim();
        if (!double.IsFinite(first) || !double.IsFinite(last) ||
            first != Math.Truncate(first) || last != Math.Truncate(last) ||
            first < 1 || last < first || last > _frames.Count || (!clear && name.Length == 0))
        {
            StatusText.Text = "Enter an existing whole first/last cel range and a non-empty group name.";
            return;
        }
        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        try
        {
            bool changed = InkCelGroups.Assign(_frames, (int)first - 1, (int)last - 1, name);
            if (changed)
            {
                MarkEditChanged();
            }
            CommitEdit();
            RefreshTimeline();
            StatusText.Text = changed
                ? clear ? "Cel range ungrouped. Drawings and timing are unchanged."
                    : $"Cel range labelled '{name}'. Adjacent cels with the same name form a group; drawings and timing are unchanged."
                : "Cel grouping is unchanged.";
        }
        catch (ArgumentException ex)
        {
            CommitEdit();
            StatusText.Text = $"Could not group cels: {ex.Message}";
        }
    }
}
