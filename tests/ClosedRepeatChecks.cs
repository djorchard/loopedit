using System.Reflection;
using System.Security.Cryptography;
using NAudio.Wave;
using OrchardSound;

public static class ClosedRepeatChecks
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static async Task Run()
    {
        int checks = 0;
        void Check(bool condition, string name)
        { if (!condition) throw new Exception("FAILED CLOSED REPEAT: " + name); checks++; Console.WriteLine("PASS CLOSED REPEAT " + name); }
        static float[] Stereo(params float[] values) => values.SelectMany(value => new[] { value, -value / 2 }).ToArray();
        static AudioTrack Track(float[] samples, long offset = 0) => new() { Path = "Closed repeat fixture.wav", Samples = samples, Peaks = new PeakPyramid(samples), Offset = offset };
        static bool Near(float[] first, float[] second) => first.Length == second.Length && first.Zip(second).All(pair => Math.Abs(pair.First - pair.Second) < .00001f);
        static void Commit(EffectPreviewResult result) { foreach (var state in result.ReplacementStates ?? []) state.ApplyToSource(); }
        var settings = new SeamlessRepeatSettings { RepeatCount = 3, CrossfadeMilliseconds = 2, SmartSeam = false, AdaptiveCrossfade = false };
        var source = Stereo(.1f, .2f, .3f, .4f, .5f, .6f, .7f, .8f);
        var closed = SeamlessRepeatEffect.ProcessStereo(source, 1000, settings); var period = Stereo(.5f, .4f, .3f, .4f, .5f, .6f);
        Check(settings.LoopEndToStart && Near(closed, Enumerable.Range(0, 3).SelectMany(_ => period).ToArray()), "closed repetition renders the expected six-frame overlap-add period three times");
        Check(closed.Length / 2 == 3 * (8 - 2) && closed.Take(12).SequenceEqual(closed.Skip(12).Take(12)) && closed.Take(12).SequenceEqual(closed.Skip(24).Take(12)), "every repeated cycle is bit-identical and includes its closing overlap");
        float internalStep = closed[12] - closed[10], wrapStep = closed[0] - closed[^2];
        Check(Math.Abs(internalStep - wrapStep) < .000001 && Math.Abs(wrapStep + .1f) < .000001
            && Math.Abs(closed[2] - closed[0]) < .100001 && Math.Abs(closed[4] - closed[2]) < .100001, "the final wrap matches internal wraps and spreads the raw edge jump into small steps");
        var single = SeamlessRepeatEffect.ProcessStereo(source, 1000, settings with { RepeatCount = 1 });
        Check(Near(single, period) && single.SequenceEqual(closed.Take(period.Length)), "one closed repetition is the same shorter period used by a longer repeat");
        var open = SeamlessRepeatEffect.ProcessStereo(source, 1000, settings with { LoopEndToStart = false, RepeatCount = 2 });
        Check(Near(single, open.Skip(period.Length).Take(period.Length).ToArray()) && open.Length / 2 == 14 && open[0] == source[0] && open[^2] == source[^2], "turning closure off retains the published open-chain duration and source edges");
        var constant = Stereo(Enumerable.Repeat(.4f, 8).ToArray());
        Check(Near(SeamlessRepeatEffect.ProcessStereo(constant, 1000, settings with { AdaptiveCrossfade = true }), Stereo(Enumerable.Repeat(.4f, 18).ToArray())), "adaptive closed cycles keep correlated constant audio at its original gain");
        float[] unrelated = [0, 1, 0, 1, .1f, .1f, .1f, .1f, .1f, .1f, .1f, .1f, 1, 0, 1, 0];
        var adaptive = SeamlessRepeatEffect.ProcessStereo(unrelated, 1000, settings with { AdaptiveCrossfade = true, RepeatCount = 1 });
        Check(Math.Abs(adaptive[0] * adaptive[0] + adaptive[1] * adaptive[1] - 1) < .00001 && Math.Abs(adaptive[2] * adaptive[2] + adaptive[3] * adaptive[3] - 1) < .00001,
            "uncorrelated closing overlaps use bounded equal-power stereo weights");
        var opposed = Stereo(1, 1, .1f, .1f, .1f, .1f, -1, -1);
        Check(SeamlessRepeatEffect.ProcessStereo(opposed, 1000, settings with { AdaptiveCrossfade = true }).All(value => float.IsFinite(value) && Math.Abs(value) <= Math.Sqrt(2)), "opposed-phase closed seams remain finite without excessive normalization");
        Check(SeamlessRepeatEffect.ProcessStereo(source, 1000, settings with { CrossfadeMilliseconds = 0 }).SequenceEqual(Enumerable.Range(0, 3).SelectMany(_ => source)), "zero-overlap closure retains ordinary adjacent repetition");

        var target = Track(Stereo(Enumerable.Range(0, 240).Select(index => .1f + index * .0001f).ToArray()), 100);
        var crossing = Track(Stereo(Enumerable.Range(0, 140).Select(index => index * .001f).ToArray()), 250); crossing.Enabled = false;
        var inside = Track(Stereo(Enumerable.Range(0, 80).Select(index => index * .001f).ToArray()), 310); inside.Enabled = false;
        var later = Track(Stereo(.2f), 500); var all = new[] { target, crossing, inside, later };
        var before = all.Select(track => track.Snapshot()).ToArray();
        var closedSettings = settings with { RepeatCount = 1, CrossfadeMilliseconds = 1 };
        var preview = await SeamlessRepeatEffect.PrepareAsync(before, new HashSet<AudioTrack> { target }, 100, 340, closedSettings);
        Check(preview.SelectionEnd == 292 && preview.AuditionEnd == 292 && preview.CueFrames!.SequenceEqual(new long[] { 100, 292 }) && preview.ReplacementStates?.Length == 4,
            "one closed cycle returns a valid signed timeline contraction and exact cue boundaries");
        Check(preview.Detail?.Contains("end-to-start") == true && !preview.Detail.Contains("no joins"), "closed count-one status discloses its wrap crossfade and result duration");
        Check(preview.PlaybackTracks[0].Offset == 100 && preview.PlaybackTracks[0].Frames == 192 && target.Frames == 240 && target.Patches.Length == 0,
            "shorter closed preview remains detached from its original source");
        Check(preview.PlaybackTracks[1].Offset == 250 && preview.PlaybackTracks[1].Frames == 92 && !preview.PlaybackTracks[1].Enabled
            && preview.PlaybackTracks[1].Source.SourceFrameAt(42) == 90, "an excluded row crossing the removed tail retains its two surrounding source ranges");
        Check(preview.PlaybackTracks[2].Offset == 292 && preview.PlaybackTracks[2].Frames == 50 && preview.PlaybackTracks[2].Source.SourceFrameAt(0) == 30
            && preview.PlaybackTracks[3].Offset == 452, "excluded rows starting inside the removed tail clamp correctly while later rows shift left");
        Commit(preview); var after = all.Select(track => track.Snapshot()).ToArray();
        Check(target.Frames == 192 && crossing.Frames == 92 && inside.Offset == 292 && later.Offset == 452, "Apply commits the shorter audio and aligned contraction on all rows");
        foreach (var state in before) state.ApplyToSource();
        Check(target.Frames == 240 && target.Patches.Length == 0 && crossing.Frames == 140 && inside.Offset == 310 && later.Offset == 500, "snapshot Undo restores the original audio extents and positions");
        foreach (var state in after) state.ApplyToSource();
        Check(target.Frames == 192 && crossing.Frames == 92 && later.Offset == 452, "snapshot Redo restores the closed contraction");
        var identity = Track(source);
        var noOverlap = await SeamlessRepeatEffect.PrepareAsync([identity.Snapshot()], new HashSet<AudioTrack> { identity }, 0, 8, closedSettings with { CrossfadeMilliseconds = 0 });
        Commit(noOverlap);
        Check(noOverlap.SelectionEnd == 8 && noOverlap.ReplacementStates?.Length == 1 && identity.SourceFrames == 8 && identity.Patches.Length == 0,
            "closed count-one zero-overlap remains a committable identity without appended source audio");

        var other = Track(Stereo(Enumerable.Range(0, 240).Select(index => .2f * (float)Math.Sin(index * .7)).ToArray()), 100);
        var shared = await SeamlessRepeatEffect.PrepareAsync([before[0], other.Snapshot()], new HashSet<AudioTrack> { target, other }, 100, 340,
            closedSettings with { SmartSeam = true, RepeatCount = 3 });
        long sharedPeriod = shared.CueFrames![1] - 100;
        Check(shared.PlaybackTracks.All(state => state.Frames == sharedPeriod * 3) && shared.SelectionEnd == 100 + sharedPeriod * 3,
            "smart seam uses one common closed-cycle length across different enabled stereo tracks");
        var pulse = new float[240 * 2]; pulse[238 * 2] = .8f; pulse[238 * 2 + 1] = -.4f;
        var tailSource = Track(pulse);
        var tailChain = await EffectChain.PrepareAsync([tailSource], [tailSource], 0, 240,
            [new(EffectKind.Delay) { DelaySeconds = .001, Wet = .5f, Feedback = 0, CutTail = false },
                new(EffectKind.SeamlessRepeat) { SeamlessRepeat = closedSettings }, new(EffectKind.Reverse)]);
        Check(tailChain.SelectionEnd == 192 && tailChain.AuditionEnd == 239 && tailChain.PlaybackTracks[0].Frames == 239
            && Math.Abs(tailChain.PlaybackTracks[0].Source.ReadVisibleSamples(238, 239)[0] - .4f) < .00001, "earlier effect tails shift left with contraction and remain audible after later effects");
        var shortPulse = new float[160 * 2]; shortPulse[159 * 2] = .8f;
        var shortSource = Track(shortPulse);
        var clampedTail = await EffectChain.PrepareAsync([shortSource], [shortSource], 0, 240,
            [new(EffectKind.Delay) { DelaySeconds = .001, Wet = .5f, Feedback = 0, CutTail = false }, new(EffectKind.SeamlessRepeat) { SeamlessRepeat = closedSettings }]);
        Check(clampedTail.SelectionEnd == 192 && clampedTail.AuditionEnd == 192 && clampedTail.PlaybackTracks[0].Frames == 192,
            "an earlier audible end inside the removed tail clamps to the new end instead of leaving obsolete preview silence");

        var bufferTrack = Track(source);
        using (var buffer = new BufferedPlayback(new MixProvider([bufferTrack.Snapshot()], 0, 8, true, 7), 20))
        {
            var replacement = new MixProvider([bufferTrack.Snapshot()], 0, 6, true, 0); buffer.Replace(replacement, false);
            var old = new float[buffer.CapacitySamples]; buffer.Read(old, 0, old.Length);
            if (!SpinWait.SpinUntil(() => ReferenceEquals(typeof(BufferedPlayback).GetField("source", Private)!.GetValue(buffer), replacement) && buffer.BufferedSamples >= 2, TimeSpan.FromSeconds(2))) throw new TimeoutException("Loop contraction replacement deadline exceeded.");
            var frame = new float[2]; buffer.Read(frame, 0, 2);
            Check(buffer.Position == 1 && frame[0] == .1f && buffer.TryGetAudiblePosition(old.Length / 2 + 1, out long audible) && audible == 1,
                "live loop replacement rebases the producer head into the shorter loop and records its exact audible frame");
        }
        using (var buffer = new BufferedPlayback(new MixProvider([bufferTrack.Snapshot()], 0, 8, false, 7), 20))
        {
            var frame = new float[2]; buffer.Read(frame, 0, 2);
            var replacement = new MixProvider([bufferTrack.Snapshot()], 0, 6, false, 6); buffer.Replace(replacement, false);
            if (!SpinWait.SpinUntil(() => ReferenceEquals(typeof(BufferedPlayback).GetField("source", Private)!.GetValue(buffer), replacement), TimeSpan.FromSeconds(2))) throw new TimeoutException("One-shot contraction replacement deadline exceeded.");
            var clock = System.Diagnostics.Stopwatch.StartNew(); bool ended = false, restarted = false;
            while (clock.Elapsed < TimeSpan.FromSeconds(2))
            {
                int count = buffer.Read(frame, 0, 2); if (count == 0) { ended = true; break; }
                restarted |= frame.Any(value => value != 0); Thread.Yield();
            }
            Check(ended && !restarted, "contracted cursor one-shots remain at EOF instead of restarting from the file beginning");
        }
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel(); bool rejected = false;
            try { await SeamlessRepeatEffect.PrepareAsync(before, new HashSet<AudioTrack> { target }, 100, 340, closedSettings, cancel.Token); } catch (OperationCanceledException) { rejected = true; }
            Check(rejected && target.Frames == 192, "cancelled closed-loop jobs do not modify current committed tracks");
        }

        string root = Path.Combine(Path.GetTempPath(), "loopsmith-closed-repeat-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root); string path = Path.Combine(root, "Original.wav"), session = Path.Combine(root, "Closed.orchardsound");
            var samples = Stereo(Enumerable.Repeat(.2f, 240).ToArray());
            using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2))) writer.WriteSamples(samples, 0, samples.Length);
            byte[] hash = SHA256.HashData(await File.ReadAllBytesAsync(path)); var file = await AudioFiles.LoadAsync(path); file.Offset = 100;
            var prepared = await SeamlessRepeatEffect.PrepareAsync([file.Snapshot()], new HashSet<AudioTrack> { file }, 100, 340, closedSettings); Commit(prepared);
            var store = new SelectionStore(); store.SavePoint("Inside removed tail", 320); store.SavePoint("Later", 500); store.ApplyDelete(292, 340);
            await SessionFiles.SaveAsync(session, [file], store); var restored = await SessionFiles.LoadAsync(session);
            Check(restored.Tracks[0].Frames == 192 && restored.Tracks[0].Offset == 100 && restored.Tracks[0].ReadVisibleSamples(0, 192).SequenceEqual(file.ReadVisibleSamples(0, 192))
                && restored.Selections.Select(point => point.Start).SequenceEqual(new long[] { 292, 452 }), "sessions roundtrip closed audio, virtual segments and contracted cue positions");
            Check(SHA256.HashData(await File.ReadAllBytesAsync(path)).SequenceEqual(hash) && Directory.GetFiles(root).Length == 2, "closed-loop processing preserves original source files without scratch audio outputs");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine($"{checks} closed repeat checks passed.");
    }
}
