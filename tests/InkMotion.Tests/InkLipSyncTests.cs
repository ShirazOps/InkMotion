using Microsoft.Graphics.Canvas;
using NAudio.Wave;
using Xunit;

namespace InkMotion.Tests;

public sealed class InkLipSyncTests : IDisposable
{
    private readonly string _audio = Path.Combine(Path.GetTempPath(), $"inkmotion-mouth-{Guid.NewGuid():N}.wav");

    public InkLipSyncTests()
    {
        using var writer = new WaveFileWriter(_audio, WaveFormat.CreateIeeeFloatWaveFormat(24000, 1));
        for (int index = 0; index < 48000; index++)
        {
            double seconds = index / 24000d;
            float sample = seconds is >= 0.5 and < 1.5 ? (float)(0.6 * Math.Sin(index * Math.PI * 2 * 220 / 24000)) : 0;
            writer.WriteSample(sample);
        }
    }

    public void Dispose() => File.Delete(_audio);

    [Fact]
    public void AcousticCuesFollowAudioPlacementTrimAndTempoWithClosedSilence()
    {
        var clip = new AudioTrackProject { AudioPath = _audio, StartFrame = 25, SourceInSeconds = 0.25, SourceOutSeconds = 1.75, TempoPercent = 200 };
        InkLipSync result = InkLipSyncs.Analyse(clip, 24, -40, InkLipSyncs.Starter(), CancellationToken.None);
        Assert.Equal(1, result.StartSeconds);
        Assert.Equal(1.75, result.EndSeconds);
        Assert.Equal(InkViseme.Closed, InkLipSyncs.Evaluate(result, 1.01));
        Assert.Equal(InkViseme.Open, InkLipSyncs.Evaluate(result, 1.3));
        Assert.Equal(InkViseme.Closed, InkLipSyncs.Evaluate(result, 1.7));
        Assert.Equal(InkViseme.Closed, InkLipSyncs.Evaluate(result, 1.9));
        Assert.All(result.Cues.Where(cue => cue.Seconds < result.EndSeconds),
            cue => Assert.InRange(Math.Abs((cue.Seconds - 1) * 24 - Math.Round((cue.Seconds - 1) * 24)), 0, 1e-6));
        var project = new EditorProject { AudioTracks = [clip] };
        Assert.True(InkLipSyncs.IsSourceCurrent(result, project));
        clip.StartFrame++;
        Assert.False(InkLipSyncs.IsSourceCurrent(result, project));
    }

    [Fact]
    public void MouthLibraryAndEditableCuesPersistAndCloneWithLayer()
    {
        var clip = new AudioTrackProject { AudioPath = _audio };
        var project = new EditorProject { AudioTracks = [clip] };
        InkLipSync sync = InkLipSyncs.Analyse(clip, 24, -40, InkLipSyncs.Starter(), CancellationToken.None);
        InkLipSyncs.SetCue(sync, 1, InkViseme.Round);
        Assert.Equal(InkViseme.Round, InkLipSyncs.Evaluate(sync, 1.01));
        project.InkLayers[0].LipSync = sync;
        EditorProject restored = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        EditorProject clone = CompoundTimelines.Clone(restored);
        clone.InkLayers[0].LipSync!.Mouths[InkViseme.Round][0][0].X = 0;
        clone.InkLayers[0].LipSync!.Cues.Clear();
        Assert.NotEqual(0, restored.InkLayers[0].LipSync!.Mouths[InkViseme.Round][0][0].X);
        Assert.NotEmpty(restored.InkLayers[0].LipSync!.Cues);
        Assert.True(ExportPlanning.HasInk(restored));
        Assert.Equal(2, ExportPlanning.GetInkDurationSeconds(restored));
        Assert.Throws<ArgumentException>(() => InkLipSyncs.SetCue(sync, 2, InkViseme.Open));
        sync.Mouths[InkViseme.Open][0][0].X = double.NaN;
        Assert.Throws<InvalidDataException>(() => EditorProjectSerialiser.Serialise(project));
    }

    [Fact]
    public void CancellationAndInvalidLibraryLeaveOriginalAnimationUntouched()
    {
        InkLipSync library = InkLipSyncs.Starter();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => InkLipSyncs.Analyse(new() { AudioPath = _audio }, 24, -40, library, cancelled.Token));
        Assert.Empty(library.Cues);
        Assert.False(library.Enabled);
        library.Mouths.Remove(InkViseme.Teeth);
        Assert.Throws<InvalidDataException>(() => InkLipSyncs.Validate(library));
    }

    [Fact]
    public void RendererSwitchesMouthsAtExactCueAndRetainsLayerEffects()
    {
        var clip = new AudioTrackProject { AudioPath = _audio };
        InkLipSync sync = InkLipSyncs.Analyse(clip, 24, -40, InkLipSyncs.Starter(), CancellationToken.None);
        sync.Mouths[InkViseme.Closed] = [];
        sync.Mouths[InkViseme.Open] = [[new() { X = 0.5, Y = 0.5, BrushWidth = 10, Pressure = 1, BrushColour = 0xFFFF0000 }]];
        var project = new EditorProject { AudioTracks = [clip] };
        project.InkLayers[0].LipSync = sync;
        project.InkLayers[0].GlowRadius = 2;
        using var device = new CanvasDevice(true);
        using var background = new CanvasRenderTarget(device, 100, 100, 96);
        using (var session = background.CreateDrawingSession()) session.Clear(Microsoft.UI.Colors.Transparent);
        using CanvasRenderTarget silence = TimelineCompositor.CompositeInkFrame(device, background, 100, 100, project, 0, 100, 100, 1, 0.1);
        using CanvasRenderTarget speech = TimelineCompositor.CompositeInkFrame(device, background, 100, 100, project, 0, 100, 100, 1, 1);
        Assert.Equal(0, silence.GetPixelBytes(50, 50, 1, 1)[3]);
        Assert.True(speech.GetPixelBytes(50, 50, 1, 1)[3] > 200);
        project.CompositeGraph = InkCompositeGraphs.FromLayerStack(project.InkLayers);
        using CanvasRenderTarget graph = TimelineCompositor.CompositeInkFrame(device, background, 100, 100, project, 0, 100, 100, 1, 1);
        Assert.Equal(speech.GetPixelBytes(), graph.GetPixelBytes());
        clip.TempoPercent = 110;
        Assert.Empty(InkCharacterRigs.Strokes(project, 0, project.InkLayers[0], 1, 100, 100));
    }
}
