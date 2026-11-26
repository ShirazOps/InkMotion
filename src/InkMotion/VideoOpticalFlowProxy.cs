using System.Globalization;

namespace InkMotion;

public static class VideoOpticalFlowProxy
{
    public const double MaximumFrameRate = 240;

    public static VideoProxyResult Generate(
        string sourcePath,
        double durationSeconds,
        double projectFrameRate,
        string ffmpegPath,
        string directory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Path.IsPathFullyQualified(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("Choose an existing local video source.", sourcePath);
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0 ||
            !double.IsFinite(projectFrameRate) || projectFrameRate <= 0 || projectFrameRate > MaximumFrameRate)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), "A valid source duration and project frame rate are required.");
        }

        FFmpegAudioCapabilities capabilities = FFmpegAudioEncoder.Discover(ffmpegPath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        HdrVideoSourceInfo? info = HdrVideoProbe.Probe(capabilities.ExecutablePath, sourcePath);
        cancellationToken.ThrowIfCancellationRequested();
        if (info is null)
            throw new InvalidDataException("FFmpeg could not determine the video source's frame rate and colour transfer.");
        if (info.Transfer != VideoSourceTransfer.Sdr)
            throw new NotSupportedException("Optical-flow proxies currently support SDR sources only; HDR sources retain their original media.");

        double outputFrameRate = Math.Min(MaximumFrameRate, Math.Ceiling(projectFrameRate * 4));
        if (info.FrameRate >= outputFrameRate)
            throw new NotSupportedException("The source frame rate is already at or above the optical-flow proxy rate.");

        var source = new FileInfo(sourcePath);
        long sourceLength = source.Length;
        long sourceTicks = source.LastWriteTimeUtc.Ticks;
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, $"{Guid.NewGuid():N}.mp4");
        string temporary = destination + ".partial";
        try
        {
            string rate = outputFrameRate.ToString("R", CultureInfo.InvariantCulture);
            string duration = durationSeconds.ToString("R", CultureInfo.InvariantCulture);
            string padding = Math.Max(1, 4 / info.FrameRate).ToString("R", CultureInfo.InvariantCulture);
            FFmpegAudioEncoder.Run(capabilities.ExecutablePath,
            [
                "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", source.FullName,
                "-map", "0:v:0", "-an", "-sn", "-dn",
                "-vf", $"setpts=PTS-STARTPTS,tpad=stop_mode=clone:stop_duration={padding}," +
                    $"minterpolate=fps={rate}:mi_mode=mci:mc_mode=aobmc:me_mode=bidir:vsbmc=1," +
                    $"trim=duration={duration},pad=ceil(iw/2)*2:ceil(ih/2)*2",
                "-c:v", "libx264", "-crf", "16", "-preset", "fast", "-pix_fmt", "yuv420p",
                "-movflags", "+faststart", "-f", "mp4", temporary,
            ], cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0)
                throw new IOException("FFmpeg did not produce an optical-flow proxy.");
            source.Refresh();
            if (!source.Exists || source.Length != sourceLength || source.LastWriteTimeUtc.Ticks != sourceTicks)
                throw new IOException("The source video changed while its optical-flow proxy was being generated.");

            File.Move(temporary, destination);
            return new VideoProxyResult(destination, sourceLength, sourceTicks);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
