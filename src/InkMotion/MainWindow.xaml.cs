using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Dispatching;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.Graphics.Canvas.Text;
using System.Diagnostics;
using System.IO;
using System.Numerics;
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
    private const string BaseInkLayerId = "ink-1";
    private readonly List<EditorFrame> _frames = [new()];
    private readonly List<InkLayerProject> _inkLayers = [new()];
    private readonly List<Button> _timelineButtons = [];
    private readonly HashSet<int> _interpolatedFrameIndices = [];
    private readonly Stack<EditorProject> _undoHistory = [];
    private readonly Stack<EditorProject> _redoHistory = [];
    private readonly List<AudioTrackRuntime> _audioTracks = [];
    private AudioChannelLayout _audioMixLayout = AudioChannelLayout.Stereo;
    private readonly List<RenderedStroke> _renderedStrokes = [];
    private readonly List<RenderedText> _renderedText = [];
    private readonly DispatcherQueueTimer _playbackTimer;
    private readonly Stopwatch _timelineClock = new();
    private int _selectedFrameIndex;
    private int _playheadFramePosition;
    private int _selectedAudioTrackIndex = -1;
    private double _frameRate = FramesPerSecond;
    private string _activeInkLayerId = BaseInkLayerId;
    private uint _brushColour = 0xFFFFA500;
    private double _brushWidth = 4;
    private uint? _activePointerId;
    private List<InkPoint>? _activeStroke;
    private EditorProject? _pendingEdit;
    private bool _pendingEditChanged;
    private StorageFile? _projectFile;
    private bool _isPlaying;
    private bool _isEraser;
    private bool _isRestoringProject;
    private bool _isInitialising = true;
    private TimeSpan _timelinePosition;
    private TimeSpan _timelineStartPosition;

    public MainWindow()
    {
        InitializeComponent();
        _isInitialising = false;
        Title = "InkMotion";
        _playbackTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _playbackTimer.Interval = TimeSpan.FromMilliseconds(16);
        _playbackTimer.Tick += PlaybackTimer_Tick;
        Closed += MainWindow_Closed;
        InitialiseVideoTimeline();
        UpdateSelectedAudioTrack();
        RefreshInkLayers();
        RefreshTimeline();
        RenderCurrentInk();
        UpdateFrameStatus();
        UpdateExportProfileReadout();
    }

    private EditorFrame CurrentFrame => _frames[_selectedFrameIndex];
    private InkLayerProject CurrentInkLayer => _inkLayers.First(layer => layer.Id == _activeInkLayerId);
    private List<List<InkPoint>> CurrentStrokes => GetLayerStrokes(CurrentFrame, _activeInkLayerId, create: true);

    private List<List<InkPoint>> GetLayerStrokes(EditorFrame frame, string layerId, bool create = false)
    {
        if (layerId == BaseInkLayerId)
        {
            return frame.Strokes;
        }

        if (frame.LayerStrokes.TryGetValue(layerId, out List<List<InkPoint>>? strokes))
        {
            return strokes;
        }

        if (!create)
        {
            return [];
        }

        strokes = [];
        frame.LayerStrokes.Add(layerId, strokes);
        return strokes;
    }

    private IReadOnlyList<List<InkPoint>> GetDisplayedLayerStrokes(int frameIndex, string layerId) =>
        InkAnimation.GetDisplayedStrokes(_frames, frameIndex, frame => GetLayerStrokes(frame, layerId));

    private void AddInkLayer_Click(object sender, RoutedEventArgs e)
    {
        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        string id = $"ink-{Guid.NewGuid():N}";
        var layer = new InkLayerProject { Id = id, Name = $"Ink {_inkLayers.Count + 1}" };
        _inkLayers.Add(layer);
        _activeInkLayerId = id;
        MarkEditChanged();
        CommitEdit();
        RefreshInkLayers();
        RefreshTimeline();
        RenderCurrentInk();
        UpdateFrameStatus();
        StatusText.Text = $"{layer.Name} added.";
    }

    private void RefreshInkLayers()
    {
        _isRestoringProject = true;
        InkLayerSelector.ItemsSource = null;
        InkLayerSelector.ItemsSource = _inkLayers;
        InkLayerSelector.SelectedItem = _inkLayers.FirstOrDefault(layer => layer.Id == _activeInkLayerId);
        UpdateInkLayerControls();
        _isRestoringProject = false;
    }

    private void UpdateInkLayerControls()
    {
        InkLayerVisibilityButton.IsChecked = CurrentInkLayer.IsVisible;
        InkLayerVisibilityButton.Content = CurrentInkLayer.IsVisible ? "Hide layer" : "Show layer";
        InkLayerOpacitySlider.Value = CurrentInkLayer.Opacity;
        InkLayerOpacitySlider.IsEnabled = CurrentInkLayer.IsVisible;
    }

    private void InkLayer_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRestoringProject || InkLayerSelector.SelectedItem is not InkLayerProject layer)
        {
            return;
        }

        FinishActiveEdit();
        _activeInkLayerId = layer.Id;
        UpdateInkLayerControls();
        RefreshTimeline();
        RenderCurrentInk();
        UpdateFrameStatus();
    }

    private void InkLayerVisibility_Click(object sender, RoutedEventArgs e)
    {
        bool visible = InkLayerVisibilityButton.IsChecked == true;
        if (!visible && _inkLayers.Count(layer => layer.IsVisible) == 1)
        {
            _isRestoringProject = true;
            InkLayerVisibilityButton.IsChecked = true;
            _isRestoringProject = false;
            StatusText.Text = "At least one ink layer must remain visible.";
            return;
        }

        BeginEdit();
        CurrentInkLayer.IsVisible = visible;
        MarkEditChanged();
        CommitEdit();
        UpdateInkLayerControls();
        RenderCurrentInk();
    }

    private void InkLayerOpacity_Changed(
        object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isRestoringProject || _isInitialising)
        {
            return;
        }

        BeginEdit();
        CurrentInkLayer.Opacity = e.NewValue;
        MarkEditChanged();
        RenderCurrentInk();
    }

    private void InkLayerOpacity_LostFocus(object sender, RoutedEventArgs e)
    {
        FinishActiveEdit();
    }

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
            EditorProject project = EditorProjectSerialiser.Deserialise(await FileIO.ReadTextAsync(file));
            ClearHistory();
            RestoreProjectState(project);
            string? audioWarning = await LoadAudioTracksAsync(project.AudioTracks);
            _projectFile = file;
            UnsavedIndicator.Visibility = Visibility.Collapsed;

            string? sourceWarning = await LoadTimelineSourcesAsync();
            StatusText.Text = sourceWarning is null
                ? $"Project opened  ·  {file.Name}"
                : $"Project opened  ·  {sourceWarning}";

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
            await FileIO.WriteTextAsync(file, EditorProjectSerialiser.Serialise(CreateProjectSnapshot()));
            _projectFile = file;
            UnsavedIndicator.Visibility = Visibility.Collapsed;
            StatusText.Text = $"Project saved  ·  {file.Name}";
        }
        catch (Exception ex) when (IsProjectFileException(ex))
        {
            StatusText.Text = $"Could not save project: {ex.Message}";
        }
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
            if (_audioTracks[_selectedAudioTrackIndex].LoadError is string loadError)
            {
                ClearHistory();
                UpdateSelectedAudioTrack();
                RefreshAudioTracks();
                StatusText.Text = $"Could not load audio: {loadError}";
                return;
            }

            await LoadAudioWaveformAsync(_audioTracks[_selectedAudioTrackIndex]);
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

    private void Workspace_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string workspace })
        {
            return;
        }

        VideoWorkspaceButton.Background = WorkspaceButtonBackground(workspace == "Video");
        AudioWorkspaceButton.Background = WorkspaceButtonBackground(workspace == "Audio");
        InkWorkspaceButton.Background = WorkspaceButtonBackground(workspace == "Ink");

        bool isAudioWorkspace = workspace == "Audio";
        bool isInkWorkspace = workspace == "Ink";
        PreviewStage.Visibility = isAudioWorkspace ? Visibility.Collapsed : Visibility.Visible;
        AudioWorkspacePanel.Visibility = isAudioWorkspace ? Visibility.Visible : Visibility.Collapsed;
        VideoInspectorPanel.Visibility = workspace == "Video" ? Visibility.Visible : Visibility.Collapsed;
        AudioInspectorPanel.Visibility = isAudioWorkspace ? Visibility.Visible : Visibility.Collapsed;
        InkInspectorPanel.Visibility = isInkWorkspace ? Visibility.Visible : Visibility.Collapsed;
        VideoTimelineWorkspace.Visibility = workspace == "Video" ? Visibility.Visible : Visibility.Collapsed;
        AudioTimelineWorkspace.Visibility = isAudioWorkspace ? Visibility.Visible : Visibility.Collapsed;
        InkTimelineWorkspace.Visibility = isInkWorkspace ? Visibility.Visible : Visibility.Collapsed;
        InkSurface.Visibility = isInkWorkspace ? Visibility.Visible : Visibility.Collapsed;

        if (isAudioWorkspace)
        {
            RefreshAudioTracks();
        }
        else if (isInkWorkspace)
        {
            RenderCurrentInk();
        }
    }

    private static SolidColorBrush WorkspaceButtonBackground(bool selected) =>
        new(selected ? Microsoft.UI.Colors.DodgerBlue : Microsoft.UI.Colors.Transparent);

    private void LoadAudio(AudioTrackRuntime track, StorageFile file)
    {
        PausePlayback();
        track.Project.AudioPath = file.Path;
        track.Project.StartFrame = _playheadFramePosition + 1;
        track.Project.SourceInSeconds = 0;
        track.Project.SourceOutSeconds = null;
        track.Project.FadeInSeconds = 0;
        track.Project.FadeOutSeconds = 0;
        track.Project.GainDecibels = 0;
        track.WaveformPeaks = [];
        OpenAudioPlayer(track);
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
        project.TagsInitialised = true;
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
            track.Project.TagsInitialised = true;
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
            StartFrame = _playheadFramePosition + 1,
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
        bool hasClip = hasSelection && _audioTracks[_selectedAudioTrackIndex].HasSource;
        AudioVolumeSlider.IsEnabled = hasSelection;
        AudioMuteButton.IsEnabled = hasSelection;
        MoveAudioClipButton.IsEnabled = hasClip;
        TrimAudioStartButton.IsEnabled = hasClip;
        TrimAudioEndButton.IsEnabled = hasClip;
        SplitAudioButton.IsEnabled = hasClip;
        AudioFadeInBox.IsEnabled = hasClip;
        AudioFadeOutBox.IsEnabled = hasClip;
        AudioTempoBox.IsEnabled = hasClip;
        AudioPitchBox.IsEnabled = hasClip;
        ResetAudioTempoPitchButton.IsEnabled = hasClip;
        NormaliseAudioButton.IsEnabled = hasClip;
        ResetAudioGainButton.IsEnabled = hasClip;
        if (!hasSelection)
        {
            _isRestoringProject = true;
            AudioTrackLabel.Text = "No audio track selected";
            AudioClipRangeText.Text = "No clip selected";
            AudioGainText.Text = "Clip gain: 0.0 dB";
            AudioFadeInBox.Value = 0;
            AudioFadeOutBox.Value = 0;
            AudioTempoBox.Value = 100;
            AudioPitchBox.Value = 0;
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
        AudioFadeInBox.Value = track.Project.FadeInSeconds;
        AudioFadeOutBox.Value = track.Project.FadeOutSeconds;
        AudioTempoBox.Value = track.Project.TempoPercent;
        AudioPitchBox.Value = track.Project.PitchSemitones;
        AudioGainText.Text = $"Clip gain: {track.Project.GainDecibels:+0.0;-0.0;0.0} dB";
        AudioClipRangeText.Text = track.Player is AudioClipPlayer player
            ? $"Source {FormatSeconds(track.Project.SourceInSeconds)} – " +
              $"{FormatSeconds(AudioClipEditing.GetClipEnd(track.Project.SourceInSeconds, track.Project.SourceOutSeconds, player.DurationSeconds))}" +
              $" · {FormatSeconds(AudioClipEditing.GetTimelineDuration(track.Project, player.DurationSeconds))} on the timeline" +
              $" · {player.ChannelCount} channel{(player.ChannelCount == 1 ? string.Empty : "s")} ({AudioChannelLayouts.DescribeSource(player.ChannelCount)})" +
              $" mixed to {AudioChannelLayouts.Get(_audioMixLayout).DisplayName}"
            : track.LoadError ?? "No clip on this track";
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
        int playheadFrameNumber = _playheadFramePosition + 1;
        if (track.Project.StartFrame == playheadFrameNumber)
        {
            StatusText.Text = "The audio clip already starts at the playhead.";
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        track.Project.StartFrame = playheadFrameNumber;
        MarkEditChanged();
        CommitEdit();
        SyncAudioTracks(TimeSpan.FromSeconds(_playheadFramePosition / _frameRate), startPlayback: false);
        UpdateSelectedAudioTrack();
        RefreshAudioTracks();
        StatusText.Text = $"Audio clip moved to frame {playheadFrameNumber}.";
    }

    private bool TryGetSelectedAudioClip([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AudioTrackRuntime? track)
    {
        track = _selectedAudioTrackIndex >= 0 && _selectedAudioTrackIndex < _audioTracks.Count
            ? _audioTracks[_selectedAudioTrackIndex]
            : null;
        return track?.HasSource == true;
    }

    private void ApplyAudioClipEdit(Func<AudioTrackRuntime, bool> edit, string successMessage, string failureMessage)
    {
        if (!TryGetSelectedAudioClip(out AudioTrackRuntime? track))
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        if (edit(track))
        {
            MarkEditChanged();
            StatusText.Text = successMessage;
        }
        else
        {
            StatusText.Text = failureMessage;
        }

        CommitEdit();
        track.Player?.Update(track.Project, _audioMixLayout);
        SyncAudioTracks(TimeSpan.FromSeconds(_playheadFramePosition / _frameRate), startPlayback: false);
        UpdateSelectedAudioTrack();
        RefreshAudioTracks();
    }

    private void TrimAudioStart_Click(object sender, RoutedEventArgs e) =>
        ApplyAudioClipEdit(
            track => AudioClipEditing.TryTrimStart(track.Project, _playheadFramePosition + 1, _frameRate, track.SourceDurationSeconds),
            $"Audio clip start trimmed to frame {_playheadFramePosition + 1}.",
            "Move the playhead inside the audio clip to trim its start.");

    private void TrimAudioEnd_Click(object sender, RoutedEventArgs e) =>
        ApplyAudioClipEdit(
            track => AudioClipEditing.TryTrimEnd(track.Project, _playheadFramePosition + 1, _frameRate, track.SourceDurationSeconds),
            $"Audio clip end trimmed at frame {_playheadFramePosition + 1}.",
            "Move the playhead inside the audio clip to trim its end.");

    private void SplitAudio_Click(object sender, RoutedEventArgs e) =>
        ApplyAudioClipEdit(
            track =>
            {
                AudioTrackProject? right = AudioClipEditing.Split(
                    track.Project,
                    _playheadFramePosition + 1,
                    _frameRate,
                    track.SourceDurationSeconds);
                if (right is null)
                {
                    return false;
                }

                var runtime = new AudioTrackRuntime(right) { WaveformPeaks = track.WaveformPeaks };
                OpenAudioPlayer(runtime);
                _audioTracks.Insert(_selectedAudioTrackIndex + 1, runtime);
                return true;
            },
            $"Audio clip split at frame {_playheadFramePosition + 1}; the second part is on the next track.",
            "Move the playhead inside the audio clip to split it.");

    private void AudioFade_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isInitialising || _isRestoringProject || !TryGetSelectedAudioClip(out AudioTrackRuntime? track))
        {
            return;
        }

        double seconds = double.IsFinite(args.NewValue) ? args.NewValue : 0;
        bool isFadeIn = ReferenceEquals(sender, AudioFadeInBox);
        double current = isFadeIn ? track.Project.FadeInSeconds : track.Project.FadeOutSeconds;
        if (Math.Abs(current - seconds) < 0.0001)
        {
            return;
        }

        ApplyAudioClipEdit(
            clip =>
            {
                if (isFadeIn)
                {
                    clip.Project.FadeInSeconds = seconds;
                }
                else
                {
                    clip.Project.FadeOutSeconds = seconds;
                }

                AudioClipEditing.ClampFades(clip.Project, clip.SourceDurationSeconds);
                return true;
            },
            isFadeIn ? $"Fade in set to {seconds:0.0#} s." : $"Fade out set to {seconds:0.0#} s.",
            string.Empty);
    }

    private async void NormaliseAudio_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedAudioClip(out AudioTrackRuntime? track) || track.Project.AudioPath is not string path)
        {
            return;
        }

        double start = track.Project.SourceInSeconds;
        double end = AudioClipEditing.GetClipEnd(start, track.Project.SourceOutSeconds, track.SourceDurationSeconds);
        NormaliseAudioButton.IsEnabled = false;
        StatusText.Text = "Analysing clip peak level…";
        float peak;
        try
        {
            peak = await Task.Run(() => AudioWaveformReader.ReadPeak(path, start, end));
        }
        catch (Exception ex) when (
            IsFileAccessException(ex) ||
            ex is InvalidDataException or NotSupportedException or InvalidOperationException or NAudio.MmException)
        {
            StatusText.Text = $"Could not analyse the clip: {ex.Message}";
            UpdateSelectedAudioTrack();
            return;
        }

        if (!_audioTracks.Contains(track) || _audioTracks.IndexOf(track) != _selectedAudioTrackIndex)
        {
            UpdateSelectedAudioTrack();
            return;
        }

        if (peak <= 0)
        {
            StatusText.Text = "The clip is silent, so it cannot be normalised.";
            UpdateSelectedAudioTrack();
            return;
        }

        double gain = AudioClipEditing.CalculateNormalisationGain(peak);
        ApplyAudioClipEdit(
            clip =>
            {
                clip.Project.GainDecibels = gain;
                return true;
            },
            $"Clip normalised to {AudioClipEditing.NormalisationTargetDecibels:0} dBFS ({gain:+0.0;-0.0;0.0} dB).",
            string.Empty);
    }

    private void ResetAudioGain_Click(object sender, RoutedEventArgs e) =>
        ApplyAudioClipEdit(
            track =>
            {
                if (track.Project.GainDecibels == 0)
                {
                    return false;
                }

                track.Project.GainDecibels = 0;
                return true;
            },
            "Clip gain reset to 0 dB.",
            "Clip gain is already 0 dB.");

    private void AudioTempoPitch_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isInitialising || _isRestoringProject || !TryGetSelectedAudioClip(out AudioTrackRuntime? track))
        {
            return;
        }

        bool isTempo = ReferenceEquals(sender, AudioTempoBox);
        double value = double.IsFinite(args.NewValue) ? args.NewValue : isTempo ? 100 : 0;
        value = isTempo
            ? Math.Clamp(value, AudioClipEditing.MinimumTempoPercent, AudioClipEditing.MaximumTempoPercent)
            : Math.Clamp(Math.Round(value * 10) / 10, AudioClipEditing.MinimumPitchSemitones, AudioClipEditing.MaximumPitchSemitones);
        double current = isTempo ? track.Project.TempoPercent : track.Project.PitchSemitones;
        if (Math.Abs(current - value) < 0.0001)
        {
            if (!double.IsFinite(args.NewValue) || Math.Abs(args.NewValue - value) > 0.0001)
            {
                UpdateSelectedAudioTrack();
            }

            return;
        }

        ApplyAudioClipEdit(
            clip =>
            {
                if (isTempo)
                {
                    clip.Project.TempoPercent = value;
                    AudioClipEditing.ClampFades(clip.Project, clip.SourceDurationSeconds);
                }
                else
                {
                    clip.Project.PitchSemitones = value;
                }

                return true;
            },
            isTempo ? $"Tempo set to {value:0.#}% without changing pitch." : $"Pitch set to {value:+0.#;-0.#;0} semitones without changing tempo.",
            string.Empty);
    }

    private void ResetAudioTempoPitch_Click(object sender, RoutedEventArgs e) =>
        ApplyAudioClipEdit(
            track =>
            {
                if (track.Project.TempoPercent == 100 && track.Project.PitchSemitones == 0)
                {
                    return false;
                }

                track.Project.TempoPercent = 100;
                track.Project.PitchSemitones = 0;
                AudioClipEditing.ClampFades(track.Project, track.SourceDurationSeconds);
                return true;
            },
            "Tempo and pitch reset.",
            "Tempo and pitch are already at their original values.");

    private void AudioMixLayout_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitialising || _isRestoringProject ||
            AudioMixLayoutSelector.SelectedItem is not ComboBoxItem { Tag: string tag } ||
            !Enum.TryParse(tag, out AudioChannelLayout layout) ||
            layout == _audioMixLayout)
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        _audioMixLayout = layout;
        MarkEditChanged();
        CommitEdit();
        foreach (AudioTrackRuntime track in _audioTracks)
        {
            track.Player?.Update(track.Project, _audioMixLayout);
        }

        UpdateAudioMixLayoutSelector();
        UpdateSelectedAudioTrack();
        StatusText.Text = $"Project mix layout set to {AudioChannelLayouts.Get(layout).DisplayName}. Monitoring folds it down to stereo.";
    }

    private void UpdateAudioMixLayoutSelector()
    {
        bool wasRestoringProject = _isRestoringProject;
        _isRestoringProject = true;
        AudioMixLayoutSelector.SelectedItem = AudioMixLayoutSelector.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag as string == _audioMixLayout.ToString());
        _isRestoringProject = wasRestoringProject;
        AudioChannelLayoutInfo info = AudioChannelLayouts.Get(_audioMixLayout);
        AudioMixLayoutText.Text =
            $"{info.ChannelCount} channel{(info.ChannelCount == 1 ? string.Empty : "s")} · " +
            string.Join(" ", info.Speakers.Select(SpeakerAbbreviation)) +
            (info.ChannelCount > 2 ? " · monitored as a stereo fold-down" : string.Empty);
    }

    private static string SpeakerAbbreviation(AudioSpeaker speaker) => speaker switch
    {
        AudioSpeaker.FrontLeft => "L",
        AudioSpeaker.FrontRight => "R",
        AudioSpeaker.FrontCentre => "C",
        AudioSpeaker.LowFrequency => "LFE",
        AudioSpeaker.BackLeft => "Lb",
        AudioSpeaker.BackRight => "Rb",
        AudioSpeaker.BackCentre => "Cb",
        AudioSpeaker.SideLeft => "Ls",
        AudioSpeaker.SideRight => "Rs",
        AudioSpeaker.TopFrontLeft => "Ltf",
        AudioSpeaker.TopFrontRight => "Rtf",
        AudioSpeaker.TopBackLeft => "Ltb",
        AudioSpeaker.TopBackRight => "Rtb",
        _ => speaker.ToString(),
    };

    private static string FormatSeconds(double seconds) =>
        double.IsFinite(seconds) ? TimeSpan.FromSeconds(seconds).ToString(@"m\:ss\.ff") : "end";

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
            AudioTracksPanel.Children.Add(CreateAudioTrackLane(track, i));
        }
    }

    private Button CreateAudioTrackLane(AudioTrackRuntime track, int index)
    {
        var waveform = new CanvasControl
        {
            ClearColor = Windows.UI.Color.FromArgb(0, 0, 0, 0),
            Height = 36,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Tag = track,
        };
        waveform.Draw += AudioWaveform_Draw;

        var content = new Grid();
        content.Children.Add(waveform);
        content.Children.Add(new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Microsoft.UI.Colors.DarkSlateGray),
            Padding = new Thickness(6, 2, 6, 2),
            Child = new TextBlock
            {
                Text = AudioWaveformLabel(track),
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
        });

        var button = new Button
        {
            Height = 42,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(6, 2, 6, 2),
            Background = new SolidColorBrush(index == _selectedAudioTrackIndex
                ? Microsoft.UI.Colors.DarkSlateBlue
                : Microsoft.UI.Colors.DarkSlateGray),
            Content = content,
            Tag = index,
        };
        button.Click += AudioTrack_Click;
        return button;
    }

    private static string AudioWaveformLabel(AudioTrackRuntime track)
    {
        if (track.WaveformError is not null)
        {
            return track.WaveformError;
        }

        if (track.WaveformLoading)
        {
            return "Generating waveform…";
        }

        return string.IsNullOrWhiteSpace(track.Project.AudioPath)
            ? "Empty audio track"
            : $"{System.IO.Path.GetFileName(track.Project.AudioPath)}  ·  F{track.Project.StartFrame:000}";
    }

    private void AudioWaveform_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (sender.Tag is not AudioTrackRuntime { WaveformPeaks.Length: > 0 } track)
        {
            return;
        }

        float centre = (float)sender.ActualHeight / 2;
        float halfHeight = centre * 0.82f;
        double sourceDuration = track.SourceDurationSeconds;
        int firstPeak = 0;
        int lastPeak = track.WaveformPeaks.Length;
        double clipDuration = AudioClipEditing.GetClipDuration(track.Project, sourceDuration);
        if (sourceDuration > 0)
        {
            double clipEnd = track.Project.SourceInSeconds + clipDuration;
            firstPeak = Math.Clamp((int)Math.Floor(track.Project.SourceInSeconds / sourceDuration * lastPeak), 0, lastPeak - 1);
            lastPeak = Math.Clamp((int)Math.Ceiling(clipEnd / sourceDuration * lastPeak), firstPeak + 1, lastPeak);
        }

        int visiblePeaks = lastPeak - firstPeak;
        float width = (float)sender.ActualWidth;
        float columnWidth = width / visiblePeaks;
        float gain = (float)Math.Clamp(AudioClipEditing.DecibelsToLinear(track.Project.GainDecibels), 0, 4);
        Windows.UI.Color colour = ToColour(0xFF64C8FF);
        for (int i = 0; i < visiblePeaks; i++)
        {
            float amplitude = Math.Min(1, track.WaveformPeaks[firstPeak + i] * gain) * halfHeight;
            if (amplitude <= 0)
            {
                continue;
            }

            float x = (i + 0.5f) * columnWidth;
            args.DrawingSession.DrawLine(
                x,
                centre - amplitude,
                x,
                centre + amplitude,
                colour,
                Math.Max(1, columnWidth * 0.7f));
        }

        clipDuration = AudioClipEditing.GetTimelineDuration(track.Project, sourceDuration);
        if (clipDuration <= 0 || !double.IsFinite(clipDuration))
        {
            return;
        }

        Windows.UI.Color fadeColour = ToColour(0xFFFFC857);
        float top = 2;
        float bottom = (float)sender.ActualHeight - 2;
        if (track.Project.FadeInSeconds > 0)
        {
            float fadeEnd = (float)(track.Project.FadeInSeconds / clipDuration) * width;
            args.DrawingSession.DrawLine(0, bottom, fadeEnd, top, fadeColour, 1.5f);
        }

        if (track.Project.FadeOutSeconds > 0)
        {
            float fadeStart = width - (float)(track.Project.FadeOutSeconds / clipDuration) * width;
            args.DrawingSession.DrawLine(fadeStart, top, width, bottom, fadeColour, 1.5f);
        }
    }

    private async Task LoadAudioWaveformAsync(AudioTrackRuntime track)
    {
        string? audioPath = track.Project.AudioPath;
        if (string.IsNullOrWhiteSpace(audioPath))
        {
            return;
        }

        track.WaveformPeaks = [];
        track.WaveformError = null;
        track.WaveformLoading = true;
        RefreshAudioTracks();
        try
        {
            track.WaveformPeaks = await Task.Run(() => AudioWaveformReader.ReadPeaks(audioPath));
        }
        catch (Exception ex) when (
            ex is IOException or
            InvalidDataException or
            UnauthorizedAccessException or
            NotSupportedException or
            ArgumentException or
            InvalidOperationException or
            NAudio.MmException or
            System.Runtime.InteropServices.COMException)
        {
            track.WaveformError = $"Waveform unavailable: {ex.Message}";
        }
        finally
        {
            track.WaveformLoading = false;
            RefreshAudioTracks();
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

            OpenAudioPlayer(runtime);
            if (runtime.HasSource)
            {
                await LoadAudioWaveformAsync(runtime);
                if (!HasAudioTags(project))
                {
                    await LoadAudioTagsAsync(runtime);
                }
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
            track.Dispose();
        }

        _audioTracks.Clear();
        _selectedAudioTrackIndex = -1;
    }

    private void OpenAudioPlayer(AudioTrackRuntime track)
    {
        track.Dispose();
        track.LoadError = null;
        string? path = track.Project.AudioPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!File.Exists(path))
        {
            track.LoadError = "source audio is missing";
            return;
        }

        try
        {
            track.Player = new AudioClipPlayer(path, track.Project, _audioMixLayout);
        }
        catch (Exception ex) when (
            IsFileAccessException(ex) ||
            ex is InvalidDataException or NotSupportedException or InvalidOperationException or NAudio.MmException)
        {
            track.LoadError = $"source audio is unavailable: {ex.Message}";
        }
    }

    private static bool IsProjectFileException(Exception exception) =>
        exception is JsonException or InvalidDataException or IOException or
            UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException;

    private static bool IsFileAccessException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or
            ArgumentException or System.Runtime.InteropServices.COMException;

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (!HasTimelineClips && !HasLoadedAudio)
        {
            StatusText.Text = "Add a video, image, title or audio clip before playback.";
            return;
        }

        if (_isPlaying)
        {
            PausePlayback();
        }
        else
        {
            FinishActiveEdit();
            double end = GetPlaybackEndSeconds();
            if (end > 0 && _timelinePosition.TotalSeconds >= end - (0.5 / _frameRate))
            {
                _timelinePosition = TimeSpan.Zero;
                SelectFrame(0, seekVideo: false);
            }

            _isPlaying = true;
            _timelineStartPosition = _timelinePosition;
            _timelineClock.Restart();
            SyncAudioTracks(_timelineStartPosition, startPlayback: true);
            SyncVideoPreview(_timelineStartPosition.TotalSeconds, forceSeek: true);
            _playbackTimer.Start();
            PlayButton.Content = "Pause";
        }
    }

    private void PreviousFrame_Click(object sender, RoutedEventArgs e)
    {
        SelectTimelineFrame(Math.Max(0, _playheadFramePosition - 1));
    }

    private void NextFrame_Click(object sender, RoutedEventArgs e)
    {
        SelectTimelineFrame(_playheadFramePosition + 1);
    }

    private void SelectTimelineFrame(int framePosition)
    {
        EnsureFrameExists(framePosition);
        _playheadFramePosition = framePosition;
        SelectFrame(
            InkTimeline.GetFrameIndexAtPosition(_frames, framePosition),
            seekVideo: false,
            preservePlayhead: true);
        SeekToCurrentFrame();
        UpdateFrameReadout();
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
        EditorFrame clonedSource = CloneFrame(sourceFrame);
        var duplicate = new EditorFrame
        {
            Number = sourceFrame.Number + 1,
            ExposureFrames = sourceFrame.ExposureFrames,
            IsKeyframe = sourceFrame.IsKeyframe,
            InterpolationMode = sourceFrame.InterpolationMode,
            Strokes = clonedSource.Strokes,
            LayerStrokes = clonedSource.LayerStrokes,
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

    private void DeleteFrame_Click(object sender, RoutedEventArgs e)
    {
        if (_frames.Count <= 1)
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        _selectedFrameIndex = InkTimeline.DeleteFrame(_frames, _selectedFrameIndex);
        _playheadFramePosition = InkTimeline.GetStartFramePosition(_frames, _selectedFrameIndex);
        MarkEditChanged();
        CommitEdit();
        RefreshTimeline();
        RenderCurrentInk();
        SeekToCurrentFrame();
        UpdateFrameStatus();
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

    private void InkInterpolation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRestoringProject ||
            !CurrentFrame.IsKeyframe ||
            InkInterpolationSelector.SelectedItem is not ComboBoxItem { Tag: string modeText } ||
            !Enum.TryParse(modeText, out InkInterpolationMode mode) ||
            !Enum.IsDefined(mode))
        {
            return;
        }

        if (CurrentFrame.InterpolationMode == mode)
        {
            return;
        }

        BeginEdit();
        CurrentFrame.InterpolationMode = mode;
        MarkEditChanged();
        CommitEdit();
        RenderCurrentInk();
        StatusText.Text = $"Tween set to {modeText}.";
    }

    private void ExposureFrames_Changed(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (_isRestoringProject || !double.IsFinite(e.NewValue))
        {
            return;
        }

        int exposureFrames = (int)Math.Clamp(Math.Round(e.NewValue), 1, 999);
        if (CurrentFrame.ExposureFrames == exposureFrames)
        {
            return;
        }

        BeginEdit();
        CurrentFrame.ExposureFrames = exposureFrames;
        _playheadFramePosition = InkTimeline.GetStartFramePosition(_frames, _selectedFrameIndex);
        MarkEditChanged();
        RefreshTimeline();
        SeekToCurrentFrame();
    }

    private void ExposureFrames_LostFocus(object sender, RoutedEventArgs e)
    {
        FinishActiveEdit();
        UpdateFrameReadout();
    }

    private void UpdateInterpolationSelector()
    {
        _isRestoringProject = true;
        InkInterpolationSelector.IsEnabled = CurrentFrame.IsKeyframe &&
            _frames.Skip(_selectedFrameIndex + 1).Any(frame => frame.IsKeyframe);
        InkInterpolationSelector.SelectedItem = InkInterpolationSelector.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag is string value &&
                Enum.TryParse(value, out InkInterpolationMode mode) &&
                mode == CurrentFrame.InterpolationMode);
        _isRestoringProject = false;
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
        VideoTimelineEditing.RescaleFrameRate(_videoTracks, _frameRate, rate);
        _frameRate = rate;
        MarkEditChanged();
        CommitEdit();
        SeekToCurrentFrame();
        RefreshVideoTimeline();
        UpdateExportProfileReadout();
        StatusText.Text = $"Timeline frame rate: {_frameRate:0} fps";
    }

    private void ExportResolution_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateExportProfileReadout();
    }

    private void UpdateExportProfileReadout()
    {
        if (ExportProfileReadout is null ||
            ExportResolutionSelector?.SelectedItem is not ComboBoxItem { Tag: string resolutionName } ||
            !Enum.TryParse(resolutionName, out ExportResolution resolution))
        {
            return;
        }

        VideoExportProfile profile = VideoExportProfile.Create(resolution, _frameRate);
        double megabits = ExportPlanning.GetVideoBitRate(profile, GetSelectedExportCodec()) / 1_000_000d;
        ExportProfileReadout.Text =
            $"{profile.Width} × {profile.Height} · {profile.FrameRate:0} fps · {megabits:0.#} Mbps";
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
            CurrentStrokes.Add(_activeStroke);
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

    private void AddInkText_Click(object sender, RoutedEventArgs e)
    {
        string text = InkTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusText.Text = "Enter text before adding an overlay.";
            return;
        }

        string fontFamily = InkTextFontSelector.SelectedItem is ComboBoxItem { Tag: string font }
            ? font
            : "Segoe UI";
        double fontSize = double.IsFinite(InkTextSizeBox.Value)
            ? Math.Clamp(InkTextSizeBox.Value, 6, 512)
            : 48;

        FinishActiveEdit();
        BeginEdit();
        CurrentFrame.TextOverlays.Add(new InkTextOverlay
        {
            Text = text,
            FontFamily = fontFamily,
            FontSize = fontSize,
            Colour = _brushColour,
        });
        MarkEditChanged();
        CommitEdit();
        RenderCurrentInk();
        StatusText.Text = $"Text overlay added to cel {CurrentFrame.Number}.";
    }

    private void RemoveInkText_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentFrame.TextOverlays.Count == 0)
        {
            StatusText.Text = "This cel has no text overlays.";
            return;
        }

        FinishActiveEdit();
        BeginEdit();
        CurrentFrame.TextOverlays.RemoveAt(CurrentFrame.TextOverlays.Count - 1);
        MarkEditChanged();
        CommitEdit();
        RenderCurrentInk();
        StatusText.Text = $"Last text overlay removed from cel {CurrentFrame.Number}.";
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
            BrushColour = _brushColour,
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
        double normalisedX = point.X / InkSurface.ActualWidth;
        double normalisedY = point.Y / InkSurface.ActualHeight;
        bool changed = CurrentStrokes.RemoveAll(stroke =>
            stroke.Any(inkPoint =>
                Math.Abs(inkPoint.X - normalisedX) <= radiusX &&
                Math.Abs(inkPoint.Y - normalisedY) <= radiusY)) > 0;

        if (changed)
        {
            MarkEditChanged();
            RenderCurrentInk();
        }
    }

    private void RenderCurrentInk()
    {
        _renderedStrokes.Clear();
        _renderedText.Clear();
        if (OnionSkinToggle?.IsChecked == true)
        {
            double opacity = OnionSkinOpacitySlider?.Value ?? 0.25;
            if (_selectedFrameIndex > 0)
            {
                DrawVisibleLayers(_selectedFrameIndex - 1, 0xFF00BFFF, opacity);
                DrawVisibleText(_selectedFrameIndex - 1, opacity);
            }

            if (_selectedFrameIndex + 1 < _frames.Count)
            {
                DrawVisibleLayers(_selectedFrameIndex + 1, 0xFFFF69B4, opacity);
                DrawVisibleText(_selectedFrameIndex + 1, opacity);
            }
        }

        DrawVisibleLayers(_selectedFrameIndex, null, 1);
        DrawVisibleText(_selectedFrameIndex, 1);
        InkSurface.Invalidate();
    }

    private void DrawVisibleText(int frameIndex, double opacity)
    {
        foreach (InkTextOverlay overlay in _frames[frameIndex].TextOverlays)
        {
            _renderedText.Add(new RenderedText(overlay, opacity));
        }
    }

    private void DrawVisibleLayers(int frameIndex, uint? colour, double opacityScale)
    {
        foreach (InkLayerProject layer in _inkLayers.Where(layer => layer.IsVisible))
        {
            double opacity = layer.Opacity * opacityScale;
            foreach (List<InkPoint> stroke in GetDisplayedLayerStrokes(frameIndex, layer.Id))
            {
                if (stroke.Count > 0)
                {
                    _renderedStrokes.Add(new RenderedStroke(stroke, colour, opacity));
                }
            }
        }
    }

    private void InkSurface_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        float width = (float)sender.ActualWidth;
        float height = (float)sender.ActualHeight;
        foreach (RenderedStroke renderedStroke in _renderedStrokes)
        {
            List<InkPoint> stroke = renderedStroke.Points;
            Windows.UI.Color colour = TimelineCompositor.WithOpacity(
                ToColour(renderedStroke.Colour ?? stroke[0].BrushColour),
                renderedStroke.Opacity);
            TimelineCompositor.DrawInkStroke(args.DrawingSession, stroke, colour, width, height, 1);
        }

        foreach (RenderedText renderedText in _renderedText)
        {
            TimelineCompositor.DrawInkText(args.DrawingSession, renderedText.Overlay, renderedText.Opacity, width, height, 1);
        }
    }

    private static Windows.UI.Color ToColour(uint argb) => TimelineCompositor.ToColour(argb);

    private void InkColour_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (InkColourSelector.SelectedItem is ComboBoxItem { Tag: string colour } &&
            uint.TryParse(colour, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out uint argb))
        {
            _brushColour = argb;
        }
    }

    private void InkWidth_Changed(
        object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_isInitialising)
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

    private void SelectFrame(int index, bool seekVideo = true, bool preservePlayhead = false)
    {
        if (index < 0 || index >= _frames.Count)
        {
            return;
        }

        if (!preservePlayhead)
        {
            _playheadFramePosition = InkTimeline.GetStartFramePosition(_frames, index);
        }

        if (index == _selectedFrameIndex)
        {
            if (seekVideo)
            {
                SeekToCurrentFrame();
            }

            UpdateFrameReadout();
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
        UpdateInterpolationSelector();
        UpdateFrameStatus();
    }

    private void SeekToCurrentFrame()
    {
        TimeSpan position = TimeSpan.FromSeconds(_playheadFramePosition / _frameRate);
        _timelinePosition = position;
        if (_isPlaying)
        {
            _timelineStartPosition = position;
            _timelineClock.Restart();
        }

        SyncAudioTracks(position, startPlayback: _isPlaying);
        SyncVideoPreview(position.TotalSeconds, forceSeek: true);
        InvalidateVideoTimeline();
    }

    private void SyncAudioTracks(TimeSpan timelinePosition, bool startPlayback)
    {
        foreach (AudioTrackRuntime track in _audioTracks)
        {
            if (track.Player is not AudioClipPlayer player)
            {
                continue;
            }

            double clipOffset = timelinePosition.TotalSeconds - AudioClipEditing.GetClipStartSeconds(track.Project, _frameRate);
            double clipDuration = AudioClipEditing.GetTimelineDuration(track.Project, player.DurationSeconds);
            if (clipOffset < 0 || clipOffset >= clipDuration)
            {
                player.Pause();
                if (clipOffset < 0)
                {
                    player.Seek(track.Project.SourceInSeconds);
                }

                continue;
            }

            double sourcePosition = AudioClipEditing.TimelineToSourceSeconds(track.Project, clipOffset);
            if (startPlayback)
            {
                if (!player.IsPlaying || Math.Abs(player.AudiblePositionSeconds - sourcePosition) > 0.25)
                {
                    player.Seek(sourcePosition);
                }

                player.Play();
            }
            else
            {
                player.Pause();
                player.Seek(sourcePosition);
            }
        }
    }

    private bool HasLoadedAudio => _audioTracks.Any(track => track.HasSource);

    private TimeSpan? GetAudioTimelineEnd()
    {
        double[] ends = _audioTracks
            .Where(track => track.HasSource && track.SourceDurationSeconds > 0)
            .Select(track =>
                AudioClipEditing.GetClipStartSeconds(track.Project, _frameRate) +
                AudioClipEditing.GetTimelineDuration(track.Project, track.SourceDurationSeconds))
            .Where(end => end > 0)
            .ToArray();
        return ends.Length == 0 ? null : TimeSpan.FromSeconds(ends.Max());
    }

    private EditorProject CreateProjectSnapshot()
    {
        List<AudioTrackProject> tracks = _audioTracks.Select(track => CloneAudioTrack(track.Project)).ToList();
        AudioTrackProject? firstLoadedTrack = tracks.FirstOrDefault(track => !string.IsNullOrWhiteSpace(track.AudioPath));
        return new EditorProject
        {
            FrameRate = _frameRate,
            VideoTracks = _videoTracks.Select(VideoTimelineEditing.Clone).ToList(),
            AudioPath = firstLoadedTrack?.AudioPath,
            AudioVolume = firstLoadedTrack?.Volume ?? 1,
            AudioMuted = firstLoadedTrack?.Muted ?? false,
            AudioTracks = tracks,
            AudioMixLayout = _audioMixLayout,
            InkLayers = _inkLayers.Select(CloneInkLayer).ToList(),
            ActiveInkLayerId = _activeInkLayerId,
            SelectedFrameIndex = _selectedFrameIndex,
            Frames = _frames.Select(CloneFrame).ToList(),
        };
    }

    private static AudioTrackProject CloneAudioTrack(AudioTrackProject track) => AudioClipEditing.Clone(track);

    private static InkLayerProject CloneInkLayer(InkLayerProject layer) => new()
    {
        Id = layer.Id,
        Name = layer.Name,
        IsVisible = layer.IsVisible,
        Opacity = layer.Opacity,
    };

    private static bool HasAudioTags(AudioTrackProject project) =>
        project.TagsInitialised ||
        !string.IsNullOrWhiteSpace(project.Title) ||
        !string.IsNullOrWhiteSpace(project.Artist) ||
        !string.IsNullOrWhiteSpace(project.Album) ||
        !string.IsNullOrWhiteSpace(project.Genre) ||
        !string.IsNullOrWhiteSpace(project.Comments) ||
        project.Year > 0;

    private static EditorFrame CloneFrame(EditorFrame frame) => new()
    {
        Number = frame.Number,
        ExposureFrames = frame.ExposureFrames,
        IsKeyframe = frame.IsKeyframe,
        InterpolationMode = frame.InterpolationMode,
        TextOverlays = frame.TextOverlays.Select(text => new InkTextOverlay
        {
            Text = text.Text,
            X = text.X,
            Y = text.Y,
            FontFamily = text.FontFamily,
            FontSize = text.FontSize,
            Colour = text.Colour,
        }).ToList(),
        Strokes = frame.Strokes
            .Select(stroke => stroke.Select(point => new InkPoint
            {
                X = point.X,
                Y = point.Y,
                Pressure = point.Pressure,
                BrushWidth = point.BrushWidth,
                BrushColour = point.BrushColour,
            }).ToList())
            .ToList(),
            LayerStrokes = frame.LayerStrokes.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Select(stroke => stroke.Select(point => new InkPoint
                {
                    X = point.X,
                    Y = point.Y,
                    Pressure = point.Pressure,
                    BrushWidth = point.BrushWidth,
                    BrushColour = point.BrushColour,
                }).ToList()).ToList(),
                StringComparer.Ordinal),
    };

    private void RestoreProjectState(EditorProject project)
    {
        PausePlayback();
        _isRestoringProject = true;
        _frames.Clear();
        _frames.AddRange(project.Frames.Select(CloneFrame));
        _inkLayers.Clear();
        _inkLayers.AddRange(project.InkLayers.Select(CloneInkLayer));
        _activeInkLayerId = project.ActiveInkLayerId;
        _selectedFrameIndex = project.SelectedFrameIndex;
        _frameRate = project.FrameRate;
        RestoreVideoTracks(project.VideoTracks);
        _audioMixLayout = project.AudioMixLayout;
        RestoreAudioTrackModels(project.AudioTracks);
        UpdateAudioMixLayoutSelector();
        RefreshInkLayers();
        _isRestoringProject = false;
        _activePointerId = null;
        _activeStroke = null;
        _pendingEdit = null;
        _pendingEditChanged = false;
        _playheadFramePosition = InkTimeline.GetStartFramePosition(_frames, _selectedFrameIndex);
        _timelinePosition = TimeSpan.FromSeconds(_playheadFramePosition / _frameRate);
        RenderCurrentInk();
        SeekToCurrentFrame();
        RefreshTimeline();
        RefreshVideoTimeline();
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
                reused.Player?.Update(reused.Project, _audioMixLayout);
                restoredTracks.Add(reused);
            }
            else
            {
                var restored = new AudioTrackRuntime(CloneAudioTrack(project));
                OpenAudioPlayer(restored);
                AudioTrackRuntime? sameSource = previousTracks.FirstOrDefault(previous =>
                    previous.WaveformPeaks.Length > 0 &&
                    string.Equals(previous.Project.AudioPath, project.AudioPath, StringComparison.OrdinalIgnoreCase));
                restored.WaveformPeaks = sameSource?.WaveformPeaks ?? [];
                restoredTracks.Add(restored);
            }
        }

        foreach (AudioTrackRuntime removed in previousTracks.Except(restoredTracks))
        {
            removed.Dispose();
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
            CurrentStrokes.Remove(_activeStroke);
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

        TimeSpan position = _timelineStartPosition + _timelineClock.Elapsed;
        double end = GetPlaybackEndSeconds();
        if (end > 0 && position.TotalSeconds >= end)
        {
            _timelinePosition = TimeSpan.FromSeconds(end);
            PausePlayback();
            return;
        }

        _timelinePosition = position;
        SyncAudioTracks(position, startPlayback: true);
        SyncVideoPreview(position.TotalSeconds, forceSeek: false);
        int framePosition = Math.Max(0, (int)Math.Floor(position.TotalSeconds * _frameRate));
        EnsureFrameExists(framePosition);
        _playheadFramePosition = framePosition;
        SelectFrame(
            InkTimeline.GetFrameIndexAtPosition(_frames, framePosition),
            seekVideo: false,
            preservePlayhead: true);
        UpdateFrameReadout();
        CompositionSurface.Invalidate();
        InvalidateVideoTimeline();
    }

    private void EnsureFrameExists(int framePosition)
    {
        int totalExposureFrames = InkTimeline.GetTotalExposureFrames(_frames);
        while (totalExposureFrames <= framePosition)
        {
            var frame = new EditorFrame { Number = _frames.Count + 1 };
            _frames.Add(frame);
            AddTimelineButton(frame, _frames.Count - 1);
            totalExposureFrames++;
            UnsavedIndicator.Visibility = Visibility.Visible;
        }
    }

    private void PausePlayback()
    {
        if (!_isPlaying)
        {
            return;
        }

        _previewMediaPlayer.Pause();
        foreach (AudioTrackRuntime track in _audioTracks)
        {
            track.Player?.Pause();
        }

        if (_timelineClock.IsRunning)
        {
            _timelinePosition = _timelineStartPosition + _timelineClock.Elapsed;
            _timelineClock.Stop();
        }

        _playbackTimer.Stop();
        _isPlaying = false;
        PlayButton.Content = "Play";
    }

    private void PromoteInterpolatedFrameForEditing()
    {
        List<List<InkPoint>> currentStrokes = CurrentStrokes;
        if (CurrentFrame.IsKeyframe || currentStrokes.Count > 0)
        {
            return;
        }

        IReadOnlyList<List<InkPoint>> displayed = GetDisplayedLayerStrokes(_selectedFrameIndex, _activeInkLayerId);
        if (_isEraser && displayed.Count == 0)
        {
            return;
        }

        if (displayed.Count > 0)
        {
            currentStrokes.AddRange(displayed
                .Select(stroke => stroke.Select(point => new InkPoint
                {
                    X = point.X,
                    Y = point.Y,
                    Pressure = point.Pressure,
                    BrushWidth = point.BrushWidth,
                    BrushColour = point.BrushColour,
                }).ToList())
                .ToList());
        }

        CurrentFrame.IsKeyframe = true;
        MarkEditChanged();
        RefreshTimeline();
        UpdateFrameStatus();
    }

    private void RefreshTimeline()
    {
        _interpolatedFrameIndices.Clear();
        _interpolatedFrameIndices.UnionWith(InkAnimation.GetInterpolatedFrameIndices(
            _frames,
            frame => GetLayerStrokes(frame, _activeInkLayerId)));
        InkFramesPanel.Children.Clear();
        _timelineButtons.Clear();
        for (int i = 0; i < _frames.Count; i++)
        {
            AddTimelineButton(_frames[i], i);
        }

        UpdateFrameReadout();
        KeyframeButton.Content = CurrentFrame.IsKeyframe ? "Remove keyframe" : "Set keyframe";
        UpdateInterpolationSelector();
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
        button.Width = Math.Min(220, 54 + ((frame.ExposureFrames - 1) * 4));
        bool isInterpolated = _interpolatedFrameIndices.Contains(index);
        string prefix = frame.IsKeyframe ? "K " : isInterpolated ? "~" : "";
        button.Content = $"{prefix}{frame.Number:000}";
        ToolTipService.SetToolTip(
            button,
            $"{(frame.IsKeyframe ? "Keyframe" : isInterpolated ? "Interpolated cel" : "Cel")} {frame.Number}; " +
            $"exposed for {frame.ExposureFrames} output frame(s)");
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
        if (_isInitialising || _isRestoringProject ||
            _selectedAudioTrackIndex < 0 ||
            _selectedAudioTrackIndex >= _audioTracks.Count)
        {
            return;
        }

        AudioTrackRuntime track = _audioTracks[_selectedAudioTrackIndex];
        track.Project.Volume = e.NewValue;
        track.Player?.Update(track.Project, _audioMixLayout);
        UnsavedIndicator.Visibility = Visibility.Visible;
    }

    private void AudioMute_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitialising || _isRestoringProject ||
            _selectedAudioTrackIndex < 0 ||
            _selectedAudioTrackIndex >= _audioTracks.Count)
        {
            return;
        }

        AudioTrackRuntime track = _audioTracks[_selectedAudioTrackIndex];
        track.Project.Muted = AudioMuteButton.IsChecked == true;
        track.Player?.Update(track.Project, _audioMixLayout);
        UnsavedIndicator.Visibility = Visibility.Visible;
    }

    private void MainWindow_Closed(object sender, WindowEventArgs e)
    {
        _playbackTimer.Stop();
        DisposeAudioTracks();
        DisposeVideoTimeline();
    }

    private sealed class AudioTrackRuntime(AudioTrackProject project) : IDisposable
    {
        public AudioTrackProject Project { get; set; } = project;
        public AudioClipPlayer? Player { get; set; }
        public bool HasSource => Player is not null;
        public double SourceDurationSeconds => Player?.DurationSeconds ?? 0;
        public string? LoadError { get; set; }
        public float[] WaveformPeaks { get; set; } = [];
        public string? WaveformError { get; set; }
        public bool WaveformLoading { get; set; }

        public void Dispose()
        {
            Player?.Dispose();
            Player = null;
        }
    }

    private sealed record RenderedStroke(List<InkPoint> Points, uint? Colour, double Opacity);
    private sealed record RenderedText(InkTextOverlay Overlay, double Opacity);

    private void UpdateFrameStatus()
    {
        KeyframeStatus.Text = CurrentFrame.IsKeyframe
            ? $"Keyframe set at cel {CurrentFrame.Number}."
            : InkAnimation.IsInterpolated(_frames, _selectedFrameIndex, frame => GetLayerStrokes(frame, _activeInkLayerId))
                ? $"Interpolated drawing at cel {CurrentFrame.Number}."
                : CurrentStrokes.Count > 0
                    ? $"Hand-drawn exposure on cel {CurrentFrame.Number}."
                    : "No ink on this frame";
    }

    private void UpdateFrameReadout()
    {
        FrameReadout.Text = $"F {_playheadFramePosition + 1:000} · C{CurrentFrame.Number:000}";
        bool wasRestoringProject = _isRestoringProject;
        if (ExposureFramesBox.FocusState == Microsoft.UI.Xaml.FocusState.Unfocused)
        {
            _isRestoringProject = true;
            ExposureFramesBox.Value = CurrentFrame.ExposureFrames;
            _isRestoringProject = wasRestoringProject;
        }
        DeleteFrameButton.IsEnabled = _frames.Count > 1;
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
        UpdateExportProfileReadout();
    }

}
