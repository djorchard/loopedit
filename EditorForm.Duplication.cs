namespace OrchardSound;

public sealed partial class EditorForm
{
    void DuplicateSelection()
    {
        if (busy || !RequireSelection()) return;
        var bounds = canvas.SelectionBounds; long duration = bounds.End - bounds.Start;
        var before = CaptureEditState(); var oldUndo = undo.ToArray(); var oldRedo = redo.ToArray();
        try
        {
            var mapped = new SelectionStore(); mapped.Restore(selections.Capture()); mapped.ApplyInsert(bounds.End, duration);
            long newEnd = checked(bounds.End + duration);
            int needed = new[] { bounds.Start, bounds.End, newEnd }.Distinct().Count(frame => !mapped.Items.Any(item => item.Start == frame || item.End == frame));
            if (mapped.Items.Count + needed > SelectionStore.MaxSelections) throw new InvalidOperationException("This session has reached its cue point limit.");
            Remember(); StopPlayback(); rhythmCancellation?.Cancel(); InvalidateEffectPreview(); busy = true; canvas.Enabled = false;
            TimelineDuplication.Duplicate(canvas.Tracks, bounds.Start, bounds.End);
            selections.Restore(mapped.Capture()); AddCuePoint(bounds.Start); AddCuePoint(bounds.End); AddCuePoint(newEnd);
            canvas.SelectionStart = bounds.End; canvas.SelectionEnd = newEnd; canvas.CursorFrame = bounds.End;
            RebuildTrackControls(); RefreshSelectionMenu(); PruneAudioHistory(); SizeCanvas(); canvas.Invalidate();
            status.Text = "Selection duplicated after the original. Ctrl+Z to undo.";
        }
        catch (Exception error)
        {
            RestoreEditState(before); undo.Clear(); redo.Clear();
            foreach (var state in oldUndo.Reverse()) undo.Push(state);
            foreach (var state in oldRedo.Reverse()) redo.Push(state);
            RebuildTrackControls(); SizeCanvas(); status.Text = "Could not duplicate selection: " + error.Message;
        }
        finally
        {
            busy = false;
            if (!IsDisposed) { canvas.Enabled = true; canvas.Focus(); _ = RefreshRhythmAsync(); if (effectWindow != null) _ = RequestEffectPreview(); }
        }
    }
}
