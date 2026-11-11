namespace OrchardSound;

public sealed record SavedSelection(string Name, long Start, long End);

public sealed class SelectionStore
{
    public const int MaxSelections = 512;
    public const long MaxTimelineFrame = AudioTrack.Rate * 86400L * 30;
    readonly List<SavedSelection> items = [];
    public IReadOnlyList<SavedSelection> Items => items.AsReadOnly();

    public void Save(string name, long start, long end)
    {
        var selection = Validate(new SavedSelection(name, start, end));
        if (end <= start) throw new ArgumentException("Choose a nonempty selection before saving it.");
        int index = items.FindIndex(s => string.Equals(s.Name, selection.Name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) items[index] = selection;
        else
        {
            if (items.Count >= MaxSelections) throw new InvalidOperationException("This session has reached its saved selection limit.");
            items.Add(selection);
        }
    }

    public bool Remove(string name)
    {
        int index = items.FindIndex(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;
        items.RemoveAt(index); return true;
    }

    public SavedSelection[] Capture() => items.ToArray();
    public void SavePoint(string name, long frame)
    {
        var point = Validate(new(name, frame, frame));
        if (items.Count >= MaxSelections) throw new InvalidOperationException("This session has reached its cue point limit.");
        if (items.Any(s => string.Equals(s.Name, point.Name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Cue names must be unique.");
        items.Add(point);
    }
    public void ApplyInsert(long position, long duration)
    {
        if (duration <= 0) throw new ArgumentException("Insert duration must be positive.");
        var mapped = items.Select(s => Validate(s with { Start = s.Start > position ? checked(s.Start + duration) : s.Start, End = s.End > position ? checked(s.End + duration) : s.End })).ToArray();
        items.Clear(); items.AddRange(mapped);
    }

    public void Restore(IEnumerable<SavedSelection> selections)
    {
        ArgumentNullException.ThrowIfNull(selections);
        var restored = selections.Take(MaxSelections + 1).Select(Validate).ToArray();
        if (restored.Length > MaxSelections) throw new ArgumentException("Too many saved selections.");
        if (restored.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != restored.Length)
            throw new ArgumentException("Saved selection names must be unique.");
        items.Clear(); items.AddRange(restored);
    }

    public void ApplyDelete(long start, long end)
    {
        ValidateEdit(start, end);
        long duration = end - start;
        long Map(long frame) => frame <= start ? frame : frame >= end ? frame - duration : start;
        var mapped = items.Select(s => s with { Start = Map(s.Start), End = Map(s.End) }).ToArray();
        items.Clear(); items.AddRange(mapped);
    }

    public void ApplyTrim(long start, long end)
    {
        ValidateEdit(start, end);
        var mapped = items.Select(s => s with
        {
            Start = Math.Clamp(s.Start, start, end) - start,
            End = Math.Clamp(s.End, start, end) - start
        }).ToArray();
        items.Clear(); items.AddRange(mapped);
    }

    static void ValidateEdit(long start, long end)
    {
        if (start < -MaxTimelineFrame || end > MaxTimelineFrame || end <= start)
            throw new ArgumentException("Choose a valid selection to edit.");
    }

    static SavedSelection Validate(SavedSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        string name = selection.Name?.Trim() ?? "";
        if (name.Length == 0 || name.Length > 120 || name.Any(char.IsControl))
            throw new ArgumentException("Selection names must contain 1 to 120 visible characters.");
        if (selection.Start < -MaxTimelineFrame || selection.End > MaxTimelineFrame || selection.End < selection.Start)
            throw new ArgumentException("Saved selection bounds are invalid.");
        return selection with { Name = name };
    }
}
