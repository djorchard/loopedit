using System.Security.Cryptography;
using NAudio.Wave;
using OrchardSound;

public static class SeamlessRepeatChecks
{
    public static async Task Run()
    {
        int checks = 0;
        void Check(bool condition, string name)
        { if (!condition) throw new Exception("FAILED SEAMLESS REPEAT: " + name); checks++; Console.WriteLine("PASS SEAMLESS REPEAT " + name); }
        static float[] Stereo(params float[] values) => values.SelectMany(value => new[] { value, -value / 2 }).ToArray();
        static AudioTrack Track(float[] samples) => new() { Path = "Seamless fixture.wav", Samples = samples, Peaks = new PeakPyramid(samples) };
        static bool Near(float[] actual, float[] expected, float tolerance = .00001f) => actual.Length == expected.Length && actual.Zip(expected).All(pair => Math.Abs(pair.First - pair.Second) <= tolerance);
        static void Commit(EffectPreviewResult result) { foreach (var state in result.ReplacementStates ?? []) state.ApplyToSource(); }
        var defaults = new SeamlessRepeatSettings();
        Check(defaults.RepeatCount == 10 && defaults.CrossfadeMilliseconds == 20 && defaults.SmartSeam && defaults.AdaptiveCrossfade && defaults.LoopEndToStart, "defaults enable ten total repetitions with smart adaptive twenty-millisecond closed seams");
        var settings = defaults with { RepeatCount = 3, CrossfadeMilliseconds = 2, SmartSeam = false, AdaptiveCrossfade = false, LoopEndToStart = false };
        var original = Stereo(.1f, .2f, .3f, .4f, .5f, .6f, .7f, .8f);
        var repeated = SeamlessRepeatEffect.ProcessStereo(original, 1000, settings);
        var expected = Stereo(.1f, .2f, .3f, .4f, .5f, .6f, .5f, .4f, .3f, .4f, .5f, .6f, .5f, .4f, .3f, .4f, .5f, .6f, .7f, .8f);
        Check(Near(repeated, expected) && repeated.Length / 2 == 3 * 8 - 2 * 2, "linear overlap joins produce the shortened stereo timeline without gain summation");
        Check(original.SequenceEqual(Stereo(.1f, .2f, .3f, .4f, .5f, .6f, .7f, .8f)) && repeated.Take(2).SequenceEqual(original.Take(2)) && repeated.TakeLast(2).SequenceEqual(original.TakeLast(2)), "repetition keeps source samples immutable and preserves the first and last stereo edges");
        var constant = Stereo(Enumerable.Repeat(.4f, 8).ToArray());
        Check(Near(SeamlessRepeatEffect.ProcessStereo(constant, 1000, settings with { AdaptiveCrossfade = true }), Stereo(Enumerable.Repeat(.4f, 20).ToArray())), "correlated constant loops do not receive a crossfade gain bump");
        float[] orthogonal = [0, 1, 0, 1, .1f, .1f, .1f, .1f, .1f, .1f, .1f, .1f, 1, 0, 1, 0];
        var adaptive = SeamlessRepeatEffect.ProcessStereo(orthogonal, 1000, settings with { RepeatCount = 2, AdaptiveCrossfade = true });
        var linear = SeamlessRepeatEffect.ProcessStereo(orthogonal, 1000, settings with { RepeatCount = 2 });
        double energy = adaptive[12] * adaptive[12] + adaptive[13] * adaptive[13], linearEnergy = linear[12] * linear[12] + linear[13] * linear[13];
        Check(Math.Abs(energy - 1) < .00001 && energy > linearEnergy * 1.5, "uncorrelated stereo edges use adaptive equal-power weighting instead of an energy dip");
        float[] opposite = [1, -.5f, 1, -.5f, 0, 0, 0, 0, 0, 0, 0, 0, -1, .5f, -1, .5f];
        var safePhase = SeamlessRepeatEffect.ProcessStereo(opposite, 1000, settings with { RepeatCount = 2, AdaptiveCrossfade = true });
        Check(safePhase.All(float.IsFinite) && safePhase.All(sample => Math.Abs(sample) <= Math.Sqrt(2)), "opposed phase never drives unsafe crossfade normalization");
        Check(SeamlessRepeatEffect.AnalyzeSeam(original, 1000, settings with { CrossfadeMilliseconds = 10000 }).OverlapFrames == 3, "short selections clamp overlap strictly below half their length");
        Check(SeamlessRepeatEffect.AnalyzeSeam(Stereo(.1f, .2f), 1000, defaults).OverlapFrames == 0, "one-frame and two-frame selections keep a valid zero-overlap join");
        Check(SeamlessRepeatEffect.ProcessStereo(original, 1000, settings with { RepeatCount = 1 }).SequenceEqual(original), "one repetition is an exact audio identity");
        Check(SeamlessRepeatEffect.ProcessStereo(original, 1000, settings with { CrossfadeMilliseconds = 0 }).SequenceEqual(Enumerable.Range(0, 3).SelectMany(_ => original)), "zero overlap gives ordinary adjacent repetition");

        float[] Matching(int match)
        {
            var samples = Stereo(Enumerable.Repeat(-.4f, 40).ToArray());
            for (int frame = 0; frame < match; frame++)
            { float value = .1f + frame * .02f; samples[frame * 2] = samples[(40 - match + frame) * 2] = value; samples[frame * 2 + 1] = samples[(40 - match + frame) * 2 + 1] = -value / 2; }
            return samples;
        }
        var smartSource = Matching(10); var smartSettings = settings with { CrossfadeMilliseconds = 8, SmartSeam = true };
        var fixedInfo = SeamlessRepeatEffect.AnalyzeSeam(smartSource, 1000, smartSettings with { SmartSeam = false });
        var smartInfo = SeamlessRepeatEffect.AnalyzeSeam(smartSource, 1000, smartSettings);
        Check(smartInfo.OverlapFrames == 10 && smartInfo.Mismatch < fixedInfo.Mismatch && smartInfo.Correlation > .999, "smart seam finds a closer stereo match within the bounded nearby search");
        Check(smartInfo == SeamlessRepeatEffect.AnalyzeSeam(smartSource, 1000, smartSettings), "smart seam selection is deterministic");
        var a = Track(smartSource); var b = Track(Matching(6)); b.Gain = .8f;
        var off = Track(Stereo(Enumerable.Repeat(.05f, 48).ToArray())); off.Enabled = false;
        var later = Track(Stereo(.2f)); later.Offset = 50;
        var all = new[] { a, b, off, later }; var snapshots = all.Select(track => track.Snapshot()).ToArray();
        var prepared = await SeamlessRepeatEffect.PrepareAsync(snapshots, new HashSet<AudioTrack> { a, b }, 0, 40, smartSettings);
        long newEnd = prepared.SelectionEnd!.Value, delta = newEnd - 40, step = prepared.CueFrames![1];
        Check(prepared.CueFrames.SequenceEqual(new[] { 0L, step, step * 2, newEnd }) && prepared.PlaybackTracks[0].Frames == newEnd && prepared.PlaybackTracks[1].Frames == newEnd,
            "different target rows use one pooled smart overlap and identical copy timing");
        Check(prepared.AuditionEnd == newEnd && prepared.Detail?.Contains("ms overlap") == true && all.Select(track => track.Frames).SequenceEqual(new long[] { 40, 40, 48, 1 }) && a.Patches.Length == 0,
            "preview discloses the chosen overlap without changing original tracks");
        var offPreview = prepared.PlaybackTracks[2].Source;
        Check(!offPreview.Enabled && offPreview.Frames == 48 + delta && offPreview.ReadVisibleSamples(40, newEnd).All(sample => sample == 0)
            && Near(offPreview.ReadVisibleSamples(newEnd, newEnd + 8), Stereo(Enumerable.Repeat(.05f, 8).ToArray())) && prepared.PlaybackTracks[3].Offset == 50 + delta,
            "unticked rows retain their original audio with aligned inserted silence and later rows shift right");
        Commit(prepared); var edited = all.Select(track => track.Snapshot()).ToArray();
        Check(a.Frames == newEnd && Near(a.ReadVisibleSamples(0, newEnd), prepared.PlaybackTracks[0].Source.ReadVisibleSamples(0, newEnd)), "Apply replacement states match the non-destructive preview exactly");
        foreach (var state in snapshots) state.ApplyToSource();
        Check(a.Frames == 40 && a.Patches.Length == 0 && off.Frames == 48 && later.Offset == 50, "snapshot Undo restores all rows after overlap repetition");
        foreach (var state in edited) state.ApplyToSource();
        Check(a.Frames == newEnd && off.Frames == 48 + delta && later.Offset == 50 + delta, "snapshot Redo restores repeated audio and aligned timeline insertion");

        var identity = Track(original); var identityBefore = identity.Snapshot();
        var oneCopy = await SeamlessRepeatEffect.PrepareAsync([identityBefore], new HashSet<AudioTrack> { identity }, 0, 8, settings with { RepeatCount = 1 });
        Check(oneCopy.ReplacementStates?.Length == 1 && oneCopy.SelectionEnd == 8 && oneCopy.CueFrames!.SequenceEqual(new long[] { 0, 8 }), "one repetition returns a valid committable unchanged transaction");
        Commit(oneCopy); Check(identity.SourceFrames == 8 && identity.Patches.Length == 0 && identity.ReadVisibleSamples(0, 8).SequenceEqual(original), "applying one repetition adds no duplicate source samples or audio changes");
        var faded = Track(Stereo(Enumerable.Repeat(.5f, 8).ToArray())); faded.Gain = .25f; faded.Fades = [new(0, 8, true)];
        var fadedResult = await SeamlessRepeatEffect.PrepareAsync([faded.Snapshot()], new HashSet<AudioTrack> { faded }, 2, 6,
            settings with { RepeatCount = 2, CrossfadeMilliseconds = 0 });
        var fadedExpected = Stereo(new[] { 0f, 1f, 2f, 3f, 4f, 5f, 2f, 3f, 4f, 5f, 6f, 7f }.Select(value => value * .5f / 7).ToArray());
        Check(Near(Editing.RenderSelection(fadedResult.PlaybackTracks[0].Source, 0, 12), fadedExpected) && fadedResult.PlaybackTracks[0].Gain == .25f,
            "repetition bakes selected fades once and preserves outside fade curves and track gain");
        var sparse = Track(Stereo(0)); sparse.Patches = [new(AudioTrack.MaxSourceFrames - 1, Stereo(.1f))]; sparse.Segments = [new(AudioTrack.MaxSourceFrames - 1, 1)];
        bool rejected = false;
        try { await SeamlessRepeatEffect.PrepareAsync([sparse.Snapshot()], new HashSet<AudioTrack> { sparse }, 0, 1, settings); } catch (IOException) { rejected = true; }
        Check(rejected && sparse.Frames == 1 && sparse.SourceFrames == AudioTrack.MaxSourceFrames, "source extent limits reject repetition before allocating large rendered audio");
        var shared = new float[65536]; var crowded = Enumerable.Range(0, 2049).Select(_ => Track(shared)).ToArray();
        rejected = false;
        try { await SeamlessRepeatEffect.PrepareAsync(crowded.Select(track => track.Snapshot()).ToArray(), new HashSet<AudioTrack> { crowded[0] }, 0, 1, settings); } catch (IOException) { rejected = true; }
        Check(rejected && crowded[0].Patches.Length == 0, "combined source and patch session budget is checked before rendering repetitions");
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel(); rejected = false;
            try { await SeamlessRepeatEffect.PrepareAsync([identity.Snapshot()], new HashSet<AudioTrack> { identity }, 0, 8, settings, cancel.Token); } catch (OperationCanceledException) { rejected = true; }
            Check(rejected && identity.Patches.Length == 0, "cancelled preparation leaves source data unchanged");
        }
        var chainSource = Track(original);
        var chain = await EffectChain.PrepareAsync([chainSource], [chainSource], 0, 8,
            [new(EffectKind.SeamlessRepeat) { SeamlessRepeat = settings with { CrossfadeMilliseconds = .0416666666667 } }, new(EffectKind.FadeOut)]);
        Check(chain.SelectionEnd == 20 && chain.AuditionEnd == 20 && chain.PlaybackTracks[0].Frames == 20 && chain.Changes.Length == 0
            && chain.PlaybackTracks[0].Source.ReadVisibleSamples(19, 20).All(sample => sample == 0), "later preview stages process the overlap-adjusted duration without hidden commits");

