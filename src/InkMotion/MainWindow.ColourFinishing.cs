using Microsoft.UI.Xaml;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;

namespace InkMotion;

public sealed partial class MainWindow
{
    private VideoShotStatistics? _shotReference;

    private async Task<VideoShotStatistics> SampleSelectedShotAsync(TimelineClipLocation location, bool includeMatch)
    {
        if (_projectColour is not null)
            throw new InvalidOperationException("Shot matching is available only in SDR projects.");
        string path = location.Clip.SourcePath ?? throw new InvalidOperationException("The shot has no media source.");
        double seconds = CurrentPlaybackSeconds;
        if (seconds < VideoTimelineEditing.GetStartSeconds(location.Clip, _frameRate) ||
            seconds >= VideoTimelineEditing.GetEndSeconds(location.Clip, location.Track.Kind, _frameRate))
            throw new InvalidOperationException("Move the playhead inside the selected shot.");
        using var sample = new CanvasRenderTarget(CompositionSurface.Device, 128, 72, 96);
        using var grading = new VideoColourGradingEffect();
        void Draw(CanvasBitmap bitmap, Rect bounds)
        {
            using var drawing = sample.CreateDrawingSession();
            drawing.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            drawing.DrawImage(grading.GetOutput(bitmap, location.Clip,
                VideoColourGradingKeyframes.GetValueAtTime(location.Clip, seconds, _frameRate), includeMatch),
                new Rect(0, 0, 128, 72), bounds);
        }
        if (location.Track.Kind == TimelineTrackKind.Image)
        {
            using var bitmap = await CanvasBitmap.LoadAsync(CompositionSurface.Device, path);
            Draw(bitmap, bitmap.Bounds);
        }
        else
        {
            using var source = new TimelineVideoFrameSource(path);
            CanvasBitmap bitmap = source.GetFrame(CompositionSurface.Device,
                VideoTimelineEditing.GetSourceSeconds(location.Clip, seconds, _frameRate), location.Clip.RollingShutterProfile) ??
                throw new InvalidOperationException("The selected video frame could not be decoded.");
            var (x, y, width, height) = source.Aperture;
            Draw(bitmap, new Rect(x, y, width, height));
        }
        return VideoShotStatistics.Analyse(sample.GetPixelBytes());
    }

    private async void CaptureShotReference_Click(object sender, RoutedEventArgs e) =>
        await ProcessShotMatchAsync(capture: true);

    private async void MatchShot_Click(object sender, RoutedEventArgs e) =>
        await ProcessShotMatchAsync(capture: false);

    private async Task ProcessShotMatchAsync(bool capture)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image) ||
            (!capture && selected.Track.IsLocked)) return;
        if (!capture && _shotReference is null)
        {
            StatusText.Text = "Capture a reference shot first.";
            return;
        }
        PausePlayback();
        FinishActiveEdit();
        string before = EditorProjectSerialiser.Serialise(CreateProjectSnapshot());
        double seconds = CurrentPlaybackSeconds;
        VideoShotStatistics? reference = _shotReference;
        try
        {
            VideoShotStatistics statistics = await SampleSelectedShotAsync(selected, includeMatch: capture);
            if (!CanApplyLanBackgroundEdit()) return;
            if (before != EditorProjectSerialiser.Serialise(CreateProjectSnapshot()) ||
                SelectedTimelineClip?.Clip.Id != selected.Clip.Id || CurrentPlaybackSeconds != seconds)
                throw new InvalidOperationException("The project, shot or playhead changed. Try again.");
            if (capture)
            {
                _shotReference = statistics;
                ShotReferenceText.Text = $"Reference: {Path.GetFileName(selected.Clip.SourcePath)} at {seconds:0.###} s.";
                StatusText.Text = "Captured the selected shot's graded RGB statistics.";
                return;
            }
            if (!TryBeginClipPropertyEdit(out TimelineClipLocation location)) return;
            location.Clip.ShotMatch = statistics.MatchTo(reference!);
            ApplyClipPreview();
            CommitEdit();
            UpdateVideoInspector();
            StatusText.Text = "Applied a static RGB mean/contrast match to the selected shot.";
        }
        catch (Exception ex) when (IsFileAccessException(ex) || ex is InvalidOperationException or ArgumentException)
        {
            StatusText.Text = $"Could not process shot matching: {ex.Message}";
        }
    }

    private void ClearShotMatch_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected || selected.Clip.ShotMatch.IsNeutral ||
            !TryBeginClipPropertyEdit(out TimelineClipLocation location)) return;
        location.Clip.ShotMatch = new();
        ApplyClipPreview();
        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = "Cleared the selected shot's match.";
    }

    private void UpdateHslQualifierControls(VideoHslQualifier value)
    {
        QualifierEnabled.IsChecked = value.Enabled;
        QualifierHue.Value = value.Hue;
        QualifierWidth.Value = value.HueWidth;
        QualifierSaturationMin.Value = value.MinimumSaturation;
        QualifierSaturationMax.Value = value.MaximumSaturation;
        QualifierLightnessMin.Value = value.MinimumLightness;
        QualifierLightnessMax.Value = value.MaximumLightness;
        QualifierSoftness.Value = value.Softness;
        QualifierHueShift.Value = value.HueShift;
        QualifierSaturation.Value = value.Saturation;
        QualifierLightness.Value = value.Lightness;
    }

    private void ApplyHslQualifier_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimelineClip is not TimelineClipLocation selected ||
            selected.Track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image) ||
            selected.Track.IsLocked) return;
        var value = new VideoHslQualifier
        {
            Enabled = QualifierEnabled.IsChecked == true,
            Hue = QualifierHue.Value,
            HueWidth = QualifierWidth.Value,
            MinimumSaturation = QualifierSaturationMin.Value,
            MaximumSaturation = QualifierSaturationMax.Value,
            MinimumLightness = QualifierLightnessMin.Value,
            MaximumLightness = QualifierLightnessMax.Value,
            Softness = QualifierSoftness.Value,
            HueShift = QualifierHueShift.Value,
            Saturation = QualifierSaturation.Value,
            Lightness = QualifierLightness.Value,
        };
        if (!value.IsValid)
        {
            StatusText.Text = "Enter finite HSL values within the displayed ranges; each minimum must not exceed its maximum.";
            return;
        }
        if (_projectColour is not null && value.Enabled)
        {
            StatusText.Text = "HSL qualification is available only in SDR projects. Disable it before using HDR.";
            return;
        }
        FinishActiveEdit();
        if (!TryBeginClipPropertyEdit(out TimelineClipLocation location)) return;
        location.Clip.HslQualifier = value;
        ApplyClipPreview();
        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = "Applied the clip-wide HSL qualifier.";
    }
}
