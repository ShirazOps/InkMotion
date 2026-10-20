using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using VirtualKey = Windows.System.VirtualKey;

namespace InkMotion;

public sealed partial class MainWindow
{
    private EditorLayoutPreferences _editorLayouts = new();
    private DispatcherQueueTimer? _layoutSaveTimer;
    private bool _layoutReady;
    private bool _layoutDirty;
    private readonly string _layoutPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InkMotion", "editor-layout.json");

    private void InitialiseEditorLayout()
    {
        try
        {
            _editorLayouts = EditorLayoutPreferences.Load(_layoutPath);
        }
        catch (Exception ex) when (IsLayoutFileException(ex))
        {
            StatusText.Text = $"Could not restore editor layout; using defaults: {ex.Message}";
        }
        _layoutSaveTimer = DispatcherQueue.CreateTimer();
        _layoutSaveTimer.Interval = TimeSpan.FromMilliseconds(300);
        _layoutSaveTimer.IsRepeating = false;
        _layoutSaveTimer.Tick += (_, _) => SaveEditorLayout();
        _layoutReady = true;
        ApplyEditorLayout();
    }

    private static bool IsLayoutFileException(Exception ex) =>
        ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException;

    private void ApplyEditorLayout()
    {
        if (!_layoutReady) return;
        WorkspaceLayout layout = _editorLayouts.Workspaces[_workspace];
        double width = Math.Max(0, EditorLayoutGrid.ActualWidth - 16);
        double height = Math.Max(0, EditorLayoutGrid.ActualHeight - 16);
        EditorOptionsColumn.Width = new GridLength(layout.GetOptionsWidth(width));
        EditorDividerColumn.Width = new GridLength(layout.ShowOptions ? 10 : 0);
        EditorPreviewRow.Height = layout.ShowTimeline
            ? new GridLength(layout.GetPreviewHeight(height)) : new GridLength(1, GridUnitType.Star);
        EditorDividerRow.Height = new GridLength(layout.ShowPreview && layout.ShowTimeline ? 10 : 0);
        EditorTimelineRow.Height = layout.ShowTimeline ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        EditorPreviewHeader.Visibility = layout.ShowPreview ? Visibility.Visible : Visibility.Collapsed;
        PreviewViewport.Visibility = layout.ShowPreview ? Visibility.Visible : Visibility.Collapsed;
        EditorPreviewPanel.RowSpacing = layout.ShowPreview ? 8 : 0;
        EditorOptionsGrip.Visibility = layout.ShowOptions ? Visibility.Visible : Visibility.Collapsed;
        EditorTimelineGrip.Visibility = layout.ShowPreview && layout.ShowTimeline ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetRowSpan(EditorOptionsGrip, layout.ShowPreview ? 1 : 3);
        foreach (Grid panel in new[] { VideoInspectorPanel, AudioInspectorPanel, InkInspectorPanel, ExportInspectorPanel })
        {
            bool selected = panel == (_workspace switch
            {
                "Audio" => AudioInspectorPanel, "Ink" => InkInspectorPanel,
                "Export" => ExportInspectorPanel, _ => VideoInspectorPanel,
            });
            panel.Visibility = layout.ShowOptions && selected ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetRowSpan(panel, layout.ShowPreview ? 1 : 3);
        }
        foreach (Grid timeline in new[] { VideoTimelineWorkspace, AudioTimelineWorkspace, InkTimelineWorkspace })
        {
            bool selected = timeline == (_workspace switch
            {
                "Audio" => AudioTimelineWorkspace, "Ink" => InkTimelineWorkspace, _ => VideoTimelineWorkspace,
            });
            timeline.Visibility = layout.ShowTimeline && selected ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetColumnSpan(timeline, layout.ShowPreview || !layout.ShowOptions ? 3 : 1);
        }
        LayoutOptionsMenu.IsChecked = layout.ShowOptions;
        LayoutTimelineMenu.IsChecked = layout.ShowTimeline;
        LayoutPreviewMenu.IsChecked = layout.ShowPreview;
    }

    private void SetEditorLayout(WorkspaceLayout layout)
    {
        if (!_layoutReady || !layout.IsValid) return;
        _editorLayouts.Workspaces[_workspace] = layout;
        ApplyEditorLayout();
        _layoutDirty = true;
        _layoutSaveTimer!.Stop();
        _layoutSaveTimer.Start();
    }

