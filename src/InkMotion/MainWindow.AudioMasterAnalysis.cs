using Microsoft.UI.Xaml;

namespace InkMotion;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _audioMasterCancellation;
    private bool _audioMasterClosed;

    private void ClearAudioMasterAnalysis()
    {
        _audioMasterCancellation?.Cancel();
        _audioMasterCancellation = null;
        if (_audioMasterClosed || AudioMasterText is null)
        {
            return;
        }

        AudioMasterText.Text = "Analyse the complete export mix to measure summed sample peaks and RMS.";
        AudioMasterPeakProgress.Value = 0;
        AudioMasterScanProgress.Visibility = Visibility.Collapsed;
        AnalyseAudioMasterButton.IsEnabled = true;
        CancelAudioMasterButton.IsEnabled = false;
    }

    private void CancelAudioMaster_Click(object sender, RoutedEventArgs e) => ClearAudioMasterAnalysis();

    private async void AnalyseAudioMaster_Click(object sender, RoutedEventArgs e)
    {
        if (_audioMasterCancellation is not null)
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        EditorProject snapshot = CreateProjectSnapshot();
        using var cancellation = new CancellationTokenSource();
        _audioMasterCancellation = cancellation;
        AnalyseAudioMasterButton.IsEnabled = false;
        CancelAudioMasterButton.IsEnabled = true;
        AudioMasterPeakProgress.Value = 0;
        AudioMasterScanProgress.Value = 0;
        AudioMasterScanProgress.Visibility = Visibility.Visible;
        AudioMasterText.Text = "Analysing the complete export mix...";
        var progress = new Progress<double>(value =>
        {
            if (!_audioMasterClosed && ReferenceEquals(_audioMasterCancellation, cancellation))
            {
                AudioMasterScanProgress.Value = value * 100;
            }
        });
        try
        {
            AudioMasterAnalysisResult result = await Task.Run(
                () => AudioMasterAnalysis.Analyse(snapshot, cancellation.Token, progress));
            if (_audioMasterClosed || cancellation.IsCancellationRequested ||
                !ReferenceEquals(_audioMasterCancellation, cancellation))
            {
                return;
            }

            AudioChannelLayoutInfo layout = AudioChannelLayouts.Get(result.Layout);
            AudioMasterPeakProgress.Value = Math.Clamp((result.Overall.PeakDecibels + 60) / 60 * 100, 0, 100);
            string[] channels = result.Channels.Select((reading, index) =>
                $"{SpeakerAbbreviation(layout.Speakers[index])}: peak {MeterDecibels(reading.PeakDecibels)} / " +
                $"RMS {MeterDecibels(reading.RmsDecibels)} dBFS" + (reading.Clipped ? " - CLIP" : string.Empty)).ToArray();
            AudioMasterText.Text =
                $"Export mix snapshot: {layout.DisplayName}, {result.SampleFrames:N0} frames at {result.SampleRate:N0} Hz\n" +
                $"Peak {MeterDecibels(result.Overall.PeakDecibels)} / RMS {MeterDecibels(result.Overall.RmsDecibels)} dBFS" +
                (result.Overall.Clipped ? " - CLIP (held)" : string.Empty) + "\n" +
                $"Integrated loudness {FormatIntegratedLufs(result.IntegratedLufs)}\n" +
                string.Join("\n", channels) +
                (result.Warnings.Count > 0 ? "\nWarnings:\n" + string.Join("\n", result.Warnings) : string.Empty);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Cancelled or invalidated snapshots must not replace the current result.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or System.Runtime.InteropServices.COMException or NotSupportedException or OverflowException)
        {
            if (!_audioMasterClosed && !cancellation.IsCancellationRequested &&
                ReferenceEquals(_audioMasterCancellation, cancellation))
            {
                AudioMasterText.Text = $"Master mix analysis failed: {ex.Message}";
                StatusText.Text = "Master mix analysis failed.";
            }
        }
        finally
        {
            if (!_audioMasterClosed && ReferenceEquals(_audioMasterCancellation, cancellation))
            {
                _audioMasterCancellation = null;
                AnalyseAudioMasterButton.IsEnabled = true;
                CancelAudioMasterButton.IsEnabled = true;
                AudioMasterScanProgress.Visibility = Visibility.Collapsed;
            }
        }
    }

    private static string FormatIntegratedLufs(double? lufs) =>
        lufs is double value && double.IsFinite(value) ? $"{value:F1} LUFS" : "below gate / insufficient duration";
}
