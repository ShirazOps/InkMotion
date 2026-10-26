using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace InkMotion;

public sealed partial class MainWindow
{
    private AudioTrackRuntime? _audioRangeTrack;
    private CanvasControl? _audioRangeCanvas;
    private uint? _audioRangePointerId;
    private double _audioRangeDragStartX;
    private bool _audioRangeDragged;

    private void AudioRange_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_isInitialising)
        {
            ClearAudioSpectrum();
            InvalidateAudioWaveforms();
        }
    }

    private void AudioWaveformSelectionMode_Toggled(object sender, RoutedEventArgs e) => InvalidateAudioWaveforms();

    private void AudioWaveform_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not CanvasControl { Tag: AudioTrackRuntime track } canvas ||
            !e.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed ||
            canvas.ActualWidth <= 0)
        {
            return;
        }

        int index = _audioTracks.IndexOf(track);
        if (index >= 0 && index != _selectedAudioTrackIndex)
        {
            _selectedAudioTrackIndex = index;
            UpdateSelectedAudioTrack();
        }

        PausePlayback();
        FinishActiveEdit();
        _audioRangeCanvas = canvas;
        _audioRangePointerId = e.Pointer.PointerId;
        _audioRangeDragStartX = e.GetCurrentPoint(canvas).Position.X;
        _audioRangeDragged = false;
        canvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void AudioWaveform_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (sender is CanvasControl { Tag: AudioTrackRuntime track } canvas &&
            _audioRangeCanvas == canvas && _audioRangePointerId == e.Pointer.PointerId)
        {
            double x = e.GetCurrentPoint(canvas).Position.X;
            _audioRangeDragged |= Math.Abs(x - _audioRangeDragStartX) >= 3;
            if (_audioRangeDragged)
            {
                UpdateAudioWaveformDragSelection(track, x);
            }
            e.Handled = true;
        }
    }

    private void AudioWaveform_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is CanvasControl { Tag: AudioTrackRuntime track } canvas &&
            _audioRangeCanvas == canvas && _audioRangePointerId == e.Pointer.PointerId)
        {
            double x = e.GetCurrentPoint(canvas).Position.X;
            if (_audioRangeDragged || Math.Abs(x - _audioRangeDragStartX) >= 3)
            {
                UpdateAudioWaveformDragSelection(track, x);
            }
            else
            {
                double duration = AudioClipEditing.GetTimelineDuration(track.Project, track.SourceDurationSeconds);
                if (double.IsFinite(duration) && duration > 0)
                {
                    MoveTimelinePlayhead(AudioClipEditing.GetClipStartSeconds(track.Project, _frameRate) +
                        Math.Clamp(x / canvas.ActualWidth, 0, 1) * duration);
                }
                else
                {
                    StatusText.Text = "Relink this audio source before seeking within the clip.";
                }
            }
            _audioRangePointerId = null;
            _audioRangeCanvas = null;
            canvas.ReleasePointerCapture(e.Pointer);
            RefreshAudioTracks();
            e.Handled = true;
        }
    }

    private void AudioWaveform_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (sender is CanvasControl canvas &&
            _audioRangeCanvas == canvas && _audioRangePointerId == e.Pointer.PointerId)
        {
            _audioRangePointerId = null;
            _audioRangeCanvas = null;
            RefreshAudioTracks();
        }
    }

    private void UpdateAudioWaveformDragSelection(AudioTrackRuntime track, double x)
    {
        try
        {
            if (AudioWaveformSampleSelectionToggle.IsOn && track.Player is AudioClipPlayer player)
            {
                (long startSample, long endSample) = AudioRangeEditing.GetWaveformSourceSampleRange(
                    track.Project, _audioRangeDragStartX, x, _audioRangeCanvas!.ActualWidth,
                    player.SourceSampleFrames, player.SourceSampleRate);
                AudioSourceFirstSampleBox.Value = startSample;
                AudioSourceEndSampleBox.Value = endSample;
                InvalidateAudioWaveforms();
                return;
            }

            (int start, int end) = AudioRangeEditing.GetWaveformDragRange(
                track.Project, _audioRangeDragStartX, x, _audioRangeCanvas!.ActualWidth,
                _frameRate, track.SourceDurationSeconds);
            AudioRangeStartBox.Value = start;
            AudioRangeEndBox.Value = end;
            InvalidateAudioWaveforms();
        }
        catch (ArgumentOutOfRangeException)
        {
            StatusText.Text = AudioWaveformSampleSelectionToggle.IsOn
                ? "This audio clip cannot be selected by source sample."
                : "This audio clip cannot be selected as a timeline-frame range.";
        }
    }

    private void InvalidateAudioWaveforms()
    {
        foreach (Button button in AudioTracksPanel.Children.OfType<Button>())
        {
            if (button.Content is Grid grid)
            {
                foreach (CanvasControl waveform in grid.Children.OfType<CanvasControl>())
                {
                    waveform.Invalidate();
                }
            }
        }
    }

    private void UpdateAudioRangeSelection()
    {
        AudioTrackRuntime? track = TryGetSelectedAudioClip(out var selected) ? selected : null;
        AudioRangeActions.IsEnabled = track is not null;
        if (!ReferenceEquals(track, _audioRangeTrack))
        {
            _audioRangeTrack = track;
            SelectWholeAudioRange();
            ClearAudioSpectrum();
        }
    }

    private void SelectWholeAudioRange()
    {
        if (_audioRangeTrack is not { } track)
        {
            AudioRangeStartBox.Value = 1;
            AudioRangeEndBox.Value = 2;
            return;
        }

        AudioRangeStartBox.Value = track.Project.StartFrame;
        AudioRangeEndBox.Value = Math.Ceiling(track.Project.StartFrame +
            AudioClipEditing.GetTimelineDuration(track.Project, track.SourceDurationSeconds) * _frameRate - 1e-8);
    }

    private void AudioRangeBoundary_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string boundary })
        {
            return;
        }

        if (boundary == "all")
        {
            SelectWholeAudioRange();
        }
        else if (boundary == "start")
        {
            AudioRangeStartBox.Value = _playheadFramePosition + 1;
        }
        else
        {
            AudioRangeEndBox.Value = _playheadFramePosition + 1;
        }
    }

    private void AudioRangeAction_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedAudioClip(out AudioTrackRuntime? selected) || sender is not Button { Tag: string action })
        {
            StatusText.Text = "Select an available audio clip first.";
            return;
        }

        double start = AudioRangeStartBox.Value;
        double end = AudioRangeEndBox.Value;
        if (!double.IsFinite(start) || !double.IsFinite(end) || start != Math.Truncate(start) ||
            end != Math.Truncate(end) || start < 1 || start > int.MaxValue || end < 1 || end > int.MaxValue)
        {
            StatusText.Text = "Audio range boundaries must be whole timeline frame numbers.";
            return;
        }

        AudioRangeParts parts;
        try
        {
            parts = AudioRangeEditing.Partition(selected.Project, (int)start, (int)end, _frameRate, selected.SourceDurationSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            StatusText.Text = "Select a non-empty range within this audio clip; the end frame is excluded.";
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        if (action is "copy" or "cut")
        {
            _copiedAudio = AudioClipEditing.Clone(parts.Range);
            _copiedAudioPeaks = selected.WaveformPeaks;
            PasteAudioClipButton.IsEnabled = true;
        }

        if (action == "copy")
        {
            StatusText.Text = "Audio range copied. Paste at the playhead to place it on a new track.";
            return;
        }

        BeginEdit();
        int insertion = _selectedAudioTrackIndex;
        float[] peaks = selected.WaveformPeaks;
        _audioTracks.RemoveAt(insertion);
        selected.Dispose();
        var replacements = new List<AudioTrackProject>();
        if (parts.Before is not null)
        {
            replacements.Add(parts.Before);
        }

        if (action == "silence")
        {
            parts.Range.Muted = true;
            replacements.Add(parts.Range);
        }

        if (parts.After is not null)
        {
            replacements.Add(parts.After);
        }

        string? loadError = null;
        foreach (AudioTrackProject project in replacements)
        {
            var runtime = new AudioTrackRuntime(project) { WaveformPeaks = peaks };
            OpenAudioPlayer(runtime);
            loadError ??= runtime.LoadError;
            _audioTracks.Insert(insertion++, runtime);
        }

        _selectedAudioTrackIndex = _audioTracks.Count == 0 ? -1 : Math.Min(_selectedAudioTrackIndex, _audioTracks.Count - 1);
        MarkEditChanged();
        CommitEdit();
        UpdateSelectedAudioTrack();
        RefreshAudioTracks();
        SeekToCurrentFrame();
        StatusText.Text = loadError is not null
            ? $"Audio range edited, but a source could not be loaded: {loadError}"
            : action == "silence"
                ? "Range silenced on a muted segment. Other audio stays at its original timeline position."
                : "Range removed without closing the gap. Surrounding audio and source files are unchanged.";
    }
}