    private void LayoutPanel_Click(object sender, RoutedEventArgs e)
    {
        WorkspaceLayout layout = _editorLayouts.Workspaces[_workspace];
        if (sender is ToggleMenuFlyoutItem { Tag: string panel } toggle)
        {
            SetEditorLayout(panel switch
            {
                "options" => layout with { ShowOptions = toggle.IsChecked },
                "timeline" => layout with { ShowTimeline = toggle.IsChecked, ShowPreview = !toggle.IsChecked || layout.ShowPreview },
                _ => layout with { ShowPreview = toggle.IsChecked, ShowTimeline = !toggle.IsChecked || layout.ShowTimeline },
            });
            SaveEditorLayout();
        }
    }

    private void ExpandEditingView_Click(object sender, RoutedEventArgs e)
    {
        SetEditorLayout(_editorLayouts.Workspaces[_workspace] with
        {
            ShowOptions = false, ShowPreview = _workspace != "Audio", ShowTimeline = _workspace == "Audio",
        });
        SaveEditorLayout();
    }

    private void ResetEditorLayout_Click(object sender, RoutedEventArgs e)
    {
        _editorLayouts = new();
        SetEditorLayout(_editorLayouts.Workspaces[_workspace]);
        SaveEditorLayout();
        if (!_layoutDirty) StatusText.Text = "All workspace layouts reset to default and saved.";
    }

    private void EditorLayout_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyEditorLayout();

    private void AudioLayout_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateAudioLaneHeights();

    private void UpdateAudioLaneHeights()
    {
        int count = Math.Max(1, _audioTracks.Count);
        double height = Math.Max(42, (AudioTimelineWorkspace.ActualHeight - 24 - (count - 1) * 4) / count);
        foreach (FrameworkElement lane in AudioTracksPanel.Children.OfType<FrameworkElement>())
        {
            lane.Height = height;
        }
        foreach (FrameworkElement name in AudioTrackNamesPanel.Children.OfType<FrameworkElement>())
        {
            name.Height = height;
        }
    }

    private void ResizeEditorLayout(bool options, double delta)
    {
        WorkspaceLayout layout = _editorLayouts.Workspaces[_workspace];
        if (options && layout.ShowOptions)
        {
            SetEditorLayout(layout with { OptionsWidth = Math.Clamp(EditorOptionsColumn.ActualWidth - delta, 220, 900) });
        }
        else if (!options && layout.ShowPreview && layout.ShowTimeline)
        {
            double available = Math.Max(1, EditorLayoutGrid.ActualHeight - 26);
            SetEditorLayout(layout with { PreviewFraction = Math.Clamp((EditorPreviewRow.ActualHeight + delta) / available, 0.1, 0.9) });
        }
    }

    private void LayoutGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        bool options = ReferenceEquals(sender, EditorOptionsGrip);
        ResizeEditorLayout(options, options ? e.HorizontalChange : e.VerticalChange);
    }

    private void LayoutGrip_DragCompleted(object sender, DragCompletedEventArgs e) => SaveEditorLayout();

    private void LayoutGrip_PointerPressed(object sender, PointerRoutedEventArgs e) =>
        ((Control)sender).Focus(FocusState.Pointer);

    private void LayoutGrip_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool options = ReferenceEquals(sender, EditorOptionsGrip);
        if (e.Key == VirtualKey.Home)
        {
            WorkspaceLayout layout = _editorLayouts.Workspaces[_workspace];
            SetEditorLayout(options ? layout with { OptionsWidth = 340 } : layout with { PreviewFraction = 0.6 });
        }
        else if (options && e.Key is VirtualKey.Left or VirtualKey.Right)
        {
            ResizeEditorLayout(true, e.Key == VirtualKey.Left ? -16 : 16);
        }
        else if (!options && e.Key is VirtualKey.Up or VirtualKey.Down)
        {
            ResizeEditorLayout(false, e.Key == VirtualKey.Up ? -16 : 16);
        }
        else return;
        e.Handled = true;
    }

    private void SaveEditorLayout()
    {
        _layoutSaveTimer?.Stop();
        if (!_layoutDirty) return;
        try
        {
            _editorLayouts.Save(_layoutPath);
            _layoutDirty = false;
        }
        catch (Exception ex) when (IsLayoutFileException(ex))
        {
            StatusText.Text = $"Could not save editor layout: {ex.Message}";
        }
    }
}
