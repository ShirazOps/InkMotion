using NAudio.Wave;

namespace InkMotion;

public static class AudioSourcePosition
{
    public static double DurationSeconds(AudioFileReader reader) =>
        (double)(reader.Length / reader.WaveFormat.BlockAlign) / reader.WaveFormat.SampleRate;

    public static void Seek(AudioFileReader reader, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), "Use a finite non-negative source position.");
        }

        long available = reader.Length / reader.WaveFormat.BlockAlign;
        long frame = (long)Math.Clamp(Math.Round(seconds * reader.WaveFormat.SampleRate), 0, available);
        reader.Position = checked(frame * reader.WaveFormat.BlockAlign);
    }
}
