using OrchardSound;

public static class PreviewChecks
{
    public static async Task Run()
    {
        int checks = 0;
        void Check(bool condition, string name)
        { if (!condition) throw new Exception("FAILED PREVIEW: " + name); checks++; Console.WriteLine("PASS PREVIEW " + name); }
        AudioTrack Track(params float[] values)
        {
            var samples = values.SelectMany(value => new[] { value, -value }).ToArray();
            return new() { Path = "Preview fixture.wav", Samples = samples, Peaks = new PeakPyramid(samples) };
        }
        float[] Render(TrackState[] states, long start, long end)
        {
            var provider = new MixProvider(states, start, end, false); var samples = new float[(end - start) * 2];
            provider.Read(samples, 0, samples.Length); return samples.Where((_, i) => i % 2 == 0).ToArray();
        }
        bool Near(float[] actual, params float[] expected) => actual.Length == expected.Length && actual.Zip(expected).All(pair => Math.Abs(pair.First - pair.Second) < .0001);

        var a = Track(.1f, .2f, .3f, .4f, .5f, .6f); a.Offset = 2; a.Gain = .5f;
        var b = Track(.04f, .04f, .04f); b.Offset = 3;
        var rawCopy = a.Samples.ToArray(); var settings = new EffectSettings(EffectKind.Reverse);
        var result = await EffectPreview.PrepareAsync([a, b], [a], 3, 6, settings);
        Check(a.Patches.Length == 0 && a.Fades.Count == 0 && a.Offset == 2 && a.Gain == .5f && a.Samples.SequenceEqual(rawCopy), "preview never mutates original samples edit maps fades or gain");
        Check(result.Changes.Length == 1 && ReferenceEquals(result.Changes[0].Track, a) && result.Changes[0].VisibleStart == 1, "preview changes identify the original row and local selection start");
        Check(Near(Render(result.PlaybackTracks, 2, 8), .05f, .24f, .19f, .14f, .25f, .3f), "preview reverses only the selected range and preserves untargeted rows in the mix");
        Check(!ReferenceEquals(result.PlaybackTracks[0].Source, a) && ReferenceEquals(result.PlaybackTracks[1].Source, b), "target playback uses a detached clone while untouched tracks keep frozen snapshots");
        Check(ReferenceEquals(result.PlaybackTracks[0].Source.Samples, a.Samples) && ReferenceEquals(result.PlaybackTracks[0].Source.Peaks, a.Peaks), "preview clones share immutable raw source data without copying whole sounds");
        var saved = a.Snapshot();
        foreach (var change in result.Changes)
        { change.Track.WriteVisibleSamples(change.VisibleStart, change.Samples); Editing.RemoveFadesInRange(change.Track, change.VisibleStart, change.VisibleStart + change.Samples.LongLength / 2); }
        Check(Near(Render([a.Snapshot(), b.Snapshot()], 2, 8), Render(result.PlaybackTracks, 2, 8)), "explicit Apply commits the same audio that was previewed");
        saved.ApplyToSource();
        Check(a.Patches.Length == 0 && Near(Render([a.Snapshot()], 2, 8), .05f, .1f, .15f, .2f, .25f, .3f), "the pre-Apply snapshot restores original audio through Undo");

        var frozenA = Track(.1f, .2f, .3f, .4f); frozenA.Offset = 2; frozenA.Gain = .5f;
        var frozenB = Track(.02f, .02f); frozenB.Offset = 3;
        var prepared = EffectPreview.PrepareAsync([frozenA, frozenB], [frozenA], 2, 6, settings);
        frozenA.Offset = 40; frozenA.Gain = 0; frozenA.Segments = []; frozenA.Fades.Add(new(0, 4, false));
        frozenB.Offset = 50; frozenB.Gain = 0; frozenB.WriteVisibleSamples(0, [.5f, -.5f, .5f, -.5f]);
        var frozenResult = await prepared;
        Check(Near(Render(frozenResult.PlaybackTracks, 2, 6), .2f, .17f, .12f, .05f), "PrepareAsync freezes all row state synchronously before the caller can edit originals");

        var faded = Track(Enumerable.Repeat(.5f, 6).ToArray()); faded.Gain = .2f; Editing.ApplyFade([faded], 0, 6, true);
        var fadedResult = await EffectPreview.PrepareAsync([faded], [faded], 2, 4, settings);
        Check(Near(Render(fadedResult.PlaybackTracks, 0, 6), 0, .02f, .06f, .04f, .08f, .1f), "preview bakes selected fades exactly once while retaining outside fade curves");
        Check(faded.Patches.Length == 0 && faded.Fades.Count == 1, "Cancel can discard preview data without altering original fade metadata");

        var cut = Track(.1f, .2f, .3f, .4f, .5f, .6f); TimelineEditing.Delete([cut], 2, 4); cut.WriteVisibleSamples(1, [.3f, -.3f]);
        var cutResult = await EffectPreview.PrepareAsync([cut], [cut], 1, 3, settings);
        Check(Near(Render(cutResult.PlaybackTracks, 0, 4), .1f, .5f, .3f, .6f) && Near(Render([cut.Snapshot()], 0, 4), .1f, .3f, .5f, .6f), "preview processes existing patches across a cut while preserving its original edit state");

        var fadeSource = Track(.4f, .4f, .4f, .4f);
        var fadePreview = await EffectPreview.PrepareAsync([fadeSource], [fadeSource], 0, 4, new(EffectKind.FadeIn));
        Check(Near(Render(fadePreview.PlaybackTracks, 0, 4), 0, .4f / 3, .8f / 3, .4f) && fadeSource.Fades.Count == 0 && fadeSource.Patches.Length == 0, "Fade In uses the same non-destructive live preview path");

        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); bool cancelled = false;
        try { await EffectPreview.PrepareAsync([fadeSource], [fadeSource], 0, 4, settings, cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && fadeSource.Patches.Length == 0, "cancelled preparation never commits audio changes");
        bool rejected = false;
        try { await EffectPreview.PrepareAsync([fadeSource], [], 0, 4, settings); } catch (InvalidOperationException error) { rejected = error.Message.Contains("overlap", StringComparison.OrdinalIgnoreCase); }
        Check(rejected, "empty effect targets report a useful overlap error");
        rejected = false;
        try { await EffectPreview.PrepareAsync([fadeSource], [fadeSource], 8, 10, settings); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected && fadeSource.Patches.Length == 0, "a selection outside target rows does not prepare or mutate audio");
        rejected = false;
        try { await EffectPreview.PrepareAsync([fadeSource], [a], 0, 4, settings); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "targets outside the current session are rejected");
        rejected = false;
        try { await EffectPreview.PrepareAsync([fadeSource], [fadeSource], 2, 2, settings); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "an empty time range is rejected before allocating a preview");

        var budgetSource = Track(Enumerable.Repeat(.1f, 1024).ToArray()); budgetSource.Segments = Enumerable.Repeat(new AudioSegment(0, 1024), 70000).ToArray();
        rejected = false;
        try { await EffectPreview.PrepareAsync([budgetSource], [budgetSource], 0, budgetSource.Frames, settings); } catch (IOException error) { rejected = error.Message.Contains("512", StringComparison.Ordinal); }
        Check(rejected && budgetSource.Patches.Length == 0, "the selected sample budget is checked before allocating large rendered buffers");
        Console.WriteLine($"{checks} preview checks passed.");
    }
}
