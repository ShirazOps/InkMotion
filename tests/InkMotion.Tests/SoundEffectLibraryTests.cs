using NAudio.Wave;
using Xunit;

namespace InkMotion.Tests;

public sealed class SoundEffectLibraryTests
{
    public static TheoryData<string> EffectIds()
    {
        var data = new TheoryData<string>();
        foreach (SoundEffectDefinition effect in SoundEffectLibrary.All) data.Add(effect.Id);
        return data;
    }

    [Theory]
    [MemberData(nameof(EffectIds))]
    public void EffectsAreAudibleBoundedAndDeterministic(string id)
    {
        float[] samples = SoundEffectLibrary.Generate(id);
        float[] again = SoundEffectLibrary.Generate(id);

        Assert.Equal((int)Math.Round(SoundEffectLibrary.Get(id).DurationSeconds * SoundEffectLibrary.SampleRate), samples.Length);
        Assert.All(samples, sample => Assert.True(float.IsFinite(sample) && Math.Abs(sample) <= 0.851f));
        Assert.InRange(samples.Max(Math.Abs), 0.84f, 0.851f);
        Assert.Equal(samples, again);
        Assert.True(Math.Abs(samples[^1]) < 0.05f, $"{id} should end near silence, got {samples[^1]}.");
    }

    [Fact]
    public void EffectsAreDistinctSounds()
    {
        float[][] all = SoundEffectLibrary.All.Select(effect => SoundEffectLibrary.Generate(effect.Id)).ToArray();

        for (int a = 0; a < all.Length; a++)
        {
            for (int b = a + 1; b < all.Length; b++)
            {
                Assert.False(all[a].Length == all[b].Length && all[a].SequenceEqual(all[b]));
            }
        }
    }

    [Fact]
    public void WhooshPeaksMidwayWhileImpactPeaksAtOnset()
    {
        float[] whoosh = SoundEffectLibrary.Generate("whoosh");
        float[] impact = SoundEffectLibrary.Generate("impact");

        Assert.True(Rms(whoosh, 0.45, 0.65) > Rms(whoosh, 0, 0.1) * 3);
        Assert.True(Rms(impact, 0, 0.1) > Rms(impact, 0.8, 1) * 5);
    }

    [Fact]
    public void WritesReadableWavOnceAndRestoresOnlyLibraryFiles()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"inkmotion-sfx-{Guid.NewGuid():N}");
        try
        {
            string path = SoundEffectLibrary.EnsureFile("pop", directory);
            DateTime written = File.GetLastWriteTimeUtc(path);
            Assert.Equal(path, SoundEffectLibrary.EnsureFile("pop", directory));
            Assert.Equal(written, File.GetLastWriteTimeUtc(path));

            using (var reader = new AudioFileReader(path))
            {
                Assert.Equal(SoundEffectLibrary.SampleRate, reader.WaveFormat.SampleRate);
                Assert.Equal(1, reader.WaveFormat.Channels);
                Assert.InRange(reader.TotalTime.TotalSeconds, 0.14, 0.16);
            }

            Assert.False(SoundEffectLibrary.TryRestore(Path.Combine(directory, SoundEffectLibrary.GetFileName("click"))));
            Assert.False(SoundEffectLibrary.TryRestore(Path.Combine(SoundEffectLibrary.DefaultDirectory, "someone-else.wav")));
            Assert.False(SoundEffectLibrary.TryRestore(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static double Rms(float[] samples, double from, double to)
    {
        int start = (int)(samples.Length * from);
        int end = (int)(samples.Length * to);
        double sum = 0;
        for (int i = start; i < end; i++) sum += samples[i] * samples[i];
        return Math.Sqrt(sum / Math.Max(1, end - start));
    }
}
