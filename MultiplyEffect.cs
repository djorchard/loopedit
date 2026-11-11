namespace OrchardSound;

public sealed record MultiplySettings
{
    public int CopyCount { get; init; } = 4;
    public double VariationPercent { get; init; } = 25;
    public int Seed { get; init; }
    public bool EnablePitch { get; init; }
    public bool EnableFilter { get; init; }
    public bool EnableReverb { get; init; }
    public double StartSemitones { get; init; }
    public double EndSemitones { get; init; }
    public bool HighPass { get; init; }
    public double StartHz { get; init; } = 1000;
    public double EndHz { get; init; } = 1000;
    public float StartRoomSize { get; init; } = .45f;
    public float EndRoomSize { get; init; } = .45f;
    public float StartWet { get; init; } = .3f;
    public float EndWet { get; init; } = .3f;
}

public static class MultiplyEffect
{
    enum PlanKind { Unchanged, Target, Shift, Silence, Delete }
    sealed record Plan(TrackState State, PlanKind Kind, long Offset, long AppendFrames, AudioSegment[] Prefix, AudioSegment[] Suffix, Fade[] Fades);

    static void Validate(MultiplySettings settings, int rate)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.CopyCount is < 1 or > 64) throw new ArgumentException("Multiply needs 1 to 64 total copies.");
        if (!double.IsFinite(settings.VariationPercent) || settings.VariationPercent is < 1 or > 100)
            throw new ArgumentException("Random variation must be between 1% and 100%.");
        if (rate is < 1000 or > 384000) throw new ArgumentException("Multiply needs a supported sample rate.");
        if (settings.EnablePitch && (!double.IsFinite(settings.StartSemitones) || !double.IsFinite(settings.EndSemitones))
            || settings.EnableFilter && (!double.IsFinite(settings.StartHz) || !double.IsFinite(settings.EndHz))
            || settings.EnableReverb && (!float.IsFinite(settings.StartRoomSize) || !float.IsFinite(settings.EndRoomSize)
                || !float.IsFinite(settings.StartWet) || !float.IsFinite(settings.EndWet)))
            throw new ArgumentException("Multiply effect parameters must be finite.");
    }

    public static IReadOnlyList<double> GetCopyVariations(MultiplySettings settings)
    {
        Validate(settings, AudioTrack.Rate);
        var random = new Random(settings.Seed); var values = new double[settings.CopyCount]; double amount = settings.VariationPercent / 100;
        for (int copy = 0; copy < values.Length; copy++) values[copy] = (random.NextDouble() * 2 - 1) * amount;
        return Array.AsReadOnly(values);
    }

    public static IReadOnlyList<EffectSettings> EffectsForVariation(MultiplySettings settings, double signedVariation, int sampleRate = AudioTrack.Rate)
    {
        Validate(settings, sampleRate); double amount = settings.VariationPercent / 100;
        if (!double.IsFinite(signedVariation) || Math.Abs(signedVariation) > amount + 1e-12)
            throw new ArgumentOutOfRangeException(nameof(signedVariation));
        double multiplier = 1 + signedVariation;
        var effects = new List<EffectSettings>(3);
        if (settings.EnablePitch) effects.Add(new(EffectKind.PitchShift)
        {
            AutomationEnabled = true, CutTail = true,
            StartSemitones = Math.Clamp(settings.StartSemitones * multiplier, -24, 24),
            EndSemitones = Math.Clamp(settings.EndSemitones * multiplier, -24, 24)
        });
        if (settings.EnableFilter)
        {
            double upper = Math.Min(20000, sampleRate / 2d - 100);
            effects.Add(new(EffectKind.Filter)
            {
                AutomationEnabled = true, CutTail = true, HighPass = settings.HighPass,
                StartHz = Math.Clamp(settings.StartHz * multiplier, 20, upper), EndHz = Math.Clamp(settings.EndHz * multiplier, 20, upper)
            });
        }
        if (settings.EnableReverb) effects.Add(new(EffectKind.Reverb)
        {
            AutomationEnabled = true, CutTail = true,
            RoomSize = (float)Math.Clamp(settings.StartRoomSize * multiplier, 0, 1), EndRoomSize = (float)Math.Clamp(settings.EndRoomSize * multiplier, 0, 1),
            Wet = (float)Math.Clamp(settings.StartWet * multiplier, 0, 1), EndWet = (float)Math.Clamp(settings.EndWet * multiplier, 0, 1)
        });
        return effects.AsReadOnly();
    }

    public static float[] ProcessStereo(float[] stereo, int sampleRate, MultiplySettings settings, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(stereo); Validate(settings, sampleRate); cancel.ThrowIfCancellationRequested();
        if (stereo.Length % AudioTrack.Channels != 0) throw new ArgumentException("Provide interleaved stereo samples.");
        long outputSamples = stereo.LongLength;
        if (outputSamples * sizeof(float) > AudioFiles.MaxTrackBytes) throw new IOException("Multiplied audio exceeds the 256 MiB track limit.");
        for (int i = 0; i < stereo.Length; i++)
        { if ((i & 65535) == 0) cancel.ThrowIfCancellationRequested(); if (!float.IsFinite(stereo[i])) throw new ArgumentException("Multiply needs finite audio samples."); }
        var output = new float[(int)outputSamples]; var variations = GetCopyVariations(settings);
        for (int copy = 0; copy < settings.CopyCount; copy++)
        {
            cancel.ThrowIfCancellationRequested(); float[] current = stereo;
            foreach (var effect in EffectsForVariation(settings, variations[copy], sampleRate)) current = AudioEffects.Process(current, sampleRate, effect, cancel);
            if (current.Length != stereo.Length) throw new IOException("A multiplied copy changed its duration.");
            for (int sample = 0; sample < current.Length; sample++)
            {
                if ((sample & 65535) == 0) cancel.ThrowIfCancellationRequested();
                output[sample] += current[sample];
                if (!float.IsFinite(output[sample])) throw new IOException("The stacked copies exceed the supported audio range.");
            }
        }
        cancel.ThrowIfCancellationRequested(); return output;
    }

    public static Task<EffectPreviewResult> PrepareAsync(TrackState[] states, IReadOnlySet<AudioTrack> targets,
        long start, long end, MultiplySettings settings, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(states); ArgumentNullException.ThrowIfNull(targets);
        Validate(settings, AudioTrack.Rate); cancel.ThrowIfCancellationRequested();
        if (start < -SelectionStore.MaxTimelineFrame || end > SelectionStore.MaxTimelineFrame || end <= start) throw new ArgumentException("Choose a valid range to multiply.");
        var frozen = states.ToArray();
        if (frozen.Any(state => state == null || state.Source == null)) throw new ArgumentException("Multiply tracks cannot be null.");
        if (frozen.Select(state => state.Source).Distinct(ReferenceEqualityComparer.Instance).Count() != frozen.Length)
            throw new ArgumentException("A track can appear only once in Multiply.");
        if (targets.Any(track => !frozen.Any(state => ReferenceEquals(state.Source, track)))) throw new ArgumentException("Multiply targets must belong to the session.");
        var clones = frozen.Select(state => Clone(state, cancel)).ToArray();
        var requested = clones.Where((_, index) => targets.Contains(frozen[index].Source)).ToArray();
        var task = EffectPreview.PrepareAsync(clones, requested, start, end, new(EffectKind.Multiply) { Multiply = settings }, cancel);
        return Rebase(task, clones, frozen);
    }

    static async Task<EffectPreviewResult> Rebase(Task<EffectPreviewResult> task, AudioTrack[] clones, TrackState[] states)
    {
        var result = await task.ConfigureAwait(false);
        var originals = clones.Select((track, index) => (track, original: states[index].Source)).ToDictionary(pair => pair.track, pair => pair.original);
        return result with { Changes = result.Changes.Select(change => change with { Track = originals[change.Track] }).ToArray() };
    }

    internal static Task<EffectPreviewResult> PrepareRepeatedAsync(TrackState[] states, IReadOnlySet<AudioTrack> targets,
        long start, long end, long outputFrames, Func<float[], CancellationToken, float[]> render, long[] cues,
        bool unchanged, bool commitUnchanged, string? detail, CancellationToken cancel, bool allowContraction = false)
    {
        ArgumentNullException.ThrowIfNull(states); ArgumentNullException.ThrowIfNull(targets);
        cancel.ThrowIfCancellationRequested();
        var frozen = states.ToArray();
        if (frozen.Any(state => state == null || state.Source == null)) throw new ArgumentException("Repeat effect tracks cannot be null.");
        var requested = new HashSet<AudioTrack>(targets, ReferenceEqualityComparer.Instance);
        if (requested.Any(track => !frozen.Any(state => ReferenceEquals(state.Source, track)))) throw new ArgumentException("Repeat effect targets must belong to the session.");
        if (frozen.Select(state => state.Source).Distinct(ReferenceEqualityComparer.Instance).Count() != frozen.Length) throw new ArgumentException("A track can appear only once in a repeat effect.");
        long duration, extra, newEnd;
        try
        {
            if (start < -SelectionStore.MaxTimelineFrame || end <= start || end > SelectionStore.MaxTimelineFrame) throw new ArgumentException("Choose a valid range to multiply.");
            duration = checked(end - start); extra = checked(outputFrames - duration); newEnd = checked(end + extra);
            if (outputFrames <= 0 || extra < 0 && !allowContraction) throw new ArgumentException("This repeat effect cannot shorten the original selection.");
            if (newEnd > SelectionStore.MaxTimelineFrame) throw new IOException("Repeat effect exceeds the supported timeline.");
        }
        catch (OverflowException) { throw new IOException("Repeat effect exceeds the supported timeline."); }
        bool hasTarget = frozen.Any(state => state.Enabled && requested.Contains(state.Source) && state.Frames > 0
            && state.Offset < end && state.Offset <= long.MaxValue - state.Frames && state.Offset + state.Frames > start);
        if (!hasTarget || unchanged)
            return Task.Run(() => new EffectPreviewResult(frozen.Select(state => Clone(state, cancel).Snapshot()).ToArray(), [],
                commitUnchanged && hasTarget ? newEnd : null, commitUnchanged && hasTarget ? frozen : null,
                commitUnchanged && hasTarget ? newEnd : null, commitUnchanged && hasTarget ? cues.ToArray() : null, detail), cancel);

        var plans = new List<Plan>(frozen.Length);
        long bytes = 0; int patchCount = 0;
        foreach (var state in frozen)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                long trackEnd = checked(state.Offset + state.Frames);
                long sourceFrames = Math.Max(state.Source.Samples.LongLength / AudioTrack.Channels, state.Patches.Select(p => checked(p.SourceStart + p.Frames)).DefaultIfEmpty(0).Max());
                bytes = checked(bytes + state.Source.Samples.LongLength * sizeof(float) + state.Patches.Sum(p => p.Samples.LongLength * sizeof(float))); patchCount += state.Patches.Length;
                PlanKind kind = PlanKind.Unchanged; long offset = state.Offset, append = 0;
                AudioSegment[] prefix = [], suffix = []; Fade[] fades = state.Fades;
                if (state.Frames > 0 && state.Enabled && requested.Contains(state.Source) && state.Offset < end && trackEnd > start)
                {
                    kind = PlanKind.Target; append = outputFrames; offset = Math.Min(start, state.Offset);
                    long from = Math.Clamp(start - state.Offset, 0, state.Frames), to = Math.Clamp(end - state.Offset, 0, state.Frames);
                    prefix = Slice(state.Segments, 0, from); suffix = Slice(state.Segments, to, state.Frames);
                    fades = MapFades(state.Fades, [(0, from, 0), (to, state.Frames, checked(from + append))]);
                    bytes = checked(bytes + append * AudioTrack.Channels * sizeof(float));
                }
                else if (state.Frames > 0 && extra != 0 && state.Offset >= end) { kind = PlanKind.Shift; offset = checked(state.Offset + extra); }
                else if (state.Frames > 0 && extra < 0 && trackEnd > newEnd)
                {
                    kind = PlanKind.Delete; long firstEnd = Math.Clamp(newEnd - state.Offset, 0, state.Frames), secondStart = Math.Clamp(end - state.Offset, 0, state.Frames);
                    prefix = Slice(state.Segments, 0, firstEnd); suffix = Slice(state.Segments, secondStart, state.Frames);
                    fades = MapFades(state.Fades, [(0, firstEnd, 0), (secondStart, state.Frames, firstEnd)]);
                    offset = TimelineEditing.MapAfterDelete(state.Offset, newEnd, end);
                }
                else if (state.Frames > 0 && extra > 0 && trackEnd > end)
                {
                    kind = PlanKind.Silence; append = extra; long insert = end - state.Offset;
                    prefix = Slice(state.Segments, 0, insert); suffix = Slice(state.Segments, insert, state.Frames);
                    fades = MapFades(state.Fades, [(0, insert, 0), (insert, state.Frames, checked(insert + extra))]);
                    bytes = checked(bytes + AudioTrack.Channels * sizeof(float));
                }
                if (append > AudioTrack.MaxSourceFrames - sourceFrames) throw new IOException("Repeat effect exceeds a track's 256 MiB source limit. Export and reload it first.");
                long resultingFrames = kind is PlanKind.Target or PlanKind.Silence or PlanKind.Delete ? checked(prefix.Sum(s => s.Length) + append + suffix.Sum(s => s.Length)) : state.Frames;
                if (offset < -SelectionStore.MaxTimelineFrame || checked(offset + resultingFrames) > SelectionStore.MaxTimelineFrame) throw new IOException("Repeat effect exceeds the supported timeline.");
                if (kind is PlanKind.Target or PlanKind.Silence)
                {
                    patchCount++;
                    if (prefix.Length + suffix.Length + 1 > 4096 || fades.Length > 4096) throw new IOException("Repeat effect would create too many edits. Export and reload first.");
                }
                else if (kind == PlanKind.Delete && (prefix.Length + suffix.Length > 4096 || fades.Length > 4096)) throw new IOException("Repeat effect would create too many edits. Export and reload first.");
                plans.Add(new(state, kind, offset, append, prefix, suffix, fades));
            }
            catch (OverflowException) { throw new IOException("Repeat effect exceeds its audio or timeline limits."); }
        }
        if (bytes > AudioFiles.MaxSessionBytes || patchCount > 8192) throw new IOException("Repeat effect exceeds the 512 MiB session or processed-audio limit.");
        return Task.Run(() => Prepare(plans, start, end, newEnd, render, cues.ToArray(), detail, cancel), cancel);
    }

    static EffectPreviewResult Prepare(List<Plan> plans, long start, long end, long newEnd, Func<float[], CancellationToken, float[]> render, long[] cues, string? detail, CancellationToken cancel)
    {
        var playback = new TrackState[plans.Count]; var replacements = new TrackState[plans.Count];
        for (int i = 0; i < plans.Count; i++)
        {
            cancel.ThrowIfCancellationRequested(); var plan = plans[i]; var clone = Clone(plan.State, cancel);
            if (plan.Kind == PlanKind.Target)
            {
                var selected = new float[checked((int)((end - start) * AudioTrack.Channels))];
                long from = Math.Max(start, plan.State.Offset), to = Math.Min(end, plan.State.Offset + plan.State.Frames);
                var audio = Editing.RenderSelection(clone, from - plan.State.Offset, to - plan.State.Offset);
                Array.Copy(audio, 0, selected, checked((int)((from - start) * AudioTrack.Channels)), audio.Length);
                var processed = render(selected, cancel);
                if (processed.LongLength != plan.AppendFrames * AudioTrack.Channels) throw new IOException("Repeated audio returned an unexpected duration.");
                var segment = clone.AppendSourceSamples(processed); clone.Segments = [.. plan.Prefix, segment, .. plan.Suffix]; clone.Fades = plan.Fades.ToList(); clone.Rhythm = null;
            }
            else if (plan.Kind == PlanKind.Silence)
            {
                // A sparse zero source range represents the gap without an
                // allocation proportional to its duration. Source bounds still apply.
                long first = clone.SourceFrames;
                clone.Patches = [.. clone.Patches, new AudioPatch(first + plan.AppendFrames - 1, new float[AudioTrack.Channels])];
                clone.Segments = [.. plan.Prefix, new(first, plan.AppendFrames), .. plan.Suffix]; clone.Fades = plan.Fades.ToList(); clone.Rhythm = null;
            }
            else if (plan.Kind == PlanKind.Delete) { TimelineEditing.Delete([clone], newEnd, end); clone.Rhythm = null; }
            clone.Offset = plan.Offset;
            foreach (var patch in clone.Patches) { cancel.ThrowIfCancellationRequested(); _ = patch.Peaks; }
            playback[i] = clone.Snapshot(); replacements[i] = playback[i] with { Source = plan.State.Source };
        }
        cancel.ThrowIfCancellationRequested(); return new(playback, [], newEnd, replacements, newEnd, cues, detail);
    }

    static AudioTrack Clone(TrackState state, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        return new()
        {
            Path = state.Source.Path, Samples = state.Source.Samples, Peaks = state.Source.Peaks, ImportedCues = state.Source.ImportedCues.ToArray(),
            Offset = state.Offset, Gain = state.Gain, Enabled = state.Enabled, Fades = state.Fades.ToList(), Patches = state.Patches, Segments = state.Segments, Rhythm = state.Source.Rhythm
        };
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

    static Fade[] MapFades(Fade[] fades, (long Start, long End, long Destination)[] ranges)
    {
        var result = new List<Fade>();
        foreach (var range in ranges)
            foreach (var fade in fades)
            {
                long from = Math.Max(fade.Start, range.Start), to = Math.Min(fade.End, range.End);
                if (to > from) result.Add(new(range.Destination + from - range.Start, range.Destination + to - range.Start, fade.FadeIn, fade.At(from), fade.At(to - 1)));
            }
        return result.ToArray();
    }
}
