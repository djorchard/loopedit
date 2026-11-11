using NAudio.Wave;

namespace OrchardSound;

public sealed partial class EditorForm
{
    NoteBandAudition? noteAudition;

    void RefreshNoteAudition()
    {
        if (noteAudition == null) return;
        var note = !busy && !appClosing && canvas.Enabled ? canvas.HoveredSpectralNote : null;
        noteAudition.MidiNote = note is { } hit && IsNoteAuditionEnabled(hit.Pane) ? hit.MidiNote : null;
    }

    ISampleProvider WithNoteAudition(ISampleProvider source)
    {
        // Monitor the existing device stream after buffering. Edits, export,
        // loop positions and output ownership remain independent of the filter.
        var monitored = new NoteBandAudition(source);
        noteAudition = monitored;
        RefreshNoteAudition();
        return monitored;
    }
}
