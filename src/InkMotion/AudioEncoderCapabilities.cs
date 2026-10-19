using NAudio.MediaFoundation;
using NAudio.Wave;

namespace InkMotion;

public sealed record AudioEncoderFormat(AudioExportCodec Codec, int SampleRate, int Channels, int BitRate);

public static class AudioEncoderCapabilities
{
    public static IReadOnlyList<AudioEncoderFormat> Discover()
    {
        var formats = new List<AudioEncoderFormat>();
        foreach (AudioExportCodec codec in new[] { AudioExportCodec.Mp3, AudioExportCodec.Aac })
        {
            MediaType[] types = GetTypes(codec);
            try
            {
                formats.AddRange(types.Select(type => new AudioEncoderFormat(
                    codec, type.SampleRate, type.ChannelCount, type.AverageBytesPerSecond * 8)));
            }
            finally
            {
                Release(types);
            }
        }

        return formats.Where(format => format.SampleRate is 44100 or 48000 && format.Channels is 1 or 2 &&
            format.BitRate is 128000 or 160000 or 192000 or 256000 or 320000)
            .Distinct().OrderBy(format => format.Codec).ThenBy(format => format.Channels)
            .ThenBy(format => format.SampleRate).ThenBy(format => format.BitRate).ToArray();
    }

    public static void EncodeExact(IWaveProvider source, string path, AudioExportSettings settings, Action? encodingStarted = null)
    {
        MediaType[] types = GetTypes(settings.Codec);
        try
        {
            MediaType? selected = types.FirstOrDefault(type =>
                type.SampleRate == settings.SampleRate &&
                type.ChannelCount == source.WaveFormat.Channels &&
                type.AverageBytesPerSecond * 8 == settings.BitRate);
            if (selected is null)
            {
                throw new InvalidOperationException(
                    $"No Windows {settings.Codec} encoder offers {settings.SampleRate:N0} Hz, {source.WaveFormat.Channels} channels at exactly {settings.BitRate / 1000} kbps. Check installed audio encoders or choose WAV.");
            }

            using var encoder = new MediaFoundationEncoder(selected);
            encodingStarted?.Invoke();
            encoder.Encode(path, source);
        }
        finally
        {
            Release(types);
        }
    }

    private static MediaType[] GetTypes(AudioExportCodec codec)
    {
        Guid subtype = codec switch
        {
            AudioExportCodec.Mp3 => AudioSubtypes.MFAudioFormat_MP3,
            AudioExportCodec.Aac => AudioSubtypes.MFAudioFormat_AAC,
            _ => throw new ArgumentOutOfRangeException(nameof(codec), "Only Windows MP3/AAC encoders need discovery."),
        };
        MediaFoundationApi.Startup();
        return MediaFoundationEncoder.GetOutputMediaTypes(subtype);
    }

    private static void Release(IEnumerable<MediaType> types)
    {
        foreach (MediaType type in types)
        {
            MediaFoundationGuids.Release(type.MediaFoundationObject);
        }
    }
}
