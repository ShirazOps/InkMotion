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
    private const float TimelineRulerHeight = 22;
    private const float TimelineLaneHeight = 34;
    private const float TimelineLaneSpacing = 2;
    private const double PreviewDriftToleranceSeconds = 0.2;
    private readonly List<TimelineTrackProject> _videoTracks = [];
    private readonly Dictionary<string, StorageFile> _timelineSourceFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unavailableTimelineSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CanvasBitmap> _overlayBitmaps = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _overlayBitmapRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly MediaPlayer _previewMediaPlayer = new() { AutoPlay = false, IsLoopingEnabled = false };
    private MediaSource? _previewMediaSource;
    private string? _previewSourcePath;
    private bool _previewReady;
    private string? _selectedTimelineTrackId;
    private string? _selectedTimelineClipId;
    private double _timelinePixelsPerSecond = 60;
    private TimelineDrag? _timelineDrag;
    private bool _isScrubbingTimeline;
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
        _previewMediaPlayer.MediaOpened += PreviewMediaPlayer_MediaOpened;
        _previewMediaPlayer.MediaFailed += PreviewMediaPlayer_MediaFailed;
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
        StorageFile? file = null;
        if (active?.Clip.SourcePath is string path && !_unavailableTimelineSources.Contains(path))
        {
            _timelineSourceFiles.TryGetValue(path, out file);
        }

        if (active is null || file is null)
        {
            ClearPreviewSource();
            return;
        }

        if (!string.Equals(_previewSourcePath, file.Path, StringComparison.OrdinalIgnoreCase))
        {
            _previewMediaPlayer.Pause();
            MediaSource? previous = _previewMediaSource;
            _previewSourcePath = file.Path;
            _previewReady = false;
            _previewMediaSource = MediaSource.CreateFromStorageFile(file);
            _previewMediaPlayer.Source = _previewMediaSource;
            previous?.Dispose();
            PreviewPlayer.Opacity = 0;
            UpdatePreviewEmptyState();
            return;
        }

        if (!_previewReady)
        {
            return;
        }

        TimelineClipProject clip = active.Clip;
        _previewMediaPlayer.Volume = IsOriginalPreview ? 1 : clip.Volume;
        _previewMediaPlayer.IsMuted = !IsOriginalPreview && (active.Track.IsMuted || HasSoloAudio);
        PreviewPlayer.Opacity = IsOriginalPreview ? 1 : VideoTimelineEditing.GetOpacity(clip, TimelineTrackKind.Video, seconds, _frameRate);
        MediaPlaybackSession session = _previewMediaPlayer.PlaybackSession;
        double sourceSeconds = VideoTimelineEditing.GetSourceSeconds(clip, seconds, _frameRate);
        double tolerance = forceSeek || !_isPlaying ? 0.001 : PreviewDriftToleranceSeconds;
        if (Math.Abs(session.Position.TotalSeconds - sourceSeconds) > tolerance)
        {
            session.Position = TimeSpan.FromSeconds(sourceSeconds);
        }

        if (_isPlaying)
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
    }

    private void ClearPreviewSource()
    {
        if (_previewMediaPlayer.Source is not null)
        {
            _previewMediaPlayer.Pause();
            _previewMediaPlayer.Source = null;
        }

        _previewMediaSource?.Dispose();
        _previewMediaSource = null;
        _previewSourcePath = null;
        _previewReady = false;
        PreviewPlayer.Opacity = 1;
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

        foreach (TimelineClipLocation overlay in VideoTimelineEditing.GetVisibleOverlays(_videoTracks, seconds, _frameRate))
        {
            float opacity = (float)VideoTimelineEditing.GetOpacity(overlay.Clip, overlay.Track.Kind, seconds, _frameRate);
            if (opacity <= 0)
            {
                continue;
            }

            if (overlay.Track.Kind == TimelineTrackKind.Image)
            {
                DrawImageOverlay(sender, args.DrawingSession, overlay.Clip, width, height, opacity);
            }
            else
            {
                TimelineCompositor.DrawTitle(args.DrawingSession, overlay.Clip, width, height, opacity);
            }
        }
    }

    private void DrawImageOverlay(
        CanvasControl canvas,
        CanvasDrawingSession session,
        TimelineClipProject clip,
        float width,
        float height,
        float opacity)
    {
        if (clip.SourcePath is not string path)
        {
            return;
        }

        if (!_overlayBitmaps.TryGetValue(path, out CanvasBitmap? bitmap))
        {
            RequestOverlayBitmap(canvas, path);
            return;
        }

        TimelineCompositor.DrawFitted(session, bitmap, bitmap.Bounds, width, height, clip.Scale, clip.X, clip.Y, opacity);
    }

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

    private void DisposeVideoTimeline()
    {
        ClearPreviewSource();
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

        if (clip.Id == _selectedTimelineClipId)
        {
            session.DrawRoundedRectangle(bounds, 3, 3, Color.FromArgb(255, 0xF2, 0xF4, 0xF8), 2);
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
        if (!track.IsLocked)
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
            int previousStart = drag.Clip.StartFrame;
            drag.Clip.StartFrame = startFrame;
            moved = VideoTimelineEditing.TryMoveToTrack(drag.Track, target, drag.Clip, _frameRate);
            if (moved)
            {
                drag.Track = target;
                _selectedTimelineTrackId = target.Id;
                RefreshVideoTrackHeaders();
            }
            else
            {
                drag.Clip.StartFrame = previousStart;
            }
        }

        if (!moved)
        {
            moved = VideoTimelineEditing.TryMove(drag.Track, drag.Clip, startFrame, _frameRate);
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
            CompositionSurface.Invalidate();
            UpdateVideoInspector();
        }

        StatusText.Text = drag.Blocked
            ? "Clips can't overlap on a track; the clip stopped at the last free position."
            : $"Moved clip to {FormatTimelineTime(VideoTimelineEditing.GetStartSeconds(drag.Clip, _frameRate), true)} on {drag.Track.Name}.";
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
        location => VideoTimelineEditing.Split(location.Track, location.Clip, PlayheadFrameNumber, _frameRate) is not null,
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

    private void MoveClipToPlayhead_Click(object sender, RoutedEventArgs e) => ApplyClipAction(
        location => VideoTimelineEditing.TryMove(location.Track, location.Clip, PlayheadFrameNumber, _frameRate),
        "Moved the clip to the playhead.",
        "The clip would overlap another clip at the playhead.");

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
        VideoSlipActions.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
        VideoSourceInBox.Value = clip.SourceInSeconds;
        VideoSourceOutBox.Value = clip.SourceOutSeconds ?? clip.SourceDurationSeconds;
        RelinkTimelineClipButton.Visibility = isTitle ? Visibility.Collapsed : Visibility.Visible;
        RelinkMatchingSourcesButton.Visibility = RelinkTimelineClipButton.Visibility;
        ClipSourceRangeText.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
        ClipSourceRangeText.Text = isVideo
            ? $"Source {FormatSeconds(clip.SourceInSeconds)} - {FormatSeconds(clip.SourceOutSeconds ?? clip.SourceDurationSeconds)}"
            : string.Empty;
        ClipVolumeSlider.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
        ClipDurationBox.Visibility = isVideo ? Visibility.Collapsed : Visibility.Visible;
        ClipTextBox.Visibility = isTitle ? Visibility.Visible : Visibility.Collapsed;
        ClipFontSelector.Visibility = isTitle ? Visibility.Visible : Visibility.Collapsed;
        ((FrameworkElement)ClipFontSizeBox.Parent).Visibility = isTitle ? Visibility.Visible : Visibility.Collapsed;
        ClipScaleSlider.Visibility = isImage ? Visibility.Visible : Visibility.Collapsed;
        ClipXSlider.Visibility = isVideo ? Visibility.Collapsed : Visibility.Visible;
        ClipYSlider.Visibility = isVideo ? Visibility.Collapsed : Visibility.Visible;

        ClipOpacitySlider.Value = clip.Opacity;
        ClipVolumeSlider.Value = clip.Volume;
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
        ClipScaleSlider.Value = clip.Scale;
        ClipXSlider.Value = clip.X;
        ClipYSlider.Value = clip.Y;
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
        if (sender == ClipOpacitySlider)
        {
            clip.Opacity = Math.Clamp(e.NewValue, 0, 1);
        }
        else if (sender == ClipVolumeSlider)
        {
            clip.Volume = Math.Clamp(e.NewValue, 0, 1);
        }
        else if (sender == ClipScaleSlider)
        {
            clip.Scale = Math.Clamp(e.NewValue, VideoTimelineEditing.MinimumScale, VideoTimelineEditing.MaximumScale);
        }
        else if (sender == ClipXSlider)
        {
            clip.X = Math.Clamp(e.NewValue, 0, 1);
        }
        else if (sender == ClipYSlider)
        {
            clip.Y = Math.Clamp(e.NewValue, 0, 1);
        }

        ApplyClipPreview();
    }

    private void ClipProperty_LostFocus(object sender, RoutedEventArgs e)
    {
        FinishActiveEdit();
        UpdateVideoInspector();
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

    private sealed class TimelineDrag(TimelineClipProject clip, TimelineTrackProject track, double grabOffsetSeconds)
    {
        public TimelineClipProject Clip { get; } = clip;
        public TimelineTrackProject Track { get; set; } = track;
        public double GrabOffsetSeconds { get; } = grabOffsetSeconds;
        public bool Started { get; set; }
        public bool Moved { get; set; }
        public bool Blocked { get; set; }
    }
}
