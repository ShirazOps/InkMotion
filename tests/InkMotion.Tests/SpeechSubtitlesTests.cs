using System.Speech.Synthesis;
using NAudio.Wave;
using Xunit;

namespace InkMotion.Tests;

public sealed class SpeechFactAttribute : FactAttribute
{
    public SpeechFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("INKMOTION_TEST_SPEECH") != "1")
            Skip = "Opt-in desktop test: set INKMOTION_TEST_SPEECH=1 on a machine with an English Windows speech recogniser and voice.";
    }
}

public sealed class SpeechSubtitlesTests
{
    [Fact]
    public void BuildCues_SplitsOnPausesSentencesLengthAndDuration()
    {
        SpeechWord[] words =
        [
            new(0.0, 0.3, "hello"), new(0.35, 0.7, "there."), new(0.8, 1.0, "next"), new(1.05, 1.3, "sentence"),
            new(3.0, 3.2, "after"), new(3.25, 3.5, "pause"),
        ];

        IReadOnlyList<SubtitleCueText> cues = SpeechSubtitles.BuildCues(words);

        Assert.Equal(["hello there.", "next sentence", "after pause"], cues.Select(cue => cue.Text));
        Assert.Equal(0, cues[0].StartSeconds);
        Assert.Equal(0.8, cues[0].EndSeconds, 6);
        Assert.Equal(1.8, cues[1].EndSeconds, 6);
        Assert.Equal(4.0, cues[2].EndSeconds, 6);

        SpeechWord[] longRun = Enumerable.Range(0, 40)
            .Select(index => new SpeechWord(index * 0.25, index * 0.25 + 0.2, "word" + index)).ToArray();
        IReadOnlyList<SubtitleCueText> limited = SpeechSubtitles.BuildCues(longRun);
        Assert.True(limited.Count > 1);
        Assert.All(limited, cue =>
        {
            Assert.True(cue.EndSeconds - cue.StartSeconds <= 6.0001);
            Assert.All(cue.Text.Split('\n'), line => Assert.True(line.Length <= 42));
            Assert.True(cue.Text.Split('\n').Length <= 2);
        });
        Assert.Equal(40, limited.Sum(cue => cue.Text.Split([' ', '\n']).Length));
        for (int index = 1; index < limited.Count; index++)
        {
            Assert.True(limited[index - 1].EndSeconds <= limited[index].StartSeconds);
        }
    }

