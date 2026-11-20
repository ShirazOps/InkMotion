using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace InkMotion;

public static class SubtitleSrt
{
    private static readonly Regex TimingLine = new(
        @"^\s*(?<start>\d{2,}:\d{2}:\d{2}[,.]\d{3})\s+-->\s+(?<end>\d{2,}:\d{2}:\d{2}[,.]\d{3})(?:\s+.*)?\s*$",
        RegexOptions.CultureInvariant);

    private sealed record Cue(double StartSeconds, double EndSeconds, string Text);

    public static IReadOnlyList<TimelineTrackProject> Import(string content, double frameRate)
    {
        ArgumentNullException.ThrowIfNull(content);
        ValidateFrameRate(frameRate);
        List<Cue> cues = Parse(content);
        var tracks = new List<TimelineTrackProject>();

        foreach (Cue cue in cues.OrderBy(cue => cue.StartSeconds).ThenBy(cue => cue.EndSeconds))
        {
            int startFrame = ToFrameNumber(cue.StartSeconds, frameRate);
            if (startFrame >= int.MaxValue - 1)
            {
                throw new InvalidDataException("A subtitle cue is outside the supported timeline range.");
            }
            int endFrame = Math.Max(startFrame + 1, ToFrameNumber(cue.EndSeconds, frameRate));
            double duration = (endFrame - startFrame) / frameRate;
            if (duration > VideoTimelineEditing.MaximumStillDurationSeconds || cue.Text.Length > 2048)
            {
                throw new InvalidDataException("A subtitle cue exceeds the supported duration or text length.");
            }

            var clip = new TimelineClipProject
            {
                StartFrame = startFrame,
                DurationSeconds = duration,
                Text = cue.Text,
                Y = 0.88,
                FontSize = 48,
                TextBackgroundColour = 0xB0000000,
            };
            VideoTimelineEditing.PlaceClip(tracks, TimelineTrackKind.Subtitle, null, clip, frameRate);
        }

        return tracks.OrderBy(track => track.Clips.Min(clip => clip.StartFrame)).ToArray();
    }

    public static string Export(IEnumerable<TimelineTrackProject> tracks, double frameRate)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ValidateFrameRate(frameRate);
        var cues = new List<(double StartSeconds, int TrackIndex, int ClipIndex, TimelineClipProject Clip)>();
        int trackIndex = 0;
        foreach (TimelineTrackProject track in tracks)
        {
            if (track.Kind == TimelineTrackKind.Subtitle)
            {
                for (int clipIndex = 0; clipIndex < track.Clips.Count; clipIndex++)
                {
                    TimelineClipProject clip = track.Clips[clipIndex];
                    if (string.IsNullOrWhiteSpace(clip.Text) ||
                        !double.IsFinite(clip.DurationSeconds) || clip.DurationSeconds <= 0)
                    {
                        throw new InvalidDataException("A subtitle clip has invalid text or timing.");
                    }

                    cues.Add((VideoTimelineEditing.GetStartSeconds(clip, frameRate), trackIndex, clipIndex, clip));
                }
            }

            trackIndex++;
        }

        var output = new StringBuilder();
        int number = 1;
        foreach (var cue in cues.OrderBy(cue => cue.StartSeconds)
                     .ThenBy(cue => cue.TrackIndex).ThenBy(cue => cue.ClipIndex))
        {
            double endSeconds = cue.StartSeconds + cue.Clip.DurationSeconds;
            output.Append(number++).Append("\r\n")
                .Append(FormatTime(cue.StartSeconds)).Append(" --> ").Append(FormatTime(endSeconds)).Append("\r\n")
                .Append(NormaliseLineEndings(cue.Clip.Text).Replace("\n", "\r\n", StringComparison.Ordinal))
                .Append("\r\n\r\n");
        }

        return output.ToString();
    }

    private static List<Cue> Parse(string content)
    {
        string[] lines = NormaliseLineEndings(content).Split('\n');
        if (lines.Length > 0)
        {
            lines[0] = lines[0].TrimStart('\uFEFF');
        }

        var cues = new List<Cue>();
        int index = 0;
        while (index < lines.Length)
        {
            while (index < lines.Length && string.IsNullOrWhiteSpace(lines[index]))
            {
                index++;
            }

            if (index >= lines.Length)
            {
                break;
            }

            int cueLine = index + 1;
            if (long.TryParse(lines[index].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                index++;
            }

            if (index >= lines.Length)
            {
                throw new InvalidDataException($"Subtitle cue at line {cueLine} has no timestamp.");
            }

            Match match = TimingLine.Match(lines[index]);
            if (!match.Success ||
                !TryParseTime(match.Groups["start"].Value, out double startSeconds) ||
                !TryParseTime(match.Groups["end"].Value, out double endSeconds) ||
                endSeconds <= startSeconds)
            {
                throw new InvalidDataException($"Subtitle cue at line {index + 1} has invalid timing.");
            }

            index++;
            int textStart = index;
            while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]))
            {
                index++;
            }

            string text = string.Join("\n", lines[textStart..index]).TrimEnd();
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidDataException($"Subtitle cue at line {cueLine} has no text.");
            }

            cues.Add(new(startSeconds, endSeconds, text));
        }

        if (cues.Count == 0)
        {
            throw new InvalidDataException("The subtitle file contains no cues.");
        }

        return cues;
    }

    private static bool TryParseTime(string value, out double seconds)
    {
        seconds = 0;
        string[] timeParts = value.Replace(',', '.').Split(':');
        if (timeParts.Length != 3)
        {
            return false;
        }

        string[] secondParts = timeParts[2].Split('.');
        if (secondParts.Length != 2 ||
            !long.TryParse(timeParts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long hours) ||
            !int.TryParse(timeParts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) ||
            !int.TryParse(secondParts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int wholeSeconds) ||
            !int.TryParse(secondParts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int milliseconds) ||
            minutes > 59 || wholeSeconds > 59 || milliseconds > 999)
        {
            return false;
        }

        seconds = hours * 3600d + minutes * 60d + wholeSeconds + milliseconds / 1000d;
        return double.IsFinite(seconds);
    }

    private static int ToFrameNumber(double seconds, double frameRate)
    {
        double frame = Math.Round(seconds * frameRate, MidpointRounding.AwayFromZero);
        if (!double.IsFinite(frame) || frame < 0 || frame >= int.MaxValue)
        {
            throw new InvalidDataException("A subtitle cue is outside the supported timeline range.");
        }

        return (int)frame + 1;
    }

    private static string FormatTime(double seconds)
    {
        double totalMilliseconds = Math.Round(seconds * 1000, MidpointRounding.AwayFromZero);
        if (!double.IsFinite(totalMilliseconds) || totalMilliseconds < 0 || totalMilliseconds > long.MaxValue)
        {
            throw new InvalidDataException("A subtitle clip is outside the supported timeline range.");
        }

        long milliseconds = (long)totalMilliseconds;
        long hours = milliseconds / 3_600_000;
        int minutes = (int)(milliseconds / 60_000 % 60);
        int wholeSeconds = (int)(milliseconds / 1_000 % 60);
        int fraction = (int)(milliseconds % 1_000);
        return string.Create(CultureInfo.InvariantCulture, $"{hours:00}:{minutes:00}:{wholeSeconds:00},{fraction:000}");
    }

    private static void ValidateFrameRate(double frameRate)
    {
        if (!double.IsFinite(frameRate) || frameRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameRate));
        }
    }

    private static string NormaliseLineEndings(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
