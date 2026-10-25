using System.Diagnostics;
using System.Globalization;
using System.Text;
using NAudio.Wave;

namespace InkMotion;

public sealed record FFmpegAudioCapabilities(string ExecutablePath, string Version, bool Flac, bool Opus, bool Wma = false);

public static class FFmpegAudioEncoder
{
    public static FFmpegAudioCapabilities Discover(string executable, CancellationToken cancellationToken)
    {
        executable = ValidateExecutable(executable);
        string version = Run(executable, ["-version"], cancellationToken);
        string encoders = Run(executable, ["-hide_banner", "-encoders"], cancellationToken);
        bool HasEncoder(string name) => encoders.Split('\n').Any(line =>
        {
            string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return fields.Length >= 2 && fields[1] == name;
        });
        return new(executable, version.Split('\n')[0].Trim(), HasEncoder("flac"), HasEncoder("libopus"), HasEncoder("wmav2"));
    }

    public static void Encode(ISampleProvider source, AudioChannelLayout layout, double duration,
        AudioExportSettings settings, string outputPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        settings.Validate();
        using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        FFmpegAudioCapabilities capabilities = Discover(settings.FFmpegPath!, probeTimeout.Token);
        if (settings.Codec switch { AudioExportCodec.Flac => !capabilities.Flac,
            AudioExportCodec.Wma => !capabilities.Wma, _ => !capabilities.Opus })
        {
            throw new InvalidOperationException($"This FFmpeg build does not include the required {settings.Codec} encoder.");
        }
        // Encode beside the destination and replace it only after successful finalisation.
        string temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath))!,
            $".inkmotion-{Guid.NewGuid():N}{settings.Extension}");
        try
        {
            var arguments = new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                "-f", "f32le", "-ar", settings.SampleRate.ToString(CultureInfo.InvariantCulture),
                "-ac", source.WaveFormat.Channels.ToString(CultureInfo.InvariantCulture),
                "-channel_layout", $"0x{AudioChannelLayouts.Get(layout).ChannelMask:X}",
                "-i", "pipe:0", "-map", "0:a:0", "-vn",
            };
            if (settings.Codec == AudioExportCodec.Flac)
            {
                arguments.AddRange(["-c:a", "flac", "-sample_fmt", settings.BitsPerSample == 16 ? "s16" : "s32",
                    "-bits_per_raw_sample", settings.BitsPerSample.ToString(CultureInfo.InvariantCulture),
                    "-af", $"aresample=osf={(settings.BitsPerSample == 16 ? "s16" : "s32")}:dither_method=triangular:output_sample_bits={settings.BitsPerSample}",
                    "-compression_level", "5", "-f", "flac"]);
            }
            else if (settings.Codec == AudioExportCodec.Wma)
            {
                arguments.AddRange(["-c:a", "wmav2", "-b:a", settings.BitRate.ToString(CultureInfo.InvariantCulture),
                    "-f", "asf"]);
            }
            else
            {
                arguments.AddRange(["-c:a", "libopus", "-b:a", settings.BitRate.ToString(CultureInfo.InvariantCulture),
                    "-vbr", "on", "-ar", "48000", "-mapping_family", "0", "-f", "opus"]);
            }
            arguments.Add(temporary);
            using Process process = Start(capabilities.ExecutablePath, arguments, redirectInput: true);
            using CancellationTokenRegistration registration = cancellationToken.Register(() => Terminate(process));
            Task<string> errors = ReadTailAsync(process.StandardError);
            float[] samples = new float[4096 * source.WaveFormat.Channels];
            byte[] bytes = new byte[samples.Length * sizeof(float)];
            long written = 0;
            try
            {
                int read;
                while ((read = source.Read(samples, 0, samples.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Buffer.BlockCopy(samples, 0, bytes, 0, read * sizeof(float));
                    process.StandardInput.BaseStream.Write(bytes, 0, read * sizeof(float));
                    written += read;
                    progress?.Report(Math.Min(0.99, written / (duration * source.WaveFormat.SampleRate * source.WaveFormat.Channels)));
                }
                process.StandardInput.Close();
                process.WaitForExit();
                string diagnostic = errors.GetAwaiter().GetResult();
                cancellationToken.ThrowIfCancellationRequested();
                if (process.ExitCode != 0 || !File.Exists(temporary) || new FileInfo(temporary).Length == 0)
                {
                    throw new InvalidOperationException($"FFmpeg {settings.Codec} export failed (exit {process.ExitCode}): {diagnostic}");
                }
            }
            catch (IOException ex)
            {
                Terminate(process);
                process.WaitForExit();
                string diagnostic = errors.GetAwaiter().GetResult();
                cancellationToken.ThrowIfCancellationRequested();
                throw new IOException($"FFmpeg stopped accepting audio: {diagnostic}", ex);
            }
            finally
            {
                Terminate(process);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, outputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string ValidateExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            throw new FileNotFoundException("Select an existing local FFmpeg .exe using the Export page. FFmpeg is optional and is not bundled with InkMotion.", path);
        }
        return Path.GetFullPath(path);
    }

    internal static Process Start(string executable, IEnumerable<string> arguments, bool redirectInput = false)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
            RedirectStandardInput = redirectInput, RedirectStandardOutput = !redirectInput,
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new InvalidOperationException("Could not start FFmpeg.");
    }

    internal static string Run(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using Process process = Start(executable, arguments);
        using CancellationTokenRegistration registration = cancellationToken.Register(() => Terminate(process));
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errors = ReadTailAsync(process.StandardError);
        process.WaitForExit();
        cancellationToken.ThrowIfCancellationRequested();
        string diagnostic = errors.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"FFmpeg failed (exit {process.ExitCode}): {diagnostic}");
        }
        return output.GetAwaiter().GetResult();
    }

    internal static async Task<string> ReadTailAsync(StreamReader reader)
    {
        var tail = new StringBuilder();
        char[] buffer = new char[2048];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            tail.Append(buffer, 0, read);
            if (tail.Length > 8192) tail.Remove(0, tail.Length - 8192);
        }
        return tail.ToString().Trim();
    }

    internal static void Terminate(Process process)
    {
        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
        }
    }
}
