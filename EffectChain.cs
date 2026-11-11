namespace OrchardSound;

public static class EffectChain
{
    public static Task<EffectPreviewResult> PrepareAsync(IEnumerable<AudioTrack> allTracks, IEnumerable<AudioTrack> targets,
        long start, long end, IEnumerable<EffectSettings> effects, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(allTracks); ArgumentNullException.ThrowIfNull(targets); ArgumentNullException.ThrowIfNull(effects);
        cancel.ThrowIfCancellationRequested();
        if (end <= start) throw new ArgumentException("Choose a time range with a positive duration.");
        var originals = allTracks.ToArray(); var requested = targets.ToArray(); var settings = effects.ToArray();
        if (originals.Any(track => track == null) || requested.Any(track => track == null) || settings.Any(effect => effect == null))
            throw new ArgumentException("Effect chain inputs cannot be null.");
        if (requested.Any(track => !originals.Contains(track))) throw new ArgumentException("Effect targets must belong to this session.");
        var indices = Enumerable.Range(0, originals.Length).Where(index => requested.Contains(originals[index])).ToArray();
        var states = originals.Select(track => track.Snapshot()).ToArray();
        return Prepare(states, indices, start, end, settings, cancel);
    }

    static async Task<EffectPreviewResult> Prepare(TrackState[] states, int[] targets, long start, long end, EffectSettings[] settings, CancellationToken cancel)
    {
        var current = states.Select(state => new AudioTrack
        {
            Path = state.Source.Path, Samples = state.Source.Samples, Peaks = state.Source.Peaks, ImportedCues = state.Source.ImportedCues,
            Offset = state.Offset, Gain = state.Gain, Enabled = state.Enabled, Fades = state.Fades.ToList(), Patches = state.Patches, Segments = state.Segments
        }).ToArray();
        long? auditionEnd = null, selectionEnd = null; long processingEnd = end; long[]? cues = null; var details = new List<string>();
        foreach (var effect in settings)
        {
            cancel.ThrowIfCancellationRequested();
            var prepared = await EffectPreview.PrepareAsync(current, targets.Select(index => current[index]), start, processingEnd, effect, cancel).ConfigureAwait(false);
            if (prepared.SelectionEnd.HasValue)
            {
                long nextEnd = prepared.SelectionEnd.Value, delta = checked(nextEnd - processingEnd);
                if (auditionEnd is long previousEnd && previousEnd >= processingEnd) auditionEnd = checked(previousEnd + delta);
                else if (delta < 0 && auditionEnd > nextEnd) auditionEnd = nextEnd;
                processingEnd = nextEnd; selectionEnd = nextEnd;
            }
            if (prepared.AuditionEnd.HasValue) auditionEnd = Math.Max(auditionEnd ?? long.MinValue, prepared.AuditionEnd.Value);
            if (prepared.CueFrames != null) cues = prepared.CueFrames;
            if (!string.IsNullOrWhiteSpace(prepared.Detail)) details.Add(prepared.Detail);
            current = prepared.PlaybackTracks.Select(state => state.Source).ToArray();
        }
        cancel.ThrowIfCancellationRequested();
        // A chain is audition data only. Apply prepares its one chosen effect
        // separately against the current committed tracks.
        return new(current.Select(track => track.Snapshot()).ToArray(), [], auditionEnd, SelectionEnd: selectionEnd, CueFrames: cues,
            Detail: details.Count == 0 ? null : string.Join(" ", details));
    }
}
