using System.Globalization;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SherpaOnnx;

namespace InkMotion;

public sealed record SpeechCueOptions
{
    public int MaximumLineCharacters { get; init; } = 42;
    public int MaximumLines { get; init; } = 2;
    public double MaximumCueSeconds { get; init; } = 6;
    public double MinimumCueSeconds { get; init; } = 1;
    public double PauseSeconds { get; init; } = 0.8;
    public double CommaPauseSeconds { get; init; } = 0.35;
    public double MinimumConfidence { get; init; } = 0.25;
    /// <summary>When true, each recognised word becomes a timed run that is revealed as it is spoken.</summary>
    public bool WordTiming { get; init; }
}

public sealed record SpeechRecogniserInfo(string CultureName, string DisplayName);

public sealed record SpeechWord(double StartSeconds, double EndSeconds, string Text, double Confidence = 1, int? SpeakerId = null);
public sealed record SpeechSpeakerSegment(double StartSeconds, double EndSeconds, int SpeakerId);

/// <summary>
/// Offline speech-to-subtitle generation: renders the audible timeline mix, recognises it with a local speech engine
/// and groups the recognised words into readable subtitle cues.
/// </summary>
public static class SpeechSubtitles
{
    public const int RecognitionSampleRate = 16000;

    public static IReadOnlyList<SubtitleCueText> BuildCues(IReadOnlyList<SpeechWord> words, SpeechCueOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(words);
        options ??= new SpeechCueOptions();
        if (options.MaximumLineCharacters < 8 || options.MaximumLines < 1 || !(options.MaximumCueSeconds > 0) ||
            !(options.MinimumCueSeconds >= 0) || !(options.PauseSeconds > 0) ||
            !(options.CommaPauseSeconds >= 0) || !(options.MinimumConfidence is >= 0 and <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        int maximumCharacters = options.MaximumLineCharacters * options.MaximumLines;
        var groups = new List<List<SpeechWord>>();
        List<SpeechWord>? current = null;
        int currentLength = 0;
        SpeechWord[] ordered = words
            .Where(word => !string.IsNullOrWhiteSpace(word.Text) && double.IsFinite(word.StartSeconds) &&
                double.IsFinite(word.EndSeconds) && word.StartSeconds >= 0 && word.EndSeconds >= word.StartSeconds &&
                double.IsFinite(word.Confidence) && word.Confidence >= options.MinimumConfidence)
            .OrderBy(word => word.StartSeconds)
            .ToArray();
        for (int index = 1; index < ordered.Length; index++)
        {
            SpeechWord previous = ordered[index - 1];
            SpeechWord word = ordered[index];
            double pause = Math.Max(0, word.StartSeconds - previous.EndSeconds);
            bool speakerBreak = previous.SpeakerId != word.SpeakerId;
            bool sentenceBreak = pause >= options.PauseSeconds || EndsSentence(previous.Text) || speakerBreak;
            if (!EndsSentence(previous.Text) && pause >= options.CommaPauseSeconds)
            {
                ordered[index - 1] = previous with { Text = previous.Text.TrimEnd() + (sentenceBreak ? "." : ",") };
            }

            if (sentenceBreak)
            {
                ordered[index] = word with { Text = Capitalise(word.Text) };
            }
        }

        foreach (SpeechWord word in ordered)
        {
            string text = word.Text.Trim();
            bool startNew = current is null ||
                word.StartSeconds - current[^1].EndSeconds > options.PauseSeconds ||
                currentLength + 1 + text.Length > maximumCharacters ||
                word.EndSeconds - current[0].StartSeconds > options.MaximumCueSeconds ||
                EndsSentence(current[^1].Text) || current[^1].SpeakerId != word.SpeakerId;
            if (startNew)
            {
                current = [];
                groups.Add(current);
                currentLength = -1;
            }

            current!.Add(word with { Text = text });
            currentLength += 1 + text.Length;
        }

        var cues = new List<SubtitleCueText>(groups.Count);
        for (int index = 0; index < groups.Count; index++)
        {
            List<SpeechWord> group = groups[index];
            double start = group[0].StartSeconds;
            double end = Math.Max(group.Max(word => word.EndSeconds), start + options.MinimumCueSeconds);
            if (index + 1 < groups.Count)
            {
                end = Math.Min(end, groups[index + 1][0].StartSeconds);
            }

            end = Math.Max(end, start + 0.001);
            string text = string.Join(' ', group.Select(word => word.Text));
            if (group[0].SpeakerId is int speakerId)
            {
                text = $"Speaker {speakerId + 1}: {text}";
            }

            string wrapped = WrapLines(text, options.MaximumLineCharacters);
            List<SubtitleRun>? runs = null;
            if (options.WordTiming)
            {
                runs = SubtitleRichText.SplitWords(wrapped);
                int prefixRuns = runs.Count - group.Count;
                if (prefixRuns >= 0 && group.All(word => !word.Text.Any(char.IsWhiteSpace)))
                {
                    for (int wordIndex = 0; wordIndex < group.Count; wordIndex++)
                    {
                        runs[prefixRuns + wordIndex].RevealSeconds =
                            Math.Clamp(group[wordIndex].StartSeconds - start, 0, end - start);
                    }
                }
                else
                {
                    runs = null;
                }
            }

            cues.Add(new SubtitleCueText(start, end, wrapped) { Runs = runs });
        }

        return cues;
    }

    /// <summary>Splits text that exceeds one line at the space nearest its middle.</summary>
    public static string WrapLines(string text, int maximumLineCharacters)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= maximumLineCharacters)
        {
            return text;
        }

        int middle = text.Length / 2;
        int best = -1;
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] == ' ' && (best < 0 || Math.Abs(index - middle) < Math.Abs(best - middle)))
            {
                best = index;
            }
        }

        return best < 0 ? text : text[..best] + "\n" + text[(best + 1)..];
    }

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static bool EndsSentence(string text) => text.EndsWith('.') || text.EndsWith('?') || text.EndsWith('!');

    /// <summary>
    /// Renders the audible export mix (respecting mute, solo and buses) as 16 kHz 16-bit mono PCM, peak-normalised for
    /// recognition. Returns the rendered duration in seconds.
    /// </summary>
    public static double RenderRecognitionMix(EditorProject project, string outputPath,
        CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        EditorProjectValidation.Validate(project);
        string floatPath = outputPath + ".float.wav";
        try
        {
            float peak = 0;
            long frames = 0;
            using (var mixer = new ExportAudioMixer(project))
            {
                if (mixer.SourceCount == 0)
                {
                    throw new InvalidOperationException("Nothing to transcribe: add an audible audio or video source." +
                        (mixer.Warnings.Count > 0 ? "\n" + string.Join("\n", mixer.Warnings) : string.Empty));
                }

                long sourceFrames = checked((long)Math.Round(mixer.DurationSeconds * mixer.SampleRate));
                if (sourceFrames <= 0)
                {
                    throw new InvalidOperationException("The audio mix is empty.");
                }

                var mono = new MonoDownmix(mixer, sourceFrames);
                var resampled = new WdlResamplingSampleProvider(mono, RecognitionSampleRate);
                using var writer = new WaveFileWriter(floatPath, resampled.WaveFormat);
                var buffer = new float[RecognitionSampleRate];
                int read;
                while ((read = resampled.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (int index = 0; index < read; index++)
                    {
                        peak = Math.Max(peak, Math.Abs(buffer[index]));
                    }

                    writer.WriteSamples(buffer, 0, read);
                    frames += read;
                    progress?.Report(Math.Min(1, (double)mono.FramesRead / sourceFrames) * 0.5);
                }
            }

            // Quiet dialogue is boosted up to +20 dB; silence is left untouched.
            float gain = peak > 0.0001f ? Math.Min(0.9f / peak, 10f) : 1f;
            using (var reader = new WaveFileReader(floatPath))
            using (var writer = new WaveFileWriter(outputPath, new WaveFormat(RecognitionSampleRate, 16, 1)))
            {
                ISampleProvider samples = reader.ToSampleProvider();
                var buffer = new float[RecognitionSampleRate];
                var pcm = new byte[buffer.Length * 2];
                long written = 0;
                int read;
                while ((read = samples.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (int index = 0; index < read; index++)
                    {
                        short value = (short)Math.Round(Math.Clamp(buffer[index] * gain, -1f, 1f) * short.MaxValue);
                        pcm[index * 2] = (byte)value;
                        pcm[index * 2 + 1] = (byte)(value >> 8);
                    }

                    writer.Write(pcm, 0, read * 2);
                    written += read;
                    progress?.Report(0.5 + 0.5 * written / Math.Max(1, frames));
                }
            }

            return (double)frames / RecognitionSampleRate;
        }
        finally
        {
            File.Delete(floatPath);
        }
    }

    public static IReadOnlyList<SpeechRecogniserInfo> GetInstalledRecognisers()
    {
        try
        {
            return SpeechRecognitionEngine.InstalledRecognizers()
                .Select(info => new SpeechRecogniserInfo(info.Culture.Name, info.Culture.DisplayName))
                .DistinctBy(info => info.CultureName, StringComparer.OrdinalIgnoreCase)
                .OrderBy(info => info.DisplayName, StringComparer.CurrentCulture)
                .ToArray();
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or
            System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static IReadOnlyList<SpeechWord> RecogniseWithWhisper(string wavePath, string language,
            CancellationToken cancellationToken, IProgress<double>? progress = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(wavePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(language);
            WhisperModelFiles files = SpeechModelStore.Default.Whisper ??
                throw new InvalidOperationException("Import the Whisper tiny model before selecting it.");
            float[] samples = ReadRecognitionSamples(wavePath);
            var whisper = new OfflineModelConfig().Whisper;
            whisper.Encoder = files.Encoder;
            whisper.Decoder = files.Decoder;
            whisper.Language = language;
            whisper.Task = "transcribe";
            whisper.EnableTokenTimestamps = 1;
            var model = new OfflineModelConfig
            {
                Whisper = whisper,
                Tokens = files.Tokens,
                ModelType = "whisper",
                Provider = "cpu",
                NumThreads = Math.Clamp(Environment.ProcessorCount - 1, 1, 4),
            };
            var config = new OfflineRecognizerConfig { ModelConfig = model };
            using var recognizer = new OfflineRecognizer(config);
            using OfflineStream stream = recognizer.CreateStream();
            stream.AcceptWaveform(RecognitionSampleRate, samples);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0.1);
            recognizer.Decode(stream);
            cancellationToken.ThrowIfCancellationRequested();
            OfflineRecognizerResult result = stream.Result;
            progress?.Report(1);
            double duration = (double)samples.Length / RecognitionSampleRate;
            return BuildWhisperWords(result.Tokens, result.Timestamps, duration);
        }

        public static IReadOnlyList<SpeechWord> BuildWhisperWords(
            IReadOnlyList<string> tokens, IReadOnlyList<float> timestamps, double durationSeconds)
        {
            ArgumentNullException.ThrowIfNull(tokens);
            ArgumentNullException.ThrowIfNull(timestamps);
            if (!double.IsFinite(durationSeconds) || durationSeconds < 0 ||
                tokens.Count != timestamps.Count ||
                timestamps.Any(time => !float.IsFinite(time) || time < 0 || time > durationSeconds + 1))
            {
                throw new InvalidDataException("Whisper returned invalid token timestamps.");
            }

            var words = new List<SpeechWord>();
            string text = string.Empty;
            double start = 0;
            double end = 0;
            for (int index = 0; index < tokens.Count; index++)
            {
                string token = tokens[index];
                if (string.IsNullOrWhiteSpace(token) || token.StartsWith("<|", StringComparison.Ordinal))
                {
                    continue;
                }

                bool beginsWord = char.IsWhiteSpace(token[0]);
                if (text.Length > 0 && beginsWord)
                {
                    words.Add(new(start, Math.Max(start + 0.001, end), text, 1));
                    text = string.Empty;
                }

                string piece = token.Trim();
                if (piece.Length == 0)
                {
                    continue;
                }

                double tokenTime = Math.Clamp(timestamps[index], 0, durationSeconds);
                if (text.Length == 0)
                {
                    start = tokenTime;
                }

                text += piece;
                end = index + 1 < timestamps.Count
                    ? Math.Clamp(timestamps[index + 1], tokenTime, durationSeconds)
                    : durationSeconds;
            }

            if (text.Length > 0)
            {
                words.Add(new(start, Math.Max(start + 0.001, end), text, 1));
            }

            return words;
        }

        public static IReadOnlyList<SpeechSpeakerSegment> Diarise(string wavePath,
            CancellationToken cancellationToken, IProgress<double>? progress = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(wavePath);
            SpeechDiarisationModelFiles files = SpeechModelStore.Default.Diarisation ??
                throw new InvalidOperationException("Import the speaker diarisation models before enabling speaker labels.");
            float[] samples = ReadRecognitionSamples(wavePath);
            var config = new OfflineSpeakerDiarizationConfig();
            var segmentation = config.Segmentation;
            var pyannote = segmentation.Pyannote;
            pyannote.Model = files.Segmentation;
            segmentation.Pyannote = pyannote;
            config.Segmentation = segmentation;
            var embedding = config.Embedding;
            embedding.Model = files.Embedding;
            config.Embedding = embedding;
            var clustering = config.Clustering;
            clustering.Threshold = 0.5f;
            config.Clustering = clustering;
            progress?.Report(0.1);
            cancellationToken.ThrowIfCancellationRequested();
            using var diariser = new OfflineSpeakerDiarization(config);
            OfflineSpeakerDiarizationSegment[] segments = diariser.Process(samples);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(1);
            return segments
                .Where(segment => float.IsFinite(segment.Start) && float.IsFinite(segment.End) &&
                    segment.Start >= 0 && segment.End > segment.Start)
                .Select(segment => new SpeechSpeakerSegment(segment.Start, segment.End, segment.Speaker))
                .ToArray();
        }

    public static IReadOnlyList<SpeechWord> AssignSpeakers(
        IReadOnlyList<SpeechWord> words, IReadOnlyList<SpeechSpeakerSegment> segments) =>
        words.Select(word =>
        {
            SpeechSpeakerSegment? match = segments
                .Where(segment => segment.EndSeconds > word.StartSeconds && segment.StartSeconds < word.EndSeconds)
                .OrderByDescending(segment =>
                    Math.Min(segment.EndSeconds, word.EndSeconds) - Math.Max(segment.StartSeconds, word.StartSeconds))
                .FirstOrDefault();
            return word with { SpeakerId = match?.SpeakerId };
        }).ToArray();

    private static float[] ReadRecognitionSamples(string wavePath)
    {
        using var reader = new WaveFileReader(wavePath);
        if (reader.WaveFormat.SampleRate != RecognitionSampleRate || reader.WaveFormat.Channels != 1)
        {
            throw new InvalidDataException("The speech model requires a 16 kHz mono WAV file.");
        }

        const long maximumSamples = RecognitionSampleRate * 60L * 60;
        if (reader.SampleCount > maximumSamples)
        {
            throw new InvalidDataException("The local speech models currently support up to one hour of audio per run.");
        }

        ISampleProvider provider = reader.ToSampleProvider();
        var samples = new float[checked((int)reader.SampleCount)];
        int offset = 0;
        int read;
        while (offset < samples.Length && (read = provider.Read(samples, offset, samples.Length - offset)) > 0)
        {
            offset += read;
        }

        return offset == samples.Length ? samples : samples[..offset];
    }

    /// <summary>
    /// Recognises a 16 kHz mono PCM WAV with the installed Windows desktop recogniser for <paramref name="cultureName"/>
    /// using free dictation. Everything runs on this device; no audio is uploaded.
    /// </summary>
    public static IReadOnlyList<SpeechWord> Recognise(string wavePath, string cultureName, double durationSeconds,
        CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wavePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(cultureName);
        RecognizerInfo? recogniser = SpeechRecognitionEngine.InstalledRecognizers()
            .FirstOrDefault(info => string.Equals(info.Culture.Name, cultureName, StringComparison.OrdinalIgnoreCase));
        if (recogniser is null)
        {
            throw new InvalidOperationException(
                $"No Windows speech recogniser is installed for {cultureName}. Add the speech language in Windows Settings.");
        }

        var words = new List<SpeechWord>();
        var completed = new ManualResetEventSlim();
        Exception? failure = null;
        using var engine = new SpeechRecognitionEngine(recogniser);
        engine.LoadGrammar(new DictationGrammar());
        engine.SetInputToWaveFile(wavePath);
        engine.SpeechRecognized += (_, args) => AddWords(args.Result, words);
        engine.RecognizeCompleted += (_, args) =>
        {
            if (args.Error is not null && args.Error is not OperationCanceledException)
            {
                failure = args.Error;
            }

            completed.Set();
        };
        engine.RecognizeAsync(RecognizeMode.Multiple);
        using (cancellationToken.Register(engine.RecognizeAsyncCancel))
        {
            while (!completed.Wait(250))
            {
                if (durationSeconds > 0)
                {
                    progress?.Report(Math.Clamp(engine.AudioPosition.TotalSeconds / durationSeconds, 0, 1));
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (failure is not null)
        {
            throw new InvalidOperationException($"Speech recognition failed: {failure.Message}", failure);
        }

        progress?.Report(1);
        return words;
    }

    private static void AddWords(RecognitionResult result, List<SpeechWord> words)
    {
        if (result.Audio is null || result.Words.Count == 0)
        {
            return;
        }

        double phraseStart = result.Audio.AudioPosition.TotalSeconds;
        double phraseEnd = phraseStart + result.Audio.Duration.TotalSeconds;
        for (int index = 0; index < result.Words.Count; index++)
        {
            RecognizedWordUnit word = result.Words[index];
            string text = word.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            double start = phraseStart;
            double end = phraseEnd;
            try
            {
                // Word audio positions are relative to the start of the recognised phrase.
                RecognizedAudio audio = result.GetAudioForWordRange(word, word);
                start = Math.Min(phraseStart + audio.AudioPosition.TotalSeconds, phraseEnd);
                end = Math.Min(start + audio.Duration.TotalSeconds, phraseEnd);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NullReferenceException)
            {
                // Some recognisers omit per-word audio; fall back to an even split across the phrase.
                double step = (phraseEnd - phraseStart) / result.Words.Count;
                start = phraseStart + step * index;
                end = start + step;
            }

            words.Add(new SpeechWord(start, Math.Max(end, start), text, result.Confidence));
        }
    }

    /// <summary>Averages every channel into mono and stops after the mix duration.</summary>
    private sealed class MonoDownmix(ExportAudioMixer mixer, long totalFrames) : ISampleProvider
    {
        private float[] _buffer = [];

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(mixer.SampleRate, 1);

        public long FramesRead { get; private set; }

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = (int)Math.Min(count, totalFrames - FramesRead);
            if (frames <= 0)
            {
                return 0;
            }

            int channels = mixer.Channels;
            if (_buffer.Length < frames * channels)
            {
                _buffer = new float[frames * channels];
            }

            int read = mixer.Read(_buffer, 0, frames * channels) / channels;
            for (int frame = 0; frame < read; frame++)
            {
                float sum = 0;
                for (int channel = 0; channel < channels; channel++)
                {
                    sum += _buffer[frame * channels + channel];
                }

                buffer[offset + frame] = sum / channels;
            }

            FramesRead += read;
            return read;
        }
    }
}
