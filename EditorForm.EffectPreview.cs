namespace OrchardSound;

public sealed partial class EditorForm
{
    sealed class EffectToolSession(EffectDialog window)
    { public EffectDialog Window { get; } = window; }
    readonly List<EffectToolSession> effectTools = [];
    Action<EffectDialog, EditorForm> effectWindowPresenter = static (window, owner) => window.Show(owner);
    EffectDialog? effectWindow;
    EffectPreviewResult? effectPreview;
    CancellationTokenSource? effectPreviewCancellation, effectApplyCancellation;
    EffectToolSession? applyingEffect;
    bool effectAutoPlay, wasPlayingBeforeEffect, closingEffect, appClosing, resumeAfterEffectClose;
    bool EffectPreviewActive => effectPreview != null && SnapshotEffectTools().Any(state => state.Window.PreviewEnabled);

    EffectToolSession[] SnapshotEffectTools()
    {
        effectTools.RemoveAll(state => state.Window.IsDisposed);
        if (effectWindow?.IsDisposed == true) effectWindow = effectTools.LastOrDefault()?.Window;
        // Preserve the existing single-window injection contract used by callers
        // that supply a tool directly instead of opening one through the menu.
        if (effectWindow != null && !effectTools.Any(state => state.Window == effectWindow)) effectTools.Add(new(effectWindow));
        return effectTools.ToArray();
    }

    void OpenEffectTool(EffectKind kind)
    {
        if (busy || appClosing || !canvas.Tracks.Any(track => track.Frames > 0)) return;
        bool first = SnapshotEffectTools().Length == 0;
        if (first) { wasPlayingBeforeEffect = output != null && !scrubbing; resumeAfterEffectClose = false; }
        effectAutoPlay = true;
        var window = new EffectDialog(kind); var state = new EffectToolSession(window);
        int duplicate = effectTools.Count(tool => tool.Window.Settings.Kind == kind);
        if (duplicate > 0) window.Text += " " + (duplicate + 1);
        effectTools.Add(state); effectWindow = window;
        window.Activated += (_, _) => { if (effectTools.Contains(state)) effectWindow = window; };
        window.SettingsChanged += () => _ = RequestEffectPreview();
        window.ApplyRequested += async () => await CommitEffectInstance(state);
        window.CancelRequested += () => CloseEffectInstance(state, true);
        window.FormClosed += (_, _) => { if (effectTools.Contains(state)) CloseEffectInstance(state, true); };
        window.StopPlaybackRequested += () => { effectAutoPlay = false; StopPlayback(); };
        window.RestartPlaybackRequested += RestartLoop; window.CursorPlaybackRequested += StartCursorPlayback;
        try { effectWindowPresenter(window, this); _ = RequestEffectPreview(); }
        catch (Exception error) { CloseEffectInstance(state, true); if (!IsDisposed) status.Text = "Could not open effect: " + error.Message; }
    }

    void CancelChainPreview()
    {
        effectPreviewCancellation?.Cancel(); effectPreviewCancellation?.Dispose(); effectPreviewCancellation = null;
    }

    async Task RequestEffectPreview()
    {
        if (busy || appClosing || resourcesDisposed) return;
        var tools = SnapshotEffectTools(); if (tools.Length == 0) return;
        CancelChainPreview();
        var stages = tools.Where(state => state.Window.PreviewEnabled).Select(state => state.Window.Settings).ToArray();
        if (stages.Length == 0)
        {
            effectPreview = null; canvas.PreviewSources.Clear(); canvas.Invalidate();
            if (effectAutoPlay) ReplacePlayback(playbackBuffer?.Position ?? canvas.SelectionBounds.Start);
            return;
        }
        var owner = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); effectPreviewCancellation = owner;
        owner.CancelAfter(TimeSpan.FromMinutes(3)); var token = owner.Token;
        try
        {
            await Task.Delay(140, token);
            var bounds = canvas.SelectionBounds; var tracks = canvas.Tracks.ToArray();
            var prepared = await EffectChain.PrepareAsync(tracks, EffectTargets().ToArray(), bounds.Start, bounds.End, stages, token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(effectPreviewCancellation, owner) || appClosing || resourcesDisposed || SnapshotEffectTools().Length == 0) return;
            effectPreview = prepared; canvas.PreviewSources.Clear();
            for (int index = 0; index < Math.Min(tracks.Length, prepared.PlaybackTracks.Length); index++) canvas.PreviewSources[tracks[index]] = prepared.PlaybackTracks[index].Source;
            canvas.Invalidate(); long? position = playbackBuffer?.Position ?? playing?.Position;
            if (effectAutoPlay) ReplacePlayback(position ?? bounds.Start);
            status.Text = "Live effect chain. " + (prepared.Detail == null ? "" : prepared.Detail + " ") + "Each Apply commits only that window; Cancel removes only that window.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!IsDisposed && ReferenceEquals(effectPreviewCancellation, owner)) status.Text = "Preview unavailable: " + error.Message; }
    }

