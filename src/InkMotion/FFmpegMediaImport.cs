using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace InkMotion;

public static class FFmpegMediaImport
{
    public static bool CanPrepare(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".wma" or ".wmv" or ".av1" or ".mp4" or ".mov" or ".m4v" or ".mkv" or ".webm";

    public static string Prepare(string sourcePath, string executable, string directory, CancellationToken cancellationToken)
    {
        string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!CanPrepare(sourcePath)) return sourcePath;
        FFmpegAudioEncoder.Discover(executable, cancellationToken);
        if (extension is not (".wma" or ".wmv" or ".av1"))
        {
            string probe = Path.Combine(Path.GetDirectoryName(executable)!, "ffprobe.exe");
            if (!File.Exists(probe))
                throw new FileNotFoundException("AV1 compatibility imports require ffprobe.exe beside the selected FFmpeg executable.", probe);
            using JsonDocument metadata = JsonDocument.Parse(FFmpegAudioEncoder.Run(probe,
                ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name", "-of", "json", sourcePath],
                cancellationToken));
            if (!metadata.RootElement.GetProperty("streams").EnumerateArray().Any(stream =>
                    stream.GetProperty("codec_name").GetString() == "av1"))
                return sourcePath;
        }

        var source = new FileInfo(sourcePath);
        string identity = $"{source.FullName}|{source.Length}|{source.LastWriteTimeUtc.Ticks}|compatibility-v1";
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        Directory.CreateDirectory(directory);
        string name = Path.GetFileNameWithoutExtension(sourcePath);
        if (name.Length > 60) name = name[..60];
        string output = Path.Combine(directory, $"{name}-{key}" +
            (extension == ".wma" ? ".wav" : ".mp4"));
        if (File.Exists(output) && new FileInfo(output).Length > 0) return output;
        string temporary = Path.Combine(directory, $"{Guid.NewGuid():N}.partial");
        try
        {
            var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", sourcePath };
            if (extension == ".wma")
                arguments.AddRange(["-map", "0:a:0", "-vn", "-c:a", "pcm_f32le", "-f", "wav", temporary]);
            else
                arguments.AddRange(["-map", "0:v:0", "-map", "0:a:0?", "-c:v", "libx264", "-crf", "16",
                    "-preset", "fast", "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-pix_fmt", "yuv420p",
                    "-c:a", "aac", "-b:a", "320k", "-movflags", "+faststart", "-f", "mp4", temporary]);
            FFmpegAudioEncoder.Run(executable, arguments, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0)
                throw new IOException("FFmpeg did not produce an editing copy.");
            source.Refresh();
            if (identity != $"{source.FullName}|{source.Length}|{source.LastWriteTimeUtc.Ticks}|compatibility-v1")
                throw new IOException("The source changed during import. Try importing it again.");
            File.Move(temporary, output, overwrite: true);
            return output;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
