using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InkMotion;

public sealed partial class MainWindow
{
    private AudioSpectrumResult? _audioSpectrum;
    private AudioSpectrogramResult? _audioSpectrogram;
    private CancellationTokenSource? _audioSpectrumCancellation;

    private void ClearAudioAnalysis_Click(object sender, RoutedEventArgs e) => ClearAudioSpectrum();

    private void ClearAudioSpectrum()
    {
        _audioSpectrumCancellation?.Cancel();
        _audioSpectrumCancellation = null;
        _audioSpectrum = null;
        _audioSpectrogram = null;
        AudioSpectrumText.Text = "Take a short source snapshot or average every sample in the selected source range.";
        AnalyseAudioSpectrumButton.IsEnabled = _audioRangeTrack is not null;
        AverageAudioSpectrumButton.IsEnabled = _audioRangeTrack is not null;
        AnalyseAudioSpectrogramButton.IsEnabled = _audioRangeTrack is not null;
        AudioSpectrumSurface.Invalidate();
        AudioSpectrogramSurface.Invalidate();
        AudioSpectrogramText.Text = "Analyse the selected source range to show frequency content over time.";
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
        bool average = sender is Button { Tag: "average" };
        bool spectrogram = sender is Button { Tag: "spectrogram" };
        double sourceStart = parts.Range.SourceInSeconds;
        double duration = AudioClipEditing.GetClipEnd(sourceStart, parts.Range.SourceOutSeconds, track.SourceDurationSeconds) - sourceStart;
        using var cancellation = new CancellationTokenSource();
        _audioSpectrumCancellation = cancellation;
        AnalyseAudioSpectrumButton.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0 });
        AnalyseAudioSpectrumButton.IsEnabled = false;
        AverageAudioSpectrumButton.IsEnabled = false;
        AnalyseAudioSpectrogramButton.IsEnabled = false;
        if (!spectrogram)
        {
            AudioSpectrumText.Text = "Analysing source audio...";
        }
        try
        {
            if (spectrogram)
            {
                _audioSpectrogram = null;
                AudioSpectrogramSurface.Invalidate();
                AudioSpectrogramText.Text = "Analysing source spectrogram...";
                AudioSpectrogramResult image = await Task.Run(
                    () => AudioSpectrogram.Read(path, sourceStart, duration, cancellation.Token));
                if (!cancellation.IsCancellationRequested)
                {
                    _audioSpectrogram = image;
                    AudioSpectrogramText.Text = $"Source {image.FirstSampleFrame / (double)image.SampleRate:0.000} to " +
                        $"{(image.FirstSampleFrame + image.SampleFrames) / (double)image.SampleRate:0.000} s | " +
                        $"{image.SampleFrames:N0} sample frames | {image.Decibels.Length} time columns\n" +
                        $"{image.FftFrames}-frame Hann FFT | {image.BinWidthHz:0.00} Hz bins | " +
                        "channel powers averaged; time columns average consecutive window powers.";
                    AudioSpectrogramSurface.Invalidate();
                }
                return;
            }

            AudioSpectrumResult result = await Task.Run(() => average
                ? AudioSpectrum.ReadAverage(path, sourceStart, duration, cancellation.Token)
                : AudioSpectrum.Read(path, sourceStart, duration, cancellation.Token));
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            _audioSpectrum = result;
            string peak = result.Peak > 0 ? (20 * Math.Log10(result.Peak)).ToString("0.0") : "-inf";
            string rms = result.Rms > 0 ? (20 * Math.Log10(result.Rms)).ToString("0.0") : "-inf";
            string frequency = result.Peak > 1e-12 ? $"{result.DominantFrequencyHz:N1} Hz" : "silence";
            AudioSpectrumText.Text = $"{Path.GetFileName(path)} at {sourceStart:0.000} s | {result.AnalysedFrames:N0} sample frames at {result.SampleRate:N0} Hz\n" +
                $"Peak {peak} / RMS {rms} dBFS | strongest non-DC bin: {frequency}\n" +
                $"Bin spacing {result.BinWidthHz:0.00} Hz; {result.Windows:N0} Hann window(s)" +
                (average ? "; frame-weighted power average, final partial window zero-padded." : "; source snapshot.");
            AudioSpectrumSurface.Invalidate();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Changing the selection discards the previous analysis.
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or NotSupportedException or FormatException or System.Runtime.InteropServices.COMException)
        {
            if (!cancellation.IsCancellationRequested)
            {
                if (spectrogram)
                {
                    AudioSpectrogramText.Text = $"Spectrogram analysis failed: {ex.Message}";
                }
                else
                {
                    AudioSpectrumText.Text = $"Spectrum analysis failed: {ex.Message}";
                }
                StatusText.Text = spectrogram ? "Source spectrogram analysis failed." : "Source spectrum analysis failed.";
            }
        }
        finally
        {
            if (ReferenceEquals(_audioSpectrumCancellation, cancellation))
            {
                _audioSpectrumCancellation = null;
                AnalyseAudioSpectrumButton.IsEnabled = true;
                AverageAudioSpectrumButton.IsEnabled = true;
                AnalyseAudioSpectrogramButton.IsEnabled = true;
            }
        }
    }

    private void AudioSpectrogramSurface_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var session = args.DrawingSession;
        session.Clear(Colors.Black);
        if (_audioSpectrogram is not { } image || sender.ActualWidth < 1 || sender.ActualHeight < 1)
        {
            return;
        }

        float width = (float)sender.ActualWidth;
        float height = (float)sender.ActualHeight;
        int rows = Math.Min(256, (int)Math.Ceiling(height));
        double nyquist = image.SampleRate / 2d;
        if (nyquist <= 20)
        {
            return;
        }

        long position = 0;
        for (int column = 0; column < image.Decibels.Length; column++)
        {
            float left = (float)(position / (double)image.SampleFrames * width);
            position += image.ColumnFrames[column];
            float right = (float)(position / (double)image.SampleFrames * width);
            for (int row = 0; row < rows; row++)
            {
                double high = 20 * Math.Pow(nyquist / 20, 1 - row / (double)rows);
                double low = 20 * Math.Pow(nyquist / 20, 1 - (row + 1d) / rows);
                int first = Math.Clamp((int)Math.Floor(low / image.BinWidthHz), 1, image.Decibels[column].Length - 1);
                int last = Math.Clamp((int)Math.Ceiling(high / image.BinWidthHz), first, image.Decibels[column].Length - 1);
                float level = -120;
                for (int bin = first; bin <= last; bin++)
                {
                    level = Math.Max(level, image.Decibels[column][bin]);
                }

                double intensity = Math.Clamp((level + 120) / 120, 0, 1);
                var colour = Windows.UI.Color.FromArgb(255,
                    (byte)(255 * Math.Max(0, (intensity - 0.5) * 2)),
                    (byte)(255 * Math.Min(1, intensity * 2)),
                    (byte)(255 * Math.Min(1, intensity * 2)));
                session.FillRectangle(left, row * height / rows, right - left,
                    height / rows + 0.5f, colour);
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
