using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Dispatching;
using System.IO;
using System.Text.Json;
using Windows.Media.Core;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Foundation;
using WinRT.Interop;

namespace InkMotion;

public sealed partial class MainWindow : Window
{
    private const double FramesPerSecond = 24;
    private const int MaximumHistoryEntries = 100;
    private readonly List<EditorFrame> _frames = [new()];
    private readonly List<Button> _timelineButtons = [];
    private readonly HashSet<int> _interpolatedFrameIndices = [];
    private readonly Stack<EditorProject> _undoHistory = [];
    private readonly Stack<EditorProject> _redoHistory = [];
    private readonly DispatcherQueueTimer _playbackTimer;
    private int _selectedFrameIndex;
    private double _frameRate = FramesPerSecond;
    private uint? _activePointerId;
    private List<InkPoint>? _activeStroke;
    private EditorProject? _pendingEdit;
    private bool _pendingEditChanged;
    private StorageFile? _projectFile;
    private string? _videoPath;
    private bool _isPlaying;
    private bool _hasVideo;
    private bool _isEraser;

    public MainWindow()
    {
        InitializeComponent();
        Title = "InkMotion";
        _playbackTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _playbackTimer.Interval = TimeSpan.FromMilliseconds(16);
        _playbackTimer.Tick += PlaybackTimer_Tick;
        RefreshTimeline();
        RenderCurrentInk();
        UpdateFrameStatus();
    }

    private EditorFrame CurrentFrame => _frames[_selectedFrameIndex];

