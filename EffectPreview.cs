namespace OrchardSound;

public sealed record EffectPreviewChange(AudioTrack Track, long VisibleStart, float[] Samples);
public sealed record EffectPreviewResult(TrackState[] PlaybackTracks, EffectPreviewChange[] Changes, long? AuditionEnd = null,
    TrackState[]? ReplacementStates = null, long? SelectionEnd = null, long[]? CueFrames = null, string? Detail = null);

public static class EffectPreview
{
    public const long MaxSelectedBytes = 512L * 1024 * 1024;

    // Capture every edit map on the caller's thread before scheduling work. Source
    // sample buffers and patch buffers are immutable and may be shared by clones.
    public static Task<EffectPreviewResult> PrepareAsync(IEnumerable<AudioTrack> allTracks, IEnumerable<AudioTrack> targets,
        long start, long end, EffectSettings settings, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(allTracks); ArgumentNullException.ThrowIfNull(targets); ArgumentNullException.ThrowIfNull(settings);
        cancel.ThrowIfCancellationRequested();
        if (end <= start) throw new ArgumentException("Choose a time range with a positive duration.");
        var originals = allTracks.ToArray(); var requested = targets.ToArray();
        if (originals.Any(track => track == null) || requested.Any(track => track == null)) throw new ArgumentException("Preview tracks cannot be null.");
        var targetSet = new HashSet<AudioTrack>(requested, ReferenceEqualityComparer.Instance);
        if (targetSet.Any(track => !originals.Contains(track))) throw new ArgumentException("Effect targets must belong to this session.");
        var states = originals.Select(track => track.Snapshot()).ToArray();
        if (settings.Kind == EffectKind.SeamlessRepeat) return SeamlessRepeatEffect.PrepareAsync(states, targetSet, start, end, settings.SeamlessRepeat ?? new SeamlessRepeatSettings(), cancel);
        bool wholeTrack = settings.Kind == EffectKind.Maximize && !settings.SelectionOnly;
        var work = new List<(int Index, long Start, long End)>(); long selectedBytes = 0;
        for (int i = 0; i < states.Length; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var state = states[i]; if (!targetSet.Contains(originals[i]) || settings.Kind == EffectKind.Multiply && !state.Enabled) continue;
            long trackEnd;
            try { trackEnd = checked(state.Offset + state.Frames); }
            catch (OverflowException) { throw new ArgumentException("A track has invalid timeline bounds."); }
            long from = wholeTrack ? state.Offset : Math.Max(start, state.Offset), to = wholeTrack ? trackEnd : Math.Min(end, trackEnd);
            if (to <= from) continue;
            long count = to - from;
            if (count > (MaxSelectedBytes - selectedBytes) / (AudioTrack.Channels * sizeof(float)))
                throw new IOException(wholeTrack ? "The effect audio exceeds the 512 MiB preview limit. Turn on Apply to selection only or use fewer tracks." : "The selected effect audio exceeds the 512 MiB preview limit. Choose a smaller range or fewer tracks.");
            selectedBytes += count * AudioTrack.Channels * sizeof(float);
            work.Add((i, from - state.Offset, to - state.Offset));
        }
        if (work.Count == 0) throw new InvalidOperationException(wholeTrack ? "No target tracks contain audio." : "No target tracks overlap the selected time range.");
        return Task.Run(() => Prepare(states, originals, work, settings, cancel), cancel);
    }

    static EffectPreviewResult Prepare(TrackState[] states, AudioTrack[] originals, List<(int Index, long Start, long End)> work,
        EffectSettings settings, CancellationToken cancel)
    {
        var playback = states.ToArray(); var changes = new List<EffectPreviewChange>(); long processedBytes = 0; long? auditionEnd = null;
        foreach (var item in work)
        {
            cancel.ThrowIfCancellationRequested(); var state = states[item.Index];
            var clone = new AudioTrack
            {
                Path = state.Source.Path, Samples = state.Source.Samples, Peaks = state.Source.Peaks,
                Offset = state.Offset, Gain = state.Gain, Enabled = state.Enabled, Fades = state.Fades.ToList(), Patches = state.Patches, Segments = state.Segments
            };
            var rendered = Editing.RenderSelection(clone, item.Start, item.End);
            cancel.ThrowIfCancellationRequested();
            var processed = AudioEffects.Process(rendered, AudioTrack.Rate, settings, cancel);
            cancel.ThrowIfCancellationRequested();
            processedBytes += processed.LongLength * sizeof(float);
            if (processedBytes > MaxSelectedBytes) throw new IOException("The processed effect audio exceeds the 512 MiB preview limit.");
            long affectedEnd = item.Start + processed.LongLength / AudioTrack.Channels;
            if (processed.Length > rendered.Length)
            {
                long overlapEnd = Math.Min(state.Frames, affectedEnd);
                if (overlapEnd > item.End)
                {
                    var following = Editing.RenderSelection(clone, item.End, overlapEnd);
                    for (int sample = 0; sample < following.Length; sample++) processed[rendered.Length + sample] += following[sample];
                }
                long tailEnd = state.Offset + affectedEnd; auditionEnd = auditionEnd.HasValue ? Math.Max(auditionEnd.Value, tailEnd) : tailEnd;
            }
            clone.WriteVisibleSamples(item.Start, processed);
            Editing.RemoveFadesInRange(clone, item.Start, affectedEnd);
            // Peak construction is part of the offline job, so the first live
            // waveform paint only reads cached processed-audio geometry.
            foreach (var patch in clone.Patches)
            { cancel.ThrowIfCancellationRequested(); _ = patch.Peaks; }
            cancel.ThrowIfCancellationRequested();
            playback[item.Index] = clone.Snapshot(); changes.Add(new(originals[item.Index], item.Start, processed));
            long sessionBytes = playback.Sum(track => track.Source.Samples.LongLength * sizeof(float) + track.Patches.Sum(patch => patch.Samples.LongLength * sizeof(float)));
            if (sessionBytes > AudioFiles.MaxSessionBytes) throw new IOException("Processed audio exceeds the 512 MiB session memory limit. Use a shorter selection or fewer tracks.");
        }
        cancel.ThrowIfCancellationRequested();
        return new(playback, changes.ToArray(), auditionEnd);
    }
}
