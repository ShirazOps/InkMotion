using Microsoft.UI.Xaml;

namespace InkMotion;

public sealed partial class MainWindow
{
    private void JoinVideoClip_Click(object sender, RoutedEventArgs e)
    {
        PausePlayback();
        FinishActiveEdit();
        if (!TryGetEditableClip(out TimelineClipLocation selected))
        {
            return;
        }

        TimelineClipProject[] ordered = selected.Track.Clips.OrderBy(clip => clip.StartFrame).ToArray();
        int index = Array.IndexOf(ordered, selected.Clip);
        if (index < 0 || index + 1 >= ordered.Length)
        {
            StatusText.Text = "Select a video clip with a following clip on the same track.";
            return;
        }

        TimelineClipProject next = ordered[index + 1];
        TimelineClipProject joined;
        try
        {
            joined = VideoClipJoining.Join(selected.Track, selected.Clip, next, _frameRate);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            StatusText.Text = ex.Message;
            return;
        }

        BeginEdit();
        selected.Track.Clips[selected.Track.Clips.IndexOf(selected.Clip)] = joined;
        selected.Track.Clips.Remove(next);
        MarkEditChanged();
        CommitEdit();
        RefreshVideoTimeline();
        SeekToCurrentFrame();
        StatusText.Text = "Adjacent video sections joined without rendering. Outer fades and source timing are retained; undo restores the cut.";
    }
}
