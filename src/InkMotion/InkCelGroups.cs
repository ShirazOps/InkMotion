namespace InkMotion;

public sealed record InkCelGroupRange(string Name, int First, int Last);

public static class InkCelGroups
{
    public static IReadOnlyList<InkCelGroupRange> GetRanges(IReadOnlyList<EditorFrame> frames)
    {
        var groups = new List<InkCelGroupRange>();
        for (int index = 0; index < frames.Count; index++)
        {
            string name = frames[index].GroupName;
            if (name.Length == 0)
            {
                continue;
            }
            int first = index;
            while (index + 1 < frames.Count && frames[index + 1].GroupName == name)
            {
                index++;
            }
            groups.Add(new(name, first, index));
        }
        return groups;
    }

    public static bool Assign(IReadOnlyList<EditorFrame> frames, int first, int last, string name)
    {
        if (first < 0 || last < first || last >= frames.Count || last - first >= 10000 ||
            name is null || name.Length > 80 || name != name.Trim() ||
            name.Any(char.IsControl))
        {
            throw new ArgumentException("Choose up to 10,000 existing cels and a group name of at most 80 characters without control characters or surrounding spaces.");
        }
        bool changed = false;
        for (int index = first; index <= last; index++)
        {
            changed |= frames[index].GroupName != name;
            frames[index].GroupName = name;
        }
        return changed;
    }
}
