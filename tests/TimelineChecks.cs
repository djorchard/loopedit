using OrchardSound;

public static class TimelineChecks
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool condition, string name)
        { if (!condition) throw new Exception("FAILED TIMELINE: " + name); checks++; Console.WriteLine("PASS TIMELINE " + name); }
        AudioTrack Track(params float[] values)
        {
            var samples = values.SelectMany(value => new[] { value, -value }).ToArray();
            return new AudioTrack { Path = "Timeline fixture.wav", Samples = samples, Peaks = new PeakPyramid(samples) };
        }
        float[] Render(IEnumerable<TrackState> states, long start, long end)
        {
            var provider = new MixProvider(states, start, end, false); var samples = new float[(end - start) * 2];
            provider.Read(samples, 0, samples.Length); return samples.Where((_, index) => index % 2 == 0).ToArray();
        }
        bool Near(float[] actual, params float[] expected) => actual.Length == expected.Length && actual.Zip(expected).All(pair => Math.Abs(pair.First - pair.Second) < .0001);

        var middle = Track(.1f, .2f, .3f, .4f, .5f, .6f); var raw = middle.Samples; var rawCopy = raw.ToArray(); var peaks = middle.Peaks;
        var beforeCut = middle.Snapshot(); TimelineEditing.Delete([middle], 2, 4);
        Check(middle.Frames == 4 && Near(Render([middle.Snapshot()], 0, 4), .1f, .2f, .5f, .6f), "delete removes the selected samples and closes their gap");
        Check(ReferenceEquals(raw, middle.Samples) && raw.SequenceEqual(rawCopy) && ReferenceEquals(peaks, middle.Peaks), "delete preserves original source bytes and peak cache");
        Check(middle.SourceFrameAt(0) == 0 && middle.SourceFrameAt(2) == 4 && middle.SourceFrameAt(3) == 5, "visible frames map through both retained source segments");
        Check(middle.SourceFrameAt(-1) == -1 && middle.SourceFrameAt(4) == -1, "source frame mapping rejects out-of-range visible positions");
        var cutPeaks = middle.QueryPeaks(1, 3);
        Check(Math.Abs(cutPeaks.Max - .5f) < .0001 && Math.Abs(cutPeaks.Min + .5f) < .0001, "visible peak queries combine exact ranges across a cut");
        Check(beforeCut.Frames == 6 && Near(Render([beforeCut], 0, 6), .1f, .2f, .3f, .4f, .5f, .6f), "snapshot playback remains isolated from later source edits");
        beforeCut.ApplyToSource();
        Check(middle.Frames == 6 && middle.Offset == 0 && Near(Render([middle.Snapshot()], 0, 6), .1f, .2f, .3f, .4f, .5f, .6f), "snapshot restore reinstates deleted audio without copying source samples");

        var leading = Track(.1f, .2f, .3f, .4f, .5f); TimelineEditing.Delete([leading], 0, 2);
        Check(leading.Offset == 0 && Near(Render([leading.Snapshot()], 0, 3), .3f, .4f, .5f), "deleting the leading region retains the remainder at the cut start");
        var tail = Track(.1f, .2f, .3f, .4f, .5f); TimelineEditing.Delete([tail], 3, 20);
        Check(tail.Frames == 3 && Near(Render([tail.Snapshot()], 0, 3), .1f, .2f, .3f), "deleting a trailing region clamps to the sound length");
        var future = Track(.1f, .2f); future.Offset = 30; TimelineEditing.Delete([future], 10, 20);
        Check(future.Offset == 20 && future.Frames == 2, "a later track moves left by the globally removed duration");
        var earlier = Track(.1f, .2f); earlier.Offset = 1; TimelineEditing.Delete([earlier], 10, 20);
        Check(earlier.Offset == 1 && earlier.Frames == 2, "a track before the deleted region is unchanged");
        var crossing = Track(.1f, .2f, .3f, .4f, .5f); crossing.Offset = 2; TimelineEditing.Delete([crossing], 0, 4);
        Check(crossing.Offset == 0 && Near(Render([crossing.Snapshot()], 0, 3), .3f, .4f, .5f), "a track starting inside the cut retains its later content at the new timeline boundary");
        var negative = Track(.1f, .2f, .3f, .4f); negative.Offset = -3; TimelineEditing.Delete([negative], -2, 0);
        Check(negative.Offset == -3 && Near(Render([negative.Snapshot()], -3, -1), .1f, .4f), "negative track offsets retain sample-accurate cut mapping");

        var first = Track(.05f, .1f, .15f, .2f, .25f, .3f); var second = Track(.2f, .3f, .4f, .5f, .6f); second.Offset = 2;
        TimelineEditing.Delete([first, second], 1, 4);
        Check(Near(Render([first.Snapshot(), second.Snapshot()], 0, 4), .05f, .65f, .8f, .6f), "global deletion keeps multiple offset tracks synchronized");
        TimelineEditing.Delete([first], 1, 2);
        Check(first.Frames == 2 && Near(Render([first.Snapshot()], 0, 2), .05f, .3f), "a second deletion operates on the already edited timeline");

        var trimmed = Track(.1f, .2f, .3f, .4f, .5f, .6f, .7f); trimmed.Offset = 3;
        TimelineEditing.Trim([trimmed], 5, 9);
        Check(trimmed.Offset == 0 && trimmed.Frames == 4 && Near(Render([trimmed.Snapshot()], 0, 4), .3f, .4f, .5f, .6f), "trim keeps the selected global region and rebases its beginning to zero");
        var late = Track(.2f, .3f, .4f); late.Offset = 5; var outside = Track(.1f); outside.Offset = 30;
        TimelineEditing.Trim([late, outside], 3, 10);
        Check(late.Offset == 2 && outside.Frames == 0 && Near(Render([late.Snapshot(), outside.Snapshot()], 0, 5), 0, 0, .2f, .3f, .4f), "trim preserves relative track timing and empties out-of-range tracks");

        var faded = Track(Enumerable.Repeat(.5f, 10).ToArray()); Editing.ApplyFade([faded], 0, 10, true);
        var originalFade = Render([faded.Snapshot()], 0, 10); TimelineEditing.Delete([faded], 3, 7);
        Check(Near(Render([faded.Snapshot()], 0, 6), originalFade.Take(3).Concat(originalFade.Skip(7)).ToArray()), "cut preserves each retained sample's existing fade gain");
        Editing.ApplyFade([faded], 0, 6, false); var withNewFade = Render([faded.Snapshot()], 0, 6);
        Check(Math.Abs(withNewFade[0] - originalFade[0]) < .0001 && withNewFade[^1] == 0 && Math.Abs(withNewFade[3] - originalFade[7] * .4f) < .0001, "new fades progress continuously across discontiguous retained source segments");
        var trimFade = Track(Enumerable.Repeat(.5f, 10).ToArray()); Editing.ApplyFade([trimFade], 0, 10, false);
        var originalTrimFade = Render([trimFade.Snapshot()], 0, 10); TimelineEditing.Trim([trimFade], 2, 8);
        Check(Near(Render([trimFade.Snapshot()], 0, 6), originalTrimFade.Skip(2).Take(6).ToArray()), "trim preserves the original fade levels at both new edges");

        var rhythm = Track(Enumerable.Repeat(.1f, 10).ToArray()); rhythm.Rhythm = new(120, [0, 2, 4, 6, 8], [1, 5, 9]);
        TimelineEditing.Delete([rhythm], 3, 7);
        Check(rhythm.VisibleRhythmFrames(false).SequenceEqual(new long[] { 0, 2, 4 }) && rhythm.VisibleRhythmFrames(true).SequenceEqual(new long[] { 1, 5 }), "beat and kick markers follow retained source identities after deletion");
        Check(rhythm.Rhythm.Bpm == 120 && rhythm.Rhythm.BeatFrames.SequenceEqual(new long[] { 0, 2, 4, 6, 8 }), "edits do not modify the original rhythm analysis");
        var empty = Track(.1f, .2f); TimelineEditing.Delete([empty], -1, 4);
        Check(empty.Frames == 0 && empty.Segments!.Length == 0 && empty.QueryPeaks(0, 2) == (0f, 0f) && Near(Render([empty.Snapshot()], 0, 2), 0, 0), "deleting the entire sound leaves a safe silent empty row");
        Check(TimelineEditing.MapAfterDelete(2, 3, 7) == 2 && TimelineEditing.MapAfterDelete(5, 3, 7) == 3 && TimelineEditing.MapAfterDelete(9, 3, 7) == 5, "saved positions before inside and after a deletion map consistently");
        Check(TimelineEditing.MapAfterTrim(1, 3, 7) == 0 && TimelineEditing.MapAfterTrim(5, 3, 7) == 2 && TimelineEditing.MapAfterTrim(9, 3, 7) == 4, "trim mapping clamps saved positions and rebases to zero");
        bool rejected = false; try { trimmed.Segments = [new(-1, 1)]; } catch (ArgumentException) { rejected = true; }
        Check(rejected && trimmed.Frames == 4, "invalid source segments are rejected before mutating the track");
        var supplied = new[] { new AudioSegment(0, 2) }; trimmed.Segments = supplied; supplied[0] = new(0, 1);
        Check(trimmed.Frames == 2, "segment assignment copies the edit map to isolate caller mutations");

        var processed = Track(.1f, .2f, .3f, .4f, .5f, .6f); var beforeProcessing = processed.Snapshot();
        float[] effectInput = [.8f, -.3f, .7f, -.2f]; processed.WriteVisibleSamples(1, effectInput); effectInput[0] = 0;
        Check(Near(Render([processed.Snapshot()], 0, 6), .1f, .8f, .7f, .4f, .5f, .6f), "selected processed overlays replace audio without altering surrounding samples");
        Check(processed.GetSample(1, 0) == .8f && processed.GetSample(1, 1) == -.3f && processed.Samples[2] == .2f, "overlays preserve independent stereo channels and immutable raw samples");
        Check(Near(Render([beforeProcessing], 0, 6), .1f, .2f, .3f, .4f, .5f, .6f), "a pre-effect snapshot never reads later processed overlays");
        var processedPeak = processed.QueryPeaks(1, 3);
        Check(Math.Abs(processedPeak.Max - .8f) < .0001 && Math.Abs(processedPeak.Min + .3f) < .0001, "waveform peak queries include processed overlay samples");
        processed.Gain = .2f; Editing.ApplyFade([processed], 0, 6, true);
        var materialized = processed.ReadVisibleSamples(1, 3);
        Check(materialized.SequenceEqual(new[] { .8f, -.3f, .7f, -.2f }), "effect materialization reads patched samples before gain and fade envelopes");
        materialized[0] = 0;
        Check(processed.GetSample(1, 0) == .8f, "materialized buffers do not alias stored processed patches");
        beforeProcessing.ApplyToSource();
        Check(processed.Patches.Length == 0 && processed.Gain == 1 && processed.Fades.Count == 0 && Near(Render([processed.Snapshot()], 0, 6), .1f, .2f, .3f, .4f, .5f, .6f), "snapshot Undo restores patches gain and fade state together");
        processed.WriteVisibleSamples(0, new[] { .1f, -.1f, .2f, -.2f, .3f, -.3f, .4f, -.4f, .5f, -.5f, .6f, -.6f });
        var firstEffect = processed.Snapshot();
        processed.WriteVisibleSamples(2, [.9f, -.9f, .8f, -.8f]);
        Check(processed.Patches.Length == 3 && processed.Patches.Sum(patch => patch.Frames) == 6 && Near(Render([processed.Snapshot()], 0, 6), .1f, .2f, .9f, .8f, .5f, .6f), "a partial overwrite splits old overlays without retaining covered audio");
        Check(Near(Render([firstEffect], 0, 6), .1f, .2f, .3f, .4f, .5f, .6f), "older effect snapshots retain their original patch buffers");
        processed.WriteVisibleSamples(0, Enumerable.Repeat(.25f, 12).ToArray());
        Check(processed.Patches.Length == 1 && processed.Patches[0].Frames == 6, "a complete overwrite discards all covered overlays from the current state");

        var cutEffect = Track(.1f, .2f, .3f, .4f, .5f, .6f); TimelineEditing.Delete([cutEffect], 2, 4);
        cutEffect.WriteVisibleSamples(1, [.7f, -.7f, .8f, -.8f]);
        Check(cutEffect.Patches.Select(patch => patch.SourceStart).SequenceEqual(new long[] { 1, 4 }) && Near(Render([cutEffect.Snapshot()], 0, 4), .1f, .7f, .8f, .6f), "effects across a cut map to each discontiguous original segment");
        TimelineEditing.Trim([cutEffect], 1, 3);
        Check(Near(Render([cutEffect.Snapshot()], 0, 2), .7f, .8f), "processed patches retain their sample identities through a later trim");
        var beforeInvalid = cutEffect.Snapshot(); bool invalidPatch = false;
        try { cutEffect.WriteVisibleSamples(0, [float.NaN, 0]); } catch (ArgumentException) { invalidPatch = true; }
        Check(invalidPatch && Near(Render([cutEffect.Snapshot()], 0, 2), .7f, .8f), "invalid effect output is rejected before mutating the audio");
        invalidPatch = false;
        try { cutEffect.Patches = [new AudioPatch(AudioTrack.MaxSourceFrames, [.2f, -.2f])]; } catch (ArgumentException) { invalidPatch = true; }
        Check(invalidPatch && cutEffect.Patches.Length == beforeInvalid.Patches.Length && Near(Render([cutEffect.Snapshot()], 0, 2), .7f, .8f), "patches beyond the bounded virtual source are rejected atomically");
        var baked = Track(Enumerable.Repeat(.5f, 6).ToArray()); baked.Gain = .2f; Editing.ApplyFade([baked], 0, 6, true);
        var beforeBake = baked.Snapshot(); var bakeInput = Editing.RenderSelection(baked, 2, 4);
        Check(bakeInput.Zip(new[] { .2f, -.2f, .3f, -.3f }).All(pair => Math.Abs(pair.First - pair.Second) < .0001), "effect rendering bakes existing fades exactly once and excludes the volume fader");
        baked.WriteVisibleSamples(2, [bakeInput[2], bakeInput[3], bakeInput[0], bakeInput[1]]); Editing.RemoveFadesInRange(baked, 2, 4);
        Check(Near(Render([baked.Snapshot()], 0, 6), 0, .02f, .06f, .04f, .08f, .1f), "reversing baked faded samples removes selected fades while preserving both outside curves");
        beforeBake.ApplyToSource();
        Check(baked.Patches.Length == 0 && baked.Fades.Count == 1 && baked.Gain == .2f && Near(Render([baked.Snapshot()], 0, 6), 0, .02f, .04f, .06f, .08f, .1f), "snapshot Undo restores pre-effect fade shape patches and fader after baking");
        Console.WriteLine($"{checks} timeline checks passed.");
    }
}
