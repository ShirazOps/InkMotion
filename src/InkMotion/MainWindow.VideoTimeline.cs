using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace InkMotion;

public sealed partial class MainWindow
{
    private sealed class VideoAudioPreviewState
    {
        public AudioClipPlayer? Player { get; set; }
        public AudioTrackProject? Settings { get; set; }
        public string? ClipId { get; set; }
        public string? Path { get; set; }
        public string? FailedClipId { get; set; }
        public string? FailedPath { get; set; }
        public VideoAudioEnvelopeKey? EnvelopeKey { get; set; }
        public bool FallbackActive { get; set; }

        public void Clear()
        {
            Player?.Dispose();
            Player = null;
            Settings = null;
            ClipId = null;
            Path = null;
            FailedClipId = null;
            FailedPath = null;
            EnvelopeKey = null;
            FallbackActive = false;
        }
    }

    private readonly record struct VideoAudioEnvelopeKey(
        double Volume,
        double FadeInSeconds,
        double FadeOutSeconds,
        double PlaybackSpeed,
        bool IsReversed,
        double SourceInSeconds,
        double SourceOutSeconds,
        VideoAudioTransition Transition);

    private const float TimelineRulerHeight = 22;
    private const float TimelineLaneHeight = 34;
    private const float TimelineLaneSpacing = 2;
    private const double PreviewDriftToleranceSeconds = 0.2;
    private readonly List<TimelineTrackProject> _videoTracks = [];
    private readonly List<VideoMulticamGroup> _videoMulticamGroups = [];
    private readonly Dictionary<string, StorageFile> _timelineSourceFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unavailableTimelineSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CanvasBitmap> _overlayBitmaps = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _overlayBitmapRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unavailablePreviewVideoSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VideoColourGradingEffect> _previewColourGradingEffects = [];
    private readonly Dictionary<string, VideoChromaKeyEffect> _previewChromaKeyEffects = [];
    private readonly Dictionary<string, VideoBlurCanvasEffect> _previewBlurEffects = [];
    private readonly Dictionary<string, VideoSharpenCanvasEffect> _previewSharpenEffects = [];
    private readonly Dictionary<string, VideoVignetteCanvasEffect> _previewVignetteEffects = [];
    private bool _isStabilisationRunning;
    private bool _isMaskTrackingRunning;
    private readonly MediaPlayer _previewMediaPlayer = new() { AutoPlay = false, IsLoopingEnabled = false };
    private readonly VideoAudioPreviewState _videoAudioPreview = new();
    private readonly VideoAudioPreviewState _transitionVideoAudioPreview = new();
    private bool _videoAudioFallbackActive =>
        _videoAudioPreview.FallbackActive || _transitionVideoAudioPreview.FallbackActive;
    private readonly CompositeTransform _previewVideoTransform = new();
    private CanvasRenderTarget? _videoScopeSampleTarget;
    private CanvasBitmap? _videoLumaWaveformBitmap;
    private CanvasBitmap? _videoVectorscopeBitmap;
    private VideoRgbHistogramData? _videoRgbHistogram;
    private long _lastVideoScopeSampleMilliseconds;
    private readonly Dictionary<string, (string Path, TimelineVideoFrameSource Source)> _previewVideoFrameSources = [];
    private MediaSource? _previewMediaSource;
    private string? _previewSourcePath;
    private bool _previewReady;
    private string? _selectedTimelineTrackId;
    private string? _selectedTimelineClipId;
    private double _timelinePixelsPerSecond = 60;
    private TimelineDrag? _timelineDrag;
    private bool _isScrubbingTimeline;
    private TimelineToolMode _timelineToolMode = TimelineToolMode.Select;
    private TimelineClipProject? _draggedCurveClip;
    private VideoColourCurveChannel _draggedCurveChannel;
    private int _draggedCurvePointIndex = -1;
    private uint? _curveDragPointerId;
    private CanvasTextFormat? _timelineLabelFormat;
    private CanvasTextFormat? _timelineRulerFormat;

    private bool HasTimelineClips => _videoTracks.Any(track => track.Clips.Count > 0);

    private int PlayheadFrameNumber => _playheadFramePosition + 1;

    private TimelineTrackProject? SelectedTimelineTrack =>
        _videoTracks.FirstOrDefault(track => track.Id == _selectedTimelineTrackId) ?? _videoTracks.FirstOrDefault();

    private TimelineClipLocation? SelectedTimelineClip => VideoTimelineEditing.FindClip(_videoTracks, _selectedTimelineClipId);

    private double CurrentPlaybackSeconds => _isPlaying
        ? (_timelineStartPosition + _timelineClock.Elapsed).TotalSeconds
        : _timelinePosition.TotalSeconds;

    private void InitialiseVideoTimeline()
    {
        PreviewPlayer.SetMediaPlayer(_previewMediaPlayer);
        _previewVideoTransform.ScaleX = _previewVideoTransform.ScaleY = 1;
        PreviewPlayer.RenderTransformOrigin = new Point(0, 0);
        PreviewPlayer.RenderTransform = _previewVideoTransform;
        _previewMediaPlayer.MediaOpened += PreviewMediaPlayer_MediaOpened;
        _previewMediaPlayer.MediaFailed += PreviewMediaPlayer_MediaFailed;
        InitialiseSourceMonitor();
        EnsureDefaultVideoTrack();
        RefreshVideoTimeline();
    }

    private void EnsureDefaultVideoTrack()
    {
        if (_videoTracks.Count == 0)
        {
            _videoTracks.Add(VideoTimelineEditing.CreateTrack(_videoTracks, TimelineTrackKind.Video));
        }
    }

    private void RestoreVideoTracks(IEnumerable<TimelineTrackProject> tracks)
    {
        DisposePreviewVideoFrameSources();
        _unavailablePreviewVideoSources.Clear();
        _videoTracks.Clear();
        _videoTracks.AddRange(tracks.Select(VideoTimelineEditing.Clone));
        EnsureDefaultVideoTrack();
        if (_videoTracks.All(track => track.Id != _selectedTimelineTrackId))
        {
            _selectedTimelineTrackId = _videoTracks[0].Id;
        }

        if (SelectedTimelineClip is null)
        {
            _selectedTimelineClipId = null;
        }
    }

    private double GetPlaybackEndSeconds() => Math.Max(
        VideoTimelineEditing.GetTimelineEndSeconds(_videoTracks, _frameRate),
        GetAudioTimelineEnd()?.TotalSeconds ?? 0);

    private async void AddVideoClip_Click(object sender, RoutedEventArgs e)
    {
        PausePlayback();
        FinishActiveEdit();
        StorageFile? file = await PickTimelineFileAsync(VideoFileTypes.SupportedExtensions);
        if (file is null)
        {
            return;
        }

        double duration = await ProbeVideoDurationAsync(file);
        if (duration <= 0)
        {
            StatusText.Text = $"Could not read the length of {file.Name}; the format may not be supported.";
            return;
        }

        RegisterTimelineSource(file);
        AddTimelineClip(
            TimelineTrackKind.Video,
            new TimelineClipProject { SourcePath = file.Path, SourceDurationSeconds = duration, StartFrame = PlayheadFrameNumber },
            $"Added video  ·  {file.Name}");
    }

    private async void AddImageClip_Click(object sender, RoutedEventArgs e)
    {
        PausePlayback();
        FinishActiveEdit();
        StorageFile? file = await PickTimelineFileAsync(VideoTimelineEditing.ImageExtensions);
        if (file is null)
        {
            return;
        }

        RegisterTimelineSource(file);
        AddTimelineClip(
            TimelineTrackKind.Image,
            new TimelineClipProject { SourcePath = file.Path, StartFrame = PlayheadFrameNumber },
            $"Added image  ·  {file.Name}");
    }

    private void AddTitleClip_Click(object sender, RoutedEventArgs e)
    {
        PausePlayback();
        FinishActiveEdit();
        AddTimelineClip(
            TimelineTrackKind.Text,
            new TimelineClipProject { Text = "Title", Y = 0.8, StartFrame = PlayheadFrameNumber },
            "Added title. Edit its text in the inspector.");
    }

    private async Task<StorageFile?> PickTimelineFileAsync(IEnumerable<string> extensions)
    {
        var picker = new FileOpenPicker();
        foreach (string extension in extensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        return await picker.PickSingleFileAsync();
    }

    private void RegisterTimelineSource(StorageFile file)
    {
        _timelineSourceFiles[file.Path] = file;
        _unavailableTimelineSources.Remove(file.Path);
    }

    private void AddTimelineClip(TimelineTrackKind kind, TimelineClipProject clip, string message)
    {
        BeginEdit();
        TimelineTrackProject? preferred = SelectedTimelineTrack is { } selected && selected.Kind == kind ? selected : null;
        TimelineTrackProject track = VideoTimelineEditing.PlaceClip(_videoTracks, kind, preferred, clip, _frameRate);
        MarkEditChanged();
        CommitEdit();
        _selectedTimelineTrackId = track.Id;
        _selectedTimelineClipId = clip.Id;
        RefreshVideoTimeline();
        SeekToCurrentFrame();
        StatusText.Text = ReferenceEquals(track, preferred) || preferred is null
            ? message
            : $"{message}  ·  placed on {track.Name} to avoid an overlap";
    }

    private void CreateMulticamGroup_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not { Track.Kind: TimelineTrackKind.Video } selected ||
            selected.Track.IsLocked)
        {
            StatusText.Text = "Select an unlocked video camera clip that is active at the playhead.";
            return;
        }

        double seconds = _playheadFramePosition / _frameRate;
        if (_videoTracks.Any(track =>
                track.Kind == TimelineTrackKind.Video &&
                VideoTransitions.GetAt(track, seconds, _frameRate) is not null))
        {
            StatusText.Text = "Create multicamera groups outside cross-dissolves so each angle has a single active clip.";
            return;
        }

        List<TimelineClipLocation> active = VideoTimelineEditing.GetVisibleLayers(_videoTracks, seconds, _frameRate)
            .Where(location => location.Track.Kind == TimelineTrackKind.Video)
            .DistinctBy(location => location.Clip.Id)
            .ToList();
        active = active
            .GroupBy(location => location.Track.Id, StringComparer.Ordinal)
            .Select(group => group.FirstOrDefault(location => location.Clip.Id == selected.Clip.Id) ?? group.Last())
            .ToList();
        if (!active.Any(location => location.Clip.Id == selected.Clip.Id) || active.Count < 2)
        {
            StatusText.Text = "At least two visible camera clips must overlap at the playhead, including the selected clip.";
            return;
        }

        var groupedClipIds = _videoMulticamGroups
            .SelectMany(group => group.Angles)
            .Select(angle => angle.TrackId)
            .ToHashSet(StringComparer.Ordinal);
        if (active.Any(location => groupedClipIds.Contains(location.Track.Id)))
        {
            StatusText.Text = "One or more overlapping clips already belong to a multicamera group.";
            return;
        }

