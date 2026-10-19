namespace InkMotion;

public sealed class TimelineMarkerProject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int Frame { get; set; } = 1;
    public string Name { get; set; } = "Marker";
    public override string ToString() => $"F{Frame:D3} - {Name}";
}

public static class TimelineMarkers
{
    public static TimelineMarkerProject Clone(TimelineMarkerProject marker) => new()
    {
        Id = marker.Id,
        Frame = marker.Frame,
        Name = marker.Name,
    };

    public static int? FindAdjacent(IEnumerable<TimelineMarkerProject> markers, int frame, bool next) =>
        next
            ? markers.Where(marker => marker.Frame > frame).Select(marker => (int?)marker.Frame).Min()
            : markers.Where(marker => marker.Frame < frame).Select(marker => (int?)marker.Frame).Max();

    public static int ConvertFrame(int frame, double oldRate, double newRate)
    {
        if (frame < 1 || !double.IsFinite(oldRate) || oldRate <= 0 || !double.IsFinite(newRate) || newRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frame));
        }

        double converted = Math.Round((frame - 1d) / oldRate * newRate) + 1;
        if (converted > int.MaxValue)
        {
            throw new InvalidOperationException("A marker is too far along the timeline for the new frame rate.");
        }

        return (int)converted;
    }

    public static void Validate(IReadOnlyList<TimelineMarkerProject> markers)
    {
        if (markers.Any(marker => marker is null || string.IsNullOrWhiteSpace(marker.Id) ||
            marker.Frame < 1 || string.IsNullOrWhiteSpace(marker.Name) || marker.Name.Length > 200) ||
            markers.Select(marker => marker.Id).Distinct(StringComparer.Ordinal).Count() != markers.Count)
        {
            throw new InvalidDataException("Timeline markers need unique IDs, positive frame numbers and names of 1-200 characters.");
        }
    }
}
