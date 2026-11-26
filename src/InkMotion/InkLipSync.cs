using System.Text.Json;
using NAudio.Wave;

namespace InkMotion;

public enum InkViseme { Closed, Open, Wide, Round, Teeth }
public sealed record InkMouthCue(double Seconds, InkViseme Mouth);

public sealed class InkLipSync
{
    public bool Enabled { get; set; }
    public string AudioSignature { get; set; } = "";
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }
    public List<InkMouthCue> Cues { get; set; } = [];
    public Dictionary<InkViseme, List<List<InkPoint>>> Mouths { get; set; } = [];
}

public static class InkLipSyncs
{
    public const int MaximumAnalysisFrames = 72000;
    public static InkLipSync Clone(InkLipSync sync) => new()
    {
        Enabled = sync.Enabled, AudioSignature = sync.AudioSignature,
        StartSeconds = sync.StartSeconds, EndSeconds = sync.EndSeconds, Cues = sync.Cues.ToList(),
        Mouths = sync.Mouths.ToDictionary(pair => pair.Key, pair => InkEditing.CloneStrokes(pair.Value)),
    };

    public static string Signature(AudioTrackProject track, double frameRate) => JsonSerializer.Serialize(new
    {
        track.AudioPath, track.StartFrame, track.SourceInSeconds, track.SourceOutSeconds, track.TempoPercent, FrameRate = frameRate,
    });

    public static bool IsSourceCurrent(InkLipSync sync, EditorProject project) =>
        project.AudioTracks.Any(track => Signature(track, project.FrameRate) == sync.AudioSignature);

    public static void Validate(InkLipSync sync)
    {
        if (sync.AudioSignature is null || sync.AudioSignature.Length > 8192 ||
            !double.IsFinite(sync.StartSeconds) || sync.StartSeconds is < 0 or > 86400 ||
            !double.IsFinite(sync.EndSeconds) || sync.EndSeconds < sync.StartSeconds || sync.EndSeconds > 86400 ||
            sync.Cues is not { Count: <= MaximumAnalysisFrames + 1 } ||
            sync.Cues.Any(cue => cue is null || !double.IsFinite(cue.Seconds) ||
                cue.Seconds < sync.StartSeconds || cue.Seconds > sync.EndSeconds || !Enum.IsDefined(cue.Mouth)) ||
            !sync.Cues.Zip(sync.Cues.Skip(1)).All(pair => pair.First.Seconds < pair.Second.Seconds) ||
            sync.Mouths is null || sync.Mouths.Count != Enum.GetValues<InkViseme>().Length ||
            Enum.GetValues<InkViseme>().Any(mouth => !sync.Mouths.ContainsKey(mouth)) ||
            (sync.Enabled && (sync.Cues.Count == 0 || sync.AudioSignature.Length == 0 || sync.EndSeconds <= sync.StartSeconds)))
            throw new InvalidDataException("Lip-sync needs five mouth drawings and ordered, finite audio-timed cues.");
        foreach (List<List<InkPoint>> drawing in sync.Mouths.Values)
        {
            if (drawing is null || drawing.Count > 10000) throw new InvalidDataException("Invalid mouth drawing.");
            foreach (List<InkPoint> stroke in drawing) EditorProjectValidation.ValidateStroke(stroke);
        }
    }

