using System.Security.Cryptography;
using NAudio.Wave;
using OrchardSound;

public static class AutomationChecks
{
    public static async Task Run()
    {
        int checks = 0;
        void Check(bool condition, string name)
        { if (!condition) throw new Exception("FAILED AUTOMATION: " + name); checks++; Console.WriteLine("PASS AUTOMATION " + name); }
        bool Near(float a, float b, float tolerance = .00001f) => Math.Abs(a - b) <= tolerance;
        AudioTrack Track(float[] samples, string path = "Automation fixture.wav") => new() { Path = path, Samples = samples, Peaks = new PeakPyramid(samples) };
        void Commit(EffectPreviewResult preview)
        {
            foreach (var change in preview.Changes)
            {
                change.Track.WriteVisibleSamples(change.VisibleStart, change.Samples);
                Editing.RemoveFadesInRange(change.Track, change.VisibleStart, change.VisibleStart + change.Samples.LongLength / 2);
            }
        }
        float[] constant = Enumerable.Range(0, 5).SelectMany(_ => new[] { .2f, -.1f }).ToArray();
        var peak = AudioEffects.Process(constant, 1000, new(EffectKind.Maximize) { TargetPeak = .25f, EndTargetPeak = 1 });
        Check(Near(peak[0], .25f) && Near(peak[4], .625f) && Near(peak[8], 1) && Near(peak[9], -.5f), "maximize target peak interpolates linearly and preserves stereo ratios");
        var heldPeak = AudioEffects.Process(constant, 1000, new(EffectKind.Maximize) { TargetPeak = .25f, EndTargetPeak = 1, AutomationEnabled = false });
        Check(heldPeak.Where((_, i) => i % 2 == 0).All(value => Near(value, .25f)), "disabled automation holds the start peak across the range");
        var fade = AudioEffects.Process(constant, 1000, new(EffectKind.FadeOut) { FadeStrength = 0, EndFadeStrength = 1 });
        Check(Near(fade[0], .2f) && Near(fade[4], .15f) && Near(fade[8], 0), "fade strength follows a linear parameter ramp over the fade curve");
        var wet = AudioEffects.Process(constant, 1000, new(EffectKind.Delay) { DelaySeconds = .1, Wet = 0, EndWet = 1 });
        Check(Near(wet[0], .2f) && Near(wet[4], .1f) && Near(wet[8], 0), "delay mix interpolates linearly from dry to wet");
        var heldMix = AudioEffects.Process(constant, 1000, new(EffectKind.Delay) { DelaySeconds = .1, Wet = 0, EndWet = 1, AutomationEnabled = false });
        Check(heldMix.SequenceEqual(constant), "disabled wet automation is an exact dry bypass");

        var impulse = new float[100 * 2]; impulse[0] = 1;
        var feedback = AudioEffects.Process(impulse, 1000, new(EffectKind.Delay) { DelaySeconds = .01, Wet = 1, Feedback = 0, EndFeedback = .9f });
        Check(Near(feedback[20], 1) && Near(feedback[40], .9f * 10 / 99) && Near(feedback[60], (.9f * 10 / 99) * (.9f * 20 / 99)), "delay feedback changes at each repeat according to its linear ramp");
        var bpm = new EffectSettings(EffectKind.Delay) { DelayInBpm = true, DelayBpm = 120, EndDelayBpm = 240, Wet = 1, Feedback = 0 };
        Check(Math.Abs(AudioEffects.DelaySecondsAt(bpm, .5) - 1d / 3) < .000001 && Math.Abs(AudioEffects.DelaySecondsAt(bpm with { AutomationEnabled = false }, 1) - .5) < .000001,
            "BPM timing converts a quarter beat after linearly interpolating tempo");
        var slowImpulse = new float[600 * 2]; slowImpulse[0] = 1;
        var bpmDelay = AudioEffects.Process(slowImpulse, 1000, bpm with { EndDelayBpm = null });
        Check(Near(bpmDelay[500 * 2], 1) && Near(bpmDelay[0], 0), "120 BPM delay produces a quarter-beat echo at500 milliseconds");
        Check(Math.Abs(AudioEffects.DelaySecondsAt(new(EffectKind.Delay) { DelaySeconds = .1, EndDelaySeconds = .3 }, .5) - .2) < .000001,
            "millisecond delay mode linearly interpolates its time endpoints");
        var roomStart = new EffectSettings(EffectKind.Reverb) { RoomSize = .1f, EndRoomSize = .9f, Wet = .5f, EndWet = .8f, AutomationEnabled = false };
        Check(AudioEffects.Process(impulse, 1000, roomStart).SequenceEqual(AudioEffects.Process(impulse, 1000, roomStart with { EndRoomSize = null, EndWet = null })),
            "disabled room and mix automation preserve constant reverb settings");
        Check(!AudioEffects.Process(impulse, 1000, roomStart with { AutomationEnabled = true }).SequenceEqual(AudioEffects.Process(impulse, 1000, roomStart)),
            "room and mix automation change the resulting reverb");
        var filterStart = new EffectSettings(EffectKind.Filter) { StartHz = 100, EndHz = 300, AutomationEnabled = false };
        Check(AudioEffects.Process(impulse, 1000, filterStart).SequenceEqual(AudioEffects.Process(impulse, 1000, filterStart with { EndHz = 100 })), "disabled filter automation holds its starting frequency");
        bool rejected = false;
        try { AudioEffects.Process(impulse, 1000, new(EffectKind.Delay) { EndFeedback = 1 }); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "unstable automated feedback endpoints are rejected");

        float[] shortImpulse = [1, -.5f, 0, 0, 0, 0];
        var delaySettings = new EffectSettings(EffectKind.Delay) { DelaySeconds = .01, Wet = .5f, Feedback = 0 };
        var cut = AudioEffects.Process(shortImpulse, 1000, delaySettings);
        var tail = AudioEffects.Process(shortImpulse, 1000, delaySettings with { CutTail = false });
        Check(cut.Length == shortImpulse.Length && tail.Length == 22 && Near(tail[20], .5f) && Near(tail[21], -.25f), "Cut tail preserves the selected length while disabling it retains the full stereo echo");
        Check(shortImpulse.SequenceEqual(new[] { 1f, -.5f, 0, 0, 0, 0 }), "tail rendering leaves input audio unchanged");
        Check(AudioEffects.Process(shortImpulse, 1000, delaySettings with { CutTail = false, Wet = 0 }).SequenceEqual(shortImpulse), "a dry delay does not append an empty tail");
        var reverbTail = AudioEffects.Process(shortImpulse, 1000, new(EffectKind.Reverb) { CutTail = false, Wet = .5f });
        Check(reverbTail.Length > shortImpulse.Length && reverbTail.Skip(shortImpulse.Length).Any(value => Math.Abs(value) > .0001), "reverb renders a zero-input decay after the selected audio");
        var bounded = AudioEffects.Process(shortImpulse, 1000, delaySettings with { CutTail = false, Feedback = .95f });
        Check(bounded.Length <= shortImpulse.Length + 20000 && bounded.All(float.IsFinite), "feedback tails have a finite ten-second bound");

        var selected = new float[144 * 2]; selected[0] = .8f; selected[1] = -.4f;
        for (int frame = 48; frame < 144; frame++) { selected[frame * 2] = .2f; selected[frame * 2 + 1] = -.1f; }
        var target = Track(selected); target.Fades = [new Fade(48, 144, false)]; target.Offset = 1000;
        var before = target.Snapshot();
        var renderSettings = new EffectSettings(EffectKind.Delay) { DelaySeconds = .005, Wet = .5f, Feedback = 0, CutTail = false };
        var preview = await EffectPreview.PrepareAsync([target], [target], 1000, 1048, renderSettings);
        var clone = preview.PlaybackTracks.Single().Source;
        Check(target.Frames == 144 && target.Patches.Length == 0 && clone.Frames == 241 && clone.SourceFrames == 241 && preview.AuditionEnd == 1241,
            "tail preview extends only its detached track and reports the audible end");
        var previewAudio = Editing.RenderSelection(clone, 0, clone.Frames);
        Check(Near(previewAudio[240 * 2], .4f) && Near(previewAudio[240 * 2 + 1], -.2f) && Near(previewAudio[50 * 2], .2f * (1 - 2f / 95)),
            "tail preview preserves following audio and bakes existing fades once");
        Check(clone.QueryPeaks(240, 241).Max >= .4f && ReferenceEquals(clone.Samples, target.Samples), "virtual tail peaks render without replacing the original source buffer");
        Commit(preview); var after = target.Snapshot();
        Check(target.Frames == 241 && target.ReadVisibleSamples(0, target.Frames).SequenceEqual(clone.ReadVisibleSamples(0, clone.Frames)), "Apply commits exactly the tail audio heard in preview");
        before.ApplyToSource(); Check(target.Frames == 144 && target.SourceFrames == 144 && target.Patches.Length == 0 && target.Fades.Count == 1, "Undo restores source extent, segments, fades and audio after tail extension");
        after.ApplyToSource(); Check(target.Frames == 241 && Near(target.GetSample(target.SourceFrameAt(240), 0), .4f), "Redo restores patch-backed tail frames before validating their segments");
        TimelineEditing.Delete([target], 1100, 1120);
        Check(target.Frames == 221 && Near(target.ReadVisibleSamples(220, 221)[0], .4f), "deleting earlier audio keeps a virtual tail correctly mapped");

        var following = Track(Enumerable.Range(0, 480).SelectMany(frame => frame == 0 ? new[] { .8f, -.4f } : new[] { .2f, -.1f }).ToArray());
        var overlap = await EffectPreview.PrepareAsync([following], [following], 0, 48, renderSettings);
        Check(overlap.PlaybackTracks[0].Frames == 480 && Near(overlap.PlaybackTracks[0].Source.ReadVisibleSamples(240, 241)[0], .6f), "effect tails add to existing later audio instead of replacing it");
        var cutoff = await EffectPreview.PrepareAsync([following], [following], 0, 48, renderSettings with { CutTail = true });
        Check(cutoff.AuditionEnd == null && Near(cutoff.PlaybackTracks[0].Source.ReadVisibleSamples(240, 241)[0], .2f), "Cut tail keeps following audio untouched");

        var appended = Track([.1f, -.1f]); var retained = appended.GetSegments(); var span = appended.AppendSourceSamples([.3f, -.3f]);
        Check(appended.Frames == 1 && appended.SourceFrames == 2 && span == new AudioSegment(1, 1), "appending virtual source audio returns a span without inserting visible audio");
        appended.Segments = [.. retained, span]; Check(appended.Frames == 2 && Near(appended.ReadVisibleSamples(1, 2)[0], .3f), "returned virtual spans can be inserted by later timeline edits");
        var boundedSource = Track([0, 0]); boundedSource.Patches = [new(AudioTrack.MaxSourceFrames - 1, [.1f, -.1f])];
        rejected = false;
        try { boundedSource.AppendSourceSamples([.2f, -.2f]); } catch (IOException) { rejected = true; }
        Check(rejected && boundedSource.SourceFrames == AudioTrack.MaxSourceFrames && boundedSource.GetSample(100, 0) == 0, "virtual source gaps are silent and extension respects the per-track bound");

        string root = Path.Combine(Path.GetTempPath(), "loopsmith-automation-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root); string sourcePath = Path.Combine(root, "Original.wav"), sessionPath = Path.Combine(root, "Tail.loopsmith");
            using (var writer = new WaveFileWriter(sourcePath, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2))) writer.WriteSamples(selected, 0, selected.Length);
            byte[] originalHash = SHA256.HashData(await File.ReadAllBytesAsync(sourcePath));
            var source = await AudioFiles.LoadAsync(sourcePath); var prepared = await EffectPreview.PrepareAsync([source], [source], 0, 48, renderSettings); Commit(prepared);
            await SessionFiles.SaveAsync(sessionPath, [source], new SelectionStore()); var loaded = await SessionFiles.LoadAsync(sessionPath);
            Check(loaded.Tracks.Single().Frames == source.Frames && loaded.Tracks[0].ReadVisibleSamples(0, source.Frames).SequenceEqual(source.ReadVisibleSamples(0, source.Frames)),
                "sessions restore tail patches and extended segments with identical audio");
            Check(SHA256.HashData(await File.ReadAllBytesAsync(sourcePath)).SequenceEqual(originalHash) && Directory.GetFiles(root).Length == 2,
                "tail preview Apply and session save preserve original source bytes without scratch audio files");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine($"{checks} automation checks passed.");
    }
}
