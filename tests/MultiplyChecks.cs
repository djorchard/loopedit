using OrchardSound;

public static class MultiplyChecks
{
    public static async Task Run()
    {
        int checks = 0;
        void Check(bool result, string name) { if (!result) throw new Exception("FAILED MULTIPLY: " + name); checks++; Console.WriteLine("PASS MULTIPLY " + name); }
        static AudioTrack Track(string name, params float[] frames)
        { var samples = frames.SelectMany(value => new[] { value, -value }).ToArray(); return new() { Path = name, Samples = samples, Peaks = new(samples) }; }
        static float[] Render(TrackState state, long start, long end)
        { var output = new float[checked((int)((end - start) * 2))]; new MixProvider([state with { Enabled = true }], start, end, false).Read(output, 0, output.Length); return output; }
        static bool Near(double value, double expected) => Math.Abs(value - expected) < .000001;
        static async Task<bool> Rejects<T>(Func<Task> action) where T : Exception
        { try { await action(); return false; } catch (T) { return true; } }

        var defaults = new MultiplySettings();
        Check(defaults.CopyCount == 4 && defaults.VariationPercent == 25 && !defaults.EnablePitch && !defaults.EnableFilter && !defaults.EnableReverb,
            "defaults are four total copies with 25 percent variation and optional sub-effects off");
        var settings = defaults with { CopyCount = 64, Seed = 173 };
        var variations = MultiplyEffect.GetCopyVariations(settings); var random = new Random(173);
        Check(variations.Count == 64 && variations.All(value => value >= -.25 && value <= .25)
            && variations.SequenceEqual(Enumerable.Range(0, 64).Select(_ => (random.NextDouble() * 2 - 1) * .25)),
            "one signed uniform variation is generated per copy from the stable seed");
        Check(variations.SequenceEqual(MultiplyEffect.GetCopyVariations(settings))
            && !variations.SequenceEqual(MultiplyEffect.GetCopyVariations(settings with { Seed = 174 })), "stable seeds reproduce a preview while a reroll changes its variations");
        bool readOnly = false; try { ((IList<double>)variations)[0] = 0; } catch (NotSupportedException) { readOnly = true; }
        Check(readOnly, "variation arrays are exposed as read-only results");
        var all = defaults with
        {
            EnablePitch = true, EnableFilter = true, EnableReverb = true, StartSemitones = 1, EndSemitones = 2,
            StartHz = 1000, EndHz = 2000, StartRoomSize = .4f, EndRoomSize = .8f, StartWet = .2f, EndWet = .6f
        };
        var derived = MultiplyEffect.EffectsForVariation(all, -.25);
        Check(derived.Select(effect => effect.Kind).SequenceEqual(new[] { EffectKind.PitchShift, EffectKind.Filter, EffectKind.Reverb })
            && Near(derived[0].StartSemitones, .75) && Near(derived[0].EndSemitones, 1.5), "a minus25 percent copy changes plus1 semitone to plus0.75 and keeps Pitch, Filter, Reverb order");
        Check(Near(derived[1].StartHz, 750) && Near(derived[1].EndHz, 1500) && Near(derived[2].RoomSize, .3)
            && Near(derived[2].EndRoomSize!.Value, .6) && Near(derived[2].Wet, .15) && Near(derived[2].EndWet!.Value, .45),
            "the same signed multiplier scales every enabled start and end parameter");
        Check(derived.All(effect => effect.AutomationEnabled && effect.CutTail) && MultiplyEffect.EffectsForVariation(defaults, 0).Count == 0,
            "per-copy ramps remain enabled, tails are cut and unchecked sub-effects are omitted");
        var extreme = all with { VariationPercent = 100, StartSemitones = 24, EndSemitones = -24, StartHz = 20000, EndHz = 20, StartRoomSize = .9f, EndRoomSize = .8f, StartWet = .75f, EndWet = .25f };
        var high = MultiplyEffect.EffectsForVariation(extreme, 1); var low = MultiplyEffect.EffectsForVariation(extreme, -1);
        Check(high[0].StartSemitones == 24 && high[0].EndSemitones == -24 && high[1].StartHz == 20000
            && high[2].RoomSize == 1 && high[2].Wet == 1 && low[1].StartHz == 20 && low[1].EndHz == 20 && low[2].Wet == 0,
            "derived pitch, filter and reverb values clamp to valid DSP bounds");
        Check(MultiplyEffect.EffectsForVariation(extreme, 1, 1000)[1].StartHz == 400, "filter variation also respects the processing sample rate's Nyquist margin");

        static bool Same(float[] first, float[] second) => first.Length == second.Length && first.Zip(second).All(pair => Math.Abs(pair.First - pair.Second) < .000001);
        var source = Track("Copies.wav", .1f, .2f, .3f, .4f); var beforeSamples = source.Samples.ToArray();
        var stacked = MultiplyEffect.ProcessStereo(source.Samples, AudioTrack.Rate, defaults);
        Check(stacked.Length == source.Samples.Length && Same(stacked, source.Samples.Select(value => value * 4).ToArray())
            && source.Samples.SequenceEqual(beforeSamples), "four dry copies play simultaneously at the original stereo positions without mutating input");
        Check(AudioEffects.Process(source.Samples, AudioTrack.Rate, new(EffectKind.Multiply) { Multiply = defaults }).SequenceEqual(stacked),
            "the ordinary DSP entry point uses the same fixed-duration voice mix");
        var signal = new float[8192];
        for (int frame = 0; frame < signal.Length / 2; frame++) { signal[frame * 2] = (float)(.1 * Math.Sin(frame * .13) + .05 * Math.Sin(frame * .61)); signal[frame * 2 + 1] = -signal[frame * 2] * .7f; }
        var chainSettings = all with { CopyCount = 2, Seed = 51, StartSemitones = 4, EndSemitones = 7, StartHz = 800, EndHz = 6000, StartWet = .2f, EndWet = .7f };
        var combined = MultiplyEffect.ProcessStereo(signal, AudioTrack.Rate, chainSettings); var factors = MultiplyEffect.GetCopyVariations(chainSettings);
        var expectedMix = new float[signal.Length]; bool noncommutative = false;
        for (int copy = 0; copy < 2; copy++)
        {
            float[] voice = signal; var steps = MultiplyEffect.EffectsForVariation(chainSettings, factors[copy]);
            foreach (var effect in steps) voice = AudioEffects.Process(voice, AudioTrack.Rate, effect);
            for (int sample = 0; sample < voice.Length; sample++) expectedMix[sample] += voice[sample];
            float[] wrong = signal; foreach (var effect in steps.Reverse()) wrong = AudioEffects.Process(wrong, AudioTrack.Rate, effect);
            noncommutative |= voice.Zip(wrong, (a, b) => Math.Abs(a - b)).Max() > .00001;
        }
        Check(combined.SequenceEqual(expectedMix) && noncommutative && combined.Length == signal.Length,
            "each voice independently runs Pitch, Filter and Reverb ramps before summing at matching sample positions");
        Check(combined.SequenceEqual(MultiplyEffect.ProcessStereo(signal, AudioTrack.Rate, chainSettings)), "repeated live preparation produces identical seeded layered audio");

        var target = Track("Target.wav", .02f, .04f, .06f, .08f, .1f, .12f); target.Gain = .8f;
        var excluded = Track("Excluded.wav", .1f, .2f, .3f, .4f, .5f, .6f); excluded.Enabled = false;
        var later = Track("Later.wav", .7f, .8f); later.Offset = 5; later.Enabled = false;
        var originals = new[] { target, excluded, later }; var frozen = originals.Select(track => track.Snapshot()).ToArray();
        var prepared = await MultiplyEffect.PrepareAsync(frozen, new HashSet<AudioTrack> { target }, 2, 4, defaults with { CopyCount = 3 });
        var audible = prepared.PlaybackTracks[0].Source.ReadVisibleSamples(0, 6);
        var expectedFrames = new[] { .02f, .04f, .18f, .24f, .1f, .12f }.SelectMany(value => new[] { value, -value }).ToArray();
        Check(Same(audible, expectedFrames) && prepared.SelectionEnd == null && prepared.AuditionEnd == null && prepared.CueFrames == null,
            "selection samples stack in place with no changed selection duration or copy cues");
        Check(prepared.PlaybackTracks[1].Source.ReadVisibleSamples(0, 6).SequenceEqual(excluded.Samples) && prepared.PlaybackTracks[2].Offset == 5,
            "unticked and later rows retain their audio and positions without inserted silence");
        Check(prepared.ReplacementStates == null && prepared.Changes.Length == 1 && ReferenceEquals(prepared.Changes[0].Track, target)
            && !ReferenceEquals(prepared.PlaybackTracks[0].Source, target), "the fixed-range change points to the original track while preview audio is detached");
        Check(target.Frames == 6 && target.Patches.Length == 0 && target.Gain == .8f && excluded.Frames == 6 && !excluded.Enabled,
            "preparing stacked voices preserves committed source state and inclusion controls");
        Check(prepared.PlaybackTracks[0].Gain == .8f && !prepared.PlaybackTracks[1].Enabled && !prepared.PlaybackTracks[2].Enabled,
            "layered processing retains track gain and inclusion settings");
        foreach (var change in prepared.Changes) change.Track.WriteVisibleSamples(change.VisibleStart, change.Samples);
        var applied = target.Snapshot();
        Check(target.Frames == 6 && Same(target.ReadVisibleSamples(0, 6), expectedFrames) && later.Offset == 5, "Apply changes only the original selected audio interval");
        frozen[0].ApplyToSource();
        Check(target.Patches.Length == 0 && target.Frames == 6, "snapshot Undo restores the original selected audio without timeline changes");
        applied.ApplyToSource(); Check(Same(target.ReadVisibleSamples(0, 6), expectedFrames), "snapshot Redo restores the simultaneous voices");
        frozen[0].ApplyToSource();

        var partial = Track("Partial.wav", .2f, .4f); partial.Offset = 1;
        var padded = await MultiplyEffect.PrepareAsync([partial.Snapshot()], new HashSet<AudioTrack> { partial }, 0, 4, defaults with { CopyCount = 2 });
        Check(padded.PlaybackTracks[0].Offset == 1 && padded.PlaybackTracks[0].Frames == 2
            && Same(padded.PlaybackTracks[0].Source.ReadVisibleSamples(0, 2), new[] { .4f, -.4f, .8f, -.8f }),
            "partially overlapping rows keep their original offset and extent while voices share the same timing");
        var faded = Track("Faded.wav", .02f, .04f, .06f, .08f, .1f, .12f); faded.Gain = .5f; faded.Fades.Add(new(0, 6, true));
        var selectedBefore = Render(faded.Snapshot(), 2, 4);
        var fadedResult = await MultiplyEffect.PrepareAsync([faded.Snapshot()], new HashSet<AudioTrack> { faded }, 2, 4, defaults with { CopyCount = 3 });
        Check(Same(Render(fadedResult.PlaybackTracks[0], 2, 4), selectedBefore.Select(value => value * 3).ToArray())
            && Render(fadedResult.PlaybackTracks[0], 0, 2).SequenceEqual(Render(faded.Snapshot(), 0, 2))
            && Render(fadedResult.PlaybackTracks[0], 4, 6).SequenceEqual(Render(faded.Snapshot(), 4, 6)),
            "selected fades bake once per simultaneous voice while surrounding audio and gain stay unchanged");
        var cut = Track("Cut.wav", .1f, .2f, .3f, .4f, .5f, .6f); cut.Segments = [new(0, 2), new(4, 2)]; cut.WriteVisibleSamples(1, [.09f, -.09f]);
        var cutResult = await MultiplyEffect.PrepareAsync([cut.Snapshot()], new HashSet<AudioTrack> { cut }, 1, 3, defaults with { CopyCount = 2 });
        Check(Same(cutResult.PlaybackTracks[0].Source.ReadVisibleSamples(0, 4), new[] { .1f, .18f, 1f, .6f }.SelectMany(value => new[] { value, -value }).ToArray())
            && cut.Frames == 4 && cut.Patches.Length == 1, "in-place layered voices honor prior cuts and processed patches");
        var negative = Track("Negative.wav", .1f, .2f, .3f, .4f, .5f, .6f); negative.Offset = -2;
        var negativeResult = await MultiplyEffect.PrepareAsync([negative.Snapshot()], new HashSet<AudioTrack> { negative }, 0, 2, defaults with { CopyCount = 2 });
        Check(negativeResult.PlaybackTracks[0].Offset == -2 && negativeResult.PlaybackTracks[0].Frames == 6
            && Same(negativeResult.PlaybackTracks[0].Source.ReadVisibleSamples(2, 4), new[] { .6f, -.6f, .8f, -.8f }),
            "negative offsets retain the same global sample positions and track duration");
        Check(await Rejects<InvalidOperationException>(() => MultiplyEffect.PrepareAsync(frozen, new HashSet<AudioTrack>(), 2, 4, defaults))
            && await Rejects<InvalidOperationException>(() => MultiplyEffect.PrepareAsync(frozen, new HashSet<AudioTrack> { excluded }, 2, 4, defaults)),
            "missing or unticked targets cannot change any audio");
        var singleDry = await MultiplyEffect.PrepareAsync([target.Snapshot()], new HashSet<AudioTrack> { target }, 2, 4, defaults with { CopyCount = 1 });
        Check(singleDry.Changes.Length == 1 && singleDry.SelectionEnd == null && singleDry.PlaybackTracks[0].Source.ReadVisibleSamples(0, 6).SequenceEqual(target.Samples),
            "one dry voice is an exact identity with a valid fixed-range Apply transaction");
        var sparse = new AudioTrack { Path = "Full source.wav", Samples = [], Peaks = new([]), Patches = [new(AudioTrack.MaxSourceFrames - 1, [.1f, -.1f])], Segments = [new(AudioTrack.MaxSourceFrames - 1, 1)] };
        var sparseMix = await MultiplyEffect.PrepareAsync([sparse.Snapshot()], new HashSet<AudioTrack> { sparse }, 0, 1, defaults with { CopyCount = 2 });
        Check(sparseMix.PlaybackTracks[0].Source.SourceFrames == AudioTrack.MaxSourceFrames && Same(sparseMix.Changes[0].Samples, new[] { .2f, -.2f }),
            "layering can reuse a full virtual source extent because it adds no timeline frames");
        var shared = new float[AudioFiles.MaxSessionBytes / 128 / sizeof(float)]; var sharedPeaks = new PeakPyramid(shared);
        var crowded = Enumerable.Range(0, 128).Select(index => new AudioTrack { Path = "Budget " + index, Samples = shared, Peaks = sharedPeaks, Enabled = index == 0 }).ToArray();
        Check(await Rejects<IOException>(() => MultiplyEffect.PrepareAsync(crowded.Select(track => track.Snapshot()).ToArray(), new HashSet<AudioTrack> { crowded[0] }, 0, 1, defaults))
            && crowded.All(track => track.Patches.Length == 0), "session limits still account for original buffers and new fixed-range audio patches");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            Check(await Rejects<OperationCanceledException>(() => MultiplyEffect.PrepareAsync([target.Snapshot()], new HashSet<AudioTrack> { target }, 2, 4, defaults, cancelled.Token)),
                "cancelled previews stop without modifying source audio");
        }
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(1)))
            Check(await Rejects<OperationCanceledException>(() => Task.Run(() => MultiplyEffect.ProcessStereo(signal, AudioTrack.Rate,
                chainSettings with { CopyCount = 64 }, cancellation.Token))), "per-voice DSP checks cancellation during a long layering job");
        Check(await Rejects<ArgumentException>(() => Task.Run(() => MultiplyEffect.ProcessStereo(source.Samples, AudioTrack.Rate, defaults with { CopyCount = 65 })))
            && await Rejects<ArgumentException>(() => Task.Run(() => MultiplyEffect.ProcessStereo(source.Samples, AudioTrack.Rate, defaults with { VariationPercent = 0 }))),
            "invalid voice and variation bounds are rejected before processing");
        Check(await Rejects<ArgumentException>(() => Task.Run(() => MultiplyEffect.ProcessStereo([float.NaN, 0], AudioTrack.Rate, defaults))), "invalid samples are rejected instead of contaminating every voice");
        Console.WriteLine($"{checks} multiply checks passed. No playback or recording hardware was used.");
    }
}