    public static InkViseme Evaluate(InkLipSync sync, double seconds)
    {
        if (seconds < sync.StartSeconds || seconds >= sync.EndSeconds) return InkViseme.Closed;
        int low = 0, high = sync.Cues.Count - 1;
        InkViseme mouth = InkViseme.Closed;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            if (sync.Cues[middle].Seconds <= seconds) { mouth = sync.Cues[middle].Mouth; low = middle + 1; }
            else high = middle - 1;
        }
        return mouth;
    }

    public static void SetCue(InkLipSync sync, double seconds, InkViseme mouth)
    {
        if (!double.IsFinite(seconds) || seconds < sync.StartSeconds || seconds >= sync.EndSeconds || !Enum.IsDefined(mouth))
            throw new ArgumentException("Choose a frame within the analysed audio interval.");
        sync.Cues.RemoveAll(cue => Math.Abs(cue.Seconds - seconds) < 0.000001);
        sync.Cues.Add(new(seconds, mouth));
        sync.Cues.Sort((a, b) => a.Seconds.CompareTo(b.Seconds));
    }

    public static InkLipSync Starter(double x = 0.5, double y = 0.6, double width = 0.12, double height = 0.08)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height) ||
            width <= 0 || height <= 0 || x - width / 2 < 0 || x + width / 2 > 1 ||
            y - height / 2 < 0 || y + height / 2 > 1)
            throw new ArgumentException("Keep the mouth's positive width/height and bounds inside the canvas.");
        List<InkPoint> Ellipse(double w, double h) => Enumerable.Range(0, 48).Select(index => new InkPoint
        {
            X = x + Math.Cos(index * Math.PI / 24) * w / 2,
            Y = y + Math.Sin(index * Math.PI / 24) * h / 2,
            IsFilled = true, BrushColour = 0xFF201018, Pressure = 1, BrushWidth = 2,
        }).ToList();
        return new()
        {
            Mouths = new()
            {
                [InkViseme.Closed] = [[new() { X = x - width / 2, Y = y, BrushColour = 0xFF201018, BrushWidth = 3, Pressure = 1 },
                    new() { X = x + width / 2, Y = y, BrushColour = 0xFF201018, BrushWidth = 3, Pressure = 1 }]],
                [InkViseme.Open] = [Ellipse(width * 0.8, height)],
                [InkViseme.Wide] = [Ellipse(width, height * 0.5)],
                [InkViseme.Round] = [Ellipse(width * 0.45, height * 0.8)],
                [InkViseme.Teeth] = [Ellipse(width, height * 0.3),
                    [new() { X = x - width * 0.35, Y = y, BrushColour = 0xFFFFFFFF, BrushWidth = 2, Pressure = 1 },
                        new() { X = x + width * 0.35, Y = y, BrushColour = 0xFFFFFFFF, BrushWidth = 2, Pressure = 1 }]],
            },
        };
    }

    public static InkLipSync Analyse(AudioTrackProject track, double frameRate, double silenceDecibels,
        InkLipSync library, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(track.AudioPath) || !AudioClipEditing.HasValidClipSettings(track) ||
            !double.IsFinite(frameRate) || frameRate is < 1 or > 120 ||
            !double.IsFinite(silenceDecibels) || silenceDecibels is < -80 or > -10)
            throw new ArgumentException("Choose valid audio, a frame rate and a silence threshold from -80 to -10 dBFS.");
        using var reader = new AudioFileReader(track.AudioPath);
        double duration = AudioClipEditing.GetTimelineDuration(track, reader.TotalTime.TotalSeconds);
        int count = ExportPlanning.GetFrameCount(duration, frameRate);
        if (count < 1 || count > MaximumAnalysisFrames)
            throw new InvalidOperationException($"Analyse an audible clip of at most {MaximumAnalysisFrames:N0} timeline frames.");
        double start = AudioClipEditing.GetClipStartSeconds(track, frameRate);
        if (start + duration > 86400) throw new InvalidOperationException("Lip-sync must fit within 24 hours of timeline time.");
        int channels = reader.WaveFormat.Channels, rate = reader.WaveFormat.SampleRate;
        double tempo = AudioClipEditing.GetTempoRatio(track);
        reader.CurrentTime = TimeSpan.FromSeconds(track.SourceInSeconds);
        var features = new List<(double Rms, double Difference)>(count);
        var previous = new float[channels];
        var buffer = new float[(int)Math.Ceiling(rate * tempo / frameRate) * channels + channels];
        long consumed = 0;
        for (int frame = 0; frame < count; frame++)
        {
            token.ThrowIfCancellationRequested();
            long through = (long)Math.Round(Math.Min((frame + 1) / frameRate, duration) * rate * tempo);
            int wanted = checked((int)(through - consumed) * channels);
            int read = 0;
            while (read < wanted)
            {
                int got = reader.Read(buffer, read, wanted - read);
                if (got == 0) break;
                read += got;
            }
            if (read == 0) throw new InvalidDataException("Audio ended before the selected lip-sync interval.");
            double sum = 0, difference = 0;
            for (int index = 0; index < read; index++)
            {
                float sample = buffer[index];
                if (!float.IsFinite(sample)) throw new InvalidDataException("Audio contains non-finite samples.");
                int channel = index % channels;
                sum += sample * sample;
                difference += Math.Pow(sample - previous[channel], 2);
                previous[channel] = sample;
            }
            features.Add((Math.Sqrt(sum / read), difference / Math.Max(sum, 1e-12)));
            consumed = through;
        }
        token.ThrowIfCancellationRequested();
        double peak = features.Max(feature => feature.Rms);
        double silence = Math.Pow(10, silenceDecibels / 20);
        InkLipSync result = Clone(library);
        result.AudioSignature = Signature(track, frameRate);
        result.StartSeconds = start; result.EndSeconds = start + duration; result.Enabled = true;
        result.Cues.Clear();
        InkViseme held = InkViseme.Closed, pending = held;
        int pendingFrames = 0;
        for (int frame = 0; frame < count; frame++)
        {
            var feature = features[frame];
            double strength = feature.Rms / Math.Max(peak, 1e-12);
            // Model-free acoustic draft: energy drives opening, high-frequency energy suggests teeth/frication.
            InkViseme candidate = feature.Rms < silence ? InkViseme.Closed :
                feature.Difference > 0.45 ? InkViseme.Teeth :
                strength > 0.65 ? InkViseme.Open : strength > 0.35 ? InkViseme.Wide : InkViseme.Round;
            if (candidate == pending) pendingFrames++; else { pending = candidate; pendingFrames = 1; }
            if (frame == 0 || candidate == InkViseme.Closed || pendingFrames >= 2) held = candidate;
            if (result.Cues.Count == 0 || result.Cues[^1].Mouth != held)
                result.Cues.Add(new(start + frame / frameRate, held));
        }
        result.Cues.Add(new(result.EndSeconds, InkViseme.Closed));
        Validate(result);
        return result;
    }
}
