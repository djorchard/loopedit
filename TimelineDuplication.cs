namespace OrchardSound;

public static class TimelineDuplication
{
    sealed record Plan(AudioTrack Track, TrackState Before, RhythmAnalysis? Rhythm, long Insert, long Padding, AudioSegment[] Prefix, AudioSegment[] Suffix, Fade[] Fades, bool ShiftOnly)
    { public float[]? Samples { get; set; } }

    public static void Duplicate(IEnumerable<AudioTrack> tracks, long start, long end)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (start < -SelectionStore.MaxTimelineFrame || end > SelectionStore.MaxTimelineFrame || end <= start)
            throw new ArgumentException("Choose a valid selection to duplicate.");
        long duration = checked(end - start), copiedEnd = checked(end + duration);
        if (copiedEnd > SelectionStore.MaxTimelineFrame) throw new IOException("The duplicated selection exceeds the supported timeline.");
        var unique = new HashSet<AudioTrack>(ReferenceEqualityComparer.Instance);
        var originals = tracks.ToArray();
        if (originals.Any(track => track == null)) throw new ArgumentException("Tracks cannot be null.", nameof(tracks));
        var plans = new List<Plan>(); long memory = 0;
        foreach (var track in originals.Where(unique.Add))
        {
            var state = track.Snapshot();
            memory = checked(memory + track.Samples.LongLength * sizeof(float) + state.Patches.Sum(p => p.Samples.LongLength * sizeof(float)));
            if (state.Frames == 0) continue;
            long trackEnd = checked(state.Offset + state.Frames);
            bool shiftOnly = state.Offset >= end;
            if ((shiftOnly ? checked(state.Offset + duration) : state.Offset) < -SelectionStore.MaxTimelineFrame || checked(trackEnd + duration) > SelectionStore.MaxTimelineFrame)
                throw new IOException("A duplicated track would exceed the supported timeline.");
            long insert = Math.Clamp(end - state.Offset, 0, state.Frames), padding = Math.Max(0, end - trackEnd);
            AudioSegment[] prefix = shiftOnly ? [] : Slice(state.Segments, 0, insert);
            AudioSegment[] suffix = shiftOnly ? [] : Slice(state.Segments, insert, state.Frames);
            var fades = shiftOnly ? state.Fades : ShiftFades(state.Fades, end - state.Offset, duration);
            if (!shiftOnly)
            {
                long appended = checked(padding + duration);
                if (appended > AudioTrack.MaxSourceFrames - track.SourceFrames) throw new IOException("Duplicating this range exceeds a track's 256 MiB audio limit.");
                if (prefix.Length + suffix.Length + 1 > 4096 || fades.Length > 4096) throw new IOException("This track has too many edits to duplicate. Export and reload it first.");
                memory = checked(memory + appended * AudioTrack.Channels * sizeof(float));
            }
            plans.Add(new(track, state, track.Rhythm, insert, padding, prefix, suffix, fades, shiftOnly));
        }
        if (memory > AudioFiles.MaxSessionBytes) throw new IOException("Duplicating this range exceeds the 512 MiB session memory limit.");
        foreach (var plan in plans.Where(plan => !plan.ShiftOnly))
        {
            var samples = new float[checked((int)((plan.Padding + duration) * AudioTrack.Channels))];
            long from = Math.Max(start, plan.Before.Offset), to = Math.Min(end, plan.Before.Offset + plan.Before.Frames);
            if (to > from)
            {
                var selected = Editing.RenderSelection(plan.Track, from - plan.Before.Offset, to - plan.Before.Offset);
                Array.Copy(selected, 0, samples, checked((int)((plan.Padding + from - start) * AudioTrack.Channels)), selected.Length);
            }
            plan.Samples = samples;
        }
        try
        {
            foreach (var plan in plans)
            {
                if (plan.ShiftOnly) plan.Track.Offset = checked(plan.Before.Offset + duration);
                else
                {
                    var inserted = plan.Track.AppendSourceSamples(plan.Samples!);
                    plan.Track.Segments = [.. plan.Prefix, inserted, .. plan.Suffix]; plan.Track.Fades = plan.Fades.ToList();
                }
                plan.Track.Rhythm = null;
            }
        }
        catch
        {
            foreach (var plan in plans) { plan.Before.ApplyToSource(); plan.Track.Rhythm = plan.Rhythm; }
            throw;
        }
    }

    static AudioSegment[] Slice(AudioSegment[] segments, long from, long to)
    {
        var retained = new List<AudioSegment>(); long position = 0;
        foreach (var segment in segments)
        {
            long first = Math.Max(from, position), last = Math.Min(to, position + segment.Length);
            if (last > first) retained.Add(new(segment.SourceStart + first - position, last - first));
            position += segment.Length; if (position >= to) break;
        }
        return retained.ToArray();
    }

    static Fade[] ShiftFades(Fade[] fades, long insert, long duration)
    {
        var shifted = new List<Fade>();
        foreach (var fade in fades)
        {
            if (fade.End <= insert) shifted.Add(fade);
            else if (fade.Start >= insert) shifted.Add(fade with { Start = checked(fade.Start + duration), End = checked(fade.End + duration) });
            else
            {
                shifted.Add(new(fade.Start, insert, fade.FadeIn, fade.At(fade.Start), fade.At(insert - 1)));
                shifted.Add(new(checked(insert + duration), checked(fade.End + duration), fade.FadeIn, fade.At(insert), fade.At(fade.End - 1)));
            }
        }
        return shifted.ToArray();
    }
}
