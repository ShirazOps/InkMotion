using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InkMotion;

public sealed partial class MainWindow
{
    private readonly List<TimelineMarkerProject> _timelineMarkers = [];
    private bool _refreshingMarkers;

    private void JumpToMarker(int frame)
    {
        PausePlayback();
        FinishActiveEdit();
        _playheadFramePosition = frame - 1;
        SelectFrame(InkTimeline.GetFrameIndexAtPosition(_frames, _playheadFramePosition),
            seekVideo: false, preservePlayhead: true);
        SeekToCurrentFrame();
        UpdateFrameReadout();
        InvalidateVideoTimeline();
        double x = _playheadFramePosition / _frameRate * _timelinePixelsPerSecond;
        VideoTimelineScroller.ChangeView(Math.Max(0, x - VideoTimelineScroller.ActualWidth / 2), null, null, true);
    }

    private void RefreshMarkers(string? selectedId = null)
    {
        if (MarkerSelector is null)
        {
            return;
        }

        selectedId ??= (MarkerSelector.SelectedItem as TimelineMarkerProject)?.Id;
        _refreshingMarkers = true;
        MarkerSelector.ItemsSource = _timelineMarkers.OrderBy(marker => marker.Frame).ThenBy(marker => marker.Name).ToList();
        MarkerSelector.SelectedItem = _timelineMarkers.FirstOrDefault(marker => marker.Id == selectedId);
        MarkerNameBox.Text = (MarkerSelector.SelectedItem as TimelineMarkerProject)?.Name ?? string.Empty;
        MarkerActions.IsEnabled = MarkerSelector.SelectedItem is TimelineMarkerProject;
        _refreshingMarkers = false;
        InvalidateVideoTimeline();
    }

    private void Marker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingMarkers)
        {
            return;
        }

        MarkerNameBox.Text = (MarkerSelector.SelectedItem as TimelineMarkerProject)?.Name ?? string.Empty;
        MarkerActions.IsEnabled = MarkerSelector.SelectedItem is TimelineMarkerProject;
    }

    private void AddMarker_Click(object sender, RoutedEventArgs e)
    {
        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        var marker = new TimelineMarkerProject { Frame = PlayheadFrameNumber, Name = $"Marker {_timelineMarkers.Count + 1}" };
        _timelineMarkers.Add(marker);
        MarkEditChanged();
        CommitEdit();
        RefreshMarkers(marker.Id);
        MarkerSelector.StartBringIntoView();
        StatusText.Text = $"Marker added at frame {marker.Frame}.";
    }

    private void MarkerAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string action })
        {
            return;
        }

        if (action is "previous" or "next")
        {
            int? frame = TimelineMarkers.FindAdjacent(_timelineMarkers, PlayheadFrameNumber, action == "next");
            if (frame is int adjacent)
            {
                JumpToMarker(adjacent);
                RefreshMarkers(_timelineMarkers.First(marker => marker.Frame == adjacent).Id);
            }
            else
            {
                StatusText.Text = $"No {action} marker.";
            }

            return;
        }

        if (MarkerSelector.SelectedItem is not TimelineMarkerProject selected)
        {
            StatusText.Text = "Select a timeline marker first.";
            return;
        }

        if (action == "jump")
        {
            JumpToMarker(selected.Frame);
            return;
        }

        string name = MarkerNameBox.Text.Trim();
        if (action == "rename" && (name.Length == 0 || name.Length > 200))
        {
            StatusText.Text = "Marker names must contain 1-200 characters.";
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        switch (action)
        {
            case "rename":
                selected.Name = name;
                break;
            case "move":
                selected.Frame = PlayheadFrameNumber;
                break;
            case "delete":
                _timelineMarkers.Remove(selected);
                break;
            default:
                CommitEdit();
                return;
        }

        MarkEditChanged();
        CommitEdit();
        RefreshMarkers(selected.Id);
        StatusText.Text = action == "delete" ? "Marker removed; media is unchanged." : "Timeline marker updated.";
    }
}
