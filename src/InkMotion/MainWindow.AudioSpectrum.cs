using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;

namespace InkMotion;

public sealed partial class MainWindow
{
    private AudioSpectrumResult? _audioSpectrum;
    private CancellationTokenSource? _audioSpectrumCancellation;

    private void ClearAudioSpectrum()
    {
        _audioSpectrumCancellation?.Cancel();
        _audioSpectrumCancellation = null;
        _audioSpectrum = null;
        AudioSpectrumText.Text = "Analyse the start of the selected source range (up to 32768 samples).";
        AnalyseAudioSpectrumButton.IsEnabled = _audioRangeTrack is not null;
        AudioSpectrumSurface.Invalidate();
    }

    private async void AnalyseAudioSpectrum_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedAudioClip(out AudioTrackRuntime? track) || _audioSpectrumCancellation is not null)
        {
            return;
        }

        double start = AudioRangeStartBox.Value;
        double end = AudioRangeEndBox.Value;
        if (!double.IsFinite(start) || !double.IsFinite(end) || start != Math.Truncate(start) ||
            end != Math.Truncate(end) || start < 1 || end > int.MaxValue || start >= end)
        {
            StatusText.Text = "Select a non-empty range of whole timeline frames for spectrum analysis.";
            return;
        }

        AudioRangeParts parts;
        try
        {
            parts = AudioRangeEditing.Partition(track.Project, (int)start, (int)end, _frameRate, track.SourceDurationSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            StatusText.Text = "Select a source range inside the current audio clip.";
            return;
        }

        string path = parts.Range.AudioPath!;
        double sourceStart = parts.Range.SourceInSeconds;
        double duration = AudioClipEditing.GetClipEnd(sourceStart, parts.Range.SourceOutSeconds, track.SourceDurationSeconds) - sourceStart;
        using var cancellation = new CancellationTokenSource();
        _audioSpectrumCancellation = cancellation;
        AnalyseAudioSpectrumButton.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0 });
        AnalyseAudioSpectrumButton.IsEnabled = false;
        AudioSpectrumText.Text = "Analysing source audio...";
        try
        {
            AudioSpectrumResult result = await Task.Run(() => AudioSpectrum.Read(path, sourceStart, duration, cancellation.Token));
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            _audioSpectrum = result;
            string peak = result.Peak > 0 ? (20 * Math.Log10(result.Peak)).ToString("0.0") : "-inf";
            string rms = result.Rms > 0 ? (20 * Math.Log10(result.Rms)).ToString("0.0") : "-inf";
            string frequency = result.Peak > 1e-12 ? $"{result.DominantFrequencyHz:N1} Hz" : "silence";
            AudioSpectrumText.Text = $"{Path.GetFileName(path)} at {sourceStart:0.000} s | {result.Frames} samples at {result.SampleRate:N0} Hz\n" +
                $"Peak {peak} / RMS {rms} dBFS | strongest non-DC bin: {frequency}\nBin spacing {result.BinWidthHz:0.00} Hz; Hann window.";
            AudioSpectrumSurface.Invalidate();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Changing the selection discards the previous analysis.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            if (!cancellation.IsCancellationRequested)
            {
                AudioSpectrumText.Text = $"Spectrum analysis failed: {ex.Message}";
                StatusText.Text = "Source spectrum analysis failed.";
            }
        }
        finally
        {
            if (ReferenceEquals(_audioSpectrumCancellation, cancellation))
            {
                _audioSpectrumCancellation = null;
                AnalyseAudioSpectrumButton.IsEnabled = true;
            }
        }
    }

    private void AudioSpectrumSurface_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var session = args.DrawingSession;
        session.Clear(Colors.Black);
        if (_audioSpectrum is not { } spectrum)
        {
            return;
        }

        float width = (float)sender.ActualWidth;
        float height = (float)sender.ActualHeight;
        double maximum = spectrum.SampleRate / 2d;
        if (width < 1 || height < 1 || maximum <= 20)
        {
            return;
        }

        foreach (int decibels in new[] { -90, -60, -30, 0 })
        {
            float y = (float)(Math.Clamp(-decibels / 120d, 0, 1) * (height - 1));
            session.DrawLine(0, y, width, y, Colors.DimGray);
        }

        int columns = (int)Math.Ceiling(width);
        float? previousY = null;
        for (int x = 0; x < columns; x++)
        {
            double low = 20 * Math.Pow(maximum / 20, x / (double)columns);
            double high = 20 * Math.Pow(maximum / 20, (x + 1d) / columns);
            int first = Math.Clamp((int)Math.Floor(low / spectrum.BinWidthHz), 1, spectrum.Decibels.Length - 1);
            int last = Math.Clamp((int)Math.Ceiling(high / spectrum.BinWidthHz), first, spectrum.Decibels.Length - 1);
            double level = -120;
            for (int bin = first; bin <= last; bin++)
            {
                level = Math.Max(level, spectrum.Decibels[bin]);
            }

            float y = (float)(Math.Clamp(-level / 120, 0, 1) * (height - 1));
            if (previousY is float previous)
            {
                session.DrawLine(x - 1, previous, x, y, Colors.Cyan, 1);
            }

            previousY = y;
        }
    }
}
