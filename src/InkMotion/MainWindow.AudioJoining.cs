using Microsoft.UI.Xaml;

namespace InkMotion;

public sealed partial class MainWindow
{
    private void JoinAudioClips_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedAudioClip(out AudioTrackRuntime? left) ||
            _selectedAudioTrackIndex + 1 >= _audioTracks.Count ||
            !_audioTracks[_selectedAudioTrackIndex + 1].HasSource)
        {
            StatusText.Text = "Select a clip with its consecutive source segment on the next audio track.";
            return;
        }

        AudioTrackRuntime right = _audioTracks[_selectedAudioTrackIndex + 1];
        AudioTrackProject joined;
        try
        {
            joined = AudioClipJoining.Join(left.Project, right.Project, _frameRate, left.SourceDurationSeconds);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            StatusText.Text = $"Cannot join audio clips: {ex.Message}";
            return;
        }

        ApplyAudioClipEdit(track =>
        {
            track.Project = joined;
            _audioTracks.RemoveAt(_selectedAudioTrackIndex + 1);
            right.Dispose();
            _audioRangeTrack = null;
            return true;
        }, "Consecutive source segments joined; outer fades and tags retained. Processing now runs continuously across the join.",
            "Could not join these audio clips.");
    }
}
