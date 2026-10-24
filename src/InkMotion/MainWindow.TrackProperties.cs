using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace InkMotion;

public sealed partial class MainWindow
{
    private bool _trackPropertiesOpen;

    private void InkTrackProperties_Click(object sender, RoutedEventArgs e)
    {
        PausePlayback();
        FinishActiveEdit();
        ShowWorkspace("Ink");
        SetEditorLayout(_editorLayouts.Workspaces["Ink"] with { ShowOptions = true });
        InkLayerSelector.StartBringIntoView();
        InkLayerSelector.Focus(FocusState.Programmatic);
    }

    private MenuFlyout CreateTimelineTrackMenu(TimelineTrackProject track, TimelineClipProject? clip = null)
    {
        var menu = new MenuFlyout();
        var properties = new MenuFlyoutItem { Text = $"{VideoTimelineEditing.KindLabel(track.Kind)} track properties..." };
        properties.Click += (_, _) => ShowTimelineTrackProperties(track);
        menu.Items.Add(properties);
        if (clip is not null)
        {
            var clipProperties = new MenuFlyoutItem { Text = $"{VideoTimelineEditing.KindLabel(track.Kind)} clip properties..." };
            clipProperties.Click += (_, _) => ShowTimelineClipProperties(track, clip);
            menu.Items.Add(clipProperties);
        }
        if (track.Kind == TimelineTrackKind.Text && track.Clips.Count > 0)
        {
            var edit = new MenuFlyoutItem { Text = "Edit title text...", IsEnabled = !track.IsLocked };
            edit.Click += (_, _) =>
            {
                if (clip is null)
                {
                    SelectTitleOnTrack(track);
                }
                else
                {
                    ShowTimelineClipProperties(track, clip);
                }
                EditSelectedTitle();
            };
            menu.Items.Add(edit);
        }
        return menu;
    }

    private void VideoTimelineCanvas_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        EndTimelinePointer();
        var point = e.GetPosition(VideoTimelineCanvas);
        int index = GetTrackIndexAt(point.Y);
        if (index < 0)
        {
            return;
        }
        TimelineTrackProject track = _videoTracks[index];
        TimelineClipProject? clip = VideoTimelineEditing.FindClipAt(
            track, Math.Max(0, point.X / _timelinePixelsPerSecond), _frameRate)?.Clip;
        PausePlayback();
        FinishActiveEdit();
        _selectedTimelineTrackId = track.Id;
        _selectedTimelineClipId = clip?.Id;
        RefreshVideoTrackHeaders();
        UpdateVideoInspector();
        InvalidateVideoTimeline();
        CreateTimelineTrackMenu(track, clip).ShowAt(VideoTimelineCanvas, point);
        e.Handled = true;
    }

    private void ShowTimelineClipProperties(TimelineTrackProject track, TimelineClipProject clip)
    {
        PausePlayback();
        FinishActiveEdit();
        _selectedTimelineTrackId = track.Id;
        _selectedTimelineClipId = clip.Id;
        ShowWorkspace("Video");
        SetEditorLayout(_editorLayouts.Workspaces["Video"] with { ShowOptions = true, ShowPreview = true });
        MoveTimelinePlayhead(VideoTimelineEditing.GetStartSeconds(clip, _frameRate));
        RefreshVideoTrackHeaders();
        UpdateVideoInspector();
        ClipTextBox.StartBringIntoView();
        if (track.Kind == TimelineTrackKind.Text)
        {
            ClipTextBox.Focus(FocusState.Programmatic);
        }
        else
        {
            ClipOpacitySlider.StartBringIntoView();
            ClipOpacitySlider.Focus(FocusState.Programmatic);
        }
    }

    private async void ShowTimelineTrackProperties(TimelineTrackProject track)
    {
        if (_trackPropertiesOpen || !_videoTracks.Contains(track))
        {
            return;
        }
        PausePlayback();
        FinishActiveEdit();
        var name = new TextBox { Header = "Track name", Text = track.Name, MaxLength = 128 };
        var visible = new CheckBox { Content = "Visible", IsChecked = track.IsVisible };
        var locked = new CheckBox { Content = "Locked", IsChecked = track.IsLocked };
        var muted = new CheckBox { Content = "Mute embedded audio", IsChecked = track.IsMuted,
            Visibility = track.Kind == TimelineTrackKind.Video ? Visibility.Visible : Visibility.Collapsed };
        var panel = new StackPanel { Spacing = 12, MinWidth = 300 };
        panel.Children.Add(name);
        panel.Children.Add(new TextBlock { Text = $"{VideoTimelineEditing.KindLabel(track.Kind)} · {track.Clips.Count} clip(s)" });
        panel.Children.Add(visible);
        panel.Children.Add(locked);
        panel.Children.Add(muted);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot, Title = $"{VideoTimelineEditing.KindLabel(track.Kind)} track properties",
            Content = panel, PrimaryButtonText = "Apply", CloseButtonText = "Cancel",
        };
        name.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(name.Text);
        _trackPropertiesOpen = true;
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && _videoTracks.Contains(track) &&
                !string.IsNullOrWhiteSpace(name.Text))
            {
                string newName = name.Text.Trim();
                bool newMuted = track.Kind == TimelineTrackKind.Video && muted.IsChecked == true;
                if (track.Name != newName || track.IsVisible != (visible.IsChecked == true) ||
                    track.IsLocked != (locked.IsChecked == true) || track.IsMuted != newMuted)
                {
                    BeginEdit();
                    track.Name = newName;
                    track.IsVisible = visible.IsChecked == true;
                    track.IsLocked = locked.IsChecked == true;
                    track.IsMuted = newMuted;
                    MarkEditChanged();
                    CommitEdit();
                    RefreshVideoTimeline();
                    SyncVideoPreview(CurrentPlaybackSeconds, forceSeek: true);
                    TitleAdornerSurface.Invalidate();
                }
            }
        }
        finally
        {
            _trackPropertiesOpen = false;
        }
    }

    private MenuFlyout CreateAudioPropertiesMenu(AudioTrackRuntime track)
    {
        var menu = new MenuFlyout();
        var item = new MenuFlyoutItem { Text = "Audio clip properties..." };
        item.Click += (_, _) =>
        {
            int index = _audioTracks.IndexOf(track);
            if (index < 0)
            {
                return;
            }
            PausePlayback();
            FinishActiveEdit();
            ShowWorkspace("Audio");
            SetEditorLayout(_editorLayouts.Workspaces["Audio"] with { ShowOptions = true });
            SelectAudioTrack(index);
            AudioVolumeSlider.StartBringIntoView();
        };
        menu.Items.Add(item);
        return menu;
    }
}
