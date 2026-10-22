using Microsoft.UI.Xaml;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace InkMotion;

public sealed partial class MainWindow
{
    private readonly MediaPlayer _sourceMonitorMediaPlayer = new() { AutoPlay = false, IsLoopingEnabled = false };
    private MediaSource? _sourceMonitorMediaSource;
    private StorageFile? _sourceMonitorFile;
    private double _sourceMonitorDurationSeconds;
    private double? _sourceMonitorInSeconds;
    private double? _sourceMonitorOutSeconds;

    private void InitialiseSourceMonitor()
    {
        SourceMonitorPlayer.SetMediaPlayer(_sourceMonitorMediaPlayer);
        _sourceMonitorMediaPlayer.MediaFailed += SourceMonitorMediaPlayer_MediaFailed;
    }

    private async void LoadSourceMonitor_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        foreach (string extension in VideoFileTypes.SupportedExtensions)
        {
            picker.FileTypeFilter.Add(extension);
        }
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        double duration = await ProbeVideoDurationAsync(file);
        if (duration <= 0)
        {
            SourceMonitorStatus.Text = $"Could not read the length of {file.Name}; the format may not be supported.";
            return;
        }

        MediaSource? source = null;
        try
        {
            _sourceMonitorMediaPlayer.Pause();
            source = MediaSource.CreateFromStorageFile(file);
            _sourceMonitorMediaPlayer.Source = source;
            _sourceMonitorMediaSource?.Dispose();
            _sourceMonitorMediaSource = source;
            _sourceMonitorFile = file;
            _sourceMonitorDurationSeconds = duration;
            _sourceMonitorInSeconds = null;
            _sourceMonitorOutSeconds = null;
            SourceMonitorStatus.Text = $"Loaded {file.Name} · {FormatSeconds(duration)}. Use the player's controls to find an edit, then mark In and Out.";
            UpdateSourceMonitorControls();
        }
        catch (Exception ex) when (IsFileAccessException(ex) || ex is InvalidOperationException or
            NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            source?.Dispose();
            SourceMonitorStatus.Text = $"Could not load source media: {ex.Message}";
        }
    }

    private void SourceMonitorMark_Click(object sender, RoutedEventArgs e)
    {
        if (_sourceMonitorFile is null)
        {
            return;
        }

        double position = Math.Clamp(_sourceMonitorMediaPlayer.Position.TotalSeconds, 0, _sourceMonitorDurationSeconds);
        if ((sender as FrameworkElement)?.Tag as string == "in")
        {
            _sourceMonitorInSeconds = position;
        }
        else
        {
            _sourceMonitorOutSeconds = position;
        }

        UpdateSourceMonitorControls();
    }

    private void ClearSourceMonitorMarks_Click(object sender, RoutedEventArgs e)
    {
        _sourceMonitorInSeconds = null;
        _sourceMonitorOutSeconds = null;
        UpdateSourceMonitorControls();
    }

    private void AddMarkedVideoClip_Click(object sender, RoutedEventArgs e)
    {
        if (_sourceMonitorFile is null ||
            !VideoSourceRangeMarks.TryCreateClip(
                _sourceMonitorFile.Path, _sourceMonitorDurationSeconds, PlayheadFrameNumber, _frameRate,
                _sourceMonitorInSeconds ?? double.NaN, _sourceMonitorOutSeconds ?? double.NaN, out TimelineClipProject? clip) ||
            clip is null)
        {
            SourceMonitorStatus.Text = "Set valid In and Out marks at least one project frame apart and within the source duration.";
            return;
        }

        RegisterTimelineSource(_sourceMonitorFile);
        AddTimelineClip(TimelineTrackKind.Video, clip,
            $"Added marked range · {Path.GetFileName(_sourceMonitorFile.Path)} · " +
            $"{FormatSeconds(clip.SourceInSeconds)}–{FormatSeconds(clip.SourceOutSeconds!.Value)}");
    }

    private void UpdateSourceMonitorControls()
    {
        bool loaded = _sourceMonitorFile is not null;
        SourceMonitorMarkInButton.IsEnabled = loaded;
        SourceMonitorMarkOutButton.IsEnabled = loaded;
        ClearSourceMonitorMarksButton.IsEnabled = _sourceMonitorInSeconds is not null || _sourceMonitorOutSeconds is not null;
        AddMarkedVideoClipButton.IsEnabled = loaded && VideoSourceRangeMarks.TryCreateClip(
            _sourceMonitorFile!.Path, _sourceMonitorDurationSeconds, PlayheadFrameNumber, _frameRate,
            _sourceMonitorInSeconds ?? double.NaN, _sourceMonitorOutSeconds ?? double.NaN, out _);
        SourceMonitorMarksText.Text =
            $"In: {FormatMark(_sourceMonitorInSeconds)}   Out: {FormatMark(_sourceMonitorOutSeconds)}";
    }

    private static string FormatMark(double? seconds) =>
        seconds is double value ? FormatSeconds(value) : "not set";

    private void SourceMonitorMediaPlayer_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() =>
            SourceMonitorStatus.Text = $"Source preview failed: {args.ErrorMessage}");
    }

    private void DisposeSourceMonitor()
    {
        _sourceMonitorMediaPlayer.MediaFailed -= SourceMonitorMediaPlayer_MediaFailed;
        _sourceMonitorMediaPlayer.Pause();
        _sourceMonitorMediaPlayer.Source = null;
        _sourceMonitorMediaSource?.Dispose();
        _sourceMonitorMediaSource = null;
        _sourceMonitorMediaPlayer.Dispose();
    }
}
