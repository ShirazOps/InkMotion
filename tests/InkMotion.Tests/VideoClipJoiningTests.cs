using System.Text.Json;
using NAudio.Wave;
using Xunit;

namespace InkMotion.Tests;

public sealed class VideoClipJoiningTests
{
    [Fact]
    public void JoinedVideoAudio_MatchesSplitMixAtNativeSampleRateIncludingOuterFades()
    {
        string path = Path.Combine(Path.GetTempPath(), $"inkmotion-video-join-{Guid.NewGuid():N}.wav");
        try
        {
            using (var writer = new WaveFileWriter(path, new WaveFormat(48000, 16, 1)))
            {
                for (int sample = 0; sample < 48000; sample++)
                {
                    writer.WriteSample((float)(0.2 * Math.Sin(sample * Math.PI * 2 * 440 / 48000)));
                }
            }
            var left = new TimelineClipProject
            {
                SourcePath = path, SourceInSeconds = 0.125, SourceOutSeconds = 0.875,
                SourceDurationSeconds = 1, Volume = 0.5, FadeInSeconds = 0.1, FadeOutSeconds = 0.1,
            };
            var track = new TimelineTrackProject { Clips = [left] };
            TimelineClipProject right = Assert.IsType<TimelineClipProject>(VideoTimelineEditing.Split(track, left, 10, 24));
            var project = new EditorProject { VideoTracks = [track] };
            float[] before = ReadMix(project);
            TimelineClipProject joined = VideoClipJoining.Join(track, left, right, 24);
            track.Clips = [joined];
            float[] after = ReadMix(project);
            Assert.Equal(before.Length, after.Length);
            Assert.InRange(after.Max(Math.Abs), 0.05f, 0.15f);
            for (int sample = 0; sample < before.Length; sample++)
            {
                Assert.InRange(Math.Abs(before[sample] - after[sample]), 0, 0.00001f);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EmbeddedVideoAudio_UsesClipSpeedAndReverseAudioChoice()
    {
        string path = Path.Combine(Path.GetTempPath(), $"inkmotion-video-speed-{Guid.NewGuid():N}.wav");
        try
        {
            using (var writer = new WaveFileWriter(path, new WaveFormat(48000, 16, 1)))
            {
                for (int sample = 0; sample < 48000; sample++)
                {
                    writer.WriteSample((float)(0.2 * sample / 48000));
                }
            }

            var clip = new TimelineClipProject
            {
                SourcePath = path,
                SourceOutSeconds = 1,
                SourceDurationSeconds = 1,
                PlaybackSpeed = 2,
                IsReversed = true,
            };
            var project = new EditorProject
            {
                VideoTracks = [new TimelineTrackProject { Kind = TimelineTrackKind.Video, Clips = [clip] }],
            };
            using (var reader = new MediaFoundationReader(path))
            {
                var reverse = new ReverseSampleProvider(
                    reader.ToSampleProvider(),
                    seconds => reader.CurrentTime = TimeSpan.FromSeconds(seconds),
                    0,
                    1);
                float[] initial = new float[8192];
                int read = reverse.Read(initial, 0, initial.Length);
                Assert.True(initial[0] > initial[read - 1], $"Expected reader-backed reversal, got {initial[0]} then {initial[read - 1]}.");
            }

            using (var mixer = new ExportAudioMixer(project, 48000))
            {
                Assert.Equal(0.5, mixer.DurationSeconds, 6);
                float[] samples = new float[24000 * mixer.Channels];
                int read = mixer.Read(samples, 0, samples.Length);
                Assert.Equal(samples.Length, read);
                Assert.True(samples[0] > samples[^1], $"Expected reversed audio to descend, got {samples[0]} then {samples[^1]}.");
            }

            clip.IsReversed = false;
            using (var spedMixer = new ExportAudioMixer(project, 48000))
            {
                Assert.Equal(0.5, spedMixer.DurationSeconds, 6);
                float[] samples = new float[24000 * spedMixer.Channels];
                int read = spedMixer.Read(samples, 0, samples.Length);
                Assert.Equal(samples.Length, read);
                Assert.True(samples[0] < samples[^1], $"Expected forward audio to ascend, got {samples[0]} then {samples[^1]}.");
            }

            clip.IsReversed = true;
            clip.ReverseEmbeddedAudio = false;
            using var mutedMixer = new ExportAudioMixer(project, 48000);
            Assert.Equal(0, mutedMixer.SourceCount);
            Assert.Equal(0, mutedMixer.DurationSeconds);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Join_ReversedSplitPreservesSpeedAndSourceMapping()
    {
        var original = new TimelineClipProject
        {
            SourcePath = @"C:\Media\source.mp4",
            StartFrame = 25,
            SourceInSeconds = 2,
            SourceOutSeconds = 6,
            SourceDurationSeconds = 10,
            PlaybackSpeed = 2,
            IsReversed = true,
        };
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Video, Clips = [original] };

        TimelineClipProject right = Assert.IsType<TimelineClipProject>(
            VideoTimelineEditing.Split(track, original, 49, 24));
        TimelineClipProject joined = VideoClipJoining.Join(track, original, right, 24);

        Assert.Equal(2, joined.PlaybackSpeed);
        Assert.True(joined.IsReversed);
        Assert.Equal(2, joined.SourceInSeconds);
        Assert.Equal(6, joined.SourceOutSeconds);
        for (int frame = 25; frame < 121; frame++)
        {
            double seconds = VideoTimelineEditing.FrameToSeconds(frame, 24);
            TimelineClipProject part = seconds < VideoTimelineEditing.GetStartSeconds(right, 24) ? original : right;
            Assert.Equal(
                VideoTimelineEditing.GetSourceSeconds(part, seconds, 24),
                VideoTimelineEditing.GetSourceSeconds(joined, seconds, 24),
                8);
        }
    }

    [Fact]
    public void Join_RejectsClipsWithDifferentColourCurves()
    {
        TimelineClipProject left = new()
        {
            SourcePath = @"C:\Media\source.mp4",
            SourceOutSeconds = 2,
            SourceDurationSeconds = 4,
        };
        TimelineTrackProject track = new()
        {
            Kind = TimelineTrackKind.Video,
            Clips = [left],
        };
        TimelineClipProject right = Assert.IsType<TimelineClipProject>(
            VideoTimelineEditing.Split(track, left, 25, 24));
        right.RedCurve = [new(0, 0), new(0.5, 0.3), new(1, 1)];

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => VideoClipJoining.Join(track, left, right, 24));
        Assert.Contains("colour", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("enabled")]
    [InlineData("colour")]
    [InlineData("tolerance")]
    [InlineData("feather")]
    [InlineData("blur")]
    [InlineData("vignette")]
    [InlineData("stabilised")]
    public void Join_RejectsClipsWithDifferentVideoEffectSettings(string setting)
    {
        TimelineClipProject left = new()
        {
            SourcePath = @"C:\Media\source.mp4",
            SourceOutSeconds = 2,
            SourceDurationSeconds = 4,
        };
        TimelineTrackProject track = new()
        {
            Kind = TimelineTrackKind.Video,
            Clips = [left],
        };
        TimelineClipProject right = Assert.IsType<TimelineClipProject>(
            VideoTimelineEditing.Split(track, left, 25, 24));
        switch (setting)
        {
            case "enabled":
                right.ChromaKeyEnabled = true;
                break;
            case "colour":
                right.ChromaKeyColour = 0xFF0000FF;
                break;
            case "tolerance":
                right.ChromaKeyTolerance = 0.2;
                break;
            case "feather":
                right.ChromaKeyFeather = false;
                break;
            case "blur":
                right.BlurAmount = 2;
                break;
            case "vignette":
                right.VignetteAmount = 0.5;
                break;
            case "stabilised":
                right.TransformKeyframes = [new VideoTransformKeyframe { FrameOffset = 1, X = 0.45, Y = 0.5, Scale = 1.1 }];
                break;
        }

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => VideoClipJoining.Join(track, left, right, 24));
        Assert.Contains(
            setting == "stabilised" ? "keyframes" : "styling",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    private static float[] ReadMix(EditorProject project)
    {
        using var mixer = new ExportAudioMixer(project, 48000);
        Assert.Equal(0.75, mixer.DurationSeconds, 8);
        Assert.Empty(mixer.Warnings);
        float[] samples = new float[36000 * mixer.Channels];
        int offset = 0;
        while (offset < samples.Length)
        {
            int count = Math.Min(512 * mixer.Channels, samples.Length - offset);
            int read = mixer.Read(samples, offset, count);
            Assert.True(read > 0);
            offset += read;
        }
        return samples;
    }

    [Theory]
    [InlineData(24)]
    [InlineData(30)]
    [InlineData(60)]
    public void Join_ReversesSplitAndPreservesEveryFrameSourceOpacityAndAudioGain(int rate)
    {
        var original = new TimelineClipProject
        {
            SourcePath = @"C:\Media\source.mp4", StartFrame = rate + 1,
            SourceDurationSeconds = 10, SourceInSeconds = 2, SourceOutSeconds = 6,
            Opacity = 0.6, Volume = 0.4, FadeInSeconds = 0.5, FadeOutSeconds = 0.75,
        };
        var track = new TimelineTrackProject { Clips = [original] };
        TimelineClipProject right = Assert.IsType<TimelineClipProject>(
            VideoTimelineEditing.Split(track, original, 3 * rate + 1, rate));
        string before = JsonSerializer.Serialize(track);
        TimelineClipProject joined = VideoClipJoining.Join(track, original, right, rate);
        Assert.Equal(before, JsonSerializer.Serialize(track));
        Assert.NotSame(original, joined);
        Assert.Equal(original.Id, joined.Id);
        Assert.Equal(rate + 1, joined.StartFrame);
        Assert.Equal(2, joined.SourceInSeconds);
        Assert.Equal(6, joined.SourceOutSeconds);
        Assert.Equal(0.5, joined.FadeInSeconds);
        Assert.Equal(0.75, joined.FadeOutSeconds);
        for (int sample = rate * 2; sample < rate * 10; sample++)
        {
            double seconds = sample / (rate * 2d);
            TimelineClipProject clip = seconds < 3 ? original : right;
            Assert.Equal(VideoTimelineEditing.GetSourceSeconds(clip, seconds, rate),
                VideoTimelineEditing.GetSourceSeconds(joined, seconds, rate), 10);
            Assert.Equal(VideoTimelineEditing.GetOpacity(clip, track.Kind, seconds, rate),
                VideoTimelineEditing.GetOpacity(joined, track.Kind, seconds, rate), 10);
            double elapsed = seconds - VideoTimelineEditing.GetStartSeconds(clip, rate);
            Assert.Equal(ExportPlanning.GetVideoClipAudioGain(clip, elapsed),
                ExportPlanning.GetVideoClipAudioGain(joined, seconds - 1), 10);
        }
        track.Clips = [joined];
        var project = new EditorProject { FrameRate = rate, VideoTracks = [track] };
        EditorProject reopened = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        TimelineClipProject saved = Assert.Single(reopened.VideoTracks[0].Clips);
        Assert.Equal(JsonSerializer.Serialize(joined), JsonSerializer.Serialize(saved));
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("overlap")]
    [InlineData("source-gap")]
    [InlineData("repeated-source")]
    [InlineData("different-source")]
    [InlineData("different-duration")]
    [InlineData("unprobed")]
    [InlineData("boundary-fade-out")]
    [InlineData("boundary-fade-in")]
    [InlineData("long-outer-in")]
    [InlineData("long-outer-out")]
    [InlineData("volume")]
    [InlineData("opacity")]
    [InlineData("transform")]
    [InlineData("colour")]
    [InlineData("style")]
    [InlineData("locked")]
    [InlineData("image")]
    [InlineData("rate")]
    [InlineData("same-clip")]
    [InlineData("wrong-order")]
    [InlineData("absent")]
    public void Join_RejectsUnsafeCasesWithoutMutation(string reason)
    {
        TimelineTrackProject track = Track();
        TimelineClipProject left = track.Clips[0];
        TimelineClipProject right = track.Clips[1];
        double rate = 24;
        switch (reason)
        {
            case "gap": right.StartFrame++; break;
            case "overlap": right.StartFrame--; break;
            case "source-gap": right.SourceInSeconds += 0.25; break;
            case "repeated-source": right.SourceInSeconds = 1; break;
            case "different-source": right.SourcePath = @"C:\Media\other.mp4"; break;
            case "different-duration": right.SourceDurationSeconds = 12; break;
            case "unprobed": left.SourceDurationSeconds = 0; break;
            case "boundary-fade-out": left.FadeOutSeconds = 0.1; break;
            case "boundary-fade-in": right.FadeInSeconds = 0.1; break;
            case "long-outer-in": left.FadeInSeconds = 2.1; break;
            case "long-outer-out": right.FadeOutSeconds = 2.1; break;
            case "volume": right.Volume = 0.5; break;
            case "opacity": right.Opacity = 0.5; break;
            case "transform": right.X = 0.6; break;
            case "colour": right.Exposure = 1; break;
            case "style": right.Colour = 0xFF123456; break;
            case "locked": track.IsLocked = true; break;
            case "image": track.Kind = TimelineTrackKind.Image; break;
            case "rate": rate = double.NaN; break;
            case "same-clip": right = left; break;
            case "wrong-order": (left, right) = (right, left); break;
            case "absent": right = VideoTimelineEditing.Clone(right); break;
        }
        string before = JsonSerializer.Serialize(track);
        Assert.Throws<InvalidOperationException>(() => VideoClipJoining.Join(track, left, right, rate));
        Assert.Equal(before, JsonSerializer.Serialize(track));
    }

    [Fact]
    public void Join_UsesTimelineOrderCanonicalPathsAndImplicitSourceOut()
    {
        TimelineTrackProject track = Track();
        TimelineClipProject left = track.Clips[0];
        TimelineClipProject right = track.Clips[1];
        right.SourcePath = @"c:\media\folder\..\SOURCE.mp4";
        right.SourceOutSeconds = null;
        track.Clips.Reverse();
        TimelineClipProject joined = VideoClipJoining.Join(track, left, right, 24);
        Assert.Equal(10, joined.SourceOutSeconds);
        Assert.Equal(1, joined.StartFrame);
        Assert.Equal(10, joined.SourceDurationSeconds);
    }

    private static TimelineTrackProject Track() => new()
    {
        Clips =
        [
            new() { SourcePath = @"C:\Media\source.mp4", SourceInSeconds = 1, SourceOutSeconds = 3, SourceDurationSeconds = 10 },
            new() { SourcePath = @"C:\Media\source.mp4", StartFrame = 49, SourceInSeconds = 3, SourceOutSeconds = 5, SourceDurationSeconds = 10 },
        ],
    };
}