    private async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        PausePlayback();
        FinishActiveEdit();
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".inkmotion");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            EditorProject project = EditorProjectSerializer.Deserialize(await FileIO.ReadTextAsync(file));
            ClearHistory();
            RestoreProjectState(project);
            _projectFile = file;
            UnsavedIndicator.Visibility = Visibility.Collapsed;

            if (!string.IsNullOrWhiteSpace(_videoPath) && File.Exists(_videoPath))
            {
                try
                {
                    LoadVideo(await StorageFile.GetFileFromPathAsync(_videoPath), markDirty: false);
                    StatusText.Text = $"Project opened  ·  {file.Name}";
                }
                catch (Exception ex) when (IsFileAccessException(ex))
                {
                    ClearVideoSource();
                    StatusText.Text = $"Project opened; source video is unavailable: {ex.Message}";
                }
            }
            else
            {
                ClearVideoSource();
                StatusText.Text = string.IsNullOrWhiteSpace(_videoPath)
                    ? $"Project opened  ·  {file.Name}"
                    : "Project opened; source video is missing. Use Open video to relink it.";
            }
        }
        catch (Exception ex) when (IsProjectFileException(ex))
        {
            StatusText.Text = $"Could not open project: {ex.Message}";
        }
    }

    private async void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        PausePlayback();
        FinishActiveEdit();
        StorageFile? file = _projectFile;
        if (file is null)
        {
            var picker = new FileSavePicker
            {
                SuggestedFileName = "Untitled.inkmotion",
                DefaultFileExtension = ".inkmotion",
            };
            picker.FileTypeChoices.Add("InkMotion project", [".inkmotion"]);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }
        }

        try
        {
            await FileIO.WriteTextAsync(file, EditorProjectSerializer.Serialize(CreateProjectSnapshot()));
            _projectFile = file;
            UnsavedIndicator.Visibility = Visibility.Collapsed;
            StatusText.Text = $"Project saved  ·  {file.Name}";
        }
        catch (Exception ex) when (IsProjectFileException(ex))
        {
            StatusText.Text = $"Could not save project: {ex.Message}";
        }
    }

    private async void OpenVideo_Click(object sender, RoutedEventArgs e)
    {
        PausePlayback();
        FinishActiveEdit();
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".mp4");
        picker.FileTypeFilter.Add(".mov");
        picker.FileTypeFilter.Add(".m4v");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        LoadVideo(file);
        ClearHistory();
        SeekToCurrentFrame();
        StatusText.Text = $"Loaded  ·  {file.Name}";
    }

    private void LoadVideo(StorageFile file, bool markDirty = true)
    {
        PausePlayback();
        PreviewPlayer.Source = MediaSource.CreateFromStorageFile(file);
        PreviewPlayer.MediaPlayer.IsLoopingEnabled = false;
        EmptyState.Visibility = Visibility.Collapsed;
        VideoTrackLabel.Text = file.Name;
        _videoPath = file.Path;
        _hasVideo = true;
        UnsavedIndicator.Visibility = markDirty ? Visibility.Visible : Visibility.Collapsed;
        SeekToCurrentFrame();
    }

    private void ClearVideoSource()
    {
        PausePlayback();
        PreviewPlayer.Source = null;
        EmptyState.Visibility = Visibility.Visible;
        VideoTrackLabel.Text = "Drop a video here or use Open video";
        _hasVideo = false;
        _isPlaying = false;
        PlayButton.Content = "Play";
    }

    private static bool IsProjectFileException(Exception exception) =>
        exception is JsonException or InvalidDataException or IOException or
            UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException;

    private static bool IsFileAccessException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or
            ArgumentException or System.Runtime.InteropServices.COMException;

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (!_hasVideo)
        {
            StatusText.Text = "Open a video before playback.";
            return;
        }

        if (_isPlaying)
        {
            PausePlayback();
        }
        else
        {
            FinishActiveEdit();
            TimeSpan duration = PreviewPlayer.MediaPlayer.PlaybackSession.NaturalDuration;
            if (duration > TimeSpan.Zero &&
                PreviewPlayer.MediaPlayer.PlaybackSession.Position >= duration)
            {
                PreviewPlayer.MediaPlayer.PlaybackSession.Position = TimeSpan.Zero;
                SelectFrame(0, seekVideo: false);
            }

            PreviewPlayer.MediaPlayer.Play();
            _playbackTimer.Start();
            _isPlaying = true;
            PlayButton.Content = "Pause";
        }
    }

    private void PreviousFrame_Click(object sender, RoutedEventArgs e)
    {
        SelectFrame(Math.Max(0, _selectedFrameIndex - 1));
    }

    private void NextFrame_Click(object sender, RoutedEventArgs e)
    {
        SelectFrame(Math.Min(_frames.Count - 1, _selectedFrameIndex + 1));
    }

    private void AddFrame_Click(object sender, RoutedEventArgs e)
    {
        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        int number = _frames.Max(frame => frame.Number) + 1;
        _frames.Add(new EditorFrame { Number = number });
        MarkEditChanged();
        CommitEdit();
        AddTimelineButton(_frames[^1], _frames.Count - 1);
        SelectFrame(_frames.Count - 1);
    }

    private void DuplicateFrame_Click(object sender, RoutedEventArgs e)
    {
        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        EditorFrame sourceFrame = CurrentFrame;
        var duplicate = new EditorFrame
        {
            Number = sourceFrame.Number + 1,
            IsKeyframe = sourceFrame.IsKeyframe,
            Strokes = CloneFrame(sourceFrame).Strokes,
        };
        _frames.Insert(_selectedFrameIndex + 1, duplicate);
        for (int i = _selectedFrameIndex + 2; i < _frames.Count; i++)
        {
            _frames[i].Number++;
        }

        MarkEditChanged();
        CommitEdit();
        RefreshTimeline();
        SelectFrame(_selectedFrameIndex + 1);
    }

    private void ToggleKeyframe_Click(object sender, RoutedEventArgs e)
    {
        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        CurrentFrame.IsKeyframe = !CurrentFrame.IsKeyframe;
        MarkEditChanged();
        CommitEdit();
        RefreshTimeline();
        UpdateFrameStatus();
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        FinishActiveEdit();
        if (_undoHistory.Count == 0)
        {
            return;
        }

        PushHistory(_redoHistory, CreateProjectSnapshot());
        RestoreProjectState(_undoHistory.Pop());
        UpdateHistoryButtons();
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        FinishActiveEdit();
        if (_redoHistory.Count == 0)
        {
            return;
        }

        PushHistory(_undoHistory, CreateProjectSnapshot());
        RestoreProjectState(_redoHistory.Pop());
        UpdateHistoryButtons();
    }

    private void SelectPen_Click(object sender, RoutedEventArgs e)
    {
        FinishActiveEdit();
        _isEraser = false;
        StatusText.Text = "Pen tool selected.";
    }

    private void SelectEraser_Click(object sender, RoutedEventArgs e)
    {
        FinishActiveEdit();
        _isEraser = true;
        StatusText.Text = "Erase mode selected.";
    }

    private void FrameRate_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedItem: ComboBoxItem item } ||
            !double.TryParse(item.Tag?.ToString(), System.Globalization.CultureInfo.InvariantCulture, out double rate) ||
            !ProjectFrameRates.Supported.Contains(rate) ||
            rate == _frameRate)
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        _frameRate = rate;
        MarkEditChanged();
        CommitEdit();
        SeekToCurrentFrame();
        StatusText.Text = $"Timeline frame rate: {_frameRate:0} fps";
    }

    private void InkSurface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_activePointerId is not null)
        {
            return;
        }

        PausePlayback();
        BeginEdit();
        PromoteInterpolatedFrameForEditing();
        Point point = e.GetCurrentPoint(InkSurface).Position;
        _activePointerId = e.Pointer.PointerId;
        InkSurface.CapturePointer(e.Pointer);
        if (_isEraser)
        {
            EraseAt(point);
        }
        else
        {
            _activeStroke = [];
            CurrentFrame.Strokes.Add(_activeStroke);
            AddPoint(point);
        }

        e.Handled = true;
    }

    private void InkSurface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        Point point = e.GetCurrentPoint(InkSurface).Position;
        if (_activePointerId == e.Pointer.PointerId && _isEraser)
        {
            EraseAt(point);
        }
        else if (_activePointerId == e.Pointer.PointerId)
        {
            AddPoint(point);
        }

        e.Handled = true;
    }

    private void InkSurface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_activePointerId == e.Pointer.PointerId)
        {
            AddPoint(e.GetCurrentPoint(InkSurface).Position);
            InkSurface.ReleasePointerCapture(e.Pointer);
            FinishActiveEdit();
        }

        e.Handled = true;
    }

    private void InkSurface_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        FinishActiveEdit();
    }

    private void InkSurface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RenderCurrentInk();
    }

    private void AddPoint(Point point)
    {
        if (_activeStroke is null || InkSurface.ActualWidth <= 0 || InkSurface.ActualHeight <= 0)
        {
            return;
        }

        var inkPoint = new InkPoint
        {
            X = Math.Clamp(point.X / InkSurface.ActualWidth, 0, 1),
            Y = Math.Clamp(point.Y / InkSurface.ActualHeight, 0, 1),
        };
        if (_activeStroke.Count == 0 ||
            _activeStroke[^1].X != inkPoint.X ||
            _activeStroke[^1].Y != inkPoint.Y)
        {
            _activeStroke.Add(inkPoint);
            MarkEditChanged();
            RenderCurrentInk();
        }
    }

    private void EraseAt(Point point)
    {
        if (InkSurface.ActualWidth <= 0 || InkSurface.ActualHeight <= 0)
        {
            return;
        }

        double radiusX = 18 / Math.Max(1, InkSurface.ActualWidth);
        double radiusY = 18 / Math.Max(1, InkSurface.ActualHeight);
        double normalizedX = point.X / InkSurface.ActualWidth;
        double normalizedY = point.Y / InkSurface.ActualHeight;
        bool changed = CurrentFrame.Strokes.RemoveAll(stroke =>
            stroke.Any(inkPoint =>
                Math.Abs(inkPoint.X - normalizedX) <= radiusX &&
                Math.Abs(inkPoint.Y - normalizedY) <= radiusY)) > 0;

        if (changed)
        {
            MarkEditChanged();
            RenderCurrentInk();
        }
    }

    private void RenderCurrentInk()
    {
        InkSurface.Children.Clear();
        foreach (List<InkPoint> stroke in InkAnimation.GetDisplayedStrokes(_frames, _selectedFrameIndex))
        {
            if (stroke.Count == 1)
            {
                const double dotSize = 4;
                var dot = new Ellipse
                {
                    Width = dotSize,
                    Height = dotSize,
                    Fill = new SolidColorBrush(Microsoft.UI.Colors.Orange),
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(dot, stroke[0].X * InkSurface.ActualWidth - dotSize / 2);
                Canvas.SetTop(dot, stroke[0].Y * InkSurface.ActualHeight - dotSize / 2);
                InkSurface.Children.Add(dot);
                continue;
            }

            var line = new Polyline
            {
                Stroke = new SolidColorBrush(Microsoft.UI.Colors.Orange),
                StrokeThickness = 4,
                IsHitTestVisible = false,
            };
            foreach (InkPoint point in stroke)
            {
                line.Points.Add(new Point(
                    point.X * InkSurface.ActualWidth,
                    point.Y * InkSurface.ActualHeight));
            }

            InkSurface.Children.Add(line);
        }
    }

    private void SelectFrame(int index, bool seekVideo = true)
    {
        if (index < 0 || index >= _frames.Count)
        {
            return;
        }

        if (index == _selectedFrameIndex)
        {
            if (seekVideo)
            {
                SeekToCurrentFrame();
            }

            return;
        }

        FinishActiveEdit();
        int previousIndex = _selectedFrameIndex;
        _selectedFrameIndex = index;
        RenderCurrentInk();
        if (seekVideo)
        {
            SeekToCurrentFrame();
        }

        UpdateTimelineButton(previousIndex);
        UpdateTimelineButton(_selectedFrameIndex);
        UpdateFrameReadout();
        KeyframeButton.Content = CurrentFrame.IsKeyframe ? "Remove keyframe" : "Set keyframe";
        UpdateFrameStatus();
    }

    private void SeekToCurrentFrame()
    {
        if (_hasVideo)
        {
            PreviewPlayer.MediaPlayer.PlaybackSession.Position =
                TimeSpan.FromSeconds((CurrentFrame.Number - 1) / _frameRate);
        }
    }

    private EditorProject CreateProjectSnapshot() => new()
    {
        FrameRate = _frameRate,
        VideoPath = _videoPath,
        SelectedFrameIndex = _selectedFrameIndex,
        Frames = _frames.Select(CloneFrame).ToList(),
    };

    private static EditorFrame CloneFrame(EditorFrame frame) => new()
    {
        Number = frame.Number,
        IsKeyframe = frame.IsKeyframe,
        Strokes = frame.Strokes
            .Select(stroke => stroke.Select(point => new InkPoint { X = point.X, Y = point.Y }).ToList())
            .ToList(),
    };

    private void RestoreProjectState(EditorProject project)
    {
        PausePlayback();
        _frames.Clear();
        _frames.AddRange(project.Frames.Select(CloneFrame));
        _selectedFrameIndex = project.SelectedFrameIndex;
        _frameRate = project.FrameRate;
        _videoPath = project.VideoPath;
        _activePointerId = null;
        _activeStroke = null;
        _pendingEdit = null;
        _pendingEditChanged = false;
        RenderCurrentInk();
        SeekToCurrentFrame();
        RefreshTimeline();
        UpdateFrameStatus();
        UpdateFrameRateSelector();
    }

    private void BeginEdit()
    {
        if (_pendingEdit is null)
        {
            _pendingEdit = CreateProjectSnapshot();
            _pendingEditChanged = false;
        }
    }

    private void MarkEditChanged()
    {
        _pendingEditChanged = true;
        UnsavedIndicator.Visibility = Visibility.Visible;
    }

    private void CommitEdit()
    {
        if (_pendingEdit is not null && _pendingEditChanged)
        {
            PushHistory(_undoHistory, _pendingEdit);
            _redoHistory.Clear();
            UpdateHistoryButtons();
        }

        _pendingEdit = null;
        _pendingEditChanged = false;
    }

    private void FinishActiveEdit()
    {
        if (_activeStroke is { Count: 0 })
        {
            CurrentFrame.Strokes.Remove(_activeStroke);
        }

        _activePointerId = null;
        _activeStroke = null;
        CommitEdit();
    }

    private void ClearHistory()
    {
        _undoHistory.Clear();
        _redoHistory.Clear();
        _pendingEdit = null;
        _pendingEditChanged = false;
        UpdateHistoryButtons();
    }

    private static void PushHistory(Stack<EditorProject> history, EditorProject project)
    {
        history.Push(project);
        if (history.Count <= MaximumHistoryEntries)
        {
            return;
        }

        EditorProject[] retainedSnapshots = history.Take(MaximumHistoryEntries).Reverse().ToArray();
        history.Clear();
        foreach (EditorProject snapshot in retainedSnapshots)
        {
            history.Push(snapshot);
        }
    }

    private void UpdateHistoryButtons()
    {
        UndoButton.IsEnabled = _undoHistory.Count > 0;
        RedoButton.IsEnabled = _redoHistory.Count > 0;
    }

    private void PlaybackTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (!_isPlaying || !_hasVideo)
        {
            return;
        }

        var session = PreviewPlayer.MediaPlayer.PlaybackSession;
        TimeSpan position = session.Position;
        TimeSpan duration = session.NaturalDuration;
        if (duration > TimeSpan.Zero && position >= duration)
        {
            PausePlayback();
            return;
        }

        int frameIndex = Math.Max(0, (int)Math.Floor(position.TotalSeconds * _frameRate));
        EnsureFrameExists(frameIndex);
        SelectFrame(frameIndex, seekVideo: false);
    }

    private void EnsureFrameExists(int frameIndex)
    {
        while (_frames.Count <= frameIndex)
        {
            var frame = new EditorFrame { Number = _frames.Count + 1 };
            _frames.Add(frame);
            AddTimelineButton(frame, _frames.Count - 1);
            UnsavedIndicator.Visibility = Visibility.Visible;
        }
    }

    private void PausePlayback()
    {
        if (!_isPlaying)
        {
            return;
        }

        PreviewPlayer.MediaPlayer.Pause();
        _playbackTimer.Stop();
        _isPlaying = false;
        PlayButton.Content = "Play";
    }

    private void PromoteInterpolatedFrameForEditing()
    {
        if (CurrentFrame.IsKeyframe || CurrentFrame.Strokes.Count > 0)
        {
            return;
        }

        IReadOnlyList<List<InkPoint>> displayed = InkAnimation.GetDisplayedStrokes(_frames, _selectedFrameIndex);
        if (_isEraser && displayed.Count == 0)
        {
            return;
        }

        if (displayed.Count > 0)
        {
            CurrentFrame.Strokes = displayed
                .Select(stroke => stroke.Select(point => new InkPoint { X = point.X, Y = point.Y }).ToList())
                .ToList();
        }

        CurrentFrame.IsKeyframe = true;
        MarkEditChanged();
        RefreshTimeline();
        UpdateFrameStatus();
    }

    private void RefreshTimeline()
    {
        _interpolatedFrameIndices.Clear();
        _interpolatedFrameIndices.UnionWith(InkAnimation.GetInterpolatedFrameIndices(_frames));
        InkFramesPanel.Children.Clear();
        _timelineButtons.Clear();
        for (int i = 0; i < _frames.Count; i++)
        {
            AddTimelineButton(_frames[i], i);
        }

        UpdateFrameReadout();
        KeyframeButton.Content = CurrentFrame.IsKeyframe ? "Remove keyframe" : "Set keyframe";
        for (int i = 0; i < _frames.Count; i++)
        {
            UpdateTimelineButton(i);
        }
    }

    private void AddTimelineButton(EditorFrame frame, int index)
    {
        var button = new Button
        {
            Width = 54,
            Padding = new Thickness(3, 0, 3, 0),
            Tag = index,
        };
        button.Click += TimelineFrame_Click;
        _timelineButtons.Add(button);
        InkFramesPanel.Children.Add(button);
        UpdateTimelineButton(index);
    }

    private void UpdateTimelineButton(int index)
    {
        if (index < 0 || index >= _timelineButtons.Count)
        {
            return;
        }

        EditorFrame frame = _frames[index];
        Button button = _timelineButtons[index];
        bool isInterpolated = _interpolatedFrameIndices.Contains(index);
        string prefix = frame.IsKeyframe ? "K " : isInterpolated ? "~" : "";
        button.Content = $"{prefix}{frame.Number:000}";
        ToolTipService.SetToolTip(
            button,
            frame.IsKeyframe ? $"Keyframe {frame.Number}" :
                isInterpolated ? $"Interpolated frame {frame.Number}" : $"Frame {frame.Number}");
        button.Background = new SolidColorBrush(
            index == _selectedFrameIndex ? Microsoft.UI.Colors.DodgerBlue : Microsoft.UI.Colors.DarkSlateGray);
    }

    private void TimelineFrame_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int index })
        {
            SelectFrame(index);
        }
    }

    private void UpdateFrameStatus()
    {
        KeyframeStatus.Text = CurrentFrame.IsKeyframe
            ? $"Keyframe set at frame {CurrentFrame.Number}."
            : InkAnimation.IsInterpolated(_frames, _selectedFrameIndex)
                ? $"Interpolated drawing at frame {CurrentFrame.Number}."
                : CurrentFrame.Strokes.Count > 0
                    ? $"Hand-drawn exposure at frame {CurrentFrame.Number}."
                    : "No ink on this frame";
    }

    private void UpdateFrameReadout()
    {
        FrameReadout.Text = $"F {CurrentFrame.Number:000}";
    }

    private void UpdateFrameRateSelector()
    {
        foreach (ComboBoxItem item in FrameRateSelector.Items.OfType<ComboBoxItem>())
        {
            if (double.TryParse(item.Tag?.ToString(), System.Globalization.CultureInfo.InvariantCulture, out double rate) &&
                rate == _frameRate)
            {
                FrameRateSelector.SelectedItem = item;
                break;
            }
        }
    }

}
