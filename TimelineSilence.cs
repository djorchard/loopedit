namespace OrchardSound;

public sealed record SilenceInsertionResult(TrackState[] PlaybackTracks, TrackState[] ReplacementStates, long Position, long Duration)
{ public long End => checked(Position + Duration); }

public static class TimelineSilence
{
    sealed record Plan(TrackState State, long Offset, long AppendFrames, AudioSegment[] Prefix, AudioSegment[] Suffix, Fade[] Fades, long SourceFrames);

    public static Task<SilenceInsertionResult> PrepareAsync(IEnumerable<AudioTrack> tracks, long position, long duration, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(tracks); cancel.ThrowIfCancellationRequested();
        var originals = tracks.ToArray();
        if (originals.Any(track => track == null)) throw new ArgumentException("Tracks cannot be null.", nameof(tracks));
        if (originals.Length > 128 || originals.Distinct(ReferenceEqualityComparer.Instance).Count() != originals.Length)
            throw new ArgumentException("Silence insertion requires at most 128 distinct tracks.", nameof(tracks));
        var frozen = originals.Select(track => track.Snapshot()).ToArray();
        if (!frozen.Any(state => state.Frames > 0)) throw new InvalidOperationException("Load audio before inserting silence.");
        long insertionEnd, latestEnd;
        try
        {
            if (duration <= 0 || position < -SelectionStore.MaxTimelineFrame || position > SelectionStore.MaxTimelineFrame)
                throw new ArgumentException("Choose a positive silence duration and a valid insertion position.");
            insertionEnd = checked(position + duration);
            if (insertionEnd > SelectionStore.MaxTimelineFrame) throw new IOException("Inserted silence exceeds the supported timeline.");
            latestEnd = frozen.Where(state => state.Frames > 0).Max(state => checked(state.Offset + state.Frames));
        }
        catch (OverflowException) { throw new IOException("Inserted silence exceeds the supported timeline."); }
        var plans = new List<Plan>(frozen.Length); long memory = 0; int patches = 0;
        foreach (var state in frozen)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                long frames = state.Frames, trackEnd = checked(state.Offset + frames);
                long sourceFrames = Math.Max(state.Source.Samples.LongLength / AudioTrack.Channels,
                    state.Patches.Select(patch => checked(patch.SourceStart + patch.Frames)).DefaultIfEmpty(0).Max());
                memory = checked(memory + state.Source.Samples.LongLength * sizeof(float) + state.Patches.Sum(patch => patch.Samples.LongLength * sizeof(float)));
                patches = checked(patches + state.Patches.Length);
                long offset = state.Offset, append = 0; AudioSegment[] prefix = state.Segments, suffix = []; Fade[] fades = state.Fades;
                if (state.Offset >= position) offset = checked(state.Offset + duration);
                else if (frames > 0 && (trackEnd >= position || position > latestEnd && trackEnd == latestEnd))
                {
                    long insert = Math.Min(frames, position - state.Offset);
                    append = checked(duration + Math.Max(0, position - trackEnd));
                    prefix = Slice(state.Segments, 0, insert); suffix = Slice(state.Segments, insert, frames);
                    fades = InsertFades(state.Fades, insert, duration);
                    if (append > AudioTrack.MaxSourceFrames - sourceFrames)
                        throw new IOException("Inserted silence exceeds a track's 256 MiB source limit. Export and reload it first.");
                    if (prefix.Length + suffix.Length + 1 > 4096 || fades.Length > 4096)
                        throw new IOException("This track has too many edits to insert silence. Export and reload it first.");
                    memory = checked(memory + AudioTrack.Channels * sizeof(float)); patches++;
                }
                long resultingFrames = checked(frames + append);
                if (offset < -SelectionStore.MaxTimelineFrame || checked(offset + resultingFrames) > SelectionStore.MaxTimelineFrame)
                    throw new IOException("Inserted silence exceeds the supported timeline.");
                plans.Add(new(state, offset, append, prefix, suffix, fades, sourceFrames));
            }
            catch (OverflowException) { throw new IOException("Inserted silence exceeds its audio or timeline limits."); }
        }
        if (memory > AudioFiles.MaxSessionBytes || patches > 8192) throw new IOException("Inserted silence exceeds the 512 MiB session or processed-audio limit.");
        return Task.Run(() => Prepare(plans, position, duration, cancel), cancel);
    }

    static SilenceInsertionResult Prepare(List<Plan> plans, long position, long duration, CancellationToken cancel)
    {
        var playback = new TrackState[plans.Count]; var replacements = new TrackState[plans.Count];
        for (int index = 0; index < plans.Count; index++)
        {
            cancel.ThrowIfCancellationRequested(); var plan = plans[index]; var state = plan.State;
            AudioPatch[] patches = state.Patches; AudioSegment[] segments = state.Segments;
            if (plan.AppendFrames > 0)
            {
                // A terminal zero patch bounds a sparse source range. Its
                // duration does not require allocating an equally long buffer.
                patches = [.. patches, new AudioPatch(plan.SourceFrames + plan.AppendFrames - 1, new float[AudioTrack.Channels])];
                segments = [.. plan.Prefix, new(plan.SourceFrames, plan.AppendFrames), .. plan.Suffix];
            }
            var clone = new AudioTrack
            {
                Path = state.Source.Path, Samples = state.Source.Samples, Peaks = state.Source.Peaks, ImportedCues = state.Source.ImportedCues.ToArray(),
                Offset = plan.Offset, Gain = state.Gain, Enabled = state.Enabled, Fades = plan.Fades.ToList(), Patches = patches, Segments = segments, Rhythm = state.Source.Rhythm
            };
            playback[index] = clone.Snapshot(); replacements[index] = playback[index] with { Source = state.Source };
        }
        cancel.ThrowIfCancellationRequested(); return new(playback, replacements, position, duration);
    }

    static AudioSegment[] Slice(AudioSegment[] segments, long from, long to)
    {
        var result = new List<AudioSegment>(); long position = 0;
        foreach (var segment in segments)
        {
            long first = Math.Max(from, position), last = Math.Min(to, position + segment.Length);
            if (last > first) result.Add(new(segment.SourceStart + first - position, last - first));
            position += segment.Length; if (position >= to) break;
        }
        return result.ToArray();
    }
    static Fade[] InsertFades(Fade[] fades, long insert, long duration)
    {
        var result = new List<Fade>();
        foreach (var fade in fades)
        {
            if (fade.End <= insert) result.Add(fade);
            else if (fade.Start >= insert) result.Add(fade with { Start = checked(fade.Start + duration), End = checked(fade.End + duration) });
            else
            {
                result.Add(new(fade.Start, insert, fade.FadeIn, fade.At(fade.Start), fade.At(insert - 1)));
                result.Add(new(checked(insert + duration), checked(fade.End + duration), fade.FadeIn, fade.At(insert), fade.At(fade.End - 1)));
            }
        }
        return result.ToArray();
    }
}