    async Task CommitEffectPreview()
    {
        var state = SnapshotEffectTools().FirstOrDefault(tool => tool.Window == effectWindow);
        if (state != null) await CommitEffectInstance(state);
    }

    async Task CommitEffectInstance(EffectToolSession state)
    {
        if (busy || appClosing || !SnapshotEffectTools().Contains(state)) return;
        CancelChainPreview(); var bounds = canvas.SelectionBounds; var settings = state.Window.Settings;
        var owner = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); effectApplyCancellation = owner; applyingEffect = state;
        owner.CancelAfter(TimeSpan.FromMinutes(3)); var token = owner.Token;
        bool committed = false; busy = true; canvas.Enabled = false;
        foreach (var tool in SnapshotEffectTools()) tool.Window.Enabled = false;
        StopPlayback();
        try
        {
            // Deliberately process committed source audio, not the combined
            // audition chain. Other windows remain uncommitted and open.
            var prepared = await EffectPreview.PrepareAsync(canvas.Tracks.ToArray(), EffectTargets().ToArray(), bounds.Start, bounds.End, settings, token);
            token.ThrowIfCancellationRequested();
            if (appClosing || !effectTools.Contains(state)) return;
            if (prepared.Changes.Length == 0 && prepared.ReplacementStates is not { Length: > 0 }) throw new InvalidOperationException("Tick a track containing selected audio before applying this effect.");
            SelectionStore? mappedSelections = null; var boundaries = new List<long>();
            if (prepared.SelectionEnd.HasValue)
            {
                long newEnd = prepared.SelectionEnd.Value, duration = bounds.End - bounds.Start;
                var cues = prepared.CueFrames;
                if (newEnd <= bounds.Start || newEnd > SelectionStore.MaxTimelineFrame || duration <= 0 || prepared.ReplacementStates is not { Length: > 0 }
                    || cues == null || cues.Length < 2 || cues.Length > SelectionStore.MaxSelections || cues[0] != bounds.Start || cues[^1] != newEnd
                    || cues.Any(frame => frame < bounds.Start || frame > newEnd) || cues.Zip(cues.Skip(1)).Any(pair => pair.Second <= pair.First))
                    throw new InvalidOperationException("The effect returned invalid selection bounds.");
                mappedSelections = new SelectionStore(); mappedSelections.Restore(selections.Capture());
                if (newEnd > bounds.End) mappedSelections.ApplyInsert(bounds.End, newEnd - bounds.End);
                else if (newEnd < bounds.End) mappedSelections.ApplyDelete(newEnd, bounds.End);
                boundaries.AddRange(cues);
                int needed = boundaries.Count(frame => !mappedSelections.Items.Any(item => item.Start == frame || item.End == frame));
                if (mappedSelections.Items.Count + needed > SelectionStore.MaxSelections) throw new InvalidOperationException("This session has reached its cue point limit.");
            }
            var before = CaptureEditState(); var oldUndo = undo.ToArray(); var oldRedo = redo.ToArray();
            busy = false; Remember(); busy = true;
            try
            {
                if (prepared.ReplacementStates != null)
                {
                    foreach (var replacement in prepared.ReplacementStates) { replacement.ApplyToSource(); replacement.Source.Rhythm = null; }
                }
                else
                {
                    foreach (var change in prepared.Changes)
                    {
                        change.Track.WriteVisibleSamples(change.VisibleStart, change.Samples);
                        Editing.RemoveFadesInRange(change.Track, change.VisibleStart, change.VisibleStart + change.Samples.LongLength / AudioTrack.Channels);
                        change.Track.Rhythm = null;
                    }
                }
                if (mappedSelections != null)
                {
                    selections.Restore(mappedSelections.Capture()); foreach (long frame in boundaries) AddCuePoint(frame);
                    canvas.SelectionStart = bounds.Start; canvas.SelectionEnd = prepared.SelectionEnd; RefreshSelectionMenu();
                }
            }
            catch
            {
                RestoreEditState(before); undo.Clear(); redo.Clear();
                foreach (var edit in oldUndo.Reverse()) undo.Push(edit);
                foreach (var edit in oldRedo.Reverse()) redo.Push(edit);
                throw;
            }
            PruneAudioHistory(); committed = true; CloseEffectInstance(state, false, false);
            RebuildTrackControls(); SizeCanvas(); status.Text = (prepared.Detail == null ? "" : prepared.Detail + " ") + "Effect applied. Other effect windows remain in preview. Ctrl+Z to undo.";
        }
        catch (OperationCanceledException) { if (!IsDisposed && !appClosing) status.Text = "Effect cancelled."; }
        catch (Exception error) { if (!IsDisposed) status.Text = "Effect failed: " + error.Message; }
        finally
        {
            if (ReferenceEquals(effectApplyCancellation, owner)) effectApplyCancellation = null;
            owner.Dispose(); applyingEffect = null; busy = false;
            if (!IsDisposed)
            {
                canvas.Enabled = true;
                foreach (var tool in SnapshotEffectTools()) tool.Window.Enabled = true;
                if (!appClosing && effectWindow != null) await RequestEffectPreview();
                else if (!appClosing && ((committed && effectAutoPlay) || resumeAfterEffectClose))
                { resumeAfterEffectClose = false; StartPlayback(bounds.Start); }
                FocusAfterEffectClose(); _ = RefreshRhythmAsync();
            }
        }
    }

    void CloseEffectInstance(EffectToolSession state, bool restorePlayback, bool rebuild = true)
    {
        if (closingEffect || !effectTools.Remove(state)) return;
        if (ReferenceEquals(applyingEffect, state)) effectApplyCancellation?.Cancel();
        InvalidateEffectPreview();
        var window = state.Window;
        if (effectWindow == window) effectWindow = effectTools.LastOrDefault(tool => !tool.Window.IsDisposed)?.Window;
        window.Applied = true;
        if (!window.IsClosing && !window.IsDisposed) { window.Close(); window.Dispose(); }
        if (effectWindow == null)
        {
            StopPlayback(); resumeAfterEffectClose = restorePlayback && wasPlayingBeforeEffect && !appClosing && !resourcesDisposed;
            if (resumeAfterEffectClose && !busy) { resumeAfterEffectClose = false; StartPlayback(canvas.SelectionBounds.Start); }
        }
        else if (rebuild && !busy)
        {
            if (output != null && !scrubbing) ReplacePlayback(playbackBuffer?.Position ?? playing?.Position);
            _ = RequestEffectPreview();
        }
        FocusAfterEffectClose();
    }

    void CloseEffectTool(bool restorePlayback)
    {
        if (closingEffect) return; closingEffect = true;
        try
        {
            var tools = SnapshotEffectTools(); CancelChainPreview(); effectApplyCancellation?.Cancel();
            effectTools.Clear(); effectWindow = null; effectPreview = null; canvas.PreviewSources.Clear(); canvas.Invalidate(); StopPlayback();
            foreach (var state in tools)
            {
                var window = state.Window; window.Applied = true;
                if (!window.IsClosing && !window.IsDisposed) { window.Close(); window.Dispose(); }
            }
            resumeAfterEffectClose = restorePlayback && wasPlayingBeforeEffect && !appClosing && !resourcesDisposed;
            if (resumeAfterEffectClose && !busy) { resumeAfterEffectClose = false; StartPlayback(canvas.SelectionBounds.Start); }
            if (tools.Length > 0) FocusAfterEffectClose();
        }
        finally { closingEffect = false; }
    }

    void FocusAfterEffectClose()
    { if (!appClosing && !resourcesDisposed && !IsDisposed && IsHandleCreated) { Activate(); canvas.Focus(); } }

    void InvalidateEffectPreview()
    { CancelChainPreview(); effectPreview = null; canvas.PreviewSources.Clear(); canvas.Invalidate(); }
}
