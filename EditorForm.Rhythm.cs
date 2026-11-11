namespace OrchardSound;

public sealed partial class EditorForm
{
    sealed record RhythmFailure(bool TimedOut, string Message);
    readonly Dictionary<AudioTrack, RhythmFailure> rhythmFailures = [];
    Func<TrackState, CancellationToken, RhythmAnalysis> rhythmAnalyzer = AnalyzeVisibleRhythm;
    TimeSpan rhythmAnalysisTimeout = TimeSpan.FromMinutes(3);

    static RhythmAnalysis AnalyzeVisibleRhythm(TrackState snapshot, CancellationToken token)
    {
        if (snapshot.Frames > AudioFiles.MaxTrackBytes / (AudioTrack.Channels * sizeof(float)))
            throw new IOException("This edited track is too long to analyze at once. Export a smaller rhythmic section first.");
        var data = new float[checked((int)(snapshot.Frames * AudioTrack.Channels))]; long position = 0;
        foreach (var segment in snapshot.Segments)
            for (long frame = 0; frame < segment.Length; frame++, position++)
            {
                if ((position & 32767) == 0) token.ThrowIfCancellationRequested();
                data[position * 2] = snapshot.GetSample(segment.SourceStart + frame, 0); data[position * 2 + 1] = snapshot.GetSample(segment.SourceStart + frame, 1);
            }
        var visible = RhythmDetector.Analyze(data, AudioTrack.Rate, token);
        long Original(long frame)
        { foreach (var segment in snapshot.Segments) { if (frame < segment.Length) return segment.SourceStart + frame; frame -= segment.Length; } return -1; }
        return visible with
        {
            BeatFrames = visible.BeatFrames.Select(Original).Where(frame => frame >= 0).Distinct().Order().ToArray(),
            KickFrames = visible.KickFrames.Select(Original).Where(frame => frame >= 0).Distinct().Order().ToArray()
        };
    }

    async Task RefreshRhythmAsync()
    {
        if (resourcesDisposed) return;
        canvas.ShowBeats = beats.Checked; canvas.ShowKicks = kicks.Checked; canvas.SmartSnap = snap.Checked;
        canvas.Invalidate(); UpdateRhythmLabels();
        rhythmCancellation?.Cancel(); rhythmCancellation?.Dispose(); rhythmCancellation = null;
        if (!beats.Checked && !kicks.Checked && !snap.Checked) return;
        var owner = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        rhythmCancellation = owner; owner.CancelAfter(rhythmAnalysisTimeout); var token = owner.Token;
        try
        {
            foreach (var track in canvas.Tracks.Where(track => track.Rhythm == null).ToArray())
            {
                token.ThrowIfCancellationRequested(); rhythmFailures.Remove(track); UpdateRhythmLabels();
                try
                {
                    var snapshot = track.Snapshot(); var analyze = rhythmAnalyzer;
                    var result = await Task.Run(() => analyze(snapshot, token), token);
                    token.ThrowIfCancellationRequested();
                    if (IsDisposed || !ReferenceEquals(rhythmCancellation, owner) || !canvas.Tracks.Contains(track)) continue;
                    if (!snapshot.Segments.SequenceEqual(track.GetSegments()) || !snapshot.Patches.SequenceEqual(track.Patches)) continue;
                    track.Rhythm = result; UpdateRhythmLabels(); canvas.Invalidate();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    if (IsDisposed || !ReferenceEquals(rhythmCancellation, owner) || !canvas.Tracks.Contains(track)) continue;
                    rhythmFailures[track] = new(false, error.Message);
                    status.Text = "Rhythm analysis failed for " + track.Name + ": " + error.Message + " Click its BPM label to retry.";
                    UpdateRhythmLabels();
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed && ReferenceEquals(rhythmCancellation, owner) && !lifetime.IsCancellationRequested)
            {
                foreach (var track in canvas.Tracks.Where(track => track.Rhythm == null)) rhythmFailures[track] = new(true, "Analysis reached the three-minute limit.");
                status.Text = "Rhythm analysis timed out. Click a BPM label to retry."; UpdateRhythmLabels();
            }
        }
        finally
        {
            if (ReferenceEquals(rhythmCancellation, owner)) { rhythmCancellation = null; owner.Dispose(); }
        }
    }

    void UpdateRhythmLabels()
    {
        var loaded = canvas.Tracks.ToHashSet();
        foreach (var track in rhythmFailures.Keys.Where(track => !loaded.Contains(track)).ToArray()) rhythmFailures.Remove(track);
        foreach (var panel in trackControls)
        {
            if (panel.Tag is not ChannelSlot slot || slot.Track is not AudioTrack track) continue;
            var label = panel.Controls.OfType<Label>().FirstOrDefault(label => label.AccessibleName == "Estimated BPM");
            if (label == null) continue;
            label.Visible = true; label.Cursor = Cursors.Hand; label.Click -= RetryRhythm; label.Click += RetryRhythm;
            bool enabled = beats.Checked || kicks.Checked || snap.Checked;
            if (track.Rhythm != null) rhythmFailures.Remove(track);
            rhythmFailures.TryGetValue(track, out var failure);
            label.Text = !enabled ? "BPM: off (B)" : failure != null ? failure.TimedOut ? "BPM: timed out" : "BPM: error"
                : track.Rhythm == null ? "Analyzing BPM..." : track.Rhythm.Bpm.HasValue ? $"{track.Rhythm.Bpm.Value:0.#} BPM"
                : track.Rhythm.Status switch
                { RhythmAnalysisStatus.Silence => "BPM: silence", RhythmAnalysisStatus.TooShort => "BPM: too short", _ => "No steady tempo" };
            string detail = !enabled ? "Enable Beats, Kick hits or Smart snap to analyze rhythm."
                : failure != null ? failure.Message : track.Rhythm == null ? "Rhythm analysis is running."
                : track.Rhythm.Bpm.HasValue ? "Estimated tempo. Half-time and double-time values can both be plausible."
                : track.Rhythm.Status switch
                {
                    RhythmAnalysisStatus.Silence => "No audible energy was detected.",
                    RhythmAnalysisStatus.TooShort => "Use a longer rhythmic section with at least four attacks.",
                    _ => "No consistent tempo was found. A longer rhythmic section may help."
                };
            label.AccessibleDescription = detail + " Click to reanalyze this track."; tips.SetToolTip(label, label.AccessibleDescription);
        }
    }
    async void RetryRhythm(object? sender, EventArgs e)
    {
        if (busy || appClosing || resourcesDisposed || sender is not Label label || label.Parent?.Tag is not ChannelSlot slot || slot.Track is not AudioTrack track) return;
        rhythmFailures.Remove(track); track.Rhythm = null;
        if (!beats.Checked && !kicks.Checked && !snap.Checked) { beats.Checked = true; return; }
        status.Text = "Retrying rhythm analysis...";
        await RefreshRhythmAsync();
        if (!IsDisposed && canvas.Tracks.Contains(track) && track.Rhythm != null)
            status.Text = track.Rhythm.Bpm.HasValue ? $"Rhythm detected: {track.Rhythm.Bpm.Value:0.#} BPM." : "Rhythm analysis complete. " + label.Text + ".";
    }
}