        string root = Path.Combine(Path.GetTempPath(), "loopsmith-seamless-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root); string path = Path.Combine(root, "Original.wav"), session = Path.Combine(root, "Seamless.orchardsound");
            using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2))) writer.WriteSamples(original, 0, original.Length);
            byte[] hash = SHA256.HashData(await File.ReadAllBytesAsync(path)); var audio = await AudioFiles.LoadAsync(path);
            var result = await EffectPreview.PrepareAsync([audio], [audio], 0, 8, new(EffectKind.SeamlessRepeat) { SeamlessRepeat = settings }); Commit(result);
            await SessionFiles.SaveAsync(session, [audio], new SelectionStore()); var loaded = await SessionFiles.LoadAsync(session);
            Check(loaded.Tracks[0].Frames == audio.Frames && loaded.Tracks[0].ReadVisibleSamples(0, audio.Frames).SequenceEqual(audio.ReadVisibleSamples(0, audio.Frames)), "sessions roundtrip shortened overlap audio and virtual source segments exactly");
            Check(SHA256.HashData(await File.ReadAllBytesAsync(path)).SequenceEqual(hash) && Directory.GetFiles(root).Length == 2, "seamless preview and Apply preserve the source file without generated audio files");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine($"{checks} seamless repeat checks passed.");
    }
}
