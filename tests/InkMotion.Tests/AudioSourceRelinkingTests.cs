using Xunit;

namespace InkMotion.Tests;

public sealed class AudioSourceRelinkingTests
{
    private static string Source => Path.Combine(Path.GetTempPath(), "inkmotion-relink-source.wav");
    private static string Replacement => Path.Combine(Path.GetTempPath(), "inkmotion-relink-replacement.wav");

    [Fact]
    public void NormalisesPathsAndRelinksAllMatchingUsesWithoutChangingOtherSettings()
    {
        var selected = new AudioTrackProject { AudioPath = Source, StartFrame = 25, SourceInSeconds = 1,
            SourceOutSeconds = 4, GainDecibels = -3, TempoPercent = 150, Title = "Keep tags", Solo = true };
        var matching = new AudioTrackProject { AudioPath = Path.Combine(Path.GetTempPath(), ".", Path.GetFileName(Source).ToUpperInvariant()),
            SourceOutSeconds = 7, Muted = true, Balance = -0.5 };
        var other = new AudioTrackProject { AudioPath = Source + ".different.wav" };
        AudioTrackProject[] tracks = [selected, matching, other];
        AudioTrackProject[] expected = tracks.Select(AudioClipEditing.Clone).ToArray();
        expected[0].AudioPath = expected[1].AudioPath = Path.GetFullPath(Replacement);
        Assert.Equal(2, AudioSourceRelinking.RelinkMatchingSources(tracks, selected, Replacement, 10, 2,
            new Dictionary<AudioTrackProject, double>()));
        Assert.Equal(EditorProjectSerialiser.Serialise(new() { AudioTracks = expected.ToList() }),
            EditorProjectSerialiser.Serialise(new() { AudioTracks = tracks.ToList() }));
    }

    [Fact]
    public void MaterialisesKnownSourceEndRatherThanExtendingToLongerReplacement()
    {
        var track = new AudioTrackProject { AudioPath = Source, SourceInSeconds = 1 };
        AudioSourceRelinking.RelinkMatchingSources([track], track, Replacement, 20, 1,
            new Dictionary<AudioTrackProject, double> { [track] = 5 });
        Assert.Equal(5, track.SourceOutSeconds);
        Assert.Equal(1, track.SourceInSeconds);
    }

    [Fact]
    public void MissingUnboundedSourcesRequireExplicitDurationConsent()
    {
        var track = new AudioTrackProject { AudioPath = Source };
        var durations = new Dictionary<AudioTrackProject, double>();
        Assert.Throws<InvalidOperationException>(() =>
            AudioSourceRelinking.RelinkMatchingSources([track], track, Replacement, 20, 1, durations));
        Assert.Equal(Source, track.AudioPath);
        Assert.Null(track.SourceOutSeconds);
        Assert.Equal(1, AudioSourceRelinking.RelinkMatchingSources([track], track, Replacement, 20, 1, durations, true));
        Assert.Equal(20, track.SourceOutSeconds);
    }

    [Theory]
    [InlineData(4, 1, false)]
    [InlineData(10, 2, true)]
    public void RejectsWholeBatchWhenAnySourceRangeOrPanIsIncompatible(double duration, int channels, bool pan)
    {
        var first = new AudioTrackProject { AudioPath = Source, SourceOutSeconds = 2 };
        var second = new AudioTrackProject { AudioPath = Source, SourceOutSeconds = 5, SpatialPanEnabled = pan };
        AudioTrackProject[] tracks = [first, second];
        string before = EditorProjectSerialiser.Serialise(new() { AudioTracks = tracks.ToList() });
        Assert.Throws<InvalidOperationException>(() => AudioSourceRelinking.RelinkMatchingSources(
            tracks, first, Replacement, duration, channels, new Dictionary<AudioTrackProject, double>()));
        Assert.Equal(before, EditorProjectSerialiser.Serialise(new() { AudioTracks = tracks.ToList() }));
    }

    [Fact]
    public void ChecksTempoAdjustedFadeDurationAndRetainsExactEndpoints()
    {
        var track = new AudioTrackProject { AudioPath = Source, SourceInSeconds = 1, SourceOutSeconds = 2,
            TempoPercent = 200, FadeInSeconds = 0.3, FadeOutSeconds = 0.3 };
        Assert.Throws<InvalidOperationException>(() => AudioSourceRelinking.RelinkMatchingSources(
            [track], track, Replacement, 2, 1, new Dictionary<AudioTrackProject, double>()));
        Assert.Equal(Source, track.AudioPath);
        track.FadeOutSeconds = 0.2;
        Assert.Equal(1, AudioSourceRelinking.RelinkMatchingSources(
            [track], track, Replacement, 2, 1, new Dictionary<AudioTrackProject, double>()));
        Assert.Equal(2, track.SourceOutSeconds);
    }

    [Fact]
    public void SupportsSamePathRepairAndRejectsInvalidSelections()
    {
        var track = new AudioTrackProject { AudioPath = Source, SourceOutSeconds = 2 };
        var durations = new Dictionary<AudioTrackProject, double>();
        Assert.Equal(1, AudioSourceRelinking.RelinkMatchingSources([track], track, Source, 2, 1, durations));
        Assert.Throws<InvalidOperationException>(() =>
            AudioSourceRelinking.RelinkMatchingSources([], track, Replacement, 2, 1, durations));
        Assert.Throws<ArgumentException>(() =>
            AudioSourceRelinking.RelinkMatchingSources([track], track, Replacement, double.NaN, 1, durations));
        Assert.Throws<ArgumentException>(() =>
            AudioSourceRelinking.RelinkMatchingSources([track], track, Replacement, 2, 0, durations));
    }

    [Fact]
    public void SnapshotUndoAndSavedProjectsRetainIndependentSourceLinks()
    {
        var project = new EditorProject
        {
            AudioTracks = [new() { AudioPath = Source, SourceOutSeconds = 3, Title = "Original tags" }],
        };
        string undo = EditorProjectSerialiser.Serialise(project);
        AudioSourceRelinking.RelinkMatchingSources(project.AudioTracks, project.AudioTracks[0],
            Replacement, 4, 1, new Dictionary<AudioTrackProject, double>());
        EditorProject saved = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        EditorProject restored = EditorProjectSerialiser.Deserialise(undo);
        Assert.Equal(Path.GetFullPath(Replacement), saved.AudioTracks[0].AudioPath);
        Assert.Equal(Source, restored.AudioTracks[0].AudioPath);
        Assert.Equal(3, saved.AudioTracks[0].SourceOutSeconds);
        Assert.Equal("Original tags", saved.AudioTracks[0].Title);
    }
}
