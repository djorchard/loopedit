namespace OrchardSound;

public sealed partial class EditorForm
{
    HoverHotkeys? hoverHotkeys;
    void InitializeAdditionalCommands()
    {
        InitializeMouseSensitivity();
        AddSelectionMenuCommand(EditorIcon.Silence, "Insert silence...", "Ctrl+I", () => _ = InsertSilence());
        Shown += (_, _) =>
        {
            if (hoverHotkeys != null) return;
            hoverHotkeys = new(this, DispatchHoverCommand, _ => FitSelectionView(), (key, down) => canvas.DispatchHoverModifier(key, down));
            try { hoverHotkeys.Start(); }
            catch (Exception error) { status.Text = "Hover shortcuts unavailable: " + error.Message; }
        };
        Disposed += (_, _) => { hoverHotkeys?.Dispose(); hoverHotkeys = null; };
    }
    internal bool DispatchAdditionalCommand(Keys keyData)
    {
        if (keyData != (Keys.Control | Keys.I)) return false;
        _ = InsertSilence(); return true;
    }
    void DispatchHoverCommand(Form target, Keys command)
    {
        if (ShortcutHelp.DispatchGuideCommand(target, command)) return;
        if (target is EffectDialog effect) { effect.DispatchHoverCommand(command); return; }
        DispatchEditorCommand(command);
    }
    async Task InsertSilence()
    {
        if (busy || appClosing) return;
        if (!canvas.Tracks.Any(track => track.Frames > 0)) { status.Text = "Load audio before inserting silence."; return; }
        using var dialog = new InsertSilenceDialog(canvas.HasSelection);
        if (dialog.ShowDialog(this) != DialogResult.OK) { if (!IsDisposed) { Activate(); canvas.Focus(); } return; }
        long position = dialog.PositionChoice switch { 1 => canvas.SelectionBounds.Start, 2 => canvas.SelectionBounds.End, _ => canvas.LastCursorFrame ?? canvas.CommittedRange.Min };
        await ApplySilence(position, dialog.DurationFrames);
        if (!IsDisposed && !appClosing) { Activate(); canvas.Focus(); }
    }
    internal async Task ApplySilence(long position, long frames)
    {
        if (busy || appClosing) return;
        bool resume = output != null && !scrubbing;
        CloseEffectTool(false); StopPlayback(); busy = true; canvas.Enabled = false;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        operation.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            var prepared = await TimelineSilence.PrepareAsync(canvas.Tracks, position, frames, operation.Token);
            operation.Token.ThrowIfCancellationRequested(); if (appClosing || IsDisposed) return;
            var mapped = new SelectionStore(); mapped.Restore(selections.Capture()); mapped.ApplyInsert(position, frames);
            int missing = new[] { position, prepared.End }.Count(frame => !mapped.Items.Any(point => point.Start == frame || point.End == frame));
            if (mapped.Items.Count + missing > SelectionStore.MaxSelections) throw new InvalidOperationException("This session has reached its cue point limit.");
            var before = CaptureEditState(); var priorUndo = undo.ToArray(); var priorRedo = redo.ToArray();
            busy = false; Remember(); busy = true;
            try
            {
                foreach (var replacement in prepared.ReplacementStates) { replacement.ApplyToSource(); replacement.Source.Rhythm = null; }
                selections.Restore(mapped.Capture()); AddCuePoint(position); AddCuePoint(prepared.End);
                canvas.SelectionStart = position; canvas.SelectionEnd = prepared.End; canvas.CursorFrame = position;
            }
            catch
            {
                RestoreEditState(before); undo.Clear(); redo.Clear();
                foreach (var item in priorUndo.Reverse()) undo.Push(item); foreach (var item in priorRedo.Reverse()) redo.Push(item);
                throw;
            }
            PruneAudioHistory(); RefreshSelectionMenu(); RebuildTrackControls(); SizeCanvas(); status.Text = "Silence inserted. Ctrl+Z to undo.";
        }
        catch (OperationCanceledException) { if (!IsDisposed) status.Text = "Silence insertion cancelled."; }
        catch (Exception error) { if (!IsDisposed) status.Text = "Could not insert silence: " + error.Message; }
        finally
        {
            busy = false;
            if (!IsDisposed && !appClosing) { canvas.Enabled = true; if (resume) StartPlayback(position); _ = RefreshRhythmAsync(); }
        }
    }
}
