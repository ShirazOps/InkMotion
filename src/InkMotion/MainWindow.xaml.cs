using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Media.Core;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Foundation;
using WinRT.Interop;

namespace InkMotion;

public sealed partial class MainWindow : Window
{
    private const double FramesPerSecond = 24;
    private readonly List<AnimationFrame> _frames = [new(1)];
    private int _selectedFrameIndex;
    private uint? _activePointerId;
    private List<InkPoint>? _activeStroke;
    private bool _isPlaying;
    private bool _hasVideo;
    private bool _isEraser;

    public MainWindow()
    {
        InitializeComponent();
        Title = "InkMotion";
        RefreshTimeline();
        RenderCurrentInk();
    }

    private AnimationFrame CurrentFrame => _frames[_selectedFrameIndex];

    private async void OpenVideo_Click(object sender, RoutedEventArgs e)
    {
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

        PreviewPlayer.Source = MediaSource.CreateFromStorageFile(file);
        PreviewPlayer.MediaPlayer.IsLoopingEnabled = false;
        EmptyState.Visibility = Visibility.Collapsed;
        VideoTrackLabel.Text = file.Name;
        _hasVideo = true;
        SeekToCurrentFrame();
        StatusText.Text = $"Loaded  ·  {file.Name}";
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (!_hasVideo)
        {
            StatusText.Text = "Open a video before playback.";
            return;
        }

        if (_isPlaying)
        {
            PreviewPlayer.MediaPlayer.Pause();
            PlayButton.Content = "Play";
        }
        else
        {
            PreviewPlayer.MediaPlayer.Play();
            PlayButton.Content = "Pause";
        }

        _isPlaying = !_isPlaying;
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
        int number = _frames[^1].Number + 1;
        _frames.Add(new AnimationFrame(number));
        SelectFrame(_frames.Count - 1);
    }

    private void DuplicateFrame_Click(object sender, RoutedEventArgs e)
    {
        AnimationFrame sourceFrame = CurrentFrame;
        var duplicate = new AnimationFrame(sourceFrame.Number + 1)
        {
            Strokes = sourceFrame.Strokes.Select(stroke => new List<InkPoint>(stroke)).ToList(),
        };
        _frames.Insert(_selectedFrameIndex + 1, duplicate);
        for (int i = _selectedFrameIndex + 2; i < _frames.Count; i++)
        {
            _frames[i].Number++;
        }

        SelectFrame(_selectedFrameIndex + 1);
    }

    private void ToggleKeyframe_Click(object sender, RoutedEventArgs e)
    {
        CurrentFrame.IsKeyframe = !CurrentFrame.IsKeyframe;
        RefreshTimeline();
        UpdateFrameStatus();
    }

    private void SelectPen_Click(object sender, RoutedEventArgs e)
    {
        _isEraser = false;
        StatusText.Text = "Pen tool selected.";
    }

    private void SelectEraser_Click(object sender, RoutedEventArgs e)
    {
        _isEraser = true;
        StatusText.Text = "Erase mode selected.";
    }

    private void InkSurface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
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
            _activePointerId = null;
            _activeStroke = null;
        }

        e.Handled = true;
    }

    private void InkSurface_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _activePointerId = null;
        _activeStroke = null;
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

        var inkPoint = new InkPoint(
            Math.Clamp(point.X / InkSurface.ActualWidth, 0, 1),
            Math.Clamp(point.Y / InkSurface.ActualHeight, 0, 1));
        if (_activeStroke.Count == 0 || _activeStroke[^1] != inkPoint)
        {
            _activeStroke.Add(inkPoint);
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
            RenderCurrentInk();
        }
    }

    private void RenderCurrentInk()
    {
        InkSurface.Children.Clear();
        foreach (List<InkPoint> stroke in CurrentFrame.Strokes)
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

    private void SelectFrame(int index)
    {
        if (index == _selectedFrameIndex)
        {
            return;
        }

        _selectedFrameIndex = index;
        _activePointerId = null;
        _activeStroke = null;
        RenderCurrentInk();
        SeekToCurrentFrame();
        RefreshTimeline();
        UpdateFrameStatus();
    }

    private void SeekToCurrentFrame()
    {
        if (_hasVideo)
        {
            PreviewPlayer.MediaPlayer.PlaybackSession.Position =
                TimeSpan.FromSeconds((CurrentFrame.Number - 1) / FramesPerSecond);
        }
    }

    private void RefreshTimeline()
    {
        InkFramesPanel.Children.Clear();
        for (int i = 0; i < _frames.Count; i++)
        {
            AnimationFrame frame = _frames[i];
            var button = new Button
            {
                Width = 54,
                Padding = new Thickness(4, 0, 4, 0),
                Content = frame.IsKeyframe ? $"K {frame.Number:000}" : frame.Number.ToString("000"),
                Tag = i,
                Background = i == _selectedFrameIndex
                    ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.DodgerBlue)
                    : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.DarkSlateGray),
            };
            button.Click += TimelineFrame_Click;
            InkFramesPanel.Children.Add(button);
        }

        FrameReadout.Text = $"F {CurrentFrame.Number:000}";
        KeyframeButton.Content = CurrentFrame.IsKeyframe ? "Remove keyframe" : "Set keyframe";
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
            : "No keyframe on this frame";
    }

    private sealed class AnimationFrame(int number)
    {
        public int Number { get; set; } = number;
        public List<List<InkPoint>> Strokes { get; set; } = [];
        public bool IsKeyframe { get; set; }
    }

    private readonly record struct InkPoint(double X, double Y);
}
