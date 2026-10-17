using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Dispatching;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Windows.Media.Core;
using Windows.Media.Playback;
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
    private readonly List<AudioTrackRuntime> _audioTracks = [];
    private readonly DispatcherQueueTimer _playbackTimer;
    private readonly Stopwatch _audioOnlyClock = new();
    private int _selectedFrameIndex;
    private int _selectedAudioTrackIndex = -1;
    private double _frameRate = FramesPerSecond;
    private uint _brushColor = 0xFFFFA500;
    private double _brushWidth = 4;
    private uint? _activePointerId;
    private List<InkPoint>? _activeStroke;
    private EditorProject? _pendingEdit;
    private bool _pendingEditChanged;
    private StorageFile? _projectFile;
    private string? _videoPath;
    private bool _isPlaying;
    private bool _hasVideo;
    private bool _isEraser;
    private bool _isRestoringProject;
    private bool _isInitializing = true;
    private TimeSpan _audioOnlyPosition;
    private TimeSpan _audioOnlyStartPosition;

    public MainWindow()
    {
        InitializeComponent();
        _isInitializing = false;
        Title = "InkMotion";
        _playbackTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _playbackTimer.Interval = TimeSpan.FromMilliseconds(16);
        _playbackTimer.Tick += PlaybackTimer_Tick;
        Closed += MainWindow_Closed;
        UpdateSelectedAudioTrack();
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
            string? audioWarning = await LoadAudioTracksAsync(project.AudioTracks);
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

            if (audioWarning is not null)
            {
                StatusText.Text += $"  ·  {audioWarning}";
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

    private async void OpenAudio_Click(object sender, RoutedEventArgs e)
    {
        PausePlayback();
        FinishActiveEdit();
        var picker = new FileOpenPicker();
        foreach (string extension in new[] { ".mp3", ".wav", ".m4a", ".aac", ".wma", ".flac" })
        {
            picker.FileTypeFilter.Add(extension);
        }

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            if (_selectedAudioTrackIndex < 0 || _selectedAudioTrackIndex >= _audioTracks.Count)
            {
                AddAudioTrack();
            }

            LoadAudio(_audioTracks[_selectedAudioTrackIndex], file);
            bool tagsLoaded = await LoadAudioTagsAsync(_audioTracks[_selectedAudioTrackIndex]);
            ClearHistory();
            UpdateSelectedAudioTrack();
            RefreshAudioTracks();
            StatusText.Text = tagsLoaded
                ? $"Loaded audio  ·  {file.Name}"
                : $"Loaded audio, but could not read its tags  ·  {file.Name}";
        }
        catch (Exception ex) when (IsFileAccessException(ex))
        {
            StatusText.Text = $"Could not load audio: {ex.Message}";
        }
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

    private void LoadAudio(AudioTrackRuntime track, StorageFile file)
    {
        PausePlayback();
        track.Player.Source = MediaSource.CreateFromStorageFile(file);
        track.Project.AudioPath = file.Path;
        track.Project.StartFrame = CurrentFrame.Number;
        track.HasSource = true;
        track.Player.Volume = track.Project.Volume;
        track.Player.IsMuted = track.Project.Muted;
        track.Player.PlaybackSession.Position = TimeSpan.Zero;
        AudioVolumeSlider.Value = track.Project.Volume;
        AudioMuteButton.IsChecked = track.Project.Muted;
        UnsavedIndicator.Visibility = Visibility.Visible;
    }

    private void AudioTag_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isRestoringProject ||
            _selectedAudioTrackIndex < 0 ||
            _selectedAudioTrackIndex >= _audioTracks.Count)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(AudioYearBox.Text) &&
            (!uint.TryParse(AudioYearBox.Text, out uint year) || year > 9999))
        {
            StatusText.Text = "Audio tag year must be between 1 and 9999.";
            return;
        }

        AudioTrackProject project = _audioTracks[_selectedAudioTrackIndex].Project;
        uint parsedYear = uint.TryParse(AudioYearBox.Text, out uint parsed) ? parsed : 0;
        if (project.Title == AudioTitleBox.Text &&
            project.Artist == AudioArtistBox.Text &&
            project.Album == AudioAlbumBox.Text &&
            project.Genre == AudioGenreBox.Text &&
            project.Year == parsedYear &&
            project.Comments == AudioCommentsBox.Text)
        {
            return;
        }

        BeginEdit();
        project.Title = AudioTitleBox.Text;
        project.Artist = AudioArtistBox.Text;
        project.Album = AudioAlbumBox.Text;
        project.Genre = AudioGenreBox.Text;
        project.Year = parsedYear;
        project.Comments = AudioCommentsBox.Text;
        project.TagsInitialized = true;
        MarkEditChanged();
        StatusText.Text = "Audio tags edited in the project. Write tags to the audio file to update its metadata.";
    }

    private void AudioTag_LostFocus(object sender, RoutedEventArgs e)
    {
        FinishActiveEdit();
    }

    private async void WriteAudioTags_Click(object sender, RoutedEventArgs e)
    {
        FinishActiveEdit();
        if (_selectedAudioTrackIndex < 0 || _selectedAudioTrackIndex >= _audioTracks.Count)
        {
            return;
        }

        AudioTrackProject project = _audioTracks[_selectedAudioTrackIndex].Project;
        if (string.IsNullOrWhiteSpace(project.AudioPath) || !File.Exists(project.AudioPath))
        {
            StatusText.Text = "The audio file is unavailable. Relink it before writing tags.";
            return;
        }

        if (!string.IsNullOrWhiteSpace(AudioYearBox.Text) &&
            (!uint.TryParse(AudioYearBox.Text, out uint year) || year > 9999))
        {
            StatusText.Text = "Audio tag year must be between 1 and 9999.";
            return;
        }

        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(project.AudioPath!);
            Windows.Storage.FileProperties.MusicProperties music =
                await file.Properties.GetMusicPropertiesAsync();
            music.Title = project.Title;
            music.Artist = project.Artist;
            music.Album = project.Album;
            music.Genre.Clear();
            foreach (string genre in project.Genre.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                music.Genre.Add(genre);
            }

            music.Year = project.Year;
            await music.SavePropertiesAsync();
            StatusText.Text = "Audio tags written to the source file. Comments remain stored in the project.";
        }
        catch (Exception ex) when (IsFileAccessException(ex))
        {
            StatusText.Text = $"Could not write audio tags: {ex.Message}";
        }
    }

    private async Task<bool> LoadAudioTagsAsync(AudioTrackRuntime track)
    {
        if (HasAudioTags(track.Project))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(track.Project.AudioPath) || !track.HasSource)
        {
            return false;
        }

        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(track.Project.AudioPath);
            Windows.Storage.FileProperties.MusicProperties music =
                await file.Properties.GetMusicPropertiesAsync();
            track.Project.Title = music.Title ?? string.Empty;
            track.Project.Artist = music.Artist ?? string.Empty;
            track.Project.Album = music.Album ?? string.Empty;
            track.Project.Genre = string.Join("; ", music.Genre);
            track.Project.Year = music.Year;
            track.Project.TagsInitialized = true;
            if (_selectedAudioTrackIndex >= 0 &&
                _selectedAudioTrackIndex < _audioTracks.Count &&
                ReferenceEquals(_audioTracks[_selectedAudioTrackIndex], track))
            {
                UpdateSelectedAudioTrack();
            }

            return true;
        }
        catch (Exception ex) when (IsFileAccessException(ex))
        {
            track.LoadError ??= "audio tags could not be read";
            return false;
        }
    }

    private void AddAudioTrack_Click(object sender, RoutedEventArgs e)
    {
        AddAudioTrack();
        ClearHistory();
        UnsavedIndicator.Visibility = Visibility.Visible;
        StatusText.Text = $"Audio track {_selectedAudioTrackIndex + 1} added.";
    }

    private void AddAudioTrack(bool select = true)
    {
        var track = new AudioTrackRuntime(new AudioTrackProject
        {
            StartFrame = CurrentFrame.Number,
        });
        _audioTracks.Add(track);
        if (select)
        {
            _selectedAudioTrackIndex = _audioTracks.Count - 1;
            UpdateSelectedAudioTrack();
        }

        RefreshAudioTracks();
    }

    private void SelectAudioTrack(int index)
    {
        if (index < 0 || index >= _audioTracks.Count)
        {
            return;
        }

        _selectedAudioTrackIndex = index;
        UpdateSelectedAudioTrack();
        RefreshAudioTracks();
    }

    private void UpdateSelectedAudioTrack()
    {
        bool hasSelection = _selectedAudioTrackIndex >= 0 && _selectedAudioTrackIndex < _audioTracks.Count;
        AudioVolumeSlider.IsEnabled = hasSelection;
        AudioMuteButton.IsEnabled = hasSelection;
        MoveAudioClipButton.IsEnabled = hasSelection && _audioTracks[_selectedAudioTrackIndex].HasSource;
        if (!hasSelection)
        {
            _isRestoringProject = true;
            AudioTrackLabel.Text = "No audio track selected";
            AudioVolumeSlider.Value = 1;
            AudioMuteButton.IsChecked = false;
            AudioTitleBox.Text = string.Empty;
            AudioArtistBox.Text = string.Empty;
            AudioAlbumBox.Text = string.Empty;
            AudioGenreBox.Text = string.Empty;
            AudioYearBox.Text = string.Empty;
            AudioCommentsBox.Text = string.Empty;
            WriteAudioTagsButton.IsEnabled = false;
            _isRestoringProject = false;
            return;
        }

        AudioTrackRuntime track = _audioTracks[_selectedAudioTrackIndex];
        _isRestoringProject = true;
        AudioTrackLabel.Text = string.IsNullOrWhiteSpace(track.Project.AudioPath)
            ? $"Audio track {_selectedAudioTrackIndex + 1} — empty"
            : $"{System.IO.Path.GetFileName(track.Project.AudioPath)} · starts at frame {track.Project.StartFrame}";
        AudioVolumeSlider.Value = track.Project.Volume;
        AudioMuteButton.IsChecked = track.Project.Muted;
        AudioTitleBox.Text = track.Project.Title;
        AudioArtistBox.Text = track.Project.Artist;
        AudioAlbumBox.Text = track.Project.Album;
        AudioGenreBox.Text = track.Project.Genre;
        AudioYearBox.Text = track.Project.Year == 0 ? string.Empty : track.Project.Year.ToString();
        AudioCommentsBox.Text = track.Project.Comments;
        WriteAudioTagsButton.IsEnabled = track.HasSource;
        _isRestoringProject = false;
    }

    private void MoveAudioClipToPlayhead_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedAudioTrackIndex < 0 ||
            _selectedAudioTrackIndex >= _audioTracks.Count ||
            !_audioTracks[_selectedAudioTrackIndex].HasSource)
        {
            return;
        }

        AudioTrackRuntime track = _audioTracks[_selectedAudioTrackIndex];
        if (track.Project.StartFrame == CurrentFrame.Number)
        {
            StatusText.Text = "The audio clip already starts at the playhead.";
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        track.Project.StartFrame = CurrentFrame.Number;
        MarkEditChanged();
        CommitEdit();
        SyncAudioTracks(TimeSpan.FromSeconds((CurrentFrame.Number - 1) / _frameRate), startPlayback: false);
        UpdateSelectedAudioTrack();
        RefreshAudioTracks();
        StatusText.Text = $"Audio clip moved to frame {CurrentFrame.Number}.";
    }

    private void RefreshAudioTracks()
    {
        AudioTracksPanel.Children.Clear();
        AudioTrackNamesPanel.Children.Clear();
        if (_audioTracks.Count == 0)
        {
            AudioTrackNamesPanel.Children.Add(CreateAudioTrackName("A1  Audio"));
            AudioTracksPanel.Children.Add(CreateAudioTrackLane("Add an audio clip with Add audio"));
            return;
        }

        for (int i = 0; i < _audioTracks.Count; i++)
        {
            AudioTrackRuntime track = _audioTracks[i];
            AudioTrackNamesPanel.Children.Add(CreateAudioTrackName($"A{i + 1}  Audio"));
            var button = new Button
            {
                Height = 36,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 0, 10, 0),
                Background = new SolidColorBrush(i == _selectedAudioTrackIndex
                    ? Microsoft.UI.Colors.DarkSlateBlue
                    : Microsoft.UI.Colors.DarkSlateGray),
                Content = string.IsNullOrWhiteSpace(track.Project.AudioPath)
                    ? "Empty audio track"
                    : $"{System.IO.Path.GetFileName(track.Project.AudioPath)}  ·  F{track.Project.StartFrame:000}",
                Tag = i,
            };
            button.Click += AudioTrack_Click;
            AudioTracksPanel.Children.Add(button);
        }
    }

    private static Border CreateAudioTrackName(string text) => new()
    {
        Height = 36,
        Background = new SolidColorBrush(Microsoft.UI.Colors.DarkSlateGray),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(10, 0, 10, 0),
        Child = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.LightGray),
        },
    };

    private static Border CreateAudioTrackLane(string text) => new()
    {
        Height = 36,
        Background = new SolidColorBrush(Microsoft.UI.Colors.DarkSlateGray),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(10, 0, 10, 0),
        Child = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
        },
    };

    private void AudioTrack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int index })
        {
            SelectAudioTrack(index);
        }
    }

    private async Task<string?> LoadAudioTracksAsync(IReadOnlyList<AudioTrackProject> tracks)
    {
        DisposeAudioTracks();
        foreach (AudioTrackProject project in tracks)
        {
            var runtime = new AudioTrackRuntime(CloneAudioTrack(project));
            _audioTracks.Add(runtime);
            if (string.IsNullOrWhiteSpace(project.AudioPath))
            {
                continue;
            }

            if (!File.Exists(project.AudioPath))
            {
                runtime.LoadError = "source audio is missing";
                continue;
            }

            try
            {
                StorageFile file = await StorageFile.GetFileFromPathAsync(project.AudioPath);
                runtime.Player.Source = MediaSource.CreateFromStorageFile(file);
                runtime.HasSource = true;
                runtime.Player.Volume = project.Volume;
                runtime.Player.IsMuted = project.Muted;
                if (!HasAudioTags(project))
                {
                    await LoadAudioTagsAsync(runtime);
                }
            }
            catch (Exception ex) when (IsFileAccessException(ex))
            {
                runtime.LoadError = "source audio is unavailable";
            }
        }

        _selectedAudioTrackIndex = _audioTracks.Count > 0 ? 0 : -1;
        UpdateSelectedAudioTrack();
        RefreshAudioTracks();
        return _audioTracks.Any(track => track.LoadError is not null)
            ? "One or more audio sources are missing or unavailable. Use Add audio to relink."
            : null;
    }

    private void DisposeAudioTracks()
    {
        foreach (AudioTrackRuntime track in _audioTracks)
        {
            track.Player.Pause();
            track.Player.Dispose();
        }

        _audioTracks.Clear();
        _selectedAudioTrackIndex = -1;
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
        if (!_hasVideo && !HasLoadedAudio)
        {
            StatusText.Text = "Open a video or add an audio clip before playback.";
            return;
        }

        if (_isPlaying)
        {
            PausePlayback();
        }
        else
        {
            FinishActiveEdit();
            if (_hasVideo)
            {
                TimeSpan duration = PreviewPlayer.MediaPlayer.PlaybackSession.NaturalDuration;
                if (duration > TimeSpan.Zero &&
                    PreviewPlayer.MediaPlayer.PlaybackSession.Position >= duration)
                {
                    PreviewPlayer.MediaPlayer.PlaybackSession.Position = TimeSpan.Zero;
                    SelectFrame(0, seekVideo: false);
                }

                PreviewPlayer.MediaPlayer.Play();
            }

            else if (GetAudioTimelineEnd() is TimeSpan audioEnd &&
                     audioEnd > TimeSpan.Zero &&
                     _audioOnlyPosition >= audioEnd)
            {
                _audioOnlyPosition = TimeSpan.Zero;
                SelectFrame(0, seekVideo: false);
            }

            _isPlaying = true;
            if (!_hasVideo)
            {
                _audioOnlyStartPosition = _audioOnlyPosition;
                _audioOnlyClock.Restart();
            }

            SyncAudioTracks(_hasVideo
                ? PreviewPlayer.MediaPlayer.PlaybackSession.Position
                : _audioOnlyStartPosition, startPlayback: true);
            _playbackTimer.Start();
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
        var pointerPoint = e.GetCurrentPoint(InkSurface);
        _activePointerId = e.Pointer.PointerId;
        InkSurface.CapturePointer(e.Pointer);
        if (_isEraser)
        {
            EraseAt(pointerPoint.Position);
        }
        else
        {
            _activeStroke = [];
            CurrentFrame.Strokes.Add(_activeStroke);
            AddPoint(pointerPoint.Position, pointerPoint.Properties.Pressure);
        }

        e.Handled = true;
    }

    private void InkSurface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pointerPoint = e.GetCurrentPoint(InkSurface);
        if (_activePointerId == e.Pointer.PointerId && _isEraser)
        {
            EraseAt(pointerPoint.Position);
        }
        else if (_activePointerId == e.Pointer.PointerId)
        {
            AddPoint(pointerPoint.Position, pointerPoint.Properties.Pressure);
        }

        e.Handled = true;
    }

    private void InkSurface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_activePointerId == e.Pointer.PointerId)
        {
            var pointerPoint = e.GetCurrentPoint(InkSurface);
            AddPoint(pointerPoint.Position, pointerPoint.Properties.Pressure);
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

    private void AddPoint(Point point, double pressure)
    {
        if (_activeStroke is null || InkSurface.ActualWidth <= 0 || InkSurface.ActualHeight <= 0)
        {
            return;
        }

        var inkPoint = new InkPoint
        {
            X = Math.Clamp(point.X / InkSurface.ActualWidth, 0, 1),
            Y = Math.Clamp(point.Y / InkSurface.ActualHeight, 0, 1),
            Pressure = Math.Clamp(pressure > 0 ? pressure : 0.5, 0, 1),
            BrushWidth = _brushWidth,
            BrushColor = _brushColor,
        };
        if (_activeStroke.Count == 0 ||
            _activeStroke[^1].X != inkPoint.X ||
            _activeStroke[^1].Y != inkPoint.Y ||
            _activeStroke[^1].Pressure != inkPoint.Pressure)
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
        if (OnionSkinToggle?.IsChecked == true)
        {
            double opacity = OnionSkinOpacitySlider?.Value ?? 0.25;
            if (_selectedFrameIndex > 0)
            {
                DrawStrokes(
                    InkAnimation.GetDisplayedStrokes(_frames, _selectedFrameIndex - 1),
                    0xFF00BFFF,
                    opacity);
            }

            if (_selectedFrameIndex + 1 < _frames.Count)
            {
                DrawStrokes(
                    InkAnimation.GetDisplayedStrokes(_frames, _selectedFrameIndex + 1),
                    0xFFFF69B4,
                    opacity);
            }
        }

        DrawStrokes(
            InkAnimation.GetDisplayedStrokes(_frames, _selectedFrameIndex),
            null,
            1);
    }

    private void DrawStrokes(IReadOnlyList<List<InkPoint>> strokes, uint? color, double opacity)
    {
        foreach (List<InkPoint> stroke in strokes)
        {
            if (stroke.Count == 0)
            {
                continue;
            }

            var brush = new SolidColorBrush(ToColor(color ?? stroke[0].BrushColor)) { Opacity = opacity };
            for (int i = 0; i < stroke.Count; i++)
            {
                InkPoint point = stroke[i];
                double x = point.X * InkSurface.ActualWidth;
                double y = point.Y * InkSurface.ActualHeight;
                double width = GetInkWidth(point);
                var dot = new Ellipse
                {
                    Width = width,
                    Height = width,
                    Fill = brush,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(dot, x - width / 2);
                Canvas.SetTop(dot, y - width / 2);
                InkSurface.Children.Add(dot);

                if (i == 0)
                {
                    continue;
                }

                InkPoint previous = stroke[i - 1];
                var segment = new Line
                {
                    X1 = previous.X * InkSurface.ActualWidth,
                    Y1 = previous.Y * InkSurface.ActualHeight,
                    X2 = x,
                    Y2 = y,
                    Stroke = brush,
                    StrokeThickness = (GetInkWidth(previous) + width) / 2,
                    IsHitTestVisible = false,
                };
                InkSurface.Children.Add(segment);
            }
        }
    }

    private static double GetInkWidth(InkPoint point) =>
        point.BrushWidth * (0.25 + (1.5 * point.Pressure));

    private static Windows.UI.Color ToColor(uint argb) => Windows.UI.Color.FromArgb(
        (byte)(argb >> 24),
        (byte)(argb >> 16),
        (byte)(argb >> 8),
        (byte)argb);

    private void InkColor_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (InkColorSelector.SelectedItem is ComboBoxItem { Tag: string color } &&
            uint.TryParse(color, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out uint argb))
        {
            _brushColor = argb;
        }
    }

    private void InkWidth_Changed(
        object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_isInitializing)
        {
            _brushWidth = e.NewValue;
        }
    }

    private void OnionSkin_Click(object sender, RoutedEventArgs e)
    {
        RenderCurrentInk();
    }

    private void OnionSkinOpacity_Changed(
        object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        RenderCurrentInk();
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
        TimeSpan position = TimeSpan.FromSeconds((CurrentFrame.Number - 1) / _frameRate);
        if (_hasVideo)
        {
            PreviewPlayer.MediaPlayer.PlaybackSession.Position = position;
        }

        if (!_hasVideo && !_isPlaying)
        {
            _audioOnlyPosition = position;
        }

        SyncAudioTracks(position, startPlayback: _isPlaying);
    }

    private void SyncAudioTracks(TimeSpan timelinePosition, bool startPlayback)
    {
        foreach (AudioTrackRuntime track in _audioTracks)
        {
            if (!track.HasSource)
            {
                continue;
            }

            TimeSpan clipStart = TimeSpan.FromSeconds((track.Project.StartFrame - 1) / _frameRate);
            TimeSpan clipPosition = timelinePosition - clipStart;
            TimeSpan duration = track.Player.PlaybackSession.NaturalDuration;
            if (clipPosition < TimeSpan.Zero || (duration > TimeSpan.Zero && clipPosition >= duration))
            {
                track.Player.Pause();
                if (clipPosition < TimeSpan.Zero)
                {
                    track.Player.PlaybackSession.Position = TimeSpan.Zero;
                }

                continue;
            }

            if (startPlayback)
            {
                if (track.Player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing ||
                    Math.Abs((track.Player.PlaybackSession.Position - clipPosition).TotalMilliseconds) > 120)
                {
                    track.Player.PlaybackSession.Position = clipPosition;
                }

                track.Player.Play();
            }
            else
            {
                track.Player.PlaybackSession.Position = clipPosition;
                track.Player.Pause();
            }
        }
    }

    private bool HasLoadedAudio => _audioTracks.Any(track => track.HasSource);

    private TimeSpan? GetAudioTimelineEnd()
    {
        TimeSpan[] ends = _audioTracks
            .Where(track => track.HasSource)
            .Select(track =>
            {
                TimeSpan duration = track.Player.PlaybackSession.NaturalDuration;
                return duration > TimeSpan.Zero
                    ? TimeSpan.FromSeconds((track.Project.StartFrame - 1) / _frameRate) + duration
                    : TimeSpan.Zero;
            })
            .Where(end => end > TimeSpan.Zero)
            .ToArray();
        return ends.Length == 0 ? null : ends.Max();
    }

    private EditorProject CreateProjectSnapshot()
    {
        List<AudioTrackProject> tracks = _audioTracks.Select(track => CloneAudioTrack(track.Project)).ToList();
        AudioTrackProject? firstLoadedTrack = tracks.FirstOrDefault(track => !string.IsNullOrWhiteSpace(track.AudioPath));
        return new EditorProject
        {
            FrameRate = _frameRate,
            VideoPath = _videoPath,
            AudioPath = firstLoadedTrack?.AudioPath,
            AudioVolume = firstLoadedTrack?.Volume ?? 1,
            AudioMuted = firstLoadedTrack?.Muted ?? false,
            AudioTracks = tracks,
            SelectedFrameIndex = _selectedFrameIndex,
            Frames = _frames.Select(CloneFrame).ToList(),
        };
    }

    private static AudioTrackProject CloneAudioTrack(AudioTrackProject track) => new()
    {
        AudioPath = track.AudioPath,
        StartFrame = track.StartFrame,
        Volume = track.Volume,
        Muted = track.Muted,
        Title = track.Title,
        Artist = track.Artist,
        Album = track.Album,
        Genre = track.Genre,
        Comments = track.Comments,
        Year = track.Year,
        TagsInitialized = track.TagsInitialized,
    };

    private static bool HasAudioTags(AudioTrackProject project) =>
        project.TagsInitialized ||
        !string.IsNullOrWhiteSpace(project.Title) ||
        !string.IsNullOrWhiteSpace(project.Artist) ||
        !string.IsNullOrWhiteSpace(project.Album) ||
        !string.IsNullOrWhiteSpace(project.Genre) ||
        !string.IsNullOrWhiteSpace(project.Comments) ||
        project.Year > 0;

    private static EditorFrame CloneFrame(EditorFrame frame) => new()
    {
        Number = frame.Number,
        IsKeyframe = frame.IsKeyframe,
        Strokes = frame.Strokes
            .Select(stroke => stroke.Select(point => new InkPoint
            {
                X = point.X,
                Y = point.Y,
                Pressure = point.Pressure,
                BrushWidth = point.BrushWidth,
                BrushColor = point.BrushColor,
            }).ToList())
            .ToList(),
    };

    private void RestoreProjectState(EditorProject project)
    {
        PausePlayback();
        _isRestoringProject = true;
        _frames.Clear();
        _frames.AddRange(project.Frames.Select(CloneFrame));
        _selectedFrameIndex = project.SelectedFrameIndex;
        _frameRate = project.FrameRate;
        _videoPath = project.VideoPath;
        RestoreAudioTrackModels(project.AudioTracks);
        _isRestoringProject = false;
        _activePointerId = null;
        _activeStroke = null;
        _pendingEdit = null;
        _pendingEditChanged = false;
        _audioOnlyPosition = TimeSpan.FromSeconds((CurrentFrame.Number - 1) / _frameRate);
        RenderCurrentInk();
        SeekToCurrentFrame();
        RefreshTimeline();
        UpdateFrameStatus();
        UpdateFrameRateSelector();
    }

    private void RestoreAudioTrackModels(IReadOnlyList<AudioTrackProject> projects)
    {
        AudioTrackRuntime[] previousTracks = _audioTracks.ToArray();
        var restoredTracks = new List<AudioTrackRuntime>(projects.Count);
        for (int index = 0; index < projects.Count; index++)
        {
            AudioTrackProject project = projects[index];
            if (index < previousTracks.Length &&
                string.Equals(previousTracks[index].Project.AudioPath, project.AudioPath, StringComparison.OrdinalIgnoreCase))
            {
                AudioTrackRuntime reused = previousTracks[index];
                reused.Project = CloneAudioTrack(project);
                reused.Player.Volume = project.Volume;
                reused.Player.IsMuted = project.Muted;
                restoredTracks.Add(reused);
            }
            else
            {
                restoredTracks.Add(new AudioTrackRuntime(CloneAudioTrack(project)));
            }
        }

        foreach (AudioTrackRuntime removed in previousTracks.Except(restoredTracks))
        {
            removed.Player.Pause();
            removed.Player.Dispose();
        }

        _audioTracks.Clear();
        _audioTracks.AddRange(restoredTracks);
        _selectedAudioTrackIndex = _audioTracks.Count == 0 ? -1 : Math.Clamp(_selectedAudioTrackIndex, 0, _audioTracks.Count - 1);
        UpdateSelectedAudioTrack();
        RefreshAudioTracks();
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
        if (!_isPlaying)
        {
            return;
        }

        TimeSpan position;
        if (_hasVideo)
        {
            var session = PreviewPlayer.MediaPlayer.PlaybackSession;
            position = session.Position;
            TimeSpan duration = session.NaturalDuration;
            if (duration > TimeSpan.Zero && position >= duration)
            {
                PausePlayback();
                return;
            }
        }
        else
        {
            position = _audioOnlyStartPosition + _audioOnlyClock.Elapsed;
            _audioOnlyPosition = position;
            if (GetAudioTimelineEnd() is TimeSpan duration &&
                duration > TimeSpan.Zero &&
                position >= duration)
            {
                PausePlayback();
                return;
            }
        }

        SyncAudioTracks(position, startPlayback: true);
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

        if (_hasVideo)
        {
            PreviewPlayer.MediaPlayer.Pause();
        }

        foreach (AudioTrackRuntime track in _audioTracks)
        {
            track.Player.Pause();
        }

        if (!_hasVideo)
        {
            _audioOnlyPosition = _audioOnlyStartPosition + _audioOnlyClock.Elapsed;
            _audioOnlyClock.Stop();
        }

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
                .Select(stroke => stroke.Select(point => new InkPoint
                {
                    X = point.X,
                    Y = point.Y,
                    Pressure = point.Pressure,
                    BrushWidth = point.BrushWidth,
                    BrushColor = point.BrushColor,
                }).ToList())
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

    private void AudioVolume_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isInitializing || _isRestoringProject ||
            _selectedAudioTrackIndex < 0 ||
            _selectedAudioTrackIndex >= _audioTracks.Count)
        {
            return;
        }

        AudioTrackRuntime track = _audioTracks[_selectedAudioTrackIndex];
        track.Project.Volume = e.NewValue;
        track.Player.Volume = e.NewValue;
        UnsavedIndicator.Visibility = Visibility.Visible;
    }

    private void AudioMute_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isRestoringProject ||
            _selectedAudioTrackIndex < 0 ||
            _selectedAudioTrackIndex >= _audioTracks.Count)
        {
            return;
        }

        AudioTrackRuntime track = _audioTracks[_selectedAudioTrackIndex];
        track.Project.Muted = AudioMuteButton.IsChecked == true;
        track.Player.IsMuted = track.Project.Muted;
        UnsavedIndicator.Visibility = Visibility.Visible;
    }

    private void MainWindow_Closed(object sender, WindowEventArgs e)
    {
        _playbackTimer.Stop();
        DisposeAudioTracks();
    }

    private sealed class AudioTrackRuntime(AudioTrackProject project)
    {
        public AudioTrackProject Project { get; set; } = project;
        public MediaPlayer Player { get; } = new() { AudioCategory = MediaPlayerAudioCategory.Media };
        public bool HasSource { get; set; }
        public string? LoadError { get; set; }
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
