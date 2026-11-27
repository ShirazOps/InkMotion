using Microsoft.UI.Xaml;

namespace InkMotion;

public sealed partial class MainWindow
{
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
        FinishActiveEdit();
        if (!TryBeginClipPropertyEdit(out TimelineClipLocation location)) return;
        location.Clip.HslQualifier = value;
        ApplyClipPreview();
        CommitEdit();
        UpdateVideoInspector();
        StatusText.Text = "Applied the clip-wide HSL qualifier.";
    }
}