        FinishActiveEdit();
        BeginEdit();
        _videoMulticamGroups.Add(new VideoMulticamGroup
        {
            Name = $"Multicam {_videoMulticamGroups.Count + 1}",
            StartFrame = PlayheadFrameNumber,
            Angles = active
                .OrderByDescending(location => location.Track.Id == selected.Track.Id)
                .DistinctBy(location => location.Track.Id)
                .Select(location => new VideoMulticamAngle { TrackId = location.Track.Id, Name = location.Track.Name })
                .ToList(),
        });
        MarkEditChanged();
        CommitEdit();
        RefreshVideoTimeline();
        SeekToCurrentFrame();
        StatusText.Text = $"Created a multicamera group with {active.Count} angles.";
    }

    private void SwitchMulticamAngle_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not { Track.Kind: TimelineTrackKind.Video } selected ||
            selected.Track.IsLocked)
        {
            StatusText.Text = "Select an unlocked video clip that belongs to a multicamera group.";
            return;
        }

        VideoMulticamGroup? group = _videoMulticamGroups.FirstOrDefault(candidate =>
            candidate.Angles.Any(angle => angle.TrackId == selected.Track.Id));
        if (group is null || PlayheadFrameNumber < group.StartFrame)
        {
            StatusText.Text = "The selected clip is not an active angle in a multicamera group at this playhead position.";
            return;
        }
        if (!VideoTimelineEditing.GetVisibleLayers(_videoTracks, _playheadFramePosition / _frameRate, _frameRate)
            .Any(location => location.Clip.Id == selected.Clip.Id))
        {
            StatusText.Text = "The selected camera clip must overlap the playhead to switch to that angle.";
            return;
        }

        FinishActiveEdit();
        BeginEdit();
        VideoMulticamEditing.SetAngleAtFrame(group, PlayheadFrameNumber, selected.Track.Id);
        MarkEditChanged();
        CommitEdit();
        RefreshVideoTimeline();
        SeekToCurrentFrame();
        StatusText.Text = $"Switched to {group.Angles.First(angle => angle.TrackId == selected.Track.Id).Name} at the playhead.";
    }

    private async void GenerateVideoProxy_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not { Track.Kind: TimelineTrackKind.Video } selected ||
            string.IsNullOrWhiteSpace(selected.Clip.SourcePath) ||
            !File.Exists(selected.Clip.SourcePath) ||
            selected.Track.IsLocked)
        {
            StatusText.Text = "Select an unlocked video clip with an available source to generate a proxy.";
            return;
        }

        string clipId = selected.Clip.Id;
        string sourcePath = selected.Clip.SourcePath;
        double duration = selected.Clip.SourceDurationSeconds;
        PausePlayback();
        FinishActiveEdit();
        GenerateVideoProxyButton.IsEnabled = false;
        StatusText.Text = "Generating a 24 fps proxy at up to 960 pixels…";
        try
        {
            var progress = new Progress<VideoProxyProgress>(value =>
                StatusText.Text = $"Generating proxy: {value.Frame}/{value.FrameCount} frames");
            VideoProxyResult proxy = await Task.Run(() => VideoProxyGenerator.Generate(sourcePath, duration, progress));
            TimelineClipLocation? current = VideoTimelineEditing.FindClip(_videoTracks, clipId);
            if (current is null || !string.Equals(current.Clip.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                StatusText.Text = "Proxy created, but the source clip changed before it could be attached.";
                return;
            }

            BeginEdit();
            current.Clip.ProxyPath = proxy.Path;
            current.Clip.ProxySourceLength = proxy.SourceLength;
            current.Clip.ProxySourceLastWriteUtcTicks = proxy.SourceLastWriteUtcTicks;
            current.Clip.UseProxy = true;
            MarkEditChanged();
            CommitEdit();
            RefreshVideoTimeline();
            SeekToCurrentFrame();
            StatusText.Text = $"Proxy created and enabled: {Path.GetFileName(proxy.Path)}";
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or
            InvalidOperationException or NotSupportedException or ArgumentException or OperationCanceledException)
        {
            StatusText.Text = $"Could not generate the video proxy: {ex.Message}";
        }
        finally
        {
            GenerateVideoProxyButton.IsEnabled = true;
        }
    }

    private void ToggleVideoProxy_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not { Track.Kind: TimelineTrackKind.Video } selected ||
            selected.Track.IsLocked)
        {
            return;
        }

        if (!selected.Clip.UseProxy && !VideoProxy.IsAvailable(selected.Clip))
        {
            StatusText.Text = "Generate a proxy before enabling proxy playback.";
            return;
        }

        FinishActiveEdit();
        BeginEdit();
        selected.Clip.UseProxy = !selected.Clip.UseProxy;
        MarkEditChanged();
        CommitEdit();
        RefreshVideoTimeline();
        SeekToCurrentFrame();
        StatusText.Text = selected.Clip.UseProxy ? "Proxy playback enabled." : "Original media playback enabled.";
    }

    private static async Task<double> ProbeVideoDurationAsync(StorageFile file)
    {
        try
        {
            using MediaSource source = MediaSource.CreateFromStorageFile(file);
            await source.OpenAsync();
            if (source.Duration is TimeSpan duration && duration > TimeSpan.Zero)
            {
                return duration.TotalSeconds;
            }
        }
        catch (Exception ex) when (IsFileAccessException(ex) || ex is InvalidOperationException)
        {
        }

        try
        {
            Windows.Storage.FileProperties.VideoProperties properties = await file.Properties.GetVideoPropertiesAsync();
            return properties.Duration.TotalSeconds;
        }
        catch (Exception ex) when (IsFileAccessException(ex) || ex is InvalidOperationException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Resolves every timeline source file after a project opens and fills in the length of video clips
    /// migrated from older projects. Returns a warning when sources are unavailable.
    /// </summary>
    private async Task<string?> LoadTimelineSourcesAsync()
    {
        _unavailableTimelineSources.Clear();
        _unavailablePreviewVideoSources.Clear();
        DisposePreviewVideoFrameSources();
        string[] paths = _videoTracks
            .Where(track => track.Kind != TimelineTrackKind.Text)
            .SelectMany(track => track.Clips)
            .Select(clip => clip.SourcePath)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (string path in paths)
        {
            if (_timelineSourceFiles.ContainsKey(path))
            {
                continue;
            }

            try
            {
                _timelineSourceFiles[path] = await StorageFile.GetFileFromPathAsync(path);
            }
            catch (Exception ex) when (IsFileAccessException(ex))
            {
                _unavailableTimelineSources.Add(path);
            }
        }

        var probed = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (TimelineClipProject clip in _videoTracks
            .Where(track => track.Kind == TimelineTrackKind.Video)
            .SelectMany(track => track.Clips)
            .Where(clip => clip.SourceDurationSeconds <= 0 && clip.SourcePath is not null))
        {
            if (!probed.TryGetValue(clip.SourcePath!, out double duration))
            {
                duration = _timelineSourceFiles.TryGetValue(clip.SourcePath!, out StorageFile? file)
                    ? await ProbeVideoDurationAsync(file)
                    : 0;
                probed[clip.SourcePath!] = duration;
            }

            if (duration > clip.SourceInSeconds)
            {
                clip.SourceDurationSeconds = duration;
            }
        }

        RefreshVideoTimeline();
        SeekToCurrentFrame();
        int missing = paths.Count(_unavailableTimelineSources.Contains);
        return missing switch
        {
            0 => null,
            1 => "1 timeline source file is missing",
            _ => $"{missing} timeline source files are missing",
        };
    }

    private void SyncVideoPreview(double seconds, bool forceSeek)
    {
        TimelineClipLocation? active = VideoTimelineEditing.GetActiveVideoClip(_videoTracks, seconds, _frameRate);
        VideoCrossDissolve? transition = !IsOriginalPreview && active is not null
            ? VideoTransitions.GetAt(active.Track, seconds, _frameRate)
            : null;
        if (transition is not null && active is not null)
        {
            bool incomingIsMain = transition.Progress >= 0.5;
            TimelineClipProject mainClip = incomingIsMain ? transition.IncomingClip : transition.OutgoingClip;
            TimelineClipLocation main = new(active.Track, mainClip);
            if (!SyncMainVideoPreview(main, seconds, forceSeek, synchronizeAudio: false,
                    preserveAudioOnSourceChange: true))
            {
                return;
            }

            var outgoing = new TimelineClipLocation(active.Track, transition.OutgoingClip);
            var incoming = new TimelineClipLocation(active.Track, transition.IncomingClip);
            VideoAudioTransition outgoingEnvelope = VideoTransitions.GetAudioTransition(
                active.Track, transition.OutgoingClip, _frameRate);
            VideoAudioTransition incomingEnvelope = VideoTransitions.GetAudioTransition(
                active.Track, transition.IncomingClip, _frameRate);
            bool outgoingRouted = SyncVideoAudioPreview(
                _videoAudioPreview,
                outgoing,
                VideoTimelineEditing.GetSourceSeconds(transition.OutgoingClip, seconds, _frameRate),
                forceSeek,
                outgoingEnvelope);
            bool incomingRouted = SyncVideoAudioPreview(
                _transitionVideoAudioPreview,
                incoming,
                VideoTimelineEditing.GetSourceSeconds(transition.IncomingClip, seconds, _frameRate),
                forceSeek,
                incomingEnvelope);
            _previewMediaPlayer.Volume = mainClip.Volume;
            _previewMediaPlayer.IsMuted = mainClip.IsReversed || outgoingRouted || incomingRouted ||
                active.Track.IsMuted || HasSoloAudio;
            return;
        }

        _transitionVideoAudioPreview.Clear();
        SyncMainVideoPreview(active, seconds, forceSeek);
    }

    private bool SyncMainVideoPreview(
        TimelineClipLocation? active,
        double seconds,
        bool forceSeek,
        bool synchronizeAudio = true,
        bool preserveAudioOnSourceChange = false)
    {
        ApplyVideoPreviewTransform(
            _previewVideoTransform,
            PreviewPlayer,
            IsOriginalPreview ? null : active?.Clip,
            seconds);
        StorageFile? file = null;
        if (active?.Clip.SourcePath is string path && !_unavailableTimelineSources.Contains(path))
        {
            _timelineSourceFiles.TryGetValue(path, out file);
        }

        if (active is null || file is null)
        {
            ClearPreviewSource();
            return false;
        }

        if (!string.Equals(_previewSourcePath, file.Path, StringComparison.OrdinalIgnoreCase))
        {
            _previewMediaPlayer.Pause();
            if (!preserveAudioOnSourceChange)
            {
                ClearVideoAudioPlayer();
            }
            _previewMediaPlayer.IsMuted = true;
            MediaSource? previous = _previewMediaSource;
            _previewSourcePath = file.Path;
            _previewReady = false;
            _previewMediaSource = MediaSource.CreateFromStorageFile(file);
            _previewMediaPlayer.Source = _previewMediaSource;
            previous?.Dispose();
            PreviewPlayer.Opacity = 0;
            UpdatePreviewEmptyState();
            return false;
        }

        if (!_previewReady)
        {
            if (!preserveAudioOnSourceChange)
            {
                PauseVideoAudioPlayers();
            }
            return false;
        }

        TimelineClipProject clip = active.Clip;
        MediaPlaybackSession session = _previewMediaPlayer.PlaybackSession;
        double sourceSeconds = VideoTimelineEditing.GetSourceSeconds(clip, seconds, _frameRate);
        bool videoAudioRouted = synchronizeAudio
            ? SyncVideoAudioPreview(_videoAudioPreview, active, sourceSeconds, forceSeek, null)
            : false;
        _previewMediaPlayer.Volume = IsOriginalPreview ? 1 : clip.Volume;
        _previewMediaPlayer.IsMuted = clip.IsReversed || videoAudioRouted ||
            (!IsOriginalPreview && (active.Track.IsMuted || HasSoloAudio));
        _previewMediaPlayer.PlaybackSession.PlaybackRate = clip.IsReversed ? 1 : clip.PlaybackSpeed;
        PreviewPlayer.Opacity = IsOriginalPreview ? 1 : 0;
        double tolerance = forceSeek || !_isPlaying
            ? 0.001
            : clip.IsReversed ? Math.Max(0.001, clip.PlaybackSpeed / _frameRate / 2) : PreviewDriftToleranceSeconds;
        if (Math.Abs(session.Position.TotalSeconds - sourceSeconds) > tolerance)
        {
            session.Position = TimeSpan.FromSeconds(sourceSeconds);
        }

        if (_isPlaying && !clip.IsReversed)
        {
            if (session.PlaybackState is MediaPlaybackState.Paused or MediaPlaybackState.None)
            {
                _previewMediaPlayer.Play();
            }
        }
        else if (session.PlaybackState == MediaPlaybackState.Playing)
        {
            _previewMediaPlayer.Pause();
        }

        return true;
    }

    private bool SyncVideoAudioPreview(
        VideoAudioPreviewState state,
        TimelineClipLocation active,
        double sourceSeconds,
        bool forceSeek,
        VideoAudioTransition? transition)
    {
        TimelineClipProject clip = active.Clip;
        string? path = clip.SourcePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            state.Clear();
            return false;
        }

        AudioTrackProject project = VideoTimelineEditing.CreatePreviewAudioProject(active, IsOriginalPreview, HasSoloAudio);
        Func<double, double>? gainEnvelope = null;
        VideoAudioEnvelopeKey? envelopeKey = null;
        if (!IsOriginalPreview && transition is { } audioTransition &&
            (audioTransition.InSeconds > 0 || audioTransition.OutSeconds > 0))
        {
            VideoAudioSourceRange sourceRange = VideoTransitions.GetAudioSourceRange(clip, audioTransition);
            project.SourceInSeconds = sourceRange.StartSeconds;
            project.SourceOutSeconds = sourceRange.EndSeconds;
            project.FadeInSeconds = 0;
            project.FadeOutSeconds = 0;
            double sourceIn = clip.SourceInSeconds;
            double sourceOut = clip.SourceOutSeconds ?? clip.SourceDurationSeconds;
            double playbackSpeed = clip.PlaybackSpeed;
            bool reversed = clip.IsReversed;
            var envelopeClip = new TimelineClipProject
            {
                SourceInSeconds = sourceIn,
                SourceOutSeconds = sourceOut,
                SourceDurationSeconds = clip.SourceDurationSeconds,
                PlaybackSpeed = playbackSpeed,
                Volume = clip.Volume,
                FadeInSeconds = clip.FadeInSeconds,
                FadeOutSeconds = clip.FadeOutSeconds,
            };
            envelopeKey = new VideoAudioEnvelopeKey(
                clip.Volume,
                clip.FadeInSeconds,
                clip.FadeOutSeconds,
                playbackSpeed,
                reversed,
                sourceIn,
                sourceOut,
                audioTransition);
            gainEnvelope = sourcePosition =>
            {
                double clipElapsed = reversed
                    ? (sourceOut - sourcePosition) / playbackSpeed
                    : (sourcePosition - sourceIn) / playbackSpeed;
                return ExportPlanning.GetVideoClipAudioEnvelope(envelopeClip, clipElapsed, audioTransition);
            };
        }

        bool shouldHear = !project.Muted;
        if (string.Equals(state.FailedClipId, clip.Id, StringComparison.Ordinal) &&
            string.Equals(state.FailedPath, path, StringComparison.OrdinalIgnoreCase))
        {
            state.FallbackActive = shouldHear;
            return false;
        }

        if (!string.Equals(state.ClipId, clip.Id, StringComparison.Ordinal) ||
            !string.Equals(state.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            state.Clear();
            state.ClipId = clip.Id;
            state.Path = path;
            try
            {
                state.Player = new AudioClipPlayer(path, project, _audioMixLayout, _audioPreviewBus, gainEnvelope);
                state.EnvelopeKey = envelopeKey;
            }
            catch (Exception ex) when (
                IsFileAccessException(ex) ||
                ex is InvalidDataException or NotSupportedException or InvalidOperationException or NAudio.MmException)
            {
                state.FailedClipId = clip.Id;
                state.FailedPath = path;
                state.FallbackActive = shouldHear;
                return false;
            }
        }

        if (state.Player is not AudioClipPlayer player)
        {
            return false;
        }

        bool routeAvailable = _audioPreviewBus.IsPlaying;
        if (state.Settings is null || !VideoAudioSettingsEqual(state.Settings, project))
        {
            player.Update(project, _audioMixLayout);
            state.Settings = project;
        }

        if (state.EnvelopeKey != envelopeKey)
        {
            player.UpdateGainEnvelope(gainEnvelope);
            state.EnvelopeKey = envelopeKey;
        }

        if (shouldHear && _isPlaying && routeAvailable)
        {
            if (forceSeek || !player.IsPlaying || Math.Abs(player.AudiblePositionSeconds - sourceSeconds) > 0.2)
            {
                player.Seek(sourceSeconds);
            }

            player.Play();
        }
        else
        {
            player.Pause();
            if (forceSeek || Math.Abs(player.AudiblePositionSeconds - sourceSeconds) > 0.05)
            {
                player.Seek(sourceSeconds);
            }
        }

        state.FallbackActive = shouldHear && _isPlaying && !routeAvailable;
        return shouldHear && routeAvailable;
    }

    private static bool VideoAudioSettingsEqual(AudioTrackProject left, AudioTrackProject right) =>
        string.Equals(left.AudioPath, right.AudioPath, StringComparison.OrdinalIgnoreCase) &&
        left.SourceInSeconds == right.SourceInSeconds &&
        left.SourceOutSeconds == right.SourceOutSeconds &&
        left.Volume == right.Volume &&
        left.FadeInSeconds == right.FadeInSeconds &&
        left.FadeOutSeconds == right.FadeOutSeconds &&
        left.TempoPercent == right.TempoPercent &&
        left.Muted == right.Muted;

    private void ClearVideoAudioPlayer()
    {
        _videoAudioPreview.Clear();
        _transitionVideoAudioPreview.Clear();
    }

    private void PauseVideoAudioPlayers()
    {
        _videoAudioPreview.Player?.Pause();
        _transitionVideoAudioPreview.Player?.Pause();
    }

    private void ApplyVideoPreviewTransform(
        CompositeTransform transform,
        MediaPlayerElement element,
        TimelineClipProject? clip,
        double seconds)
    {
        VideoTransformValue value = clip is null
            ? new VideoTransformValue(0.5, 0.5, 1, 0)
            : VideoTransformKeyframes.GetValueAtTime(clip, seconds, _frameRate);
        VideoCropValue crop = clip is null
            ? default
            : VideoCropKeyframes.GetValueAtTime(clip, seconds, _frameRate);
        MediaPlaybackSession session = _previewMediaPlayer.PlaybackSession;
        VideoPreviewTransform preview = VideoCrop.GetPreviewTransform(
            new Size(element.ActualWidth, element.ActualHeight),
            new Size(session.NaturalVideoWidth, session.NaturalVideoHeight),
            clip ?? new TimelineClipProject(),
            value,
            crop);
        transform.CenterX = preview.PivotX;
        transform.CenterY = preview.PivotY;
        transform.ScaleX = transform.ScaleY = preview.Scale;
        transform.Rotation = preview.RotationDegrees;
        transform.TranslateX = preview.TranslateX;
        transform.TranslateY = preview.TranslateY;
    }

    private void ClearPreviewSource()
    {
        ClearVideoAudioPlayer();
        if (_previewMediaPlayer.Source is not null)
        {
            _previewMediaPlayer.Pause();
            _previewMediaPlayer.Source = null;
        }

        _previewMediaSource?.Dispose();
        _previewMediaSource = null;
        _previewSourcePath = null;
        _previewReady = false;
        PreviewPlayer.Opacity = IsOriginalPreview ? 1 : 0;
        UpdatePreviewEmptyState();
    }

    private void PreviewMediaPlayer_MediaOpened(MediaPlayer sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_previewMediaSource is null || !ReferenceEquals(sender.Source, _previewMediaSource))
            {
                return;
            }

            _previewReady = true;
            SyncVideoPreview(CurrentPlaybackSeconds, forceSeek: true);
        });
    }

    private void PreviewMediaPlayer_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        string message = args.ErrorMessage;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_previewSourcePath is not string path || !ReferenceEquals(sender.Source, _previewMediaSource))
            {
                return;
            }

            _unavailableTimelineSources.Add(path);
            ClearPreviewSource();
            InvalidateVideoTimeline();
            StatusText.Text = $"Could not play {Path.GetFileName(path)}: {message}";
        });
    }

    private void CompositionSurface_CreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        DisposeOverlayBitmaps();
        DisposePreviewVideoFrameSources();
        DisposeVideoScopeSampleTarget();
    }

    private void CompositionSurface_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        double seconds = _playheadFramePosition / _frameRate;
        float width = (float)sender.ActualWidth;
        float height = (float)sender.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        DrawVideoComposition(sender, args.DrawingSession, seconds, width, height);
    }

    private void DrawVideoComposition(CanvasControl canvas, CanvasDrawingSession session, double seconds, float width, float height,
        bool requireComplete = false)
    {
        if (IsOriginalPreview)
        {
            SetVideoScopeUnavailable("Switch to Modified preview to view the active clip's RGB distribution.");
            return;
        }

        IReadOnlyList<TimelineClipLocation> layers =
            VideoTimelineEditing.GetVisibleLayers(_videoTracks, seconds, _frameRate, _videoMulticamGroups);
        IReadOnlyList<TimelineClipLocation> videoLayers =
            VideoTimelineEditing.GetVisibleVideoLayers(_videoTracks, seconds, _frameRate, _videoMulticamGroups);
        if (videoLayers.Count == 0)
        {
            DisposePreviewVideoFrameSources();
            SetVideoScopeUnavailable("No active video clip at the playhead.");
        }

        var activeIds = videoLayers.Select(location => location.Clip.Id).ToHashSet(StringComparer.Ordinal);
        foreach (string clipId in _previewVideoFrameSources.Keys.Where(id => !activeIds.Contains(id)).ToArray())
        {
            _previewVideoFrameSources[clipId].Source.Dispose();
            _previewVideoFrameSources.Remove(clipId);
        }

        string? scopeClipId = videoLayers.LastOrDefault()?.Clip.Id;
        foreach (TimelineClipLocation layer in layers)
        {
            float opacity = (float)VideoTimelineEditing.GetOpacity(layer, seconds, _frameRate);
            if (opacity <= 0)
            {
                continue;
            }

            switch (layer.Track.Kind)
            {
                case TimelineTrackKind.Video:
                    DrawComposedVideoClip(
                        canvas, session, layer.Clip, seconds, width, height, opacity,
                        updateScopes: !requireComplete && layer.Clip.Id == scopeClipId,
                        requireComplete: requireComplete);
                    break;
                case TimelineTrackKind.Image:
                    DrawImageOverlay(canvas, session, layer.Clip, width, height, opacity, requireComplete);
                    break;
                case TimelineTrackKind.Text:
                    TimelineCompositor.DrawTitle(
                        session, layer.Clip, width, height, opacity,
                        VideoTransformKeyframes.GetValueAtTime(layer.Clip, seconds, _frameRate));
                    break;
            }
        }
    }

    private void DrawComposedVideoClip(
        CanvasControl canvas,
        CanvasDrawingSession session,
        TimelineClipProject clip,
        double seconds,
        float width,
        float height,
        float opacity,
        bool updateScopes,
        bool requireComplete = false)
    {
        string path = VideoProxy.GetRenderPath(clip);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) ||
            (_unavailableTimelineSources.Contains(clip.SourcePath ?? path) && !clip.UseProxy))
        {
            if (requireComplete)
            {
                throw new InvalidOperationException($"Video source unavailable: {Path.GetFileName(path)}.");
            }

            if (updateScopes)
            {
                SetVideoScopeUnavailable("The active video clip has no available source.");
            }
            return;
        }
        if (opacity <= 0)
        {
            return;
        }

        if (_unavailablePreviewVideoSources.Contains(path))
        {
            if (requireComplete)
            {
                throw new InvalidOperationException($"The modified video frame is unavailable: {Path.GetFileName(path)}.");
            }

            if (updateScopes)
            {
                PreviewPlayer.Opacity = 1;
                SetVideoScopeUnavailable("The active video frame is unavailable for scope analysis.");
            }
            return;
        }

        TimelineVideoFrameSource? source = null;
        if (_previewVideoFrameSources.TryGetValue(clip.Id, out var cached))
        {
            if (string.Equals(cached.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                source = cached.Source;
            }
            else
            {
                cached.Source.Dispose();
                _previewVideoFrameSources.Remove(clip.Id);
            }
        }

        try
        {
            if (source is null)
            {
                source = new TimelineVideoFrameSource(path);
                _previewVideoFrameSources.Add(clip.Id, (path, source));
            }

            double sourceSeconds = VideoTimelineEditing.GetSourceSeconds(clip, seconds, _frameRate);
            if (source.GetFrame(canvas.Device, sourceSeconds, clip.RollingShutterProfile) is not CanvasBitmap bitmap)
            {
                if (requireComplete)
                {
                    throw new InvalidOperationException($"No video frame is available from {Path.GetFileName(path)}.");
                }

                if (updateScopes)
                {
                    SetVideoScopeUnavailable("Waiting for an active video frame.");
                }
                return;
            }

            (int x, int y, int sourceWidth, int sourceHeight) = source.Aperture;
            VideoTransformValue transform = VideoTransformKeyframes.GetValueAtTime(clip, seconds, _frameRate);
            VideoCropValue crop = VideoCropKeyframes.GetValueAtTime(clip, seconds, _frameRate);
            VideoColourGradingEffect? colourGradingEffect = GetPreviewColourGradingEffect(clip, seconds);
            if (opacity > 0)
            {
                TimelineCompositor.DrawFitted(
                    session,
                    bitmap,
                    VideoCrop.GetSourceRect(new Rect(x, y, sourceWidth, sourceHeight), crop),
                    width,
                    height,
                    transform.Scale,
                    transform.X,
                    transform.Y,
                    opacity,
                    transform.RotationDegrees,
                    clip,
                    colourGradingEffect,
                    GetPreviewChromaKeyEffect(clip, seconds),
                    GetPreviewBlurEffect(clip, seconds),
                    GetPreviewVignetteEffect(clip, seconds),
                    VideoTransformKeyframes.GetFrameOffset(clip, seconds, _frameRate),
                    GetPreviewSharpenEffect(clip, seconds),
                    VideoColourGradingKeyframes.GetValueAtTime(clip, seconds, _frameRate),
                    VideoVignetteKeyframes.GetValueAtTime(clip, seconds, _frameRate),
                    VideoBlurKeyframes.GetValueAtTime(clip, seconds, _frameRate),
                    VideoSharpenKeyframes.GetValueAtTime(clip, seconds, _frameRate),
                    VideoChromaKeyKeyframes.GetValueAtTime(clip, seconds, _frameRate),
                    VideoChromaKeySpillKeyframes.GetValueAtTime(clip, seconds, _frameRate),
                    VideoChromaKeyFeatherKeyframes.GetValueAtTime(clip, seconds, _frameRate),
                    VideoChromaKeyColourKeyframes.GetValueAtTime(clip, seconds, _frameRate),
                    VideoChromaKeyEnabledKeyframes.GetValueAtTime(clip, seconds, _frameRate));
            }
            if (updateScopes)
            {
                UpdateVideoRgbHistogram(canvas, bitmap, clip, colourGradingEffect);
            }
        }
        catch (Exception ex) when (!requireComplete && (ex is System.Runtime.InteropServices.COMException or IOException or
            UnauthorizedAccessException or InvalidOperationException or ArgumentException))
        {
            source?.Dispose();
            _previewVideoFrameSources.Remove(clip.Id);

            _unavailablePreviewVideoSources.Add(path);
            if (updateScopes)
            {
                PreviewPlayer.Opacity = 1;
            }
            StatusText.Text = updateScopes
                ? $"Could not render {Path.GetFileName(path)} in the modified preview; showing the native video preview instead: {ex.Message}"
                : $"Could not render composited video layer {Path.GetFileName(path)}: {ex.Message}";
        }
    }

    private void DrawImageOverlay(
        CanvasControl canvas,
        CanvasDrawingSession session,
        TimelineClipProject clip,
        float width,
        float height,
        float opacity,
        bool requireComplete = false)
    {
        if (clip.SourcePath is not string path)
        {
            if (requireComplete)
            {
                throw new InvalidOperationException("The visible image has no source path.");
            }

            return;
        }

        if (!_overlayBitmaps.TryGetValue(path, out CanvasBitmap? bitmap))
        {
            RequestOverlayBitmap(canvas, path);
            if (requireComplete)
            {
                throw new InvalidOperationException($"Image {Path.GetFileName(path)} is not loaded. Wait for its preview, then sample again.");
            }

            return;
        }

        VideoTransformValue transform = VideoTransformKeyframes.GetValueAtTime(clip, _playheadFramePosition / _frameRate, _frameRate);
        VideoCropValue crop = VideoCropKeyframes.GetValueAtTime(clip, _playheadFramePosition / _frameRate, _frameRate);
        try
        {
            TimelineCompositor.DrawFitted(session, bitmap, VideoCrop.GetSourceRect(bitmap.Bounds, crop), width, height,
                transform.Scale, transform.X, transform.Y, opacity, transform.RotationDegrees, clip,
                GetPreviewColourGradingEffect(clip, CurrentPlaybackSeconds),
                GetPreviewChromaKeyEffect(clip, _playheadFramePosition / _frameRate),
                GetPreviewBlurEffect(clip, CurrentPlaybackSeconds), GetPreviewVignetteEffect(clip, CurrentPlaybackSeconds),
                VideoTransformKeyframes.GetFrameOffset(clip, _playheadFramePosition / _frameRate, _frameRate),
                GetPreviewSharpenEffect(clip, CurrentPlaybackSeconds),
                VideoColourGradingKeyframes.GetValueAtTime(
                    clip, _playheadFramePosition / _frameRate, _frameRate),
                VideoVignetteKeyframes.GetValueAtTime(
                    clip, _playheadFramePosition / _frameRate, _frameRate),
                VideoBlurKeyframes.GetValueAtTime(
                    clip, _playheadFramePosition / _frameRate, _frameRate),
                VideoSharpenKeyframes.GetValueAtTime(
                    clip, _playheadFramePosition / _frameRate, _frameRate),
                VideoChromaKeyKeyframes.GetValueAtTime(
                    clip, _playheadFramePosition / _frameRate, _frameRate),
                VideoChromaKeySpillKeyframes.GetValueAtTime(
                    clip, _playheadFramePosition / _frameRate, _frameRate),
                VideoChromaKeyFeatherKeyframes.GetValueAtTime(
                    clip, _playheadFramePosition / _frameRate, _frameRate),
                VideoChromaKeyColourKeyframes.GetValueAtTime(
                    clip, _playheadFramePosition / _frameRate, _frameRate),
                VideoChromaKeyEnabledKeyframes.GetValueAtTime(
                    clip, _playheadFramePosition / _frameRate, _frameRate));
        }
        catch (Exception ex) when (!requireComplete && (ex is System.Runtime.InteropServices.COMException or IOException or
            UnauthorizedAccessException or InvalidOperationException or ArgumentException))
        {
            StatusText.Text = $"Could not render image {Path.GetFileName(path)} with its colour settings: {ex.Message}";
        }
    }

    private VideoColourGradingEffect? GetPreviewColourGradingEffect(TimelineClipProject clip, double? seconds = null)
    {
        VideoColourGradingValue value = VideoColourGradingKeyframes.GetValueAtTime(
            clip, seconds ?? _playheadFramePosition / _frameRate, _frameRate);
        if (VideoColourGrading.IsNeutral(clip, value))
        {
            return null;
        }

        if (!_previewColourGradingEffects.TryGetValue(clip.Id, out VideoColourGradingEffect? effect))
        {
            effect = new VideoColourGradingEffect();
            _previewColourGradingEffects.Add(clip.Id, effect);
        }

        return effect;
    }

    private VideoChromaKeyEffect? GetPreviewChromaKeyEffect(TimelineClipProject clip, double? seconds = null)
    {
        if (!VideoChromaKeyEnabledKeyframes.GetValueAtTime(
            clip, seconds ?? _playheadFramePosition / _frameRate, _frameRate))
        {
            return null;
        }

        if (!_previewChromaKeyEffects.TryGetValue(clip.Id, out VideoChromaKeyEffect? effect))
        {
            effect = new VideoChromaKeyEffect();
            _previewChromaKeyEffects.Add(clip.Id, effect);
        }

        return effect;
    }

    private VideoBlurCanvasEffect? GetPreviewBlurEffect(TimelineClipProject clip, double? seconds = null)
    {
        if (VideoBlurKeyframes.GetValueAtTime(
            clip, seconds ?? _playheadFramePosition / _frameRate, _frameRate) <= 0)
        {
            return null;
        }

        if (!_previewBlurEffects.TryGetValue(clip.Id, out VideoBlurCanvasEffect? effect))
        {
            effect = new VideoBlurCanvasEffect();
            _previewBlurEffects.Add(clip.Id, effect);
        }

        return effect;
    }

    private VideoVignetteCanvasEffect? GetPreviewVignetteEffect(TimelineClipProject clip, double? seconds = null)
    {
        double amount = VideoVignetteKeyframes.GetValueAtTime(
            clip, seconds ?? _playheadFramePosition / _frameRate, _frameRate);
        if (amount <= 0)
        {
            return null;
        }

        if (!_previewVignetteEffects.TryGetValue(clip.Id, out VideoVignetteCanvasEffect? effect))
        {
            effect = new VideoVignetteCanvasEffect();
            _previewVignetteEffects.Add(clip.Id, effect);
        }

        return effect;
    }

    private VideoSharpenCanvasEffect? GetPreviewSharpenEffect(TimelineClipProject clip, double? seconds = null)
    {
        if (VideoSharpenKeyframes.GetValueAtTime(
            clip, seconds ?? _playheadFramePosition / _frameRate, _frameRate) <= 0)
        {
            return null;
        }

        if (!_previewSharpenEffects.TryGetValue(clip.Id, out VideoSharpenCanvasEffect? effect))
        {
            effect = new VideoSharpenCanvasEffect();
            _previewSharpenEffects.Add(clip.Id, effect);
        }

        return effect;
    }

    private void VideoRgbHistogram_Draw(CanvasControl sender, CanvasDrawEventArgs args) =>
        VideoRgbHistogramData.Draw(
            args.DrawingSession,
            new Rect(0, 0, sender.ActualWidth, sender.ActualHeight),
            _videoRgbHistogram);

    private void UpdateVideoRgbHistogram(
        CanvasControl canvas,
        CanvasBitmap source,
        TimelineClipProject clip,
        VideoColourGradingEffect? colourGradingEffect)
    {
        if (VideoInspectorPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        long now = Environment.TickCount64;
        if (now - _lastVideoScopeSampleMilliseconds < 100)
        {
            return;
        }

        try
        {
            _videoScopeSampleTarget ??= new CanvasRenderTarget(canvas.Device, 128, 72, 96);
            using (CanvasDrawingSession sample = _videoScopeSampleTarget.CreateDrawingSession())
            {
                sample.Clear(Color.FromArgb(0, 0, 0, 0));
                VideoColourGradingValue value = VideoColourGradingKeyframes.GetValueAtTime(
                    clip, CurrentPlaybackSeconds, _frameRate);
                ICanvasImage image = colourGradingEffect?.GetOutput(source, clip, value) ?? source;
                sample.DrawImage(image, new Rect(0, 0, 128, 72), source.Bounds, 1);
            }

            byte[] pixels = _videoScopeSampleTarget.GetPixelBytes();
            _videoRgbHistogram = VideoRgbHistogramData.Analyse(pixels);
            VideoLumaWaveformData waveform = VideoLumaWaveformData.Analyse(
                pixels,
                (int)_videoScopeSampleTarget.SizeInPixels.Width,
                (int)_videoScopeSampleTarget.SizeInPixels.Height);
            if (_videoLumaWaveformBitmap is null)
            {
                _videoLumaWaveformBitmap = CanvasBitmap.CreateFromBytes(
                    canvas.Device,
                    waveform.BgraPixels,
                    VideoLumaWaveformData.Width,
                    VideoLumaWaveformData.Height,
                    Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    96,
                    CanvasAlphaMode.Premultiplied);
            }
            else
            {
                _videoLumaWaveformBitmap.SetPixelBytes(waveform.BgraPixels);
            }

            VideoVectorscopeData vectorscope = VideoVectorscopeData.Analyse(pixels);
            if (_videoVectorscopeBitmap is null)
            {
                _videoVectorscopeBitmap = CanvasBitmap.CreateFromBytes(
                    canvas.Device,
                    vectorscope.BgraPixels,
                    VideoVectorscopeData.Width,
                    VideoVectorscopeData.Height,
                    Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    96,
                    CanvasAlphaMode.Premultiplied);
            }
            else
            {
                _videoVectorscopeBitmap.SetPixelBytes(vectorscope.BgraPixels);
            }

            _lastVideoScopeSampleMilliseconds = now;
            VideoRgbHistogramSurface.Invalidate();
            VideoLumaWaveformSurface.Invalidate();
            VideoVectorscopeSurface.Invalidate();
            VideoScopeStatus.Text = $"{_videoRgbHistogram.SampleCount:N0} sampled pixels · red, green and blue channels";
            VideoLumaWaveformStatus.Text = $"{waveform.SampleCount:N0} sampled pixels · Rec. 709 luma";
            VideoVectorscopeStatus.Text = $"{vectorscope.SampleCount:N0} sampled pixels · Rec. 709 Cb/Cr";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or
            UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            SetVideoScopeUnavailable($"Could not update the video scope: {ex.Message}");
            StatusText.Text = $"Could not update the video scope for {Path.GetFileName(clip.SourcePath)}: {ex.Message}";
        }
    }

    private void SetVideoScopeUnavailable(string message)
    {
        if (_videoRgbHistogram is null && VideoScopeStatus.Text == message)
        {
            return;
        }

        _videoRgbHistogram = null;
        _videoLumaWaveformBitmap?.Dispose();
        _videoLumaWaveformBitmap = null;
        _videoVectorscopeBitmap?.Dispose();
        _videoVectorscopeBitmap = null;
        VideoScopeStatus.Text = message;
        VideoLumaWaveformStatus.Text = message.Replace("RGB distribution", "luma waveform", StringComparison.Ordinal);
        VideoVectorscopeStatus.Text = message.Replace("RGB distribution", "chroma distribution", StringComparison.Ordinal);
        VideoRgbHistogramSurface.Invalidate();
        VideoLumaWaveformSurface.Invalidate();
        VideoVectorscopeSurface.Invalidate();
    }

    private void DisposeVideoScopeSampleTarget()
    {
        _videoScopeSampleTarget?.Dispose();
        _videoScopeSampleTarget = null;
        _videoLumaWaveformBitmap?.Dispose();
        _videoLumaWaveformBitmap = null;
        _videoVectorscopeBitmap?.Dispose();
        _videoVectorscopeBitmap = null;
        _lastVideoScopeSampleMilliseconds = 0;
    }

    private void VideoLumaWaveform_Draw(CanvasControl sender, CanvasDrawEventArgs args) =>
        VideoLumaWaveformData.Draw(
            args.DrawingSession,
            new Rect(0, 0, sender.ActualWidth, sender.ActualHeight),
            _videoLumaWaveformBitmap);

    private void VideoVectorscope_Draw(CanvasControl sender, CanvasDrawEventArgs args) =>
        VideoVectorscopeData.Draw(
            args.DrawingSession,
            new Rect(0, 0, sender.ActualWidth, sender.ActualHeight),
            _videoVectorscopeBitmap);

    private void RequestOverlayBitmap(CanvasControl canvas, string path)
    {
        if (_unavailableTimelineSources.Contains(path) || !_overlayBitmapRequests.Add(path))
        {
            return;
        }

        _ = LoadOverlayBitmapAsync(canvas, path);
    }

    private async Task LoadOverlayBitmapAsync(CanvasControl canvas, string path)
    {
        try
        {
            if (!_timelineSourceFiles.TryGetValue(path, out StorageFile? file))
            {
                file = await StorageFile.GetFileFromPathAsync(path);
                _timelineSourceFiles[path] = file;
            }

            using Windows.Storage.Streams.IRandomAccessStreamWithContentType stream = await file.OpenReadAsync();
            CanvasBitmap bitmap = await CanvasBitmap.LoadAsync(canvas, stream);
            if (_overlayBitmaps.Remove(path, out CanvasBitmap? previous))
            {
                previous.Dispose();
            }

            _overlayBitmaps[path] = bitmap;
        }
        catch (Exception ex) when (IsFileAccessException(ex) || ex is InvalidOperationException or NotSupportedException)
        {
            _unavailableTimelineSources.Add(path);
            StatusText.Text = $"Could not load image {Path.GetFileName(path)}: {ex.Message}";
            InvalidateVideoTimeline();
        }
        finally
        {
            _overlayBitmapRequests.Remove(path);
        }

        canvas.Invalidate();
    }

    private void DisposeOverlayBitmaps()
    {
        foreach (CanvasBitmap bitmap in _overlayBitmaps.Values)
        {
            bitmap.Dispose();
        }

        _overlayBitmaps.Clear();
    }

    private void DisposePreviewVideoFrameSources()
    {
        foreach ((string _, TimelineVideoFrameSource source) in _previewVideoFrameSources.Values)
        {
            source.Dispose();
        }

        _previewVideoFrameSources.Clear();
        foreach (VideoColourGradingEffect effect in _previewColourGradingEffects.Values)
        {
            effect.Dispose();
        }
        _previewColourGradingEffects.Clear();
        foreach (VideoChromaKeyEffect effect in _previewChromaKeyEffects.Values)
        {
            effect.Dispose();
        }
        _previewChromaKeyEffects.Clear();
        foreach (VideoBlurCanvasEffect effect in _previewBlurEffects.Values)
        {
            effect.Dispose();
        }
        _previewBlurEffects.Clear();
        foreach (VideoVignetteCanvasEffect effect in _previewVignetteEffects.Values)
        {
            effect.Dispose();
        }
        _previewVignetteEffects.Clear();
        foreach (VideoSharpenCanvasEffect effect in _previewSharpenEffects.Values)
        {
            effect.Dispose();
        }
        _previewSharpenEffects.Clear();
    }

    private void DisposeVideoTimeline()
    {
        DisposeSourceMonitor();
        ClearPreviewSource();
        DisposePreviewVideoFrameSources();
        DisposeVideoScopeSampleTarget();
        _previewMediaPlayer.MediaOpened -= PreviewMediaPlayer_MediaOpened;
        _previewMediaPlayer.MediaFailed -= PreviewMediaPlayer_MediaFailed;
        _previewMediaPlayer.Dispose();
        DisposeOverlayBitmaps();
        _timelineLabelFormat?.Dispose();
        _timelineRulerFormat?.Dispose();
    }

    private void RefreshVideoTimeline()
    {
        if (VideoTrackHeadersPanel is null)
        {
            return;
        }

        RefreshVideoTrackHeaders();
        RefreshMarkers();
        UpdateVideoInspector();
        UpdatePreviewEmptyState();
        UpdateProjectMediaBin();
        CompositionSurface.Invalidate();
        InvalidateVideoTimeline();
    }

    private void InvalidateVideoTimeline()
    {
        if (VideoTimelineCanvas is null)
        {
            return;
        }

        double viewport = Math.Max(0, VideoTimelineScroller.ActualWidth);
        double markerEnd = _timelineMarkers.Count == 0 ? 0 : (_timelineMarkers.Max(marker => marker.Frame) - 1d) / _frameRate;
        double contentSeconds = Math.Max(markerEnd, Math.Max(GetPlaybackEndSeconds(), _playheadFramePosition / _frameRate)) + 10;
        double width = Math.Max(viewport, contentSeconds * _timelinePixelsPerSecond);
        double height = TimelineRulerHeight + (_videoTracks.Count * (TimelineLaneHeight + TimelineLaneSpacing));
        if (!(Math.Abs(VideoTimelineCanvas.Width - width) <= 0.5))
        {
            VideoTimelineCanvas.Width = width;
        }

        if (!(Math.Abs(VideoTimelineCanvas.Height - height) <= 0.5))
        {
            VideoTimelineCanvas.Height = height;
        }

        if (_isPlaying && viewport > 0)
        {
            double playheadX = _playheadFramePosition / _frameRate * _timelinePixelsPerSecond;
            double offset = VideoTimelineScroller.HorizontalOffset;
            if (playheadX < offset || playheadX > offset + viewport - 24)
            {
                VideoTimelineScroller.ChangeView(Math.Max(0, playheadX - (viewport * 0.1)), null, null, true);
            }
        }

        VideoTimelineCanvas.Invalidate();
    }

    private void VideoTimelineScroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        InvalidateVideoTimeline();
    }

    private void TimelineZoom_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        _timelinePixelsPerSecond = e.NewValue;
        InvalidateVideoTimeline();
    }

    private void VideoTimelineCanvas_RegionsInvalidated(CanvasVirtualControl sender, CanvasRegionsInvalidatedEventArgs args)
    {
        foreach (Rect region in args.InvalidatedRegions)
        {
            using CanvasDrawingSession session = sender.CreateDrawingSession(region);
            DrawVideoTimeline(session, region);
        }
    }

    private void DrawVideoTimeline(CanvasDrawingSession session, Rect region)
    {
        _timelineLabelFormat ??= new CanvasTextFormat
        {
            FontSize = 11,
            WordWrapping = CanvasWordWrapping.NoWrap,
            VerticalAlignment = CanvasVerticalAlignment.Center,
            TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
            Options = CanvasDrawTextOptions.Clip,
        };
        _timelineRulerFormat ??= new CanvasTextFormat { FontSize = 10, WordWrapping = CanvasWordWrapping.NoWrap };

        float left = (float)region.Left;
        float right = (float)region.Right;
        session.Clear(Color.FromArgb(255, 0x15, 0x18, 0x1D));
        TimelineTrackProject? selectedTrack = SelectedTimelineTrack;
        for (int index = 0; index < _videoTracks.Count; index++)
        {
            TimelineTrackProject track = _videoTracks[index];
            float top = LaneTop(index);
            session.FillRectangle(
                left,
                top,
                right - left,
                TimelineLaneHeight,
                ReferenceEquals(track, selectedTrack) ? Color.FromArgb(255, 0x23, 0x2A, 0x35) : Color.FromArgb(255, 0x1C, 0x20, 0x28));
            foreach (TimelineClipProject clip in track.Clips)
            {
                DrawTimelineClip(session, track, clip, top, left, right);
            }
        }

        session.FillRectangle(left, 0, right - left, TimelineRulerHeight, Color.FromArgb(255, 0x1F, 0x23, 0x2B));
        double step = GetRulerStep(_timelinePixelsPerSecond);
        double firstTick = Math.Floor(region.Left / _timelinePixelsPerSecond / step) * step;
        for (double tick = firstTick; tick * _timelinePixelsPerSecond <= right + 1; tick += step)
        {
            float x = (float)(tick * _timelinePixelsPerSecond);
            session.DrawLine(x, 13, x, TimelineRulerHeight, Color.FromArgb(255, 0x5F, 0x68, 0x77));
            float half = (float)((tick + (step / 2)) * _timelinePixelsPerSecond);
            session.DrawLine(half, 18, half, TimelineRulerHeight, Color.FromArgb(255, 0x3E, 0x45, 0x51));
            session.DrawText(FormatTimelineTime(tick, step < 1), x + 3, 1, Color.FromArgb(255, 0x8F, 0x99, 0xAA), _timelineRulerFormat);
        }

        foreach (TimelineMarkerProject marker in _timelineMarkers)
        {
            float x = (float)((marker.Frame - 1d) / _frameRate * _timelinePixelsPerSecond);
            if (x >= left - 4 && x <= right + 4)
            {
                var colour = Color.FromArgb(255, 0xFF, 0xCB, 0x66);
                session.DrawLine(x, 8, x, (float)VideoTimelineCanvas.ActualHeight, colour);
                session.FillCircle(x, 5, 3, colour);
            }
        }

        float playheadX = (float)(_playheadFramePosition / _frameRate * _timelinePixelsPerSecond);
        if (playheadX >= left - 6 && playheadX <= right + 6)
        {
            var playheadColour = Color.FromArgb(255, 0xFF, 0x4D, 0x4F);
            session.DrawLine(playheadX, 0, playheadX, (float)VideoTimelineCanvas.ActualHeight, playheadColour, 1.5f);
            session.FillRectangle(playheadX - 4, 0, 8, 7, playheadColour);
        }
    }

    private void DrawTimelineClip(
        CanvasDrawingSession session,
        TimelineTrackProject track,
        TimelineClipProject clip,
        float top,
        float left,
        float right)
    {
        float x0 = (float)(VideoTimelineEditing.GetStartSeconds(clip, _frameRate) * _timelinePixelsPerSecond);
        float x1 = Math.Max(x0 + 4, (float)(VideoTimelineEditing.GetEndSeconds(clip, track.Kind, _frameRate) * _timelinePixelsPerSecond));
        if (x1 < left || x0 > right)
        {
            return;
        }

        bool unavailable = clip.SourcePath is string path && _unavailableTimelineSources.Contains(path);
        Color colour = unavailable
            ? Color.FromArgb(255, 0x8A, 0x34, 0x34)
            : track.Kind switch
            {
                TimelineTrackKind.Video => Color.FromArgb(255, 0x2F, 0x6F, 0xB5),
                TimelineTrackKind.Image => Color.FromArgb(255, 0x6A, 0x4F, 0xB0),
                _ => Color.FromArgb(255, 0xA8, 0x74, 0x2C),
            };
        if (!track.IsVisible)
        {
            colour.A = 0x70;
        }

        var bounds = new Rect(x0, top + 2, x1 - x0, TimelineLaneHeight - 4);
        session.FillRoundedRectangle(bounds, 3, 3, colour);
        float bottom = top + TimelineLaneHeight - 2;
        var fadeColour = Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF);
        if (clip.FadeInSeconds > 0)
        {
            session.DrawLine(x0, bottom, x0 + (float)(clip.FadeInSeconds * _timelinePixelsPerSecond), top + 2, fadeColour);
        }

        if (clip.FadeOutSeconds > 0)
        {
            session.DrawLine(x1 - (float)(clip.FadeOutSeconds * _timelinePixelsPerSecond), top + 2, x1, bottom, fadeColour);
        }

        if (track.Kind == TimelineTrackKind.Video && clip.TransitionInSeconds > 0)
        {
            float transitionWidth = (float)(clip.TransitionInSeconds * _timelinePixelsPerSecond);
            float transitionStart = x0 - transitionWidth / 2;
            session.FillRectangle(transitionStart, top + 2, transitionWidth, TimelineLaneHeight - 4,
                Color.FromArgb(100, 0xFF, 0xCB, 0x66));
            session.DrawLine(transitionStart, bottom, x0 + transitionWidth / 2, top + 2,
                Color.FromArgb(220, 0xFF, 0xE0, 0xA3), 2);
        }

        if (clip.Id == _selectedTimelineClipId)
        {
            session.DrawRoundedRectangle(bounds, 3, 3, Color.FromArgb(255, 0xF2, 0xF4, 0xF8), 2);
            if (_timelineToolMode is TimelineToolMode.Trim or TimelineToolMode.Ripple or TimelineToolMode.Roll)
            {
                if (_timelineToolMode != TimelineToolMode.Roll)
                {
                    session.FillRectangle(x0, top + 5, 4, TimelineLaneHeight - 10, Color.FromArgb(255, 0xFF, 0xCB, 0x66));
                }
                session.FillRectangle(x1 - 4, top + 5, 4, TimelineLaneHeight - 10, Color.FromArgb(255, 0xFF, 0xCB, 0x66));
            }
        }

        float labelWidth = x1 - x0 - 12;
        if (labelWidth > 8)
        {
            string label = GetClipLabel(track, clip) + (unavailable ? " (unavailable)" : string.Empty);
            session.DrawText(
                label,
                new Rect(x0 + 6, top + 2, labelWidth, TimelineLaneHeight - 4),
                Color.FromArgb(255, 0xF2, 0xF4, 0xF8),
                _timelineLabelFormat);
        }
    }

    private static string GetClipLabel(TimelineTrackProject track, TimelineClipProject clip) => track.Kind == TimelineTrackKind.Text
        ? clip.Text.Split('\n', 2)[0].Trim()
        : Path.GetFileName(clip.SourcePath) ?? "Clip";

    private static void CopyInteractiveEdit(TimelineClipProject source, TimelineClipProject destination)
    {
        destination.StartFrame = source.StartFrame;
        destination.SourceInSeconds = source.SourceInSeconds;
        destination.SourceOutSeconds = source.SourceOutSeconds;
        destination.PlaybackSpeed = source.PlaybackSpeed;
        destination.IsReversed = source.IsReversed;
        destination.ReverseEmbeddedAudio = source.ReverseEmbeddedAudio;
        destination.DurationSeconds = source.DurationSeconds;
        destination.Opacity = source.Opacity;
        destination.OpacityKeyframes = VideoOpacityKeyframes.CloneKeys(source.OpacityKeyframes);
        destination.ColourGradingKeyframes =
            VideoColourGradingKeyframes.CloneKeys(source.ColourGradingKeyframes);
        destination.VignetteKeyframes = VideoVignetteKeyframes.CloneKeys(source.VignetteKeyframes);
        destination.BlurKeyframes = VideoBlurKeyframes.CloneKeys(source.BlurKeyframes);
        destination.SharpenKeyframes = VideoSharpenKeyframes.CloneKeys(source.SharpenKeyframes);
        destination.ChromaKeyKeyframes = VideoChromaKeyKeyframes.CloneKeys(source.ChromaKeyKeyframes);
        destination.ChromaKeyColourKeyframes = VideoChromaKeyColourKeyframes.CloneKeys(source.ChromaKeyColourKeyframes);
        destination.ChromaKeyFeatherKeyframes = VideoChromaKeyFeatherKeyframes.CloneKeys(source.ChromaKeyFeatherKeyframes);
        destination.ChromaKeySpillKeyframes = VideoChromaKeySpillKeyframes.CloneKeys(source.ChromaKeySpillKeyframes);
        destination.MaskShapeKeyframes = VideoMaskShapeKeyframes.CloneKeys(source.MaskShapeKeyframes);
        destination.MaskFeatherKeyframes = VideoMaskFeatherKeyframes.CloneKeys(source.MaskFeatherKeyframes);
        destination.FadeInSeconds = source.FadeInSeconds;
        destination.FadeOutSeconds = source.FadeOutSeconds;
        destination.TransitionInSeconds = source.TransitionInSeconds;
        destination.CropLeftPercent = source.CropLeftPercent;
        destination.CropTopPercent = source.CropTopPercent;
        destination.CropRightPercent = source.CropRightPercent;
        destination.CropBottomPercent = source.CropBottomPercent;
        destination.CropKeyframes = VideoCropKeyframes.CloneKeys(source.CropKeyframes);
        destination.TransformKeyframes = VideoTransformKeyframes.CloneKeys(source.TransformKeyframes);
    }

    private static float LaneTop(int index) => TimelineRulerHeight + (index * (TimelineLaneHeight + TimelineLaneSpacing));

    private static double GetRulerStep(double pixelsPerSecond)
    {
        foreach (double step in new[] { 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 })
        {
            if (step * pixelsPerSecond >= 70)
            {
                return step;
            }
        }

        return 1200;
    }

    private static string FormatTimelineTime(double seconds, bool showTenths = false)
    {
        TimeSpan time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        string whole = time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
        return showTenths ? $"{whole}.{time.Milliseconds / 100}" : whole;
    }

    private int GetTrackIndexAt(double y)
    {
        if (y < TimelineRulerHeight)
        {
            return -1;
        }

        int index = (int)((y - TimelineRulerHeight) / (TimelineLaneHeight + TimelineLaneSpacing));
        return index < _videoTracks.Count ? index : -1;
    }

    private void MoveTimelinePlayhead(double seconds)
    {
        int framePosition = Math.Max(0, (int)Math.Floor((seconds * _frameRate) + 1e-6));
        _playheadFramePosition = framePosition;
        SelectFrame(InkTimeline.GetFrameIndexAtPosition(_frames, framePosition), seekVideo: false, preservePlayhead: true);
        SeekToCurrentFrame();
        UpdateFrameReadout();
        CompositionSurface.Invalidate();
        UpdateVideoInspector();
    }

    private void VideoTimelineCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Microsoft.UI.Input.PointerPoint point = e.GetCurrentPoint(VideoTimelineCanvas);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        e.Handled = true;
        VideoTimelineCanvas.CapturePointer(e.Pointer);
        double seconds = Math.Max(0, point.Position.X / _timelinePixelsPerSecond);
        int index = GetTrackIndexAt(point.Position.Y);
        if (index < 0)
        {
            _isScrubbingTimeline = true;
            MoveTimelinePlayhead(seconds);
            return;
        }

        TimelineTrackProject track = _videoTracks[index];
        _selectedTimelineTrackId = track.Id;
        TimelineClipLocation? location = VideoTimelineEditing.FindClipAt(track, seconds, _frameRate);
        if (location is null)
        {
            _selectedTimelineClipId = null;
            _isScrubbingTimeline = true;
            RefreshVideoTrackHeaders();
            MoveTimelinePlayhead(seconds);
            return;
        }

        _selectedTimelineClipId = location.Clip.Id;
        if (_timelineToolMode == TimelineToolMode.Blade)
        {
            if (!track.IsLocked)
            {
                int bladeFrame = Math.Max(1, (int)Math.Round(seconds * _frameRate) + 1);
                BeginEdit();
                TimelineClipProject? right = VideoTimelineEditing.Split(track, location.Clip, bladeFrame, _frameRate);
                if (right is not null)
                {
                    MarkEditChanged();
                    CommitEdit();
                    _selectedTimelineClipId = right.Id;
                    RefreshVideoTimeline();
                    SeekToCurrentFrame();
                    StatusText.Text = $"Split {VideoTimelineEditing.KindLabel(track.Kind)} at frame {bladeFrame}.";
                }
                else
                {
                    CommitEdit();
                    StatusText.Text = "Blade cuts must be inside a clip, away from its edges.";
                }
            }
            return;
        }

        if (_timelineToolMode == TimelineToolMode.Slide)
        {
            if (!track.IsLocked)
            {
                _timelineDrag = TimelineDrag.CreateOperation(location.Clip, track, TimelineDragOperation.Slide,
                    grabOffsetSeconds: seconds - VideoTimelineEditing.GetStartSeconds(location.Clip, _frameRate));
            }
        }
        else if (_timelineToolMode is TimelineToolMode.Trim or TimelineToolMode.Ripple or TimelineToolMode.Roll)
        {
            double start = VideoTimelineEditing.GetStartSeconds(location.Clip, _frameRate);
            double end = VideoTimelineEditing.GetEndSeconds(location.Clip, track.Kind, _frameRate);
            double startDistance = Math.Abs(seconds - start);
            double endDistance = Math.Abs(seconds - end);
            double handleTolerance = 8 / _timelinePixelsPerSecond;
            bool allowStart = _timelineToolMode != TimelineToolMode.Roll;
            bool nearStart = allowStart && startDistance <= handleTolerance;
            bool nearEnd = endDistance <= handleTolerance;
            if (!track.IsLocked && (nearStart || nearEnd))
            {
                TimelineTrimEdge edge = nearStart && (!nearEnd || startDistance <= endDistance)
                    ? TimelineTrimEdge.Start
                    : TimelineTrimEdge.End;
                TimelineDragOperation operation = _timelineToolMode switch
                {
                    TimelineToolMode.Trim => TimelineDragOperation.Trim,
                    TimelineToolMode.Ripple => TimelineDragOperation.RippleTrim,
                    _ => TimelineDragOperation.Roll,
                };
                _timelineDrag = TimelineDrag.CreateOperation(location.Clip, track, operation, edge);
            }
            else
            {
                StatusText.Text = _timelineToolMode == TimelineToolMode.Roll
                    ? "Roll mode: drag the end handle between two adjacent clips."
                    : $"{_timelineToolMode} mode: drag a highlighted clip edge.";
            }
        }
        else if (!track.IsLocked)
        {
            _timelineDrag = new TimelineDrag(
                location.Clip,
                track,
                seconds - VideoTimelineEditing.GetStartSeconds(location.Clip, _frameRate));
        }

        RefreshVideoTrackHeaders();
        UpdateVideoInspector();
        InvalidateVideoTimeline();
    }

    private void VideoTimelineCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        Point position = e.GetCurrentPoint(VideoTimelineCanvas).Position;
        double seconds = Math.Max(0, position.X / _timelinePixelsPerSecond);
        if (_isScrubbingTimeline)
        {
            MoveTimelinePlayhead(seconds);
            return;
        }

        if (_timelineDrag is not TimelineDrag drag)
        {
            return;
        }

        if (drag.Operation != TimelineDragOperation.Move)
        {
            TimelineTrackProject workingTrack = VideoTimelineEditing.Clone(drag.OriginalTrack);
            TimelineClipProject? workingClip = workingTrack.Clips.FirstOrDefault(clip => clip.Id == drag.Clip.Id);
            if (workingClip is null)
            {
                drag.Blocked = true;
                return;
            }

            int change = 0;
            bool changed = false;
            TimelineTrimEdge? edge = drag.TrimEdge;
            if (drag.Operation == TimelineDragOperation.Slide)
            {
                int requestedStart = Math.Max(1, (int)Math.Round((seconds - drag.GrabOffsetSeconds) * _frameRate) + 1);
                change = requestedStart - workingClip.StartFrame;
                changed = change != 0 && VideoEditTools.Slide(workingTrack, workingClip, change, _frameRate);
            }
            else
            {
                int edgeFrame = Math.Max(1, (int)Math.Round(seconds * _frameRate) + 1);
                int originalEdgeFrame = edge == TimelineTrimEdge.Start
                    ? workingClip.StartFrame
                    : (int)Math.Round(VideoTimelineEditing.GetEndSeconds(workingClip, workingTrack.Kind, _frameRate) * _frameRate) + 1;
                change = edgeFrame - originalEdgeFrame;
                changed = change != 0 && (drag.Operation switch
                {
                    TimelineDragOperation.Trim =>
                        VideoEditTools.TryTrimEdge(workingTrack, workingClip, edge!.Value, edgeFrame, _frameRate),
                    TimelineDragOperation.RippleTrim when edge == TimelineTrimEdge.Start =>
                        VideoEditTools.RippleTrimStart(workingTrack, workingClip, change, _frameRate),
                    TimelineDragOperation.RippleTrim =>
                        VideoEditTools.RippleTrimEnd(workingTrack, workingClip, change, _frameRate),
                    TimelineDragOperation.Roll when edge == TimelineTrimEdge.End =>
                        VideoEditTools.RollEnd(workingTrack, workingClip, change, _frameRate),
                    _ => false,
                });
            }

            changed &= VideoTimelineEditing.Validate([workingTrack], _frameRate) is null;
            if (!changed || change == drag.LastChange)
            {
                drag.Blocked = !changed;
                return;
            }

            if (!drag.Started)
            {
                BeginEdit();
                drag.Started = true;
            }

            foreach (TimelineClipProject candidate in workingTrack.Clips)
            {
                TimelineClipProject? live = drag.Track.Clips.FirstOrDefault(clip => clip.Id == candidate.Id);
                if (live is null)
                {
                    drag.Blocked = true;
                    return;
                }

                CopyInteractiveEdit(candidate, live);
            }
            VideoTimelineEditing.SortClips(drag.Track);
            drag.LastChange = change;
            drag.Moved = true;
            drag.Blocked = false;
            MarkEditChanged();
            InvalidateVideoTimeline();
            CompositionSurface.Invalidate();
            UpdateVideoInspector();
            return;
        }

        int startFrame = Math.Max(1, (int)Math.Round((seconds - drag.GrabOffsetSeconds) * _frameRate) + 1);
        int index = GetTrackIndexAt(position.Y);
        TimelineTrackProject target = index >= 0 &&
            _videoTracks[index].Kind == drag.Track.Kind &&
            !_videoTracks[index].IsLocked
                ? _videoTracks[index]
                : drag.Track;
        if (VideoSnappingButton.IsChecked == true)
        {
            IEnumerable<double> anchors = _videoTracks.SelectMany(track => track.Clips
                .Where(clip => !ReferenceEquals(clip, drag.Clip))
                .SelectMany(clip => new[]
                {
                    VideoTimelineEditing.GetStartSeconds(clip, _frameRate),
                    VideoTimelineEditing.GetEndSeconds(clip, track.Kind, _frameRate),
                })).Concat(_audioTracks.Where(track => track.HasSource).SelectMany(track => new[]
                {
                    AudioClipEditing.GetClipStartSeconds(track.Project, _frameRate),
                    AudioClipEditing.GetClipStartSeconds(track.Project, _frameRate) +
                        AudioClipEditing.GetTimelineDuration(track.Project, track.SourceDurationSeconds),
                })).Concat(_timelineMarkers.Select(marker => (marker.Frame - 1d) / _frameRate))
                .Append(0).Append(CurrentPlaybackSeconds);
            startFrame = VideoEditTools.SnapStart(target, drag.Clip, startFrame, anchors, _frameRate,
                toleranceSeconds: 8 / _timelinePixelsPerSecond);
        }

        if (startFrame == drag.Clip.StartFrame && ReferenceEquals(target, drag.Track))
        {
            return;
        }

        if (!drag.Started)
        {
            BeginEdit();
            drag.Started = true;
        }

        bool moved = false;
        if (!ReferenceEquals(target, drag.Track))
        {
            moved = LinkedAudioVideoEditing.TryMoveVideoToTrack(
                drag.Track,
                target,
                drag.Clip,
                startFrame,
                _audioTracks.Select(audio => audio.Project).ToArray(),
                _frameRate);
            if (moved)
            {
                drag.Track = target;
                _selectedTimelineTrackId = target.Id;
                RefreshVideoTrackHeaders();
            }
        }

        if (!moved)
        {
            moved = LinkedAudioVideoEditing.TryMoveVideo(
                drag.Track,
                drag.Clip,
                startFrame,
                _audioTracks.Select(audio => audio.Project).ToArray(),
                _frameRate);
        }

        drag.Blocked = !moved;
        if (moved)
        {
            drag.Moved = true;
            MarkEditChanged();
            InvalidateVideoTimeline();
            CompositionSurface.Invalidate();
        }
    }

    private void VideoTimelineCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndTimelinePointer();
        VideoTimelineCanvas.ReleasePointerCapture(e.Pointer);
    }

    private void VideoTimelineCanvas_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        EndTimelinePointer();
    }

    private void EndTimelinePointer()
    {
        _isScrubbingTimeline = false;
        if (_timelineDrag is not TimelineDrag drag)
        {
            return;
        }

        _timelineDrag = null;
        if (!drag.Started)
        {
            return;
        }

        CommitEdit();
        if (drag.Moved)
        {
            SeekToCurrentFrame();
            if (_audioTracks.Any(audio => audio.Project.LinkedVideoClipId == drag.Clip.Id))
            {
                RefreshAudioTracks();
                UpdateSelectedAudioTrack();
            }

            CompositionSurface.Invalidate();
            UpdateVideoInspector();
        }

        StatusText.Text = drag.Blocked
            ? drag.Operation == TimelineDragOperation.Move
                ? "Clips can't overlap on a track; the clip stopped at the last free position."
                : "Edit stopped at the last valid frame; check adjacent clips, source handles, fades and track locks."
            : drag.Operation == TimelineDragOperation.Move
                ? $"Moved clip to {FormatTimelineTime(VideoTimelineEditing.GetStartSeconds(drag.Clip, _frameRate), true)} on {drag.Track.Name}."
                : $"{drag.Operation} applied on {drag.Track.Name}.";
    }

    private void RefreshVideoTrackHeaders()
    {
        VideoTrackHeadersPanel.Children.Clear();
        VideoTrackHeadersPanel.Children.Add(new Border { Height = TimelineRulerHeight });
        TimelineTrackProject? selected = SelectedTimelineTrack;
        foreach (TimelineTrackProject track in _videoTracks)
        {
            string trackId = track.Id;
            var header = new Grid
            {
                Height = TimelineLaneHeight,
                Margin = new Thickness(0, 0, 6, TimelineLaneSpacing),
                Padding = new Thickness(8, 0, 2, 0),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(ReferenceEquals(track, selected)
                    ? Color.FromArgb(255, 0x2A, 0x32, 0x40)
                    : Color.FromArgb(255, 0x22, 0x26, 0x2E)),
            };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int column = 0; column < 3; column++)
            {
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            }

            var name = new TextBlock
            {
                Text = track.Name,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 0xC7, 0xCD, 0xD7)),
            };
            ToolTipService.SetToolTip(name, $"{track.Name} · {VideoTimelineEditing.KindLabel(track.Kind)} track");
            header.Children.Add(name);
            header.Tapped += (_, _) => SelectTimelineTrack(trackId);

            AddTrackToggle(header, 1, track.IsVisible ? "\uE890" : "\uED1A", track.IsVisible ? $"Hide {track.Name}" : $"Show {track.Name}", track.IsVisible ? "hidden" : "shown", trackId, item => item.IsVisible = !item.IsVisible);
            AddTrackToggle(header, 2, track.IsLocked ? "\uE72E" : "\uE785", track.IsLocked ? $"Unlock {track.Name}" : $"Lock {track.Name}", track.IsLocked ? "unlocked" : "locked", trackId, item => item.IsLocked = !item.IsLocked);
            if (track.Kind == TimelineTrackKind.Video)
            {
                AddTrackToggle(header, 3, track.IsMuted ? "\uE74F" : "\uE767", track.IsMuted ? $"Unmute {track.Name}" : $"Mute {track.Name}", track.IsMuted ? "unmuted" : "muted", trackId, item => item.IsMuted = !item.IsMuted);
            }

            VideoTrackHeadersPanel.Children.Add(header);
        }
    }

    private void AddTrackToggle(Grid header, int column, string glyph, string label, string outcome, string trackId, Action<TimelineTrackProject> toggle)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 12 },
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            MinWidth = 0,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(button, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
        button.Click += (_, _) =>
        {
            if (_videoTracks.FirstOrDefault(track => track.Id == trackId) is not TimelineTrackProject track)
            {
                return;
            }

            FinishActiveEdit();
            BeginEdit();
            toggle(track);
            MarkEditChanged();
            CommitEdit();
            _selectedTimelineTrackId = trackId;
            RefreshVideoTimeline();
            SyncVideoPreview(CurrentPlaybackSeconds, forceSeek: true);
            StatusText.Text = $"{track.Name} {outcome}.";
        };
        Grid.SetColumn(button, column);
        header.Children.Add(button);
    }

    private void SelectTimelineTrack(string trackId)
    {
        if (_selectedTimelineTrackId == trackId)
        {
            return;
        }

        FinishActiveEdit();
        _selectedTimelineTrackId = trackId;
        if (SelectedTimelineClip is { } location && location.Track.Id != trackId)
        {
            _selectedTimelineClipId = null;
        }

        RefreshVideoTrackHeaders();
        UpdateVideoInspector();
        InvalidateVideoTimeline();
    }

    private void AddTimelineTrack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string kindName } ||
            !Enum.TryParse(kindName, out TimelineTrackKind kind))
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        TimelineTrackProject track = VideoTimelineEditing.CreateTrack(_videoTracks, kind);
        VideoTimelineEditing.InsertTrack(_videoTracks, track);
        MarkEditChanged();
        CommitEdit();
        _selectedTimelineTrackId = track.Id;
        _selectedTimelineClipId = null;
        RefreshVideoTimeline();
        StatusText.Text = $"{track.Name} added.";
    }

    private void MoveTimelineTrack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string directionText } ||
            !int.TryParse(directionText, out int direction) ||
            SelectedTimelineTrack is not TimelineTrackProject track)
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        if (VideoTimelineEditing.MoveTrack(_videoTracks, track, direction))
        {
            MarkEditChanged();
            StatusText.Text = $"Moved {track.Name} {(direction < 0 ? "up" : "down")}.";
        }

        CommitEdit();
        RefreshVideoTimeline();
        SeekToCurrentFrame();
    }

    private void DeleteTimelineTrack_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineTrack is not TimelineTrackProject track)
        {
            return;
        }

        if (_videoTracks.Count <= 1)
        {
            StatusText.Text = "The timeline needs at least one track.";
            return;
        }

        if (track.IsLocked)
        {
            StatusText.Text = $"Unlock {track.Name} before deleting it.";
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        int index = _videoTracks.IndexOf(track);
        _videoTracks.Remove(track);
        MarkEditChanged();
        CommitEdit();
        _selectedTimelineTrackId = _videoTracks[Math.Min(index, _videoTracks.Count - 1)].Id;
        _selectedTimelineClipId = null;
        RefreshVideoTimeline();
        SeekToCurrentFrame();
        StatusText.Text = track.Clips.Count == 0
            ? $"Deleted {track.Name}."
            : $"Deleted {track.Name} and its {track.Clips.Count} clip{(track.Clips.Count == 1 ? string.Empty : "s")}. Use Undo to restore it.";
    }

    private void TimelineTrackName_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            ApplyTimelineTrackName();
            e.Handled = true;
        }
    }

    private void TimelineTrackName_LostFocus(object sender, RoutedEventArgs e)
    {
        ApplyTimelineTrackName();
    }

    private void ApplyTimelineTrackName()
    {
        if (_isRestoringProject || SelectedTimelineTrack is not TimelineTrackProject track)
        {
            return;
        }

        string name = TimelineTrackNameBox.Text.Trim();
        if (name.Length == 0 || name.Length > 64)
        {
            TimelineTrackNameBox.Text = track.Name;
            StatusText.Text = "Track names must be between 1 and 64 characters.";
            return;
        }

        if (name == track.Name)
        {
            return;
        }

        FinishActiveEdit();
        BeginEdit();
        track.Name = name;
        MarkEditChanged();
        CommitEdit();
        RefreshVideoTrackHeaders();
        StatusText.Text = $"Track renamed to {name}.";
    }

    private bool TryGetEditableClip(out TimelineClipLocation location)
    {
        location = null!;
        if (SelectedTimelineClip is not TimelineClipLocation selected)
        {
            StatusText.Text = "Select a clip on the video timeline first.";
            return false;
        }

        if (selected.Track.IsLocked)
        {
            StatusText.Text = $"Unlock {selected.Track.Name} to edit its clips.";
            return false;
        }

        location = selected;
        return true;
    }

    private void ApplyClipAction(Func<TimelineClipLocation, bool> action, string success, string failure)
    {
        PausePlayback();
        FinishActiveEdit();
        if (!TryGetEditableClip(out TimelineClipLocation location))
        {
            return;
        }

        BeginEdit();
        if (action(location))
        {
            MarkEditChanged();
            StatusText.Text = success;
        }
        else
        {
            StatusText.Text = failure;
        }

        CommitEdit();
        if (SelectedTimelineClip is null)
        {
            _selectedTimelineClipId = null;
        }

        RefreshVideoTimeline();
        SeekToCurrentFrame();
    }

    private void ApplyVideoSourceRange_Click(object sender, RoutedEventArgs e)
    {
        double sourceIn = VideoSourceInBox.Value;
        double sourceOut = VideoSourceOutBox.Value;
        ApplyClipAction(
            location => VideoEditTools.SetSourceRange(location.Track, location.Clip, sourceIn, sourceOut, _frameRate),
            "Source range updated at the existing timeline start. Other clips, tracks, audio and ink are unchanged.",
            "Source range unchanged. Use available source handles, at least one project frame, enough duration for fades and no overlap with neighbouring clips.");
    }

    private void TrimClipStart_Click(object sender, RoutedEventArgs e) => ApplyClipAction(
        location => VideoTimelineEditing.TryTrimStart(location.Track, location.Clip, PlayheadFrameNumber, _frameRate),
        "Trimmed the clip start to the playhead.",
        "Move the playhead inside the selected clip to trim it.");

    private void TrimClipEnd_Click(object sender, RoutedEventArgs e) => ApplyClipAction(
        location => VideoTimelineEditing.TryTrimEnd(location.Track, location.Clip, PlayheadFrameNumber, _frameRate),
        "Trimmed the clip end to the playhead.",
        "Move the playhead inside the selected clip to trim it.");

    private void SplitClip_Click(object sender, RoutedEventArgs e) => ApplyClipAction(
        location =>
        {
            TimelineClipProject? right = VideoTimelineEditing.Split(
                location.Track, location.Clip, PlayheadFrameNumber, _frameRate);
            return right is not null;
        },
        "Split the clip at the playhead.",
        "Move the playhead inside the selected clip to split it.");

    private void SlipVideoClip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string direction } || !int.TryParse(direction, out int frames))
        {
            return;
        }

        ApplyClipAction(
            location => VideoEditTools.Slip(location.Track, location.Clip, frames / _frameRate),
            $"Source slipped by {frames:+0;-0} project frame; timeline position and length are unchanged.",
            "Select an unlocked video clip with unused source footage in that direction.");
    }

    private void RollVideoClip_Click(object sender, RoutedEventArgs e) =>
        ApplyNeighbourClipEdit(sender, roll: true);

    private void RippleTrimVideoClip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string direction } || !int.TryParse(direction, out int frames))
        {
            return;
        }

        ApplyClipAction(
            location => VideoEditTools.RippleTrimEnd(location.Track, location.Clip, frames, _frameRate),
            $"Ripple end trimmed by {frames:+0;-0} project frame. Later clips on this video track shifted; gaps are preserved.",
            "Ripple trim needs an unlocked video clip with sufficient source handles, at least one remaining frame and room for its fades. Later clips must stay within valid timeline positions.");
    }

    private void RippleTrimVideoStart_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string direction } || !int.TryParse(direction, out int frames))
        {
            return;
        }

        ApplyClipAction(
            location => VideoEditTools.RippleTrimStart(location.Track, location.Clip, frames, _frameRate),
            $"Ripple source start changed by {frames:+0;-0} project frame; clip placement is fixed and later clips on this video track shifted.",
            "Ripple start needs an unlocked video clip with available source footage, at least one remaining frame and room for its fades. Later clips must stay within valid timeline positions.");
    }

    private void SlideVideoClip_Click(object sender, RoutedEventArgs e) =>
        ApplyNeighbourClipEdit(sender, roll: false);

    private void ApplyNeighbourClipEdit(object sender, bool roll)
    {
        if (sender is not Button { Tag: string direction } || !int.TryParse(direction, out int frames))
        {
            return;
        }

        ApplyClipAction(
            location => roll
                ? VideoEditTools.RollEnd(location.Track, location.Clip, frames, _frameRate)
                : VideoEditTools.Slide(location.Track, location.Clip, frames, _frameRate),
            roll
                ? $"Rolled the end cut by {frames:+0;-0} project frame. Combined duration is unchanged."
                : $"Slid the clip by {frames:+0;-0} project frame. Its source range and the sequence length are unchanged.",
            roll
                ? "Roll needs a directly adjacent video clip on the same unlocked track, unused source handles and at least one frame per clip. Reduce fades if they no longer fit."
                : "Slide needs directly adjacent video clips on both sides, unused neighbouring source handles and at least one frame per neighbour. Reduce fades if they no longer fit.");
    }

    private async void RelinkTimelineClip_Click(object sender, RoutedEventArgs e)
    {
        bool batch = sender is Button { Tag: "batch" };
        PausePlayback();
        FinishActiveEdit();
        if (!TryGetEditableClip(out TimelineClipLocation selected) || selected.Track.Kind == TimelineTrackKind.Text)
        {
            return;
        }

        StorageFile? file = await PickTimelineFileAsync(selected.Track.Kind == TimelineTrackKind.Video
            ? VideoFileTypes.SupportedExtensions : VideoTimelineEditing.ImageExtensions);
        if (file is null)
        {
            return;
        }

        double duration = selected.Track.Kind == TimelineTrackKind.Video ? await ProbeVideoDurationAsync(file) : 0;
        if (SelectedTimelineClip?.Clip != selected.Clip || selected.Track.IsLocked ||
            !_videoTracks.Contains(selected.Track) || !selected.Track.Clips.Contains(selected.Clip))
        {
            StatusText.Text = "The clip selection changed while choosing a source. Relink the selected clip again.";
            return;
        }

        BeginEdit();
        int count;
        try
        {
            count = batch
                ? VideoEditTools.RelinkMatchingSources(_videoTracks, selected.Track, selected.Clip, file.Path, duration)
                : VideoEditTools.Relink(selected.Track, selected.Clip, file.Path, duration) ? 1 : 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            CommitEdit();
            StatusText.Text = $"Could not relink sources: {ex.Message}";
            return;
        }

        bool relinked = count > 0;
        if (relinked)
        {
            RegisterTimelineSource(file);
            MarkEditChanged();
        }

        CommitEdit();
        RefreshVideoTimeline();
        SeekToCurrentFrame();
        StatusText.Text = relinked
            ? $"{count} source clip(s) relinked; timeline placement, source ranges and clip properties are preserved."
            : "The replacement source is unavailable or shorter than the clip's source range.";
    }

    private void MoveClipToPlayhead_Click(object sender, RoutedEventArgs e)
    {
        ApplyClipAction(
            location => LinkedAudioVideoEditing.TryMoveVideo(
                location.Track,
                location.Clip,
                PlayheadFrameNumber,
                _audioTracks.Select(audio => audio.Project).ToArray(),
                _frameRate),
            "Moved the clip and any linked audio to the playhead.",
            "The clip would overlap another clip at the playhead.");
        RefreshAudioTracks();
        UpdateSelectedAudioTrack();
    }

    private void CloseGapBeforeClip_Click(object sender, RoutedEventArgs e) => ApplyClipAction(
        location => VideoEditTools.CloseGapBefore(location.Track, location.Clip, _frameRate),
        "Closed the whole-frame gap before this clip, shifting it and later clips on this track. Source ranges, later gaps and other tracks are unchanged.",
        "No whole-frame gap can be closed. Use an unlocked valid track with known clip durations; fractional-frame gaps may remain.");

    private void DeleteClip_Click(object sender, RoutedEventArgs e) => ApplyClipAction(
        location => VideoTimelineEditing.Delete(location.Track, location.Clip),
        "Clip deleted.",
        "Could not delete the clip.");

    private void RippleDeleteClip_Click(object sender, RoutedEventArgs e) => ApplyClipAction(
        location => VideoTimelineEditing.RippleDelete(location.Track, location.Clip, _frameRate),
        "Clip deleted and the gap closed.",
        "Could not delete the clip.");

    private void UpdateVideoInspector()
    {
        if (ClipPropertiesPanel is null)
        {
            return;
        }

        bool wasRestoring = _isRestoringProject;
        _isRestoringProject = true;
        TimelineTrackProject? track = SelectedTimelineTrack;
        int trackIndex = track is null ? -1 : _videoTracks.IndexOf(track);
        UpdateAudioVideoLinkControls();
        if (TimelineTrackNameBox.FocusState == FocusState.Unfocused)
        {
            TimelineTrackNameBox.Text = track?.Name ?? string.Empty;
        }

        MoveTrackUpButton.IsEnabled = trackIndex > 0;
        MoveTrackDownButton.IsEnabled = trackIndex >= 0 && trackIndex < _videoTracks.Count - 1;
        DeleteTrackButton.IsEnabled = _videoTracks.Count > 1 && track is { IsLocked: false };
        ToolbarClipActions.IsEnabled = SelectedTimelineClip is { Track.IsLocked: false };

        if (SelectedTimelineClip is not TimelineClipLocation location)
        {
            SelectedClipLabel.Text = "No clip selected. Click a clip on the timeline.";
            ClipActionsGrid.Visibility = Visibility.Collapsed;
            ClipPropertiesPanel.Visibility = Visibility.Collapsed;
            _isRestoringProject = wasRestoring;
            return;
        }

        TimelineClipProject clip = location.Clip;
        TimelineTrackKind kind = location.Track.Kind;
        double start = VideoTimelineEditing.GetStartSeconds(clip, _frameRate);
        double end = VideoTimelineEditing.GetEndSeconds(clip, kind, _frameRate);
        SelectedClipLabel.Text =
            $"{VideoTimelineEditing.KindLabel(kind)} · {GetClipLabel(location.Track, clip)}\n" +
            $"{FormatTimelineTime(start, true)} – {FormatTimelineTime(end, true)} on {location.Track.Name}" +
            (location.Track.IsLocked ? " (locked)" : string.Empty);
        ClipActionsGrid.Visibility = Visibility.Visible;
        ClipPropertiesPanel.Visibility = Visibility.Visible;
        ClipPropertiesPanel.IsHitTestVisible = !location.Track.IsLocked;
        ClipPropertiesPanel.Opacity = location.Track.IsLocked ? 0.5 : 1;

        bool isVideo = kind == TimelineTrackKind.Video;
        bool isImage = kind == TimelineTrackKind.Image;
        bool isTitle = kind == TimelineTrackKind.Text;
        bool supportsTransform = isVideo || isImage || isTitle;
        bool supportsMediaTransform = isVideo || isImage;
        VideoSlipActions.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
        VideoTransitionActions.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
        CreateMulticamGroupButton.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
        SwitchMulticamAngleButton.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
        GenerateVideoProxyButton.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
        ToggleVideoProxyButton.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
        CreateMulticamGroupButton.IsEnabled = isVideo && !location.Track.IsLocked;
        VideoMulticamGroup? selectedGroup = isVideo
            ? _videoMulticamGroups.FirstOrDefault(group => group.Angles.Any(angle => angle.TrackId == location.Track.Id))
            : null;
        SwitchMulticamAngleButton.IsEnabled = selectedGroup is not null &&
            PlayheadFrameNumber >= selectedGroup.StartFrame && !location.Track.IsLocked;
        GenerateVideoProxyButton.IsEnabled = isVideo && !location.Track.IsLocked && !_isStabilisationRunning;
        bool proxyAvailable = VideoProxy.IsAvailable(clip);
        ToggleVideoProxyButton.IsEnabled = isVideo && !location.Track.IsLocked && (clip.UseProxy || proxyAvailable);
        ToggleVideoProxyButton.Content = clip.UseProxy
            ? proxyAvailable ? "Use original media" : "Proxy unavailable; using original"
            : "Use proxy";
        ToolTipService.SetToolTip(
            ToggleVideoProxyButton,
            clip.UseProxy && !proxyAvailable
                ? "The configured proxy is unavailable; preview and export are using the original source."
                : "Choose whether preview and export decode the generated proxy or the original source.");
        TransformKeyframeActions.Visibility = supportsTransform ? Visibility.Visible : Visibility.Collapsed;
        OpacityKeyframeActions.Visibility = supportsTransform ? Visibility.Visible : Visibility.Collapsed;
        VideoStabilisationActions.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
        StabiliseVideoButton.IsEnabled = isVideo && !location.Track.IsLocked && !_isStabilisationRunning;
        ClearRollingShutterButton.IsEnabled = isVideo && !location.Track.IsLocked &&
            !_isStabilisationRunning && clip.RollingShutterProfile.Count > 0;
        ClipCropGrid.Visibility = supportsMediaTransform ? Visibility.Visible : Visibility.Collapsed;
        ClipCropLabel.Visibility = supportsMediaTransform ? Visibility.Visible : Visibility.Collapsed;
        ClipColourGradingControls.Visibility = supportsMediaTransform ? Visibility.Visible : Visibility.Collapsed;
        ClipMaskControls.Visibility = supportsMediaTransform ? Visibility.Visible : Visibility.Collapsed;
        CrossDissolveDurationBox.Value = clip.TransitionInSeconds > 0 ? clip.TransitionInSeconds : 1;
        VideoSourceInBox.Value = clip.SourceInSeconds;
        VideoSourceOutBox.Value = clip.SourceOutSeconds ?? clip.SourceDurationSeconds;
        RelinkTimelineClipButton.Visibility = isTitle ? Visibility.Collapsed : Visibility.Visible;
        RelinkMatchingSourcesButton.Visibility = RelinkTimelineClipButton.Visibility;
        ClipSourceRangeText.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
        ClipSourceRangeText.Text = isVideo
            ? $"Source {FormatSeconds(clip.SourceInSeconds)} - {FormatSeconds(clip.SourceOutSeconds ?? clip.SourceDurationSeconds)}"
            : string.Empty;
        ClipVolumeSlider.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
        ClipPlaybackSpeedBox.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
        ClipReverseVideoCheck.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
        ClipReverseAudioCheck.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
        ClipPlaybackHintText.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
        ClipDurationBox.Visibility = isVideo ? Visibility.Collapsed : Visibility.Visible;
        ClipTextBox.Visibility = isTitle ? Visibility.Visible : Visibility.Collapsed;
        ClipFontSelector.Visibility = isTitle ? Visibility.Visible : Visibility.Collapsed;
        ((FrameworkElement)ClipFontSizeBox.Parent).Visibility = isTitle ? Visibility.Visible : Visibility.Collapsed;
        ClipScaleSlider.Visibility = supportsTransform ? Visibility.Visible : Visibility.Collapsed;
        ClipRotationBox.Visibility = supportsTransform ? Visibility.Visible : Visibility.Collapsed;
        ClipXSlider.Visibility = supportsTransform ? Visibility.Visible : Visibility.Collapsed;
        ClipYSlider.Visibility = supportsTransform ? Visibility.Visible : Visibility.Collapsed;
        ClipCropKeyframeActions.Visibility = supportsMediaTransform ? Visibility.Visible : Visibility.Collapsed;

        double opacitySeconds = _playheadFramePosition / _frameRate;
        int opacityFrameOffset = VideoOpacityKeyframes.GetFrameOffset(clip, opacitySeconds, _frameRate);
        bool opacityPlayheadInside = VideoOpacityKeyframes.CanSetAtFrame(
            clip, kind, opacityFrameOffset, _frameRate);
        ClipOpacitySlider.Maximum = clip.OpacityKeyframes.Count == 0 ? 1 : clip.Opacity;
        ClipOpacitySlider.Value = VideoOpacityKeyframes.GetOpacityAtTime(clip, opacitySeconds, _frameRate);
        ClipOpacitySlider.IsEnabled = clip.OpacityKeyframes.Count == 0 || opacityPlayheadInside;
        SetOpacityKeyframeButton.IsEnabled = opacityPlayheadInside && !location.Track.IsLocked;
        RemoveOpacityKeyframeButton.IsEnabled = opacityPlayheadInside && !location.Track.IsLocked &&
            VideoOpacityKeyframes.IsKeyframeAt(clip, opacityFrameOffset);
        OpacityKeyframeStateText.Text = clip.OpacityKeyframes.Count == 0
            ? "No opacity keyframes. Opacity applies to the whole clip."
            : $"{clip.OpacityKeyframes.Count} opacity keyframe(s); values interpolate at the playhead.";
        ClipVolumeSlider.Value = clip.Volume;
        ClipPlaybackSpeedBox.Value = clip.PlaybackSpeed;
        ClipReverseVideoCheck.IsChecked = clip.IsReversed;
        ClipReverseAudioCheck.IsChecked = clip.ReverseEmbeddedAudio;
        ClipReverseAudioCheck.IsEnabled = isVideo && clip.IsReversed && !location.Track.IsLocked;
        ClipFadeInBox.Value = clip.FadeInSeconds;
        ClipFadeOutBox.Value = clip.FadeOutSeconds;
        ClipDurationBox.Value = clip.DurationSeconds;
        if (ClipTextBox.FocusState == FocusState.Unfocused)
        {
            ClipTextBox.Text = clip.Text;
        }

        ClipFontSelector.SelectedItem = ClipFontSelector.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, clip.FontFamily, StringComparison.OrdinalIgnoreCase));
        ClipFontSizeBox.Value = clip.FontSize;
        ClipColourSelector.SelectedItem = ClipColourSelector.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag is string tag &&
                uint.TryParse(tag, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint argb) &&
                argb == clip.Colour);
        VideoTransformValue transform = VideoTransformKeyframes.GetValueAtTime(
            clip, _playheadFramePosition / _frameRate, _frameRate);
        ClipScaleSlider.Value = transform.Scale;
        ClipRotationBox.Value = transform.RotationDegrees;
        VideoCropValue crop = VideoCropKeyframes.GetValueAtTime(
            clip, _playheadFramePosition / _frameRate, _frameRate);
        ClipCropLeftBox.Value = crop.Left;
        ClipCropRightBox.Value = crop.Right;
        ClipCropTopBox.Value = crop.Top;
        ClipCropBottomBox.Value = crop.Bottom;
        int cropFrameOffset = VideoTransformKeyframes.GetFrameOffset(
            clip, _playheadFramePosition / _frameRate, _frameRate);
        bool cropPlayheadInside = VideoCropKeyframes.CanSetAtFrame(
            clip, kind, cropFrameOffset, _frameRate);
        SetCropKeyframeButton.IsEnabled = cropPlayheadInside;
        RemoveCropKeyframeButton.IsEnabled = cropPlayheadInside &&
            VideoCropKeyframes.IsKeyframeAt(clip, cropFrameOffset);
        CropKeyframeStateText.Text = clip.CropKeyframes.Count == 0
            ? "No crop keyframes. Crop values apply to the whole clip."
            : $"{clip.CropKeyframes.Count} crop keyframe(s); values interpolate at the playhead.";
        VideoColourGradingValue grading = VideoColourGradingKeyframes.GetValueAtTime(
            clip, opacitySeconds, _frameRate);
        ClipExposureBox.Value = grading.Exposure;
        ClipContrastBox.Value = grading.Contrast;
        ClipWhiteBalanceTemperatureBox.Value = grading.Temperature;
        ClipWhiteBalanceTintBox.Value = grading.Tint;
        ClipSaturationBox.Value = grading.Saturation;
        int gradingFrameOffset = VideoColourGradingKeyframes.GetFrameOffset(clip, opacitySeconds, _frameRate);
        bool gradingPlayheadInside = VideoColourGradingKeyframes.CanSetAtFrame(
            clip, kind, gradingFrameOffset, _frameRate);
        SetColourGradingKeyframeButton.IsEnabled = gradingPlayheadInside && !location.Track.IsLocked;
        RemoveColourGradingKeyframeButton.IsEnabled = gradingPlayheadInside && !location.Track.IsLocked &&
            VideoColourGradingKeyframes.IsKeyframeAt(clip, gradingFrameOffset);
        ColourGradingKeyframeStateText.Text = clip.ColourGradingKeyframes.Count == 0
            ? "No grading keyframes. Colour values apply to the whole clip."
            : $"{clip.ColourGradingKeyframes.Count} grading keyframe(s); values interpolate at the playhead.";
        ClipColourLutLabel.Text = string.IsNullOrWhiteSpace(clip.ColourLutPath)
            ? "No 3D LUT selected"
            : $"3D LUT: {Path.GetFileName(clip.ColourLutPath)}";
        ToolTipService.SetToolTip(ClipColourLutLabel, clip.ColourLutPath);
        RemoveClipColourLutButton.IsEnabled = !location.Track.IsLocked &&
            !string.IsNullOrWhiteSpace(clip.ColourLutPath);
        ClipChromaKeyEnabled.IsChecked = VideoChromaKeyEnabledKeyframes.GetValueAtTime(
            clip, opacitySeconds, _frameRate);
        int chromaKeyEnabledFrameOffset = VideoChromaKeyEnabledKeyframes.GetFrameOffset(
            clip, opacitySeconds, _frameRate);
        bool chromaKeyEnabledPlayheadInside = VideoChromaKeyEnabledKeyframes.CanSetAtFrame(
            clip, kind, chromaKeyEnabledFrameOffset, _frameRate);
        SetChromaKeyEnabledKeyframeButton.IsEnabled =
            chromaKeyEnabledPlayheadInside && !location.Track.IsLocked;
        RemoveChromaKeyEnabledKeyframeButton.IsEnabled =
            chromaKeyEnabledPlayheadInside && !location.Track.IsLocked &&
            VideoChromaKeyEnabledKeyframes.IsKeyframeAt(clip, chromaKeyEnabledFrameOffset);
        ChromaKeyEnabledKeyframeStateText.Text = clip.ChromaKeyEnabledKeyframes.Count == 0
            ? "No enable keyframes. Chroma key uses the clip-wide setting."
            : $"{clip.ChromaKeyEnabledKeyframes.Count} enable keyframe(s); enabled/disabled state holds until the next key.";
        uint chromaKeyColour = VideoChromaKeyColourKeyframes.GetValueAtTime(
            clip, opacitySeconds, _frameRate);
        ClipChromaKeyColourSelector.SelectedItem = ClipChromaKeyColourSelector.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag is string tag &&
                uint.TryParse(tag, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out uint colour) &&
                colour == chromaKeyColour);
        int chromaKeyColourFrameOffset = VideoChromaKeyColourKeyframes.GetFrameOffset(
            clip, opacitySeconds, _frameRate);
        bool chromaKeyColourPlayheadInside = VideoChromaKeyColourKeyframes.CanSetAtFrame(
            clip, kind, chromaKeyColourFrameOffset, _frameRate);
        SetChromaKeyColourKeyframeButton.IsEnabled =
            chromaKeyColourPlayheadInside && !location.Track.IsLocked;
        RemoveChromaKeyColourKeyframeButton.IsEnabled =
            chromaKeyColourPlayheadInside && !location.Track.IsLocked &&
            VideoChromaKeyColourKeyframes.IsKeyframeAt(clip, chromaKeyColourFrameOffset);
        ChromaKeyColourKeyframeStateText.Text = clip.ChromaKeyColourKeyframes.Count == 0
            ? "No key-colour keyframes. The selected key colour applies to the whole clip."
            : $"{clip.ChromaKeyColourKeyframes.Count} key-colour keyframe(s); each preset holds until the next key.";
        ClipChromaKeyToleranceBox.Value = VideoChromaKeyKeyframes.GetValueAtTime(
            clip, opacitySeconds, _frameRate);
        int chromaKeyFrameOffset = VideoChromaKeyKeyframes.GetFrameOffset(clip, opacitySeconds, _frameRate);
        bool chromaKeyPlayheadInside = VideoChromaKeyKeyframes.CanSetAtFrame(
            clip, kind, chromaKeyFrameOffset, _frameRate);
        SetChromaKeyKeyframeButton.IsEnabled = chromaKeyPlayheadInside && !location.Track.IsLocked;
        RemoveChromaKeyKeyframeButton.IsEnabled = chromaKeyPlayheadInside && !location.Track.IsLocked &&
            VideoChromaKeyKeyframes.IsKeyframeAt(clip, chromaKeyFrameOffset);
        ChromaKeyKeyframeStateText.Text = clip.ChromaKeyKeyframes.Count == 0
            ? "No tolerance keyframes. Chroma-key tolerance applies to the whole clip."
            : $"{clip.ChromaKeyKeyframes.Count} tolerance keyframe(s); values interpolate at the playhead.";
        ClipChromaKeyFeather.IsChecked = VideoChromaKeyFeatherKeyframes.GetValueAtTime(
            clip, opacitySeconds, _frameRate);
        int chromaKeyFeatherFrameOffset = VideoChromaKeyFeatherKeyframes.GetFrameOffset(
            clip, opacitySeconds, _frameRate);
        bool chromaKeyFeatherPlayheadInside = VideoChromaKeyFeatherKeyframes.CanSetAtFrame(
            clip, kind, chromaKeyFeatherFrameOffset, _frameRate);
        SetChromaKeyFeatherKeyframeButton.IsEnabled =
            chromaKeyFeatherPlayheadInside && !location.Track.IsLocked;
        RemoveChromaKeyFeatherKeyframeButton.IsEnabled =
            chromaKeyFeatherPlayheadInside && !location.Track.IsLocked &&
            VideoChromaKeyFeatherKeyframes.IsKeyframeAt(clip, chromaKeyFeatherFrameOffset);
        ChromaKeyFeatherKeyframeStateText.Text = clip.ChromaKeyFeatherKeyframes.Count == 0
            ? "No edge-feather keyframes. Edge feathering applies to the whole clip."
            : $"{clip.ChromaKeyFeatherKeyframes.Count} edge-feather keyframe(s); each setting holds until the next key.";
        ClipChromaKeySpillBox.Value = VideoChromaKeySpillKeyframes.GetValueAtTime(
            clip, opacitySeconds, _frameRate);
        int chromaKeySpillFrameOffset = VideoChromaKeySpillKeyframes.GetFrameOffset(
            clip, opacitySeconds, _frameRate);
        bool chromaKeySpillPlayheadInside = VideoChromaKeySpillKeyframes.CanSetAtFrame(
            clip, kind, chromaKeySpillFrameOffset, _frameRate);
        SetChromaKeySpillKeyframeButton.IsEnabled = chromaKeySpillPlayheadInside && !location.Track.IsLocked;
        RemoveChromaKeySpillKeyframeButton.IsEnabled = chromaKeySpillPlayheadInside && !location.Track.IsLocked &&
            VideoChromaKeySpillKeyframes.IsKeyframeAt(clip, chromaKeySpillFrameOffset);
        ChromaKeySpillKeyframeStateText.Text = clip.ChromaKeySpillKeyframes.Count == 0
            ? "No spill keyframes. Spill suppression applies to the whole clip."
            : $"{clip.ChromaKeySpillKeyframes.Count} spill keyframe(s); strength interpolates at the playhead.";
        ClipBlurAmountBox.Value = VideoBlurKeyframes.GetValueAtTime(clip, opacitySeconds, _frameRate);
        int blurFrameOffset = VideoBlurKeyframes.GetFrameOffset(clip, opacitySeconds, _frameRate);
        bool blurPlayheadInside = VideoBlurKeyframes.CanSetAtFrame(clip, kind, blurFrameOffset, _frameRate);
        SetBlurKeyframeButton.IsEnabled = blurPlayheadInside && !location.Track.IsLocked;
        RemoveBlurKeyframeButton.IsEnabled = blurPlayheadInside && !location.Track.IsLocked &&
            VideoBlurKeyframes.IsKeyframeAt(clip, blurFrameOffset);
        BlurKeyframeStateText.Text = clip.BlurKeyframes.Count == 0
            ? "No blur keyframes. Blur applies to the whole clip."
            : $"{clip.BlurKeyframes.Count} blur keyframe(s); values interpolate at the playhead.";
        ClipSharpenAmountBox.Value = VideoSharpenKeyframes.GetValueAtTime(clip, opacitySeconds, _frameRate);
        int sharpenFrameOffset = VideoSharpenKeyframes.GetFrameOffset(clip, opacitySeconds, _frameRate);
        bool sharpenPlayheadInside = VideoSharpenKeyframes.CanSetAtFrame(
            clip, kind, sharpenFrameOffset, _frameRate);
        SetSharpenKeyframeButton.IsEnabled = sharpenPlayheadInside && !location.Track.IsLocked;
        RemoveSharpenKeyframeButton.IsEnabled = sharpenPlayheadInside && !location.Track.IsLocked &&
            VideoSharpenKeyframes.IsKeyframeAt(clip, sharpenFrameOffset);
        SharpenKeyframeStateText.Text = clip.SharpenKeyframes.Count == 0
            ? "No sharpening keyframes. Sharpening applies to the whole clip."
            : $"{clip.SharpenKeyframes.Count} sharpening keyframe(s); values interpolate at the playhead.";
        ClipVignetteAmountBox.Value = VideoVignetteKeyframes.GetValueAtTime(
            clip, opacitySeconds, _frameRate);
        int vignetteFrameOffset = VideoVignetteKeyframes.GetFrameOffset(clip, opacitySeconds, _frameRate);
        bool vignettePlayheadInside = VideoVignetteKeyframes.CanSetAtFrame(
            clip, kind, vignetteFrameOffset, _frameRate);
        SetVignetteKeyframeButton.IsEnabled = vignettePlayheadInside && !location.Track.IsLocked;
        RemoveVignetteKeyframeButton.IsEnabled = vignettePlayheadInside && !location.Track.IsLocked &&
            VideoVignetteKeyframes.IsKeyframeAt(clip, vignetteFrameOffset);
        VignetteKeyframeStateText.Text = clip.VignetteKeyframes.Count == 0
            ? "No vignette keyframes. Vignette applies to the whole clip."
            : $"{clip.VignetteKeyframes.Count} vignette keyframe(s); values interpolate at the playhead.";
        ClipMaskShapeSelector.SelectedItem = ClipMaskShapeSelector.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag is string tag &&
                Enum.TryParse(tag, out VideoMaskShape shape) &&
                shape == VideoMaskShapeKeyframes.GetValue(clip,
                    VideoMaskShapeKeyframes.GetFrameOffset(clip, opacitySeconds, _frameRate)));
        int maskShapeFrameOffset = VideoMaskShapeKeyframes.GetFrameOffset(
            clip, opacitySeconds, _frameRate);
        bool maskShapePlayheadInside = VideoMaskShapeKeyframes.CanSetAtFrame(
            clip, kind, maskShapeFrameOffset, _frameRate);
        SetMaskShapeKeyframeButton.IsEnabled = maskShapePlayheadInside && !location.Track.IsLocked;
        RemoveMaskShapeKeyframeButton.IsEnabled = maskShapePlayheadInside && !location.Track.IsLocked &&
            VideoMaskShapeKeyframes.IsKeyframeAt(clip, maskShapeFrameOffset);
        MaskShapeKeyframeStateText.Text = clip.MaskShapeKeyframes.Count == 0
            ? "Mask shape applies to the whole clip."
            : $"{clip.MaskShapeKeyframes.Count} shape keyframe(s); each shape holds until the next key.";
        int maskInversionFrameOffset = VideoMaskInversionKeyframes.GetFrameOffset(
            clip, opacitySeconds, _frameRate);
        ClipMaskInvertedCheck.IsChecked = VideoMaskInversionKeyframes.GetValue(
            clip, maskInversionFrameOffset);
        bool maskInversionPlayheadInside = VideoMaskInversionKeyframes.CanSetAtFrame(
            clip, kind, maskInversionFrameOffset, _frameRate);
        SetMaskInversionKeyframeButton.IsEnabled = maskInversionPlayheadInside && !location.Track.IsLocked;
        RemoveMaskInversionKeyframeButton.IsEnabled = maskInversionPlayheadInside && !location.Track.IsLocked &&
            VideoMaskInversionKeyframes.IsKeyframeAt(clip, maskInversionFrameOffset);
        MaskInversionKeyframeStateText.Text = clip.MaskInversionKeyframes.Count == 0
            ? "Mask inversion applies to the whole clip."
            : $"{clip.MaskInversionKeyframes.Count} inversion keyframe(s); state holds until the next key.";
        VideoMaskValue mask = VideoClipMask.GetValue(
            clip,
            VideoTransformKeyframes.GetFrameOffset(clip, _playheadFramePosition / _frameRate, _frameRate));
        ClipMaskXBox.Value = mask.X;
        ClipMaskYBox.Value = mask.Y;
        ClipMaskWidthBox.Value = mask.Width;
        ClipMaskHeightBox.Value = mask.Height;
        int maskFrameOffset = VideoTransformKeyframes.GetFrameOffset(
            clip, _playheadFramePosition / _frameRate, _frameRate);
        bool maskPlayheadInside = VideoClipMask.CanSetKeyframe(
            clip, kind, maskFrameOffset, _frameRate);
        SetMaskKeyframeButton.IsEnabled = maskPlayheadInside && !location.Track.IsLocked;
        RemoveMaskKeyframeButton.IsEnabled = maskPlayheadInside && !location.Track.IsLocked &&
            VideoClipMask.IsKeyframeAt(clip, maskFrameOffset);
        MaskKeyframeStateText.Text = clip.MaskKeyframes.Count == 0
            ? "Mask values apply to the whole clip. Add a keyframe to animate the mask."
            : $"{clip.MaskKeyframes.Count} mask keyframe(s); geometry interpolates at the playhead.";
        int maskFeatherFrameOffset = VideoMaskFeatherKeyframes.GetFrameOffset(
            clip, _playheadFramePosition / _frameRate, _frameRate);
        ClipMaskFeatherBox.Value = VideoMaskFeatherKeyframes.GetValue(clip, maskFeatherFrameOffset);
        bool maskFeatherPlayheadInside = VideoMaskFeatherKeyframes.CanSetAtFrame(
            clip, kind, maskFeatherFrameOffset, _frameRate);
        SetMaskFeatherKeyframeButton.IsEnabled = maskFeatherPlayheadInside && !location.Track.IsLocked;
        RemoveMaskFeatherKeyframeButton.IsEnabled = maskFeatherPlayheadInside && !location.Track.IsLocked &&
            VideoMaskFeatherKeyframes.IsKeyframeAt(clip, maskFeatherFrameOffset);
        MaskFeatherKeyframeStateText.Text = clip.MaskFeatherKeyframes.Count == 0
            ? "Mask feather applies to the whole clip."
            : $"{clip.MaskFeatherKeyframes.Count} feather keyframe(s); values interpolate at the playhead.";
        TrackMaskSubjectButton.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
        TrackMaskSubjectButton.IsEnabled = isVideo && !location.Track.IsLocked &&
            VideoMaskShapeKeyframes.GetValue(clip, maskShapeFrameOffset) != VideoMaskShape.None &&
            !_isMaskTrackingRunning;
        ClipCurveCanvas.Invalidate();
        ClipXSlider.Value = transform.X;
        ClipYSlider.Value = transform.Y;
        if (supportsTransform)
        {
            int frameOffset = VideoTransformKeyframes.GetFrameOffset(
                clip, _playheadFramePosition / _frameRate, _frameRate);
            bool playheadInside = VideoTransformKeyframes.CanSetAtFrame(clip, kind, frameOffset, _frameRate);
            SetTransformKeyframeButton.IsEnabled = playheadInside;
            RemoveTransformKeyframeButton.IsEnabled =
                playheadInside && VideoTransformKeyframes.IsKeyframeAt(clip, frameOffset);
            PreviousTransformKeyframeButton.IsEnabled =
                VideoTransformKeyframes.FindAdjacentFrame(clip, frameOffset, next: false) is not null;
            NextTransformKeyframeButton.IsEnabled =
                VideoTransformKeyframes.FindAdjacentFrame(clip, frameOffset, next: true) is not null;
            TransformKeyframeStateText.Text = clip.TransformKeyframes.Count == 0
                ? "No transform keyframes. Add one to animate this clip."
                : $"{clip.TransformKeyframes.Count} transform keyframe(s); playhead frame {frameOffset + 1}.";
        }
        _isRestoringProject = wasRestoring;
    }

    private bool TryBeginClipPropertyEdit(out TimelineClipLocation location)
    {
        location = null!;
        if (_isInitialising || _isRestoringProject || SelectedTimelineClip is not TimelineClipLocation selected)
        {
            return false;
        }

        if (selected.Track.IsLocked)
        {
            StatusText.Text = $"Unlock {selected.Track.Name} to edit its clips.";
            UpdateVideoInspector();
            return false;
        }

        BeginEdit();
        location = selected;
        return true;
    }

    private void ApplyClipPreview()
    {
        MarkEditChanged();
        CompositionSurface.Invalidate();
        SyncVideoPreview(CurrentPlaybackSeconds, forceSeek: false);
        InvalidateVideoTimeline();
    }

    private void ClipProperty_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!TryBeginClipPropertyEdit(out TimelineClipLocation location))
        {
            return;
        }

        TimelineClipProject clip = location.Clip;
        if (ReferenceEquals(sender, ClipOpacitySlider))
        {
            if (!VideoOpacityKeyframes.SetValueAtTime(
                clip, location.Track.Kind, CurrentPlaybackSeconds, _frameRate, Math.Clamp(e.NewValue, 0, 1)))
            {
                CommitEdit();
                UpdateVideoInspector();
                return;
            }
        }
        else if (ReferenceEquals(sender, ClipVolumeSlider))
        {
            clip.Volume = Math.Clamp(e.NewValue, 0, 1);
        }
        else if (ReferenceEquals(sender, ClipScaleSlider) ||
            ReferenceEquals(sender, ClipXSlider) ||
            ReferenceEquals(sender, ClipYSlider))
        {
            VideoTransformValue current = VideoTransformKeyframes.GetValueAtTime(clip, CurrentPlaybackSeconds, _frameRate);
            VideoTransformValue updated = ReferenceEquals(sender, ClipScaleSlider)
                ? current with { Scale = Math.Clamp(e.NewValue, VideoTimelineEditing.MinimumScale, VideoTimelineEditing.MaximumScale) }
                : ReferenceEquals(sender, ClipXSlider)
                    ? current with { X = Math.Clamp(e.NewValue, 0, 1) }
                    : current with { Y = Math.Clamp(e.NewValue, 0, 1) };
            if (!VideoTransformKeyframes.SetValueAtTime(clip, location.Track.Kind, CurrentPlaybackSeconds, _frameRate, updated))
            {
                CommitEdit();
                StatusText.Text = "Move the playhead inside the selected clip to edit its transform keyframes.";
                UpdateVideoInspector();
                return;
            }
        }

        ApplyClipPreview();
    }

    private void ClipProperty_LostFocus(object sender, RoutedEventArgs e)
    {
        FinishActiveEdit();
        UpdateVideoInspector();
    }

    private void ClipRotation_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isInitialising || _isRestoringProject)
        {
            return;
        }
        if (!double.IsFinite(args.NewValue) || args.NewValue is < -180 or > 180)
        {
            StatusText.Text = "Clip rotation must be a finite angle from -180 to 180 degrees.";
            UpdateVideoInspector();
            return;
        }
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image or TimelineTrackKind.Text) ||
            VideoTransformKeyframes.GetValueAtTime(selected.Clip, CurrentPlaybackSeconds, _frameRate).RotationDegrees == args.NewValue ||
            !TryBeginClipPropertyEdit(out var location))
        {
            return;
        }
        VideoTransformValue current = VideoTransformKeyframes.GetValueAtTime(location.Clip, CurrentPlaybackSeconds, _frameRate);
        if (!VideoTransformKeyframes.SetValueAtTime(
            location.Clip, location.Track.Kind, CurrentPlaybackSeconds, _frameRate,
            current with { RotationDegrees = args.NewValue }))
        {
            CommitEdit();
            StatusText.Text = "Move the playhead inside the selected clip to edit its transform keyframes.";
            UpdateVideoInspector();
            return;
        }
        ApplyClipPreview();
    }

    private void ClipCrop_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isInitialising || _isRestoringProject || !double.IsFinite(args.NewValue) ||
            SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image) ||
            !TryBeginClipPropertyEdit(out TimelineClipLocation location))
        {
            return;
        }

        TimelineClipProject clip = location.Clip;
        VideoCropValue current = VideoCropKeyframes.GetValueAtTime(clip, CurrentPlaybackSeconds, _frameRate);
        double left = current.Left;
        double right = current.Right;
        double top = current.Top;
        double bottom = current.Bottom;
        if (ReferenceEquals(sender, ClipCropLeftBox))
        {
            left = args.NewValue;
        }
        else if (ReferenceEquals(sender, ClipCropRightBox))
        {
            right = args.NewValue;
        }
        else if (ReferenceEquals(sender, ClipCropTopBox))
        {
            top = args.NewValue;
        }
        else if (ReferenceEquals(sender, ClipCropBottomBox))
        {
            bottom = args.NewValue;
        }
        else
        {
            CommitEdit();
            return;
        }

        VideoCropValue updated = new(left, top, right, bottom);
        if (updated == current)
        {
            CommitEdit();
            return;
        }

        TimelineClipProject candidate = VideoTimelineEditing.Clone(clip);
        if (!VideoCropKeyframes.SetValueAtTime(
            candidate, location.Track.Kind, CurrentPlaybackSeconds, _frameRate, updated))
        {
            CommitEdit();
            StatusText.Text = "Move the playhead inside the selected clip to edit its crop keyframes.";
            UpdateVideoInspector();
            return;
        }
        if (VideoCrop.Validate(candidate, location.Track.Kind, _frameRate) is string error)
        {
            CommitEdit();
            StatusText.Text = error;
            UpdateVideoInspector();
            return;
        }

        clip.CropLeftPercent = candidate.CropLeftPercent;
        clip.CropRightPercent = candidate.CropRightPercent;
        clip.CropTopPercent = candidate.CropTopPercent;
        clip.CropBottomPercent = candidate.CropBottomPercent;
        clip.CropKeyframes = candidate.CropKeyframes;
        ApplyClipPreview();
    }

    private void SetCropKeyframe_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected || selected.Track.IsLocked)
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        int frameOffset = VideoTransformKeyframes.GetFrameOffset(selected.Clip, CurrentPlaybackSeconds, _frameRate);
        bool set = VideoCropKeyframes.TrySetAtFrame(selected.Clip, selected.Track.Kind, frameOffset, _frameRate);
        if (set)
        {
            MarkEditChanged();
            CompositionSurface.Invalidate();
            SyncVideoPreview(CurrentPlaybackSeconds, forceSeek: false);
            InvalidateVideoTimeline();
        }

        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = set
            ? $"Crop keyframe set at clip frame {frameOffset + 1}."
            : "Move the playhead inside the selected video or image clip to add a crop keyframe.";
    }

    private void RemoveCropKeyframe_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected || selected.Track.IsLocked)
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        int frameOffset = VideoTransformKeyframes.GetFrameOffset(selected.Clip, CurrentPlaybackSeconds, _frameRate);
        bool removed = VideoCropKeyframes.TryRemoveAtFrame(selected.Clip, frameOffset);
        if (removed)
        {
            MarkEditChanged();
            CompositionSurface.Invalidate();
            SyncVideoPreview(CurrentPlaybackSeconds, forceSeek: false);
            InvalidateVideoTimeline();
        }

        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = removed ? "Crop keyframe removed." : "There is no crop keyframe at the playhead.";
    }

    private void ClipChromaKey_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitialising || _isRestoringProject ||
            SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image) ||
            !TryBeginClipPropertyEdit(out TimelineClipLocation location))
        {
            return;
        }

        bool value = ClipChromaKeyEnabled.IsChecked == true;
        bool current = VideoChromaKeyEnabledKeyframes.GetValueAtTime(
            location.Clip, CurrentPlaybackSeconds, _frameRate);
        if (current == value)
        {
            CommitEdit();
            return;
        }
        if (!VideoChromaKeyEnabledKeyframes.SetValueAtTime(
            location.Clip, location.Track.Kind, CurrentPlaybackSeconds, _frameRate, value))
        {
            CommitEdit();
            StatusText.Text = "Move the playhead inside the selected video or image clip to edit animated chroma-key enable.";
            return;
        }

        ApplyClipPreview();
        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = value ? "Chroma key enabled." : "Chroma key disabled.";
    }

    private void ClipChromaKeyFeather_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitialising || _isRestoringProject ||
            SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image) ||
            !TryBeginClipPropertyEdit(out TimelineClipLocation location))
        {
            return;
        }

        bool value = ClipChromaKeyFeather.IsChecked == true;
        bool current = VideoChromaKeyFeatherKeyframes.GetValueAtTime(
            location.Clip, CurrentPlaybackSeconds, _frameRate);
        if (current == value)
        {
            CommitEdit();
            return;
        }
        if (!VideoChromaKeyFeatherKeyframes.SetValueAtTime(
            location.Clip, location.Track.Kind, CurrentPlaybackSeconds, _frameRate, value))
        {
            CommitEdit();
            StatusText.Text = "Move the playhead inside the selected video or image clip to edit animated key-edge feather.";
            return;
        }

        ApplyClipPreview();
        CommitEdit();
        UpdateVideoInspector();
    }

    private void ClipChromaKeyColour_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitialising || _isRestoringProject ||
            SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image) ||
            ClipChromaKeyColourSelector.SelectedItem is not ComboBoxItem { Tag: string colourTag } ||
            !uint.TryParse(colourTag, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out uint colour) ||
            !TryBeginClipPropertyEdit(out TimelineClipLocation location))
        {
            return;
        }

        uint current = VideoChromaKeyColourKeyframes.GetValueAtTime(
            location.Clip, CurrentPlaybackSeconds, _frameRate);
        if (current == colour)
        {
            CommitEdit();
            return;
        }
        if (!VideoChromaKeyColourKeyframes.SetValueAtTime(
            location.Clip, location.Track.Kind, CurrentPlaybackSeconds, _frameRate, colour))
        {
            CommitEdit();
            StatusText.Text = "Move the playhead inside the selected video or image clip to edit animated key colour.";
            return;
        }

        ApplyClipPreview();
        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = "Chroma-key colour updated.";
    }

    private void ClipChromaKeyTolerance_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!double.IsFinite(args.NewValue) || args.NewValue is < 0 or > 1)
        {
            StatusText.Text = "Chroma-key tolerance must be between 0 and 1.";
            UpdateVideoInspector();
            return;
        }

        if (_isInitialising || _isRestoringProject ||
            SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image) ||
            !TryBeginClipPropertyEdit(out TimelineClipLocation location))
        {
            return;
        }

        double current = VideoChromaKeyKeyframes.GetValueAtTime(
            location.Clip, CurrentPlaybackSeconds, _frameRate);
        if (current == args.NewValue)
        {
            CommitEdit();
            return;
        }

        if (!VideoChromaKeyKeyframes.SetValueAtTime(
            location.Clip, location.Track.Kind, CurrentPlaybackSeconds, _frameRate, args.NewValue))
        {
            CommitEdit();
            StatusText.Text = "Move the playhead inside the selected video or image clip to edit animated chroma-key tolerance.";
            return;
        }

        ApplyClipPreview();
        CommitEdit();
        UpdateVideoInspector();
    }

    private void ClipChromaKeySpill_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isInitialising || _isRestoringProject)
        {
            return;
        }
        if (!double.IsFinite(args.NewValue) || args.NewValue is < 0 or > 1)
        {
            StatusText.Text = "Chroma-key spill suppression must be between 0 and 1.";
            UpdateVideoInspector();
            return;
        }
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image) ||
            !TryBeginClipPropertyEdit(out TimelineClipLocation location))
        {
            return;
        }

        double current = VideoChromaKeySpillKeyframes.GetValueAtTime(
            location.Clip, CurrentPlaybackSeconds, _frameRate);
        if (current == args.NewValue)
        {
            CommitEdit();
            return;
        }
        if (!VideoChromaKeySpillKeyframes.SetValueAtTime(
            location.Clip, location.Track.Kind, CurrentPlaybackSeconds, _frameRate, args.NewValue))
        {
            CommitEdit();
            StatusText.Text = "Move the playhead inside the selected video or image clip to edit animated spill suppression.";
            return;
        }

        ApplyClipPreview();
        CommitEdit();
        UpdateVideoInspector();
    }

    private void ClipBlurAmount_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isInitialising || _isRestoringProject)
        {
            return;
        }

        if (!double.IsFinite(args.NewValue) || args.NewValue is < 0 or > VideoBlurEffect.MaximumAmount)
        {
            StatusText.Text = $"Blur amount must be between 0 and {VideoBlurEffect.MaximumAmount:0}.";
            UpdateVideoInspector();
            return;
        }

        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image) ||
            !TryBeginClipPropertyEdit(out TimelineClipLocation location))
        {
            return;
        }

        double current = VideoBlurKeyframes.GetValueAtTime(
            location.Clip, CurrentPlaybackSeconds, _frameRate);
        if (current == args.NewValue)
        {
            return;
        }

        if (!VideoBlurKeyframes.SetValueAtTime(
            location.Clip, location.Track.Kind, CurrentPlaybackSeconds, _frameRate, args.NewValue))
        {
            CommitEdit();
            StatusText.Text = "Move the playhead inside the selected video or image clip to edit animated blur.";
            return;
        }
        ApplyClipPreview();
    }

    private void ClipVignetteAmount_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isInitialising || _isRestoringProject)
        {
            return;
        }

        if (!double.IsFinite(args.NewValue) || args.NewValue is < 0 or > VideoVignette.MaximumAmount)
        {
            StatusText.Text = "Vignette amount must be between 0 and 1.";
            UpdateVideoInspector();
            return;
        }

        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image) ||
            !TryBeginClipPropertyEdit(out TimelineClipLocation location))
        {
            return;
        }

        double current = VideoVignetteKeyframes.GetValueAtTime(
            location.Clip, CurrentPlaybackSeconds, _frameRate);
        if (current == args.NewValue)
        {
            return;
        }

        if (!VideoVignetteKeyframes.SetValueAtTime(
            location.Clip, location.Track.Kind, CurrentPlaybackSeconds, _frameRate, args.NewValue))
        {
            CommitEdit();
            StatusText.Text = "Move the playhead inside the selected video or image clip to edit animated vignette.";
            return;
        }
        ApplyClipPreview();
    }

    private void ClipSharpenAmount_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isInitialising || _isRestoringProject)
        {
            return;
        }

        if (!double.IsFinite(args.NewValue) || args.NewValue is < 0 or > VideoSharpen.MaximumAmount)
        {
            StatusText.Text = $"Sharpen amount must be between 0 and {VideoSharpen.MaximumAmount:0}.";
            UpdateVideoInspector();
            return;
        }

        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image) ||
            !TryBeginClipPropertyEdit(out TimelineClipLocation location))
        {
            return;
        }

        double current = VideoSharpenKeyframes.GetValueAtTime(
            location.Clip, CurrentPlaybackSeconds, _frameRate);
        if (current == args.NewValue)
        {
            return;
        }

        if (!VideoSharpenKeyframes.SetValueAtTime(
            location.Clip, location.Track.Kind, CurrentPlaybackSeconds, _frameRate, args.NewValue))
        {
            CommitEdit();
            StatusText.Text = "Move the playhead inside the selected video or image clip to edit animated sharpening.";
            return;
        }
        ApplyClipPreview();
    }

    private void ClipColourGrading_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isInitialising || _isRestoringProject || !double.IsFinite(args.NewValue) ||
            SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image) ||
            !TryBeginClipPropertyEdit(out TimelineClipLocation location))
        {
            return;
        }

        TimelineClipProject clip = location.Clip;
        VideoColourGradingValue current = VideoColourGradingKeyframes.GetValueAtTime(
            clip, CurrentPlaybackSeconds, _frameRate);
        VideoColourGradingValue updated = current;
        if (ReferenceEquals(sender, ClipExposureBox))
        {
            updated = updated with { Exposure = args.NewValue };
        }
        else if (ReferenceEquals(sender, ClipContrastBox))
        {
            updated = updated with { Contrast = args.NewValue };
        }
        else if (ReferenceEquals(sender, ClipWhiteBalanceTemperatureBox))
        {
            updated = updated with { Temperature = args.NewValue };
        }
        else if (ReferenceEquals(sender, ClipWhiteBalanceTintBox))
        {
            updated = updated with { Tint = args.NewValue };
        }
        else if (ReferenceEquals(sender, ClipSaturationBox))
        {
            updated = updated with { Saturation = args.NewValue };
        }
        else
        {
            CommitEdit();
            return;
        }

        var candidate = VideoTimelineEditing.Clone(clip);
        candidate.Exposure = updated.Exposure;
        candidate.Contrast = updated.Contrast;
        candidate.WhiteBalanceTemperature = updated.Temperature;
        candidate.WhiteBalanceTint = updated.Tint;
        candidate.Saturation = updated.Saturation;
        if (VideoColourGrading.Validate(candidate, location.Track.Kind) is string error)
        {
            CommitEdit();
            StatusText.Text = error;
            UpdateVideoInspector();
            return;
        }

        if (updated == current)
        {
            CommitEdit();
            return;
        }

        if (clip.ColourGradingKeyframes.Count > 0)
        {
            if (!VideoColourGradingKeyframes.SetValueAtTime(
                clip, location.Track.Kind, CurrentPlaybackSeconds, _frameRate, updated))
            {
                CommitEdit();
                StatusText.Text = "Move the playhead inside the selected clip to edit animated colour values.";
                UpdateVideoInspector();
                return;
            }
        }
        else
        {
            clip.Exposure = updated.Exposure;
            clip.Contrast = updated.Contrast;
            clip.WhiteBalanceTemperature = updated.Temperature;
            clip.WhiteBalanceTint = updated.Tint;
            clip.Saturation = updated.Saturation;
        }
        ApplyClipPreview();
    }

    private async void LoadClipColourLut_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image) ||
            selected.Track.IsLocked)
        {
            return;
        }

        string clipId = selected.Clip.Id;
        StorageFile? file = await PickTimelineFileAsync([".cube"]);
        if (file is null)
        {
            return;
        }

        try
        {
            string content = await FileIO.ReadTextAsync(file);
            _ = VideoColourLutData.Parse(content, file.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            StatusText.Text = $"Could not load LUT {file.Name}: {ex.Message}";
            return;
        }

        TimelineClipLocation? current = VideoTimelineEditing.FindClip(_videoTracks, clipId);
        if (current is null || current.Track.IsLocked)
        {
            StatusText.Text = "The selected clip is no longer available; no LUT was applied.";
            return;
        }

        FinishActiveEdit();
        if (!TryBeginClipPropertyEdit(out TimelineClipLocation location))
        {
            return;
        }

        location.Clip.ColourLutPath = file.Path;
        ApplyClipPreview();
        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = $"Applied 3D LUT {file.Name} to the selected clip.";
    }

    private void RemoveClipColourLut_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.IsLocked ||
            string.IsNullOrWhiteSpace(selected.Clip.ColourLutPath) ||
            !TryBeginClipPropertyEdit(out TimelineClipLocation location))
        {
            return;
        }

        location.Clip.ColourLutPath = null;
        ApplyClipPreview();
        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = "Removed the selected clip's 3D LUT.";
    }

    private void ClipCurveCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        CanvasDrawingSession session = args.DrawingSession;
        session.Clear(Color.FromArgb(255, 24, 28, 35));
        Rect plot = GetClipCurvePlotBounds(sender.ActualWidth, sender.ActualHeight);
        if (plot.Width <= 0 || plot.Height <= 0)
        {
            return;
        }

        Color grid = Color.FromArgb(100, 150, 160, 175);
        for (int division = 0; division <= 4; division++)
        {
            float x = (float)(plot.X + (plot.Width * division / 4));
            float y = (float)(plot.Y + (plot.Height * division / 4));
            session.DrawLine(x, (float)plot.Y, x, (float)(plot.Y + plot.Height), grid);
            session.DrawLine((float)plot.X, y, (float)(plot.X + plot.Width), y, grid);
        }

        session.DrawLine((float)plot.X, (float)(plot.Y + plot.Height),
            (float)(plot.X + plot.Width), (float)plot.Y, Color.FromArgb(110, 190, 195, 205));
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            VideoColourGrading.GetCurve(selected.Clip, GetSelectedCurveChannel()) is not { Count: >= 2 } curve)
        {
            return;
        }

        Color channelColour = GetSelectedCurveChannel() switch
        {
            VideoColourCurveChannel.Red => Color.FromArgb(255, 255, 100, 100),
            VideoColourCurveChannel.Green => Color.FromArgb(255, 100, 230, 135),
            VideoColourCurveChannel.Blue => Color.FromArgb(255, 115, 175, 255),
            _ => Color.FromArgb(255, 245, 245, 250),
        };
        for (int index = 1; index < curve.Count; index++)
        {
            Point previous = CurvePointToCanvas(curve[index - 1], plot);
            Point current = CurvePointToCanvas(curve[index], plot);
            session.DrawLine((float)previous.X, (float)previous.Y, (float)current.X, (float)current.Y, channelColour, 2);
        }

        foreach (VideoColourCurvePoint point in curve)
        {
            Point position = CurvePointToCanvas(point, plot);
            session.FillCircle((float)position.X, (float)position.Y, 5, channelColour);
        }
    }

    private void ClipCurveChannel_Changed(object sender, SelectionChangedEventArgs e) =>
        ClipCurveCanvas?.Invalidate();

    private void ClipCurveCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not CanvasControl canvas ||
            SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.IsLocked)
        {
            return;
        }

        Microsoft.UI.Input.PointerPoint pointer = e.GetCurrentPoint(canvas);
        bool isRightClick = pointer.Properties.IsRightButtonPressed;
        if (!isRightClick && !pointer.Properties.IsLeftButtonPressed)
        {
            return;
        }

        VideoColourCurveChannel channel = GetSelectedCurveChannel();
        List<VideoColourCurvePoint>? curve = VideoColourGrading.GetCurve(selected.Clip, channel);
        if (curve is null)
        {
            StatusText.Text = "The selected clip has invalid curve data.";
            return;
        }

        Rect plot = GetClipCurvePlotBounds(canvas.ActualWidth, canvas.ActualHeight);
        if (plot.Width <= 0 || plot.Height <= 0)
        {
            return;
        }

        Point position = pointer.Position;
        int nearest = FindNearestCurvePoint(curve, position, plot);
        if (isRightClick)
        {
            if (nearest <= 0 || nearest >= curve.Count - 1)
            {
                return;
            }

            if (TryBeginClipPropertyEdit(out TimelineClipLocation location))
            {
                VideoColourGrading.GetCurve(location.Clip, channel)!.RemoveAt(nearest);
                ApplyClipPreview();
                CommitEdit();
                canvas.Invalidate();
                e.Handled = true;
            }

            return;
        }

        if (nearest >= 0)
        {
            if (nearest == 0 || nearest == curve.Count - 1 ||
                !TryBeginClipPropertyEdit(out TimelineClipLocation location))
            {
                return;
            }

            _draggedCurveClip = location.Clip;
            _draggedCurveChannel = channel;
            _draggedCurvePointIndex = nearest;
        }
        else
        {
            if (curve.Count >= VideoColourGrading.MaximumCurvePoints ||
                !TryBeginClipPropertyEdit(out TimelineClipLocation location))
            {
                StatusText.Text = curve.Count >= VideoColourGrading.MaximumCurvePoints
                    ? $"A curve can contain at most {VideoColourGrading.MaximumCurvePoints} points."
                    : StatusText.Text;
                return;
            }

            curve = VideoColourGrading.GetCurve(location.Clip, channel)!;
            (double input, double output) = CanvasToCurvePoint(position, plot);
            int insertionIndex = curve.FindIndex(point => point.Input > input);
            if (insertionIndex < 1)
            {
                insertionIndex = 1;
            }

            double minimumInput = curve[insertionIndex - 1].Input + 0.005;
            double maximumInput = curve[insertionIndex].Input - 0.005;
            if (minimumInput > maximumInput)
            {
                CommitEdit();
                return;
            }

            curve.Insert(insertionIndex, new VideoColourCurvePoint(
                Math.Clamp(input, minimumInput, maximumInput), output));
            _draggedCurveClip = location.Clip;
            _draggedCurveChannel = channel;
            _draggedCurvePointIndex = insertionIndex;
            ApplyClipPreview();
        }

        _curveDragPointerId = e.Pointer.PointerId;
        canvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ClipCurveCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not CanvasControl canvas ||
            _draggedCurveClip is not TimelineClipProject clip ||
            _draggedCurvePointIndex < 1 ||
            _curveDragPointerId != e.Pointer.PointerId)
        {
            return;
        }

        List<VideoColourCurvePoint>? curve = VideoColourGrading.GetCurve(clip, _draggedCurveChannel);
        if (curve is null || _draggedCurvePointIndex >= curve.Count - 1)
        {
            return;
        }

        Rect plot = GetClipCurvePlotBounds(canvas.ActualWidth, canvas.ActualHeight);
        if (plot.Width <= 0 || plot.Height <= 0)
        {
            return;
        }

        (double input, double output) = CanvasToCurvePoint(e.GetCurrentPoint(canvas).Position, plot);
        double minimumInput = curve[_draggedCurvePointIndex - 1].Input + 0.005;
        double maximumInput = curve[_draggedCurvePointIndex + 1].Input - 0.005;
        if (minimumInput > maximumInput)
        {
            return;
        }

        curve[_draggedCurvePointIndex] = new VideoColourCurvePoint(
            Math.Clamp(input, minimumInput, maximumInput), output);
        ApplyClipPreview();
        canvas.Invalidate();
    }

    private void ClipCurveCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is CanvasControl canvas && _draggedCurvePointIndex >= 0 &&
            _curveDragPointerId == e.Pointer.PointerId)
        {
            _draggedCurveClip = null;
            _draggedCurvePointIndex = -1;
            _curveDragPointerId = null;
            canvas.ReleasePointerCapture(e.Pointer);
            CommitEdit();
            canvas.Invalidate();
        }
    }

    private void ClipCurveCanvas_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_draggedCurvePointIndex >= 0 && _curveDragPointerId == e.Pointer.PointerId)
        {
            _draggedCurveClip = null;
            _draggedCurvePointIndex = -1;
            _curveDragPointerId = null;
            CommitEdit();
        }
    }

    private void ResetClipCurve_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.IsLocked ||
            VideoColourGrading.GetCurve(selected.Clip, GetSelectedCurveChannel()) is not { } curve ||
            VideoColourGrading.IsNeutralCurve(curve) ||
            !TryBeginClipPropertyEdit(out TimelineClipLocation location))
        {
            return;
        }

        VideoColourGrading.SetCurve(location.Clip, GetSelectedCurveChannel(), [new(0, 0), new(1, 1)]);
        ApplyClipPreview();
        CommitEdit();
        ClipCurveCanvas.Invalidate();
    }

    private VideoColourCurveChannel GetSelectedCurveChannel() =>
        ClipCurveChannelSelector?.SelectedItem is ComboBoxItem { Tag: string tag } &&
        Enum.TryParse(tag, ignoreCase: true, out VideoColourCurveChannel channel)
            ? channel
            : VideoColourCurveChannel.Master;

    private static Rect GetClipCurvePlotBounds(double width, double height) =>
        new(16, 12, Math.Max(0, width - 32), Math.Max(0, height - 24));

    private static Point CurvePointToCanvas(VideoColourCurvePoint point, Rect plot) =>
        new(plot.X + (point.Input * plot.Width), plot.Y + ((1 - point.Output) * plot.Height));

    private static (double Input, double Output) CanvasToCurvePoint(Point position, Rect plot) =>
        (Math.Clamp((position.X - plot.X) / plot.Width, 0, 1),
            Math.Clamp(1 - ((position.Y - plot.Y) / plot.Height), 0, 1));

    private static int FindNearestCurvePoint(
        IReadOnlyList<VideoColourCurvePoint> curve,
        Point position,
        Rect plot)
    {
        int nearest = -1;
        double distance = 14;
        for (int index = 0; index < curve.Count; index++)
        {
            Point point = CurvePointToCanvas(curve[index], plot);
            double candidate = Math.Sqrt(Math.Pow(position.X - point.X, 2) + Math.Pow(position.Y - point.Y, 2));
            if (candidate < distance)
            {
                nearest = index;
                distance = candidate;
            }
        }

        return nearest;
    }

    private void SetTransformKeyframe_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.IsLocked)
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        int frameOffset = VideoTransformKeyframes.GetFrameOffset(selected.Clip, CurrentPlaybackSeconds, _frameRate);
        bool set = VideoTransformKeyframes.TrySetAtFrame(selected.Clip, selected.Track.Kind, frameOffset, _frameRate);
        if (set)
        {
            MarkEditChanged();
            CompositionSurface.Invalidate();
            SyncVideoPreview(CurrentPlaybackSeconds, forceSeek: false);
            InvalidateVideoTimeline();
        }

        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = set
            ? $"Transform keyframe set at clip frame {frameOffset + 1}."
            : "Move the playhead inside the selected clip to add a transform keyframe.";
    }

    private void SetOpacityKeyframe_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.IsLocked)
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        int frameOffset = VideoOpacityKeyframes.GetFrameOffset(selected.Clip, CurrentPlaybackSeconds, _frameRate);
        bool set = VideoOpacityKeyframes.TrySetAtFrame(
            selected.Clip, selected.Track.Kind, frameOffset, _frameRate);
        if (set)
        {
            MarkEditChanged();
            CompositionSurface.Invalidate();
            SyncVideoPreview(CurrentPlaybackSeconds, forceSeek: false);
            InvalidateVideoTimeline();
        }

        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = set
            ? $"Opacity keyframe set at clip frame {frameOffset + 1}."
            : "Move the playhead inside the selected clip to add an opacity keyframe.";
    }

    private void RemoveOpacityKeyframe_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.IsLocked)
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        int frameOffset = VideoOpacityKeyframes.GetFrameOffset(selected.Clip, CurrentPlaybackSeconds, _frameRate);
        bool removed = VideoOpacityKeyframes.TryRemoveAtFrame(selected.Clip, frameOffset);
        if (removed)
        {
            MarkEditChanged();
            CompositionSurface.Invalidate();
            SyncVideoPreview(CurrentPlaybackSeconds, forceSeek: false);
            InvalidateVideoTimeline();
        }

        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = removed ? "Opacity keyframe removed." : "There is no opacity keyframe at the playhead.";
    }

    private void SetChromaKeyKeyframe_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.IsLocked ||
            selected.Track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image))
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        int frameOffset = VideoChromaKeyKeyframes.GetFrameOffset(
            selected.Clip, CurrentPlaybackSeconds, _frameRate);
        bool set = VideoChromaKeyKeyframes.TrySetAtFrame(
            selected.Clip, selected.Track.Kind, frameOffset, _frameRate);
        if (set)
        {
            MarkEditChanged();
            CompositionSurface.Invalidate();
            SyncVideoPreview(CurrentPlaybackSeconds, forceSeek: false);
            InvalidateVideoTimeline();
        }

        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = set
            ? $"Chroma-key tolerance keyframe set at clip frame {frameOffset + 1}."
            : "Move the playhead inside the selected video or image clip to add a tolerance keyframe.";
    }

    private void SetChromaKeyColourKeyframe_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.IsLocked ||
            selected.Track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image))
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        int frameOffset = VideoChromaKeyColourKeyframes.GetFrameOffset(
            selected.Clip, CurrentPlaybackSeconds, _frameRate);
        bool set = VideoChromaKeyColourKeyframes.TrySetAtFrame(
            selected.Clip, selected.Track.Kind, frameOffset, _frameRate);
        if (set)
        {
            ApplyClipPreview();
        }

        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = set
            ? $"Chroma-key colour keyframe set at clip frame {frameOffset + 1}."
            : "Move the playhead inside the selected video or image clip to add a key-colour keyframe.";
    }

    private void SetChromaKeyEnabledKeyframe_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.IsLocked ||
            selected.Track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image))
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        int frameOffset = VideoChromaKeyEnabledKeyframes.GetFrameOffset(
            selected.Clip, CurrentPlaybackSeconds, _frameRate);
        bool set = VideoChromaKeyEnabledKeyframes.TrySetAtFrame(
            selected.Clip, selected.Track.Kind, frameOffset, _frameRate);
        if (set)
        {
            ApplyClipPreview();
        }

        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = set
            ? $"Chroma-key enable keyframe set at clip frame {frameOffset + 1}."
            : "Move the playhead inside the selected video or image clip to add an enable keyframe.";
    }

    private void RemoveChromaKeyEnabledKeyframe_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.IsLocked ||
            selected.Track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image))
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        int frameOffset = VideoChromaKeyEnabledKeyframes.GetFrameOffset(
            selected.Clip, CurrentPlaybackSeconds, _frameRate);
        bool removed = VideoChromaKeyEnabledKeyframes.TryRemoveAtFrame(selected.Clip, frameOffset);
        if (removed)
        {
            ApplyClipPreview();
        }

        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = removed
            ? "Chroma-key enable keyframe removed."
            : "There is no enable keyframe at the playhead.";
    }

    private void RemoveChromaKeyColourKeyframe_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.IsLocked ||
            selected.Track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image))
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        int frameOffset = VideoChromaKeyColourKeyframes.GetFrameOffset(
            selected.Clip, CurrentPlaybackSeconds, _frameRate);
        bool removed = VideoChromaKeyColourKeyframes.TryRemoveAtFrame(selected.Clip, frameOffset);
        if (removed)
        {
            ApplyClipPreview();
        }

        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = removed
            ? "Chroma-key colour keyframe removed."
            : "There is no key-colour keyframe at the playhead.";
    }

    private void RemoveChromaKeyKeyframe_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.IsLocked)
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        int frameOffset = VideoChromaKeyKeyframes.GetFrameOffset(
            selected.Clip, CurrentPlaybackSeconds, _frameRate);
        bool removed = VideoChromaKeyKeyframes.TryRemoveAtFrame(selected.Clip, frameOffset);
        if (removed)
        {
            MarkEditChanged();
            CompositionSurface.Invalidate();
            SyncVideoPreview(CurrentPlaybackSeconds, forceSeek: false);
            InvalidateVideoTimeline();
        }

        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = removed
            ? "Chroma-key tolerance keyframe removed."
            : "There is no tolerance keyframe at the playhead.";
    }

    private void SetChromaKeyFeatherKeyframe_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.IsLocked ||
            selected.Track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image))
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        int frameOffset = VideoChromaKeyFeatherKeyframes.GetFrameOffset(
            selected.Clip, CurrentPlaybackSeconds, _frameRate);
        bool set = VideoChromaKeyFeatherKeyframes.TrySetAtFrame(
            selected.Clip, selected.Track.Kind, frameOffset, _frameRate);
        if (set)
        {
            ApplyClipPreview();
        }

        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = set
            ? $"Chroma-key edge-feather keyframe set at clip frame {frameOffset + 1}."
            : "Move the playhead inside the selected video or image clip to add an edge-feather keyframe.";
    }

    private void RemoveChromaKeyFeatherKeyframe_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.IsLocked ||
            selected.Track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image))
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        int frameOffset = VideoChromaKeyFeatherKeyframes.GetFrameOffset(
            selected.Clip, CurrentPlaybackSeconds, _frameRate);
        bool removed = VideoChromaKeyFeatherKeyframes.TryRemoveAtFrame(selected.Clip, frameOffset);
        if (removed)
        {
            ApplyClipPreview();
        }

        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = removed
            ? "Chroma-key edge-feather keyframe removed."
            : "There is no edge-feather keyframe at the playhead.";
    }

    private void SetChromaKeySpillKeyframe_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.IsLocked ||
            selected.Track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image))
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        int frameOffset = VideoChromaKeySpillKeyframes.GetFrameOffset(
            selected.Clip, CurrentPlaybackSeconds, _frameRate);
        bool set = VideoChromaKeySpillKeyframes.TrySetAtFrame(
            selected.Clip, selected.Track.Kind, frameOffset, _frameRate);
        if (set)
        {
            ApplyClipPreview();
        }

        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = set
            ? $"Chroma-key spill keyframe set at clip frame {frameOffset + 1}."
            : "Move the playhead inside the selected video or image clip to add a spill keyframe.";
    }

    private void RemoveChromaKeySpillKeyframe_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.IsLocked ||
            selected.Track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image))
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        int frameOffset = VideoChromaKeySpillKeyframes.GetFrameOffset(
            selected.Clip, CurrentPlaybackSeconds, _frameRate);
        bool removed = VideoChromaKeySpillKeyframes.TryRemoveAtFrame(selected.Clip, frameOffset);
        if (removed)
        {
            ApplyClipPreview();
        }

        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = removed
            ? "Chroma-key spill keyframe removed."
            : "There is no spill keyframe at the playhead.";
    }

    private void SetBlurKeyframe_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.IsLocked ||
            selected.Track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image))
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        int frameOffset = VideoBlurKeyframes.GetFrameOffset(
            selected.Clip, CurrentPlaybackSeconds, _frameRate);
        bool set = VideoBlurKeyframes.TrySetAtFrame(
            selected.Clip, selected.Track.Kind, frameOffset, _frameRate);
        if (set)
        {
            MarkEditChanged();
            CompositionSurface.Invalidate();
            SyncVideoPreview(CurrentPlaybackSeconds, forceSeek: false);
            InvalidateVideoTimeline();
        }

        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = set
            ? $"Blur keyframe set at clip frame {frameOffset + 1}."
            : "Move the playhead inside the selected video or image clip to add a blur keyframe.";
    }

    private void RemoveBlurKeyframe_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.IsLocked)
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        int frameOffset = VideoBlurKeyframes.GetFrameOffset(
            selected.Clip, CurrentPlaybackSeconds, _frameRate);
        bool removed = VideoBlurKeyframes.TryRemoveAtFrame(selected.Clip, frameOffset);
        if (removed)
        {
            MarkEditChanged();
            CompositionSurface.Invalidate();
            SyncVideoPreview(CurrentPlaybackSeconds, forceSeek: false);
            InvalidateVideoTimeline();
        }

        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = removed
            ? "Blur keyframe removed."
            : "There is no blur keyframe at the playhead.";
    }

    private void SetSharpenKeyframe_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.IsLocked ||
            selected.Track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image))
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        int frameOffset = VideoSharpenKeyframes.GetFrameOffset(
            selected.Clip, CurrentPlaybackSeconds, _frameRate);
        bool set = VideoSharpenKeyframes.TrySetAtFrame(
            selected.Clip, selected.Track.Kind, frameOffset, _frameRate);
        if (set)
        {
            MarkEditChanged();
            CompositionSurface.Invalidate();
            SyncVideoPreview(CurrentPlaybackSeconds, forceSeek: false);
            InvalidateVideoTimeline();
        }

        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = set
            ? $"Sharpening keyframe set at clip frame {frameOffset + 1}."
            : "Move the playhead inside the selected video or image clip to add a sharpening keyframe.";
    }

    private void RemoveSharpenKeyframe_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.IsLocked)
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        int frameOffset = VideoSharpenKeyframes.GetFrameOffset(
            selected.Clip, CurrentPlaybackSeconds, _frameRate);
        bool removed = VideoSharpenKeyframes.TryRemoveAtFrame(selected.Clip, frameOffset);
        if (removed)
        {
            MarkEditChanged();
            CompositionSurface.Invalidate();
            SyncVideoPreview(CurrentPlaybackSeconds, forceSeek: false);
            InvalidateVideoTimeline();
        }

        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = removed
            ? "Sharpening keyframe removed."
            : "There is no sharpening keyframe at the playhead.";
    }

    private void SetVignetteKeyframe_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.IsLocked ||
            selected.Track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image))
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        int frameOffset = VideoVignetteKeyframes.GetFrameOffset(
            selected.Clip, CurrentPlaybackSeconds, _frameRate);
        bool set = VideoVignetteKeyframes.TrySetAtFrame(
            selected.Clip, selected.Track.Kind, frameOffset, _frameRate);
        if (set)
        {
            MarkEditChanged();
            CompositionSurface.Invalidate();
            SyncVideoPreview(CurrentPlaybackSeconds, forceSeek: false);
            InvalidateVideoTimeline();
        }

        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = set
            ? $"Vignette keyframe set at clip frame {frameOffset + 1}."
            : "Move the playhead inside the selected video or image clip to add a vignette keyframe.";
    }

    private void RemoveVignetteKeyframe_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.IsLocked)
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        int frameOffset = VideoVignetteKeyframes.GetFrameOffset(
            selected.Clip, CurrentPlaybackSeconds, _frameRate);
        bool removed = VideoVignetteKeyframes.TryRemoveAtFrame(selected.Clip, frameOffset);
        if (removed)
        {
            MarkEditChanged();
            CompositionSurface.Invalidate();
            SyncVideoPreview(CurrentPlaybackSeconds, forceSeek: false);
            InvalidateVideoTimeline();
        }

        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = removed
            ? "Vignette keyframe removed."
            : "There is no vignette keyframe at the playhead.";
    }

    private void SetColourGradingKeyframe_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.IsLocked ||
            selected.Track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image))
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        int frameOffset = VideoColourGradingKeyframes.GetFrameOffset(
            selected.Clip, CurrentPlaybackSeconds, _frameRate);
        bool set = VideoColourGradingKeyframes.TrySetAtFrame(
            selected.Clip, selected.Track.Kind, frameOffset, _frameRate);
        if (set)
        {
            MarkEditChanged();
            CompositionSurface.Invalidate();
            SyncVideoPreview(CurrentPlaybackSeconds, forceSeek: false);
            InvalidateVideoTimeline();
        }

        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = set
            ? $"Colour grading keyframe set at clip frame {frameOffset + 1}."
            : "Move the playhead inside the selected video or image clip to add a grading keyframe.";
    }

    private void RemoveColourGradingKeyframe_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.IsLocked)
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        int frameOffset = VideoColourGradingKeyframes.GetFrameOffset(
            selected.Clip, CurrentPlaybackSeconds, _frameRate);
        bool removed = VideoColourGradingKeyframes.TryRemoveAtFrame(selected.Clip, frameOffset);
        if (removed)
        {
            MarkEditChanged();
            CompositionSurface.Invalidate();
            SyncVideoPreview(CurrentPlaybackSeconds, forceSeek: false);
            InvalidateVideoTimeline();
        }

        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = removed
            ? "Colour grading keyframe removed."
            : "There is no colour grading keyframe at the playhead.";
    }

    private void RemoveTransformKeyframe_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.IsLocked)
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        int frameOffset = VideoTransformKeyframes.GetFrameOffset(selected.Clip, CurrentPlaybackSeconds, _frameRate);
        bool removed = VideoTransformKeyframes.TryRemoveAtFrame(selected.Clip, frameOffset);
        if (removed)
        {
            MarkEditChanged();
            CompositionSurface.Invalidate();
            SyncVideoPreview(CurrentPlaybackSeconds, forceSeek: false);
            InvalidateVideoTimeline();
        }

        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = removed ? "Transform keyframe removed." : "There is no keyframe at the playhead.";
    }

    private void NavigateTransformKeyframe_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            sender is not Button { Tag: string direction })
        {
            return;
        }

        int frameOffset = VideoTransformKeyframes.GetFrameOffset(selected.Clip, CurrentPlaybackSeconds, _frameRate);
        int? target = VideoTransformKeyframes.FindAdjacentFrame(selected.Clip, frameOffset, next: direction == "next");
        if (target is null)
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        MoveTimelinePlayhead(((selected.Clip.StartFrame - 1) + target.Value) / _frameRate);
    }

    private async void StabiliseVideo_Click(object sender, RoutedEventArgs e)
    {
        if (_isStabilisationRunning ||
            SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.Kind != TimelineTrackKind.Video ||
            selected.Track.IsLocked ||
            string.IsNullOrWhiteSpace(selected.Clip.SourcePath))
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        TimelineClipProject snapshot = VideoTimelineEditing.Clone(selected.Clip);
        string originalState = System.Text.Json.JsonSerializer.Serialize(snapshot);
        string clipId = selected.Clip.Id;
        _isStabilisationRunning = true;
        StabiliseVideoButton.IsEnabled = false;
        StatusText.Text = "Analysing camera motion and rolling-shutter skew…";
        try
        {
            var stabilised = await Task.Run(() =>
                (Transforms: VideoStabilisation.Analyse(snapshot, _frameRate),
                    Rolling: VideoRollingShutter.Analyse(snapshot, _frameRate)));

            if (SelectedTimelineClip is not TimelineClipLocation current ||
                current.Clip.Id != clipId ||
                System.Text.Json.JsonSerializer.Serialize(current.Clip) != originalState)
            {
                StatusText.Text = "Stabilisation was not applied because the selected clip changed during analysis.";
                return;
            }

            if (!TryBeginClipPropertyEdit(out TimelineClipLocation location))
            {
                return;
            }

            location.Clip.TransformKeyframes = stabilised.Transforms.ToList();
            location.Clip.RollingShutterProfile = stabilised.Rolling.ToList();
            ApplyClipPreview();
            CommitEdit();
            UpdateVideoInspector();
            StatusText.Text = $"Applied translation/rotation stabilisation and rolling-shutter correction with {stabilised.Transforms.Count} motion samples.";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or
            UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            StatusText.Text = $"Could not stabilise the selected clip: {ex.Message}";
        }
        finally
        {
            _isStabilisationRunning = false;
            UpdateVideoInspector();
        }
    }

    private void ClearRollingShutter_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.Kind != TimelineTrackKind.Video || selected.Track.IsLocked ||
            selected.Clip.RollingShutterProfile.Count == 0)
        {
            return;
        }
        PausePlayback();
        FinishActiveEdit();
        if (!TryBeginClipPropertyEdit(out TimelineClipLocation location))
        {
            return;
        }
        location.Clip.RollingShutterProfile.Clear();
        ApplyClipPreview();
        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = "Rolling-shutter row warp cleared; global transform stabilisation is unchanged.";
    }

    private void ClipMaskShape_Changed(object sender, SelectionChangedEventArgs e) =>
        ApplyClipMaskSettings();

    private void ClipMaskNumber_Changed(NumberBox sender, NumberBoxValueChangedEventArgs e) =>
        ApplyClipMaskSettings();

    private void ClipMaskFeather_Changed(NumberBox sender, NumberBoxValueChangedEventArgs e) =>
        ApplyClipMaskFeatherSettings(e.NewValue);

    private void ApplyClipMaskFeatherSettings(double feather)
    {
        if (_isInitialising || _isRestoringProject ||
            SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image) ||
            !double.IsFinite(feather) || feather is < 0 or > 0.25 ||
            !TryBeginClipPropertyEdit(out TimelineClipLocation location))
        {
            return;
        }

        int frameOffset = VideoMaskFeatherKeyframes.GetFrameOffset(
            location.Clip, CurrentPlaybackSeconds, _frameRate);
        double current = VideoMaskFeatherKeyframes.GetValue(location.Clip, frameOffset);
        if (current == feather)
        {
            CommitEdit();
            return;
        }
        if (!VideoMaskFeatherKeyframes.SetValueAtFrame(
            location.Clip, location.Track.Kind, frameOffset, _frameRate, feather))
        {
            CommitEdit();
            StatusText.Text = "Choose a rectangle or ellipse mask and move the playhead inside the clip to edit animated feather.";
            return;
        }

        ApplyClipPreview();
        CommitEdit();
        UpdateVideoInspector();
    }

    private void ClipMaskSettings_Changed(object sender, RoutedEventArgs e) =>
        ApplyClipMaskSettings();

    private void ApplyClipMaskSettings()
    {
        if (_isInitialising || _isRestoringProject ||
            SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image) ||
            ClipMaskShapeSelector.SelectedItem is not ComboBoxItem { Tag: string shapeTag } ||
            !Enum.TryParse(shapeTag, out VideoMaskShape shape) ||
            !double.IsFinite(ClipMaskXBox.Value) || ClipMaskXBox.Value is < 0 or > 1 ||
            !double.IsFinite(ClipMaskYBox.Value) || ClipMaskYBox.Value is < 0 or > 1 ||
            !double.IsFinite(ClipMaskWidthBox.Value) || ClipMaskWidthBox.Value is <= 0 or > 1 ||
            !double.IsFinite(ClipMaskHeightBox.Value) || ClipMaskHeightBox.Value is <= 0 or > 1 ||
            !double.IsFinite(ClipMaskFeatherBox.Value) || ClipMaskFeatherBox.Value is < 0 or > 0.25 ||
            !TryBeginClipPropertyEdit(out TimelineClipLocation location))
        {
            return;
        }

        TimelineClipProject clip = location.Clip;
        int currentFrameOffset = VideoTransformKeyframes.GetFrameOffset(clip, CurrentPlaybackSeconds, _frameRate);
        VideoMaskShape currentShape = VideoMaskShapeKeyframes.GetValue(clip, currentFrameOffset);
        VideoMaskValue currentMask = VideoClipMask.GetValue(clip, currentFrameOffset);
        var newMask = new VideoMaskValue(
            ClipMaskXBox.Value, ClipMaskYBox.Value, ClipMaskWidthBox.Value, ClipMaskHeightBox.Value);
        bool inverted = ClipMaskInvertedCheck.IsChecked == true;
        bool currentInverted = VideoMaskInversionKeyframes.GetValue(clip, currentFrameOffset);
        if (currentShape == shape && currentMask == newMask &&
            currentInverted == inverted)
        {
            CommitEdit();
            return;
        }

        if (clip.MaskShapeKeyframes.Count == 0)
        {
            clip.MaskShape = shape;
        }
        else if (currentShape != shape &&
            !VideoMaskShapeKeyframes.SetValueAtTime(
                clip, location.Track.Kind, CurrentPlaybackSeconds, _frameRate, shape))
        {
            CommitEdit();
            StatusText.Text = "Move the playhead inside the selected video or image clip to edit animated mask shape.";
            return;
        }
        if (clip.MaskInversionKeyframes.Count == 0)
        {
            clip.MaskInverted = inverted;
        }
        else if (currentInverted != inverted &&
            !VideoMaskInversionKeyframes.SetValueAtTime(
                clip, location.Track.Kind, CurrentPlaybackSeconds, _frameRate, inverted))
        {
            CommitEdit();
            StatusText.Text = "Move the playhead inside the selected video or image clip to edit animated mask inversion.";
            return;
        }
        if (shape != VideoMaskShape.None && clip.MaskKeyframes.Count > 0)
        {
            VideoClipMask.SetKeyframe(clip, location.Track.Kind, currentFrameOffset, _frameRate, newMask);
        }
        else
        {
            clip.MaskX = newMask.X;
            clip.MaskY = newMask.Y;
            clip.MaskWidth = newMask.Width;
            clip.MaskHeight = newMask.Height;
        }
        ApplyClipPreview();
        CommitEdit();
        UpdateVideoInspector();
    }

    private async void TrackMaskSubject_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not { Track.Kind: TimelineTrackKind.Video } selected ||
            selected.Track.IsLocked ||
            string.IsNullOrWhiteSpace(selected.Clip.SourcePath))
        {
            return;
        }

        TimelineClipProject snapshot = VideoTimelineEditing.Clone(selected.Clip);
        string clipId = snapshot.Id;
        string sourcePath = snapshot.SourcePath!;
        int startOffset = VideoTransformKeyframes.GetFrameOffset(
            snapshot, _playheadFramePosition / _frameRate, _frameRate);
        VideoMaskShape startShape = VideoMaskShapeKeyframes.GetValue(snapshot, startOffset);
        if (startShape == VideoMaskShape.None)
        {
            return;
        }
        VideoMaskValue initialMask = VideoClipMask.GetValue(snapshot, startOffset);
        double frameRate = _frameRate;
        _isMaskTrackingRunning = true;
        TrackMaskSubjectButton.IsEnabled = false;
        StatusText.Text = "Tracking the selected mask feature from the playhead…";
        try
        {
            IReadOnlyList<VideoMaskKeyframe> keys = await Task.Run(() =>
                VideoMaskTracking.Analyse(snapshot, frameRate, startOffset, initialMask));
            TimelineClipLocation? current = VideoTimelineEditing.FindClip(_videoTracks, clipId);
            if (current is null ||
                !string.Equals(current.Clip.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase) ||
                VideoMaskShapeKeyframes.GetValue(current.Clip, startOffset) != startShape)
            {
                StatusText.Text = "Tracking finished, but the selected clip or mask changed; no tracking keys were applied.";
                return;
            }

            FinishActiveEdit();
            BeginEdit();
            current.Clip.MaskKeyframes.RemoveAll(key => key.FrameOffset >= startOffset);
            current.Clip.MaskKeyframes.AddRange(keys);
            current.Clip.MaskKeyframes.Sort((left, right) => left.FrameOffset.CompareTo(right.FrameOffset));
            MarkEditChanged();
            CommitEdit();
            RefreshVideoTimeline();
            SeekToCurrentFrame();
            StatusText.Text = $"Tracked the mask across {keys.Count} samples. Review the keys and adjust any drift.";
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or InvalidDataException)
        {
            StatusText.Text = $"Could not track the mask: {ex.Message}";
        }
        finally
        {
            _isMaskTrackingRunning = false;
            UpdateVideoInspector();
        }
    }

    private void SetMaskKeyframe_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image) ||
            selected.Track.IsLocked)
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        int frameOffset = VideoTransformKeyframes.GetFrameOffset(selected.Clip, CurrentPlaybackSeconds, _frameRate);
        bool added = VideoClipMask.SetKeyframe(selected.Clip, selected.Track.Kind, frameOffset, _frameRate);
        if (added)
        {
            ApplyClipPreview();
        }

        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = added
            ? $"Mask keyframe set at clip frame {frameOffset + 1}."
            : "Choose a rectangle or ellipse mask and move the playhead inside the video or image clip.";
    }

    private void RemoveMaskKeyframe_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image) ||
            selected.Track.IsLocked)
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        int frameOffset = VideoTransformKeyframes.GetFrameOffset(selected.Clip, CurrentPlaybackSeconds, _frameRate);
        bool removed = VideoClipMask.TryRemoveKeyframe(selected.Clip, frameOffset);
        if (removed)
        {
            MarkEditChanged();
            CompositionSurface.Invalidate();
            SyncVideoPreview(CurrentPlaybackSeconds, forceSeek: false);
            InvalidateVideoTimeline();
        }

        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = removed ? "Mask keyframe removed." : "There is no mask keyframe at the playhead.";
    }

    private void SetMaskShapeKeyframe_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image) ||
            selected.Track.IsLocked)
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        int frameOffset = VideoMaskShapeKeyframes.GetFrameOffset(
            selected.Clip, CurrentPlaybackSeconds, _frameRate);
        bool added = VideoMaskShapeKeyframes.TrySetAtFrame(
            selected.Clip, selected.Track.Kind, frameOffset, _frameRate);
        if (added)
        {
            ApplyClipPreview();
        }

        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = added
            ? $"Mask shape keyframe set at clip frame {frameOffset + 1}."
            : "Move the playhead inside the selected video or image clip to add a mask shape keyframe.";
    }

    private void RemoveMaskShapeKeyframe_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image) ||
            selected.Track.IsLocked)
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        int frameOffset = VideoMaskShapeKeyframes.GetFrameOffset(
            selected.Clip, CurrentPlaybackSeconds, _frameRate);
        bool removed = VideoMaskShapeKeyframes.TryRemoveAtFrame(selected.Clip, frameOffset);
        if (removed)
        {
            ApplyClipPreview();
        }

        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = removed
            ? "Mask shape keyframe removed."
            : "There is no mask shape keyframe at the playhead.";
    }

    private void SetMaskInversionKeyframe_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image) ||
            selected.Track.IsLocked)
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        int frameOffset = VideoMaskInversionKeyframes.GetFrameOffset(
            selected.Clip, CurrentPlaybackSeconds, _frameRate);
        bool added = VideoMaskInversionKeyframes.TrySetAtFrame(
            selected.Clip, selected.Track.Kind, frameOffset, _frameRate);
        if (added)
        {
            ApplyClipPreview();
        }

        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = added
            ? $"Mask inversion keyframe set at clip frame {frameOffset + 1}."
            : "Move the playhead inside the selected video or image clip to add a mask inversion keyframe.";
    }

    private void RemoveMaskInversionKeyframe_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image) ||
            selected.Track.IsLocked)
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        int frameOffset = VideoMaskInversionKeyframes.GetFrameOffset(
            selected.Clip, CurrentPlaybackSeconds, _frameRate);
        bool removed = VideoMaskInversionKeyframes.TryRemoveAtFrame(selected.Clip, frameOffset);
        if (removed)
        {
            ApplyClipPreview();
        }

        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = removed
            ? "Mask inversion keyframe removed."
            : "There is no mask inversion keyframe at the playhead.";
    }

    private void SetMaskFeatherKeyframe_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image) ||
            selected.Track.IsLocked)
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        int frameOffset = VideoMaskFeatherKeyframes.GetFrameOffset(
            selected.Clip, CurrentPlaybackSeconds, _frameRate);
        bool added = VideoMaskFeatherKeyframes.SetKeyframe(
            selected.Clip, selected.Track.Kind, frameOffset, _frameRate);
        if (added)
        {
            ApplyClipPreview();
        }

        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = added
            ? $"Mask feather keyframe set at clip frame {frameOffset + 1}."
            : "Choose a rectangle or ellipse mask and move the playhead inside the clip.";
    }

    private void RemoveMaskFeatherKeyframe_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image) ||
            selected.Track.IsLocked)
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        int frameOffset = VideoMaskFeatherKeyframes.GetFrameOffset(
            selected.Clip, CurrentPlaybackSeconds, _frameRate);
        bool removed = VideoMaskFeatherKeyframes.TryRemoveAtFrame(selected.Clip, frameOffset);
        if (removed)
        {
            ApplyClipPreview();
        }

        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = removed
            ? "Mask feather keyframe removed."
            : "There is no mask feather keyframe at the playhead.";
    }

    private void ClipNumber_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!TryBeginClipPropertyEdit(out TimelineClipLocation location))
        {
            return;
        }

        TimelineClipProject clip = location.Clip;
        double value = args.NewValue;
        double current = sender == ClipFadeInBox ? clip.FadeInSeconds
            : sender == ClipFadeOutBox ? clip.FadeOutSeconds
            : sender == ClipDurationBox ? clip.DurationSeconds
            : clip.FontSize;
        if (!double.IsFinite(value) || Math.Abs(value - current) < 1e-9)
        {
            CommitEdit();
            UpdateVideoInspector();
            return;
        }

        if (sender == ClipFadeInBox)
        {
            clip.FadeInSeconds = Math.Max(0, value);
            VideoTimelineEditing.ClampFades(clip, location.Track.Kind);
        }
        else if (sender == ClipFadeOutBox)
        {
            clip.FadeOutSeconds = Math.Max(0, value);
            VideoTimelineEditing.ClampFades(clip, location.Track.Kind);
        }
        else if (sender == ClipDurationBox)
        {
            if (!VideoTimelineEditing.TrySetStillDuration(location.Track, clip, value, _frameRate))
            {
                StatusText.Text = "That duration would overlap the next clip on this track.";
                CommitEdit();
                UpdateVideoInspector();
                return;
            }
        }
        else if (sender == ClipFontSizeBox)
        {
            clip.FontSize = Math.Clamp(value, VideoTimelineEditing.MinimumFontSize, VideoTimelineEditing.MaximumFontSize);
        }

        ApplyClipPreview();
        CommitEdit();
        UpdateVideoInspector();
    }

    private void ClipPlaybackSpeed_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isInitialising || _isRestoringProject ||
            SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.Kind != TimelineTrackKind.Video ||
            !double.IsFinite(args.NewValue) || Math.Abs(args.NewValue - selected.Clip.PlaybackSpeed) < 1e-9 ||
            !TryBeginClipPropertyEdit(out TimelineClipLocation location))
        {
            return;
        }

        if (!VideoTimelineEditing.TrySetPlaybackSpeed(location.Track, location.Clip, args.NewValue, _frameRate))
        {
            CommitEdit();
            UpdateVideoInspector();
            StatusText.Text = "That playback speed would overlap another clip on this track.";
            return;
        }

        ApplyClipPreview();
        CommitEdit();
        UpdateVideoInspector();
    }

    private void ClipPlaybackOption_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitialising || _isRestoringProject ||
            SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.Kind != TimelineTrackKind.Video ||
            !TryBeginClipPropertyEdit(out TimelineClipLocation location))
        {
            return;
        }

        if (ReferenceEquals(sender, ClipReverseVideoCheck))
        {
            bool reversed = ClipReverseVideoCheck.IsChecked == true;
            if (!VideoTimelineEditing.TrySetReversed(location.Track, location.Clip, reversed, _frameRate))
            {
                CommitEdit();
                UpdateVideoInspector();
                return;
            }
        }
        else if (ReferenceEquals(sender, ClipReverseAudioCheck))
        {
            location.Clip.ReverseEmbeddedAudio = ClipReverseAudioCheck.IsChecked == true;
        }
        else
        {
            CommitEdit();
            return;
        }

        ApplyClipPreview();
        CommitEdit();
        UpdateVideoInspector();
    }

    private void ClipText_Changed(object sender, TextChangedEventArgs e)
    {
        string text = ClipTextBox.Text.Replace("\r", "\n", StringComparison.Ordinal);
        if (SelectedTimelineClip is not { } current || current.Clip.Text == text)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            if (!_isRestoringProject)
            {
                StatusText.Text = "Title text can't be empty.";
            }

            return;
        }

        if (!TryBeginClipPropertyEdit(out TimelineClipLocation location))
        {
            return;
        }

        location.Clip.Text = text;
        ApplyClipPreview();
    }

    private void ClipChoice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedItem: ComboBoxItem { Tag: string tag } } ||
            SelectedTimelineClip is not { } current)
        {
            return;
        }

        bool isFont = sender == ClipFontSelector;
        uint argb = 0;
        if (isFont
            ? string.Equals(current.Clip.FontFamily, tag, StringComparison.Ordinal)
            : !uint.TryParse(tag, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out argb) || argb == current.Clip.Colour)
        {
            return;
        }

        if (!TryBeginClipPropertyEdit(out TimelineClipLocation location))
        {
            return;
        }

        if (isFont)
        {
            location.Clip.FontFamily = tag;
        }
        else
        {
            location.Clip.Colour = argb;
        }

        ApplyClipPreview();
        CommitEdit();
    }

    private enum TimelineToolMode
    {
        Select,
        Blade,
        Trim,
        Ripple,
        Roll,
        Slide,
    }

    private enum TimelineDragOperation
    {
        Move,
        Trim,
        RippleTrim,
        Roll,
        Slide,
    }

    private sealed class TimelineDrag(TimelineClipProject clip, TimelineTrackProject track, double grabOffsetSeconds)
    {
        public TimelineClipProject Clip { get; } = clip;
        public TimelineTrackProject Track { get; set; } = track;
        public TimelineTrackProject OriginalTrack { get; private init; } = VideoTimelineEditing.Clone(track);
        public double GrabOffsetSeconds { get; } = grabOffsetSeconds;
        public TimelineDragOperation Operation { get; private init; }
        public TimelineTrimEdge? TrimEdge { get; private init; }
        public int LastChange { get; set; } = int.MinValue;
        public bool Started { get; set; }
        public bool Moved { get; set; }
        public bool Blocked { get; set; }

        public static TimelineDrag CreateOperation(
            TimelineClipProject clip, TimelineTrackProject track, TimelineDragOperation operation,
            TimelineTrimEdge? edge = null, double grabOffsetSeconds = 0) =>
            new(clip, track, grabOffsetSeconds)
            {
                Operation = operation,
                TrimEdge = edge,
                OriginalTrack = VideoTimelineEditing.Clone(track),
            };
    }

    private void TimelineToolMode_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string mode ||
            !Enum.TryParse(mode, ignoreCase: true, out TimelineToolMode selected))
        {
            return;
        }

        _timelineToolMode = selected;
        SelectTimelineToolButton.IsChecked = selected == TimelineToolMode.Select;
        BladeTimelineToolButton.IsChecked = selected == TimelineToolMode.Blade;
        TrimTimelineToolButton.IsChecked = selected == TimelineToolMode.Trim;
        RippleTimelineToolButton.IsChecked = selected == TimelineToolMode.Ripple;
        RollTimelineToolButton.IsChecked = selected == TimelineToolMode.Roll;
        SlideTimelineToolButton.IsChecked = selected == TimelineToolMode.Slide;
        VideoTimelineHint.Text = selected switch
        {
            TimelineToolMode.Select => "Select: drag clips to move. Click the ruler or empty lane to move the playhead.",
            TimelineToolMode.Blade => "Blade: click inside a clip to split it at that frame.",
            TimelineToolMode.Trim => "Trim: drag a selected clip's highlighted start or end handle.",
            TimelineToolMode.Ripple => "Ripple: drag either edge to trim and shift later clips on this track.",
            TimelineToolMode.Roll => "Roll: drag the cut between adjacent clips; sequence length stays fixed.",
            _ => "Slide: drag a clip between adjacent clips; sequence length stays fixed.",
        };
        InvalidateVideoTimeline();
        StatusText.Text = selected switch
        {
            TimelineToolMode.Select => "Select tool: click and drag clips to move them.",
            TimelineToolMode.Blade => "Blade tool: click inside a clip to split it at that frame.",
            _ => "Trim tool: drag a selected clip's highlighted start or end handle.",
        };
    }
}