    [Fact]
    public void BuildCues_IgnoresBlankAndInvalidWordsAndRejectsBadOptions()
    {
        Assert.Empty(SpeechSubtitles.BuildCues([new(0, 1, " "), new(double.NaN, 1, "x"), new(-1, 0, "y")]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SpeechSubtitles.BuildCues([], new SpeechCueOptions { MaximumLineCharacters = 2 }));
    }

    [Fact]
    public void WrapLines_BreaksNearTheMiddle()
    {
        Assert.Equal("short", SpeechSubtitles.WrapLines("short", 42));
        Assert.Equal("one two\nthree four", SpeechSubtitles.WrapLines("one two three four", 10));
        Assert.Equal("unbreakable", SpeechSubtitles.WrapLines("unbreakable", 4));
    }

    [Fact]
    public void GeneratedCuesBecomeOneEditableSubtitleTrackThatExportsAsSrt()
    {
        IReadOnlyList<SubtitleCueText> cues = SpeechSubtitles.BuildCues(
            [new(1.0, 1.4, "first"), new(1.45, 1.9, "line."), new(2.0, 2.5, "second")]);

        IReadOnlyList<TimelineTrackProject> tracks = SubtitleSrt.CreateTracks(cues, 25);

        TimelineTrackProject track = Assert.Single(tracks);
        Assert.Equal(TimelineTrackKind.Subtitle, track.Kind);
        Assert.Null(VideoTimelineEditing.Validate(tracks, 25));
        string srt = SubtitleSrt.Export(tracks, 25);
        Assert.Contains("00:00:01,000 --> 00:00:02,000\r\nfirst line.", srt);
        Assert.Contains("00:00:02,000 --> 00:00:03,000\r\nsecond", srt);
    }

    [Fact]
    public void RenderRecognitionMix_WritesAlignedNormalisedSixteenKilohertzMono()
    {
        string input = Path.Combine(Path.GetTempPath(), $"inkmotion-speech-src-{Guid.NewGuid():N}.wav");
        string output = Path.Combine(Path.GetTempPath(), $"inkmotion-speech-mix-{Guid.NewGuid():N}.wav");
        try
        {
            using (var writer = new WaveFileWriter(input, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)))
            {
                float[] tone = Enumerable.Range(0, 48000 * 2)
                    .Select(index => 0.2f * MathF.Sin(2 * MathF.PI * 440 * (index / 2) / 48000f)).ToArray();
                writer.WriteSamples(tone, 0, tone.Length);
            }

            var project = new EditorProject
            {
                AudioTracks = [new() { AudioPath = input, StartFrame = 2 * 30 + 1 }],
                FrameRate = 30,
            };

            double duration = SpeechSubtitles.RenderRecognitionMix(project, output, CancellationToken.None);

            Assert.InRange(duration, 2.9, 3.1);
            using var reader = new WaveFileReader(output);
            Assert.Equal(new WaveFormat(16000, 16, 1), reader.WaveFormat);
            float[] samples = new float[(int)(reader.SampleCount)];
            Assert.Equal(samples.Length, reader.ToSampleProvider().Read(samples, 0, samples.Length));
            Assert.True(samples.Take(30000).Max(Math.Abs) < 0.001f);
            Assert.InRange(samples.Skip(33000).Max(Math.Abs), 0.85f, 0.95f);
            Assert.False(File.Exists(output + ".float.wav"));
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    [Fact]
    public void RenderRecognitionMix_RejectsProjectsWithoutAudio()
    {
        string output = Path.Combine(Path.GetTempPath(), $"inkmotion-speech-empty-{Guid.NewGuid():N}.wav");
        Assert.Throws<InvalidOperationException>(() =>
            SpeechSubtitles.RenderRecognitionMix(new EditorProject(), output, CancellationToken.None));
        Assert.False(File.Exists(output));
    }

    [SpeechFact]
    public void Recognise_TranscribesSynthesisedSpeechAtItsTimelinePosition()
    {
        SpeechRecogniserInfo recogniser = SpeechSubtitles.GetInstalledRecognisers()
            .First(info => info.CultureName is "en-GB" or "en-US");
        string speech = Path.Combine(Path.GetTempPath(), $"inkmotion-tts-{Guid.NewGuid():N}.wav");
        string mix = Path.Combine(Path.GetTempPath(), $"inkmotion-tts-mix-{Guid.NewGuid():N}.wav");
        try
        {
            using (var synthesiser = new SpeechSynthesizer())
            {
                synthesiser.SetOutputToWaveFile(speech);
                synthesiser.Speak("Hello world. This is a test of the subtitle generator.");
            }

            var project = new EditorProject
            {
                FrameRate = 24,
                AudioTracks = [new() { AudioPath = speech, StartFrame = 3 * 24 + 1 }],
            };
            double duration = SpeechSubtitles.RenderRecognitionMix(project, mix, CancellationToken.None);
            IReadOnlyList<SpeechWord> words = SpeechSubtitles.Recognise(
                mix, recogniser.CultureName, duration, CancellationToken.None);

            Assert.NotEmpty(words);
            Assert.All(words, word => Assert.True(word.StartSeconds >= 2.8, $"{word.Text} at {word.StartSeconds}"));
            string text = string.Join(' ', words.Select(word => word.Text)).ToLowerInvariant();
            Assert.Contains("test", text);
            Assert.NotEmpty(SpeechSubtitles.BuildCues(words));
        }
        finally
        {
            File.Delete(speech);
            File.Delete(mix);
        }
    }
}
