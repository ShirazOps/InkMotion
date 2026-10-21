using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace InkMotion;

public sealed partial class MainWindow
{
    private AudioSampleWaveformResult? _sampleWaveform;
    private CancellationTokenSource? _sampleWaveformCancellation;
    private bool _sampleWaveformClosed;
    private uint? _sampleWaveformPointer;
    private double _sampleWaveformDragStart;

    private void AudioSourceSampleSelection_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_isInitialising)
        {
            InvalidateAudioWaveforms();
            AudioSampleWaveformSurface?.Invalidate();
        }
    }

    private void ClearSampleWaveform()
    {
        _sampleWaveformCancellation?.Cancel();
        _sampleWaveformCancellation = null;
        _sampleWaveform = null;
        _sampleWaveformPointer = null;
        if (_sampleWaveformClosed || AudioSampleWaveformSurface is null)
        {
            return;
        }

        AudioSampleWaveformSurface.ReleasePointerCaptures();
        AudioSampleWaveformSurface.Height = 100;
        AudioSampleWaveformText.Text = "Show a source window, then zoom to individual samples or step through the clip.";
        AudioSampleWaveformNavigation.IsEnabled = false;
        ShowAudioSamplesButton.IsEnabled = true;
        AudioSampleWaveformSurface.Invalidate();
    }

    private async void AudioSampleWindow_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedAudioClip(out AudioTrackRuntime? track) || track.Player is not AudioClipPlayer player)
        {
            StatusText.Text = "Select an available audio clip first.";
            return;
        }

        long clipStart = (long)Math.Round(track.Project.SourceInSeconds * player.SourceSampleRate);
        long clipEnd = track.Project.SourceOutSeconds is double end
            ? (long)Math.Round(end * player.SourceSampleRate) : player.SourceSampleFrames;
        long first;
        int count;
        if (sender is Button { Tag: string action } && _sampleWaveform is { } current)
        {
            first = current.FirstSampleFrame;
            count = current.SampleFrames;
            switch (action)
            {
                case "in":
                    count = Math.Max(1, count / 2);
                    first += (current.SampleFrames - count) / 2;
                    break;
                case "out":
                    count = Math.Min(AudioSampleWaveform.MaximumWindowFrames, count * 2);
                    first -= (count - current.SampleFrames) / 2;
                    break;
                case "previous": first -= count; break;
                case "next": first += count; break;
            }
        }
        else
        {
            double value = AudioSourceFirstSampleBox.Value;
            if (!double.IsFinite(value) || value != Math.Truncate(value) || value < clipStart || value >= clipEnd)
            {
                StatusText.Text = "Set the source start sample to a whole sample-frame position inside the current clip.";
                return;
            }

            first = (long)value;
            count = AudioSampleWaveform.MaximumWindowFrames;
        }

        try
        {
            (first, count) = AudioSampleWaveform.GetWindow(first, count, clipStart, clipEnd);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            StatusText.Text = ex.Message;
            return;
        }

        ClearSampleWaveform();
        using var cancellation = new CancellationTokenSource();
        _sampleWaveformCancellation = cancellation;
        ShowAudioSamplesButton.IsEnabled = false;
        AudioSampleWaveformText.Text = "Decoding source sample window...";
        try
        {
            AudioSampleWaveformResult result = await Task.Run(
                () => AudioSampleWaveform.Read(track.Project.AudioPath!, first, count, cancellation.Token));
            if (cancellation.IsCancellationRequested || _sampleWaveformClosed ||
                !ReferenceEquals(_sampleWaveformCancellation, cancellation))
            {
                return;
            }

            _sampleWaveform = result;
            AudioSampleWaveformSurface.Height = Math.Max(100, result.Channels * 72);
            AudioSampleWaveformText.Text = $"Source [{result.FirstSampleFrame:N0}, {result.EndSampleFrame:N0}) | " +
                $"{result.SampleFrames:N0} frames at {result.SampleRate:N0} Hz | {result.Channels} channel(s)\n" +
                "Drag to set source trim fields above. Apply source sample range remains explicit.";
            AudioSampleWaveformNavigation.IsEnabled = true;
            AudioSampleWaveformSurface.Invalidate();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Selection changes discard the old decoded window.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or NotSupportedException or FormatException or OverflowException or
            System.Runtime.InteropServices.COMException)
        {
            if (!cancellation.IsCancellationRequested && !_sampleWaveformClosed)
            {
                AudioSampleWaveformText.Text = $"Source waveform failed: {ex.Message}";
                StatusText.Text = "Source sample waveform could not be loaded.";
            }
        }
        finally
        {
            if (!_sampleWaveformClosed && ReferenceEquals(_sampleWaveformCancellation, cancellation))
            {
                _sampleWaveformCancellation = null;
                ShowAudioSamplesButton.IsEnabled = true;
            }
        }
    }

    private void AudioSampleWaveform_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var session = args.DrawingSession;
        session.Clear(Colors.Black);
        if (_sampleWaveform is not { } waveform || sender.ActualWidth < 1)
        {
            return;
        }

        float width = (float)sender.ActualWidth;
        float channelHeight = (float)sender.ActualHeight / waveform.Channels;
        for (int channel = 0; channel < waveform.Channels; channel++)
        {
            float centre = (channel + 0.5f) * channelHeight;
            float amplitude = channelHeight * 0.35f;
            session.DrawLine(0, centre, width, centre, Colors.DimGray);
            session.DrawText($"Ch {channel + 1}", 2, channel * channelHeight, Colors.Gray);
            if (waveform.SampleFrames <= width / 3)
            {
                float? previousY = null;
                float previousX = 0;
                for (int frame = 0; frame < waveform.SampleFrames; frame++)
                {
                    float x = (frame + 0.5f) / waveform.SampleFrames * width;
                    float y = centre - Math.Clamp(waveform.Samples[frame * waveform.Channels + channel], -1, 1) * amplitude;
                    if (previousY is float previous)
                    {
                        session.DrawLine(previousX, previous, x, y, Colors.Cyan);
                    }
                    session.FillCircle(x, y, 2, Colors.Cyan);
                    previousX = x;
                    previousY = y;
                }
            }
            else
            {
                int columns = (int)Math.Ceiling(width);
                for (int x = 0; x < columns; x++)
                {
                    int first = x * waveform.SampleFrames / columns;
                    int end = Math.Max(first + 1, (x + 1) * waveform.SampleFrames / columns);
                    float low = 1, high = -1;
                    for (int frame = first; frame < end; frame++)
                    {
                        float value = Math.Clamp(waveform.Samples[frame * waveform.Channels + channel], -1, 1);
                        low = Math.Min(low, value);
                        high = Math.Max(high, value);
                    }
                    session.DrawLine(x, centre - high * amplitude, x, centre - low * amplitude, Colors.Cyan);
                }
            }
        }

        if (double.IsFinite(AudioSourceFirstSampleBox.Value) && double.IsFinite(AudioSourceEndSampleBox.Value))
        {
            float left = (float)(Math.Clamp((AudioSourceFirstSampleBox.Value - waveform.FirstSampleFrame) / waveform.SampleFrames, 0, 1) * width);
            float right = (float)(Math.Clamp((AudioSourceEndSampleBox.Value - waveform.FirstSampleFrame) / waveform.SampleFrames, 0, 1) * width);
            if (right > left)
            {
                session.FillRectangle(left, 0, right - left, (float)sender.ActualHeight,
                    Windows.UI.Color.FromArgb(60, 0, 180, 255));
            }
        }
    }

    private void AudioSampleWaveform_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_sampleWaveform is null || !e.GetCurrentPoint(AudioSampleWaveformSurface).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _sampleWaveformPointer = e.Pointer.PointerId;
        _sampleWaveformDragStart = e.GetCurrentPoint(AudioSampleWaveformSurface).Position.X;
        AudioSampleWaveformSurface.CapturePointer(e.Pointer);
        UpdateSampleWaveformSelection(_sampleWaveformDragStart);
        e.Handled = true;
    }

    private void AudioSampleWaveform_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_sampleWaveformPointer == e.Pointer.PointerId)
        {
            UpdateSampleWaveformSelection(e.GetCurrentPoint(AudioSampleWaveformSurface).Position.X);
            e.Handled = true;
        }
    }

    private void AudioSampleWaveform_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_sampleWaveformPointer == e.Pointer.PointerId)
        {
            UpdateSampleWaveformSelection(e.GetCurrentPoint(AudioSampleWaveformSurface).Position.X);
            _sampleWaveformPointer = null;
            AudioSampleWaveformSurface.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }

    private void AudioSampleWaveform_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => _sampleWaveformPointer = null;

    private void UpdateSampleWaveformSelection(double x)
    {
        if (_sampleWaveform is not { } waveform || AudioSampleWaveformSurface.ActualWidth <= 0)
        {
            return;
        }

        (long first, long end) = AudioRangeEditing.GetSourceSampleDragRange(waveform.FirstSampleFrame,
            waveform.EndSampleFrame, _sampleWaveformDragStart, x, AudioSampleWaveformSurface.ActualWidth);
        AudioSourceFirstSampleBox.Value = first;
        AudioSourceEndSampleBox.Value = end;
        InvalidateAudioWaveforms();
        AudioSampleWaveformSurface.Invalidate();
    }
}
