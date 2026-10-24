using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InkMotion;

public sealed partial class MainWindow
{
    private List<EditorFrame> _copiedCelRange = [];

    private void InkCelRangeAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string action } || action is not ("copy" or "paste" or "move" or "delete"))
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        double first = InkCycleFirstBox.Value;
        double last = InkCycleLastBox.Value;
        double before = InkRangeBeforeBox.Value;
        if (action != "paste" && (!double.IsFinite(first) || !double.IsFinite(last) ||
            first != Math.Truncate(first) || last != Math.Truncate(last) ||
            first < 1 || last < first || last > _frames.Count))
        {
            StatusText.Text = "Choose existing whole first/last cel numbers in ascending order.";
            return;
        }

        if (action is "paste" or "move" && (!double.IsFinite(before) || before != Math.Truncate(before) ||
            before < 1 || before > (long)_frames.Count + 1))
        {
            StatusText.Text = "Choose an insertion cel from 1 to the cel count plus one (append).";
            return;
        }

        bool editing = false;
        try
        {
            if (action == "copy")
            {
                _copiedCelRange = InkEditing.CopyCelRange(_frames, (int)first - 1, (int)last - 1);
                PasteInkCelRangeButton.IsEnabled = true;
                StatusText.Text = $"{_copiedCelRange.Count} cels copied with all layers, text, exposure and tween settings. Open tween edges were materialised in the copy; project unchanged.";
                return;
            }

            if (action == "move" && before >= first && before <= last + 1)
            {
                StatusText.Text = "This insertion position leaves the cel range unchanged.";
                return;
            }

            BeginEdit();
            editing = true;
            _selectedFrameIndex = action switch
            {
                "paste" => InkEditing.PasteCelRange(_frames, _copiedCelRange, (int)before - 1),
                "delete" => InkEditing.DeleteCelRange(_frames, (int)first - 1, (int)last - 1),
                _ => InkEditing.MoveCelRange(_frames, (int)first - 1, (int)last - 1, (int)before - 1),
            };
            ClearInkSelection();
            _playheadFramePosition = InkTimeline.GetStartFramePosition(_frames, _selectedFrameIndex);
            MarkEditChanged();
            CommitEdit();
            editing = false;
            RefreshTimeline();
            RenderCurrentInk();
            SeekToCurrentFrame();
            UpdateFrameStatus();
            StatusText.Text = action switch
            {
                "paste" => "Independent cel range pasted. Later cels shift; video, audio and markers stay fixed. Undo restores the timeline.",
                "delete" => "Cel range deleted. Later cels shift earlier; video, audio and markers stay fixed. Undo restores the timeline.",
                _ => "Cel range moved using the original timeline's insertion position. Video, audio and markers stay fixed. Undo restores the timeline.",
            };
        }
        catch (InvalidOperationException ex)
        {
            if (editing)
            {
                CommitEdit();
            }

            StatusText.Text = ex.Message;
        }
    }
}
