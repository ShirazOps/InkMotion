using Xunit;

namespace InkMotion.Tests;

public sealed class CharacterVoiceGeneratorTests
{
    [Fact]
    public void ValidateRequest_RequiresTextVoiceAndDestination()
    {
        Assert.Throws<ArgumentException>(() =>
            CharacterVoiceGenerator.ValidateRequest("", "voice", 0, 100, "out.wav"));
        Assert.Throws<ArgumentException>(() =>
            CharacterVoiceGenerator.ValidateRequest("Hello", "", 0, 100, "out.wav"));
        Assert.Throws<ArgumentException>(() =>
            CharacterVoiceGenerator.ValidateRequest("Hello", "voice", 0, 100, ""));
    }

    [Theory]
    [InlineData(-11, 100)]
    [InlineData(11, 100)]
    [InlineData(0, -1)]
    [InlineData(0, 101)]
    public void ValidateRequest_RejectsRateAndVolumeOutsideWindowsRanges(int rate, int volume)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CharacterVoiceGenerator.ValidateRequest("Hello", "voice", rate, volume, "out.wav"));
    }

    [Fact]
    public void WriteWaveFile_UsesAnInstalledVoiceWhenAvailable()
    {
        CharacterVoiceOption? voice = CharacterVoiceGenerator.GetInstalledVoices().FirstOrDefault();
        if (voice is null)
        {
            return;
        }

        string directory = Path.Combine(Path.GetTempPath(), $"inkmotion-voice-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string output = Path.Combine(directory, "voice.wav");
        try
        {
            CharacterVoiceGenerator.WriteWaveFile("Hello from InkMotion.", voice.Name, 0, 100, output);

            byte[] header = File.ReadAllBytes(output)[..12];
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(header, 0, 4));
            Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(header, 8, 4));
        }
        finally
        {
            if (File.Exists(output))
            {
                File.Delete(output);
            }

            Directory.Delete(directory);
        }
    }
}
