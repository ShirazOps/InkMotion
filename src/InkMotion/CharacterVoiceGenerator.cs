using System.Speech.AudioFormat;
using System.Speech.Synthesis;

namespace InkMotion;

public sealed record CharacterVoiceOption(string Name, string CultureName, string Gender)
{
    public string DisplayName => $"{Name} · {CultureName} · {Gender}";
}

public static class CharacterVoiceGenerator
{
    public static IReadOnlyList<CharacterVoiceOption> GetInstalledVoices()
    {
        using var synthesizer = new SpeechSynthesizer();
        return synthesizer.GetInstalledVoices()
            .Where(voice => voice.Enabled)
            .Select(voice => new CharacterVoiceOption(
                voice.VoiceInfo.Name,
                voice.VoiceInfo.Culture.Name,
                voice.VoiceInfo.Gender.ToString()))
            .OrderBy(voice => voice.CultureName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(voice => voice.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static void ValidateRequest(string text, string voiceName, int rate, int volume, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (rate is < -10 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(rate), "Speech rate must be between -10 and 10.");
        }

        if (volume is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(volume), "Voice volume must be between 0 and 100.");
        }
    }

    public static void WriteWaveFile(string text, string voiceName, int rate, int volume, string outputPath)
    {
        ValidateRequest(text, voiceName, rate, volume, outputPath);
        string destination = Path.GetFullPath(outputPath);
        string temporary = destination + $".{Guid.NewGuid():N}.tmp.wav";
        try
        {
            using (var synthesizer = new SpeechSynthesizer())
            {
                synthesizer.SelectVoice(voiceName);
                synthesizer.Rate = rate;
                synthesizer.Volume = volume;
                synthesizer.SetOutputToWaveFile(
                    temporary,
                    new SpeechAudioFormatInfo(24_000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
                synthesizer.Speak(text);
                synthesizer.SetOutputToNull();
            }

            if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0)
            {
                throw new IOException("Windows speech synthesis did not produce an audio file.");
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
