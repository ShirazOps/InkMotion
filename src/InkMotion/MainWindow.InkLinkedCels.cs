using Microsoft.UI.Xaml;

namespace InkMotion;

public sealed partial class MainWindow
{
    private bool TryGetLinkedCelRange(out int first, out int last)
    {
        double a = InkCycleFirstBox.Value;
        double b = InkCycleLastBox.Value;
        first = last = 0;
        if (!double.IsFinite(a) || !double.IsFinite(b) || a != Math.Truncate(a) || b != Math.Truncate(b) ||
            a < 1 || b < a || b > _frames.Count)
        {
            StatusText.Text = "Choose existing whole first/last cel numbers in ascending order.";
            return false;
        }
        first = (int)a - 1;
        last = (int)b - 1;
        return true;
    }

    private void RepeatLinkedInkCels_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetLinkedCelRange(out int first, out int last))
        {
            return;
        }
        double copies = InkCycleCopiesBox.Value;
        if (!double.IsFinite(copies) || copies != Math.Truncate(copies) || copies is < 1 or > 100)
        {
            StatusText.Text = "Choose 1-100 additional linked copies.";
            return;
        }
        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        try
        {
            _selectedFrameIndex = InkLinkedCels.Repeat(_frames, _inkLayers, first, last, (int)copies);
            MarkEditChanged();
            CommitEdit();
            ClearInkSelection();
            _playheadFramePosition = InkTimeline.GetStartFramePosition(_frames, _selectedFrameIndex);
            RefreshTimeline();
            RenderCurrentInk();
            SeekToCurrentFrame();
            UpdateFrameStatus();
            StatusText.Text = "Linked cycle created. Source in-betweens are now explicit keyframes; drawing/text edits propagate to matching phases. Exposure and other media stay independent.";
        }
        catch (InvalidOperationException ex)
        {
            CommitEdit();
            StatusText.Text = $"Could not repeat linked cels: {ex.Message}";
        }
    }

    private void UnlinkInkCels_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetLinkedCelRange(out int first, out int last))
        {
            return;
        }
        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        try
        {
            bool changed = InkLinkedCels.Unlink(_frames, first, last);
            if (changed)
            {
                MarkEditChanged();
            }
            CommitEdit();
            RefreshTimeline();
            StatusText.Text = changed
                ? "Range drawings unlinked without changing their appearance or timing. Subsequent edits are independent; undo restores links."
                : "This range has no drawing links.";
        }
        catch (InvalidOperationException ex)
        {
            CommitEdit();
            StatusText.Text = ex.Message;
        }
    }
}
