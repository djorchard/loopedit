namespace OrchardSound;

public sealed partial class WaveCanvas
{
    int spectralHoverPane = -1;
    internal event Action? SpectralHoverChanged;

    internal (int Pane, int MidiNote)? HoveredSpectralNote
    {
        get
        {
            int pane = spectralHoverPane;
            if (!pointerOverWave || pane < 0 || pane >= heights.Count || pane > Slots.Count || Slots.Count == 0
                || GetPaneViewMode(pane) != AudioViewMode.Spectrogram) return null;
            int slot = pane == 0 ? Math.Clamp(hoveredSlot, 0, Slots.Count - 1) : pane - 1;
            if (DisplayTrack(Slots[slot].Track) is not { Frames: > 0 }) return null;
            bool nested = pane == 0 && GetNestedPrecisionBounds().Contains(previousRawPointer);
            var note = GetSpectralNoteHover(pane, previousRawPointer, nested);
            return note is { } hit ? (pane, hit.MidiNote) : null;
        }
    }

    void UpdateSpectralHoverPane(int pane)
    {
        if (pane < 0 && spectralHoverPane < 0) return;
        spectralHoverPane = pane;
        SpectralHoverChanged?.Invoke();
    }
}
