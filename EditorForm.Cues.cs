namespace OrchardSound;

public sealed partial class EditorForm
{
    void AddCuePoint(long frame, string? label = null)
    {
        if (selections.Items.Any(s => s.Start == frame || s.End == frame)) return;
        string basis = string.IsNullOrWhiteSpace(label) ? "Cue" : new string(label.Where(c => !char.IsControl(c)).Take(100).ToArray());
        int number = 1; string name;
        do { name = basis + " " + number++; } while (selections.Items.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
        selections.SavePoint(name, frame);
    }
    void ImportTrackCues(AudioTrack track)
    {
        foreach (var cue in track.ImportedCues)
        { if (selections.Items.Count >= SelectionStore.MaxSelections) break; AddCuePoint(track.Offset + cue.Frame, cue.Name); }
        RefreshSelectionMenu();
    }
    void SyncCuePoints()
    {
        canvas.CuePoints.Clear();
        foreach (var section in selections.Items)
        {
            canvas.CuePoints.Add(new(section.Start, section.Start == section.End ? section.Name : section.Name + " start"));
            if (section.End != section.Start) canvas.CuePoints.Add(new(section.End, section.Name + " end"));
        }
        var unique = canvas.CuePoints.OrderBy(c => c.Frame).DistinctBy(c => c.Frame).ToArray();
        canvas.CuePoints.Clear(); canvas.CuePoints.AddRange(unique); canvas.Invalidate();
    }
}
