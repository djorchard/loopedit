using System.Security.Cryptography;
using NAudio.Wave;
using OrchardSound;

public static class SilenceChecks
{
    public static async Task Run()
    {
        int checks = 0;
        void Check(bool value, string name)
        { if (!value) throw new Exception("FAILED SILENCE: " + name); checks++; Console.WriteLine("PASS SILENCE " + name); }
        static AudioTrack Track(params float[] values)
        {
            var samples = values.SelectMany(value => new[] { value, -value / 2 }).ToArray();
            return new() { Path = "Silence fixture.wav", Samples = samples, Peaks = new(samples) };
        }
        static bool Audio(AudioTrack track, params float[] expected)
        {
            var samples = Editing.RenderSelection(track, 0, track.Frames);
            return samples.Length == expected.Length * 2 && expected.Select((value, frame) => Math.Abs(samples[frame * 2] - value) < .00001f && Math.Abs(samples[frame * 2 + 1] + value / 2) < .00001f).All(equal => equal);
        }
        static void Commit(SilenceInsertionResult result) { foreach (var state in result.ReplacementStates) state.ApplyToSource(); }
        var a = Track(.1f, .2f, .3f, .4f); var disabled = Track(.5f, .6f, .7f); disabled.Offset = 1; disabled.Enabled = false; disabled.Gain = 2;
        var later = Track(.8f); later.Offset = 5; var earlier = Track(.9f); earlier.Offset = -3;
        var tracks = new[] { a, disabled, later, earlier }; var before = tracks.Select(track => track.Snapshot()).ToArray();
        var prepared = await TimelineSilence.PrepareAsync(tracks, 2, 3);
        Check(prepared.Position == 2 && prepared.Duration == 3 && prepared.End == 5 && prepared.ReplacementStates.Length == 4,
            "silence preparation returns exact insertion bounds and one transaction state per row");
        Check(Audio(a, .1f, .2f, .3f, .4f) && disabled.Frames == 3 && later.Offset == 5 && a.Patches.Length == 0,
            "preparing silence never changes committed source audio or offsets");
        Check(Audio(prepared.PlaybackTracks[0].Source, .1f, .2f, 0, 0, 0, .3f, .4f)
            && prepared.PlaybackTracks.All(state => !tracks.Contains(state.Source)), "detached preview inserts exact stereo zeros between the original surrounding audio");
        Check(prepared.ReplacementStates.Select(state => state.Source).SequenceEqual(tracks)
            && prepared.PlaybackTracks[1].Gain == 2 && !prepared.PlaybackTracks[1].Enabled, "transaction sources retain original identities, volume and tick state");
        Commit(prepared); var after = tracks.Select(track => track.Snapshot()).ToArray();
        Check(Audio(a, .1f, .2f, 0, 0, 0, .3f, .4f) && Audio(disabled, .5f, 0, 0, 0, .6f, .7f)
            && disabled.Offset == 1 && !disabled.Enabled, "all crossing rows insert silence and stay aligned even when disabled");
        Check(later.Offset == 8 && Audio(later, .8f) && earlier.Offset == -3 && Audio(earlier, .9f)
            && later.Patches.Length == 0 && earlier.Patches.Length == 0, "later rows shift while earlier completed rows remain unchanged without audio allocation");
        Check(a.Patches.Length == 1 && a.Patches[0].Samples.Length == 2 && a.SourceFrames == 7 && a.Samples.Length == 8,
            "a sparse two-sample terminal patch represents silence without copying the source");
        foreach (var state in before) state.ApplyToSource();
        Check(Audio(a, .1f, .2f, .3f, .4f) && later.Offset == 5 && a.Patches.Length == 0, "one snapshot Undo restores all original samples, maps and offsets");
        foreach (var state in after) state.ApplyToSource();
        Check(Audio(a, .1f, .2f, 0, 0, 0, .3f, .4f) && later.Offset == 8, "snapshot Redo restores the complete silence insertion");

        var faded = Track(1, 1, 1, 1, 1, 1); faded.Gain = .25f; faded.Fades = [new(0, 6, true)];
        Commit(await TimelineSilence.PrepareAsync([faded], 3, 2));
        Check(Audio(faded, 0, .2f, .4f, 0, 0, .6f, .8f, 1) && faded.Gain == .25f && faded.Fades.Count == 2,
            "a fade crossing insertion splits and resumes with its original sample levels");
        var negative = Track(.1f, .2f, .3f, .4f); negative.Offset = -3;
        Commit(await TimelineSilence.PrepareAsync([negative], -2, 2));
        Check(negative.Offset == -3 && Audio(negative, .1f, 0, 0, .2f, .3f, .4f), "negative timeline positions preserve prefix and suffix alignment");
        var cut = Track(.1f, .2f, .3f, .4f, .5f, .6f); TimelineEditing.Delete([cut], 1, 3); cut.WriteVisibleSamples(1, [.8f, -.4f]);
        Commit(await TimelineSilence.PrepareAsync([cut], 1, 2));
        Check(Audio(cut, .1f, 0, 0, .8f, .5f, .6f) && cut.Patches.Length == 2, "silence insertion preserves earlier cuts and processed-audio source patches");
        var end = Track(.1f, .2f, .3f, .4f); var shortRow = Track(.5f, .6f);
        Commit(await TimelineSilence.PrepareAsync([end, shortRow], 4, 3));
        Check(Audio(end, .1f, .2f, .3f, .4f, 0, 0, 0) && Audio(shortRow, .5f, .6f), "inserting at the final endpoint appends silence while shorter earlier rows stay untouched");
        var beyond = Track(.1f, .2f); var tied = Track(.3f); tied.Offset = 1; var old = Track(.4f);
        Commit(await TimelineSilence.PrepareAsync([beyond, tied, old], 5, 2));
        Check(Audio(beyond, .1f, .2f, 0, 0, 0, 0, 0) && tied.Offset == 1 && Audio(tied, .3f, 0, 0, 0, 0, 0)
            && Audio(old, .4f), "inserting beyond all audio sparsely pads only the rows sharing the latest endpoint");
        var prefix = Track(.1f, .2f); prefix.Offset = 2;
        Commit(await TimelineSilence.PrepareAsync([prefix], 2, 3));
        Check(prefix.Offset == 5 && prefix.Patches.Length == 0 && Audio(prefix, .1f, .2f), "insertion at a row start shifts that row instead of copying leading zeros");
        var gapBefore = Track(.1f); var gapAfter = Track(.2f); gapAfter.Offset = 10;
        Commit(await TimelineSilence.PrepareAsync([gapBefore, gapAfter], 5, 3));
        Check(gapBefore.Frames == 1 && gapBefore.Patches.Length == 0 && gapAfter.Offset == 13, "insertion into a timeline gap moves later material without extending earlier tracks");
        var cues = new SelectionStore(); cues.SavePoint("Before", 1); cues.SavePoint("Boundary", 2); cues.SavePoint("After", 5); cues.Save("Span", 1, 6);
        cues.ApplyInsert(2, 3);
        Check(cues.Items.Select(cue => cue.Start).SequenceEqual(new long[] { 1, 2, 8, 1 }) && cues.Items[^1].End == 9,
            "the matching cue-map insertion keeps boundary cues and shifts subsequent saved positions");

        var safe = Track(.1f, .2f); var full = Track(.3f, .4f); full.Patches = [new(AudioTrack.MaxSourceFrames - 1, new float[2])]; full.Segments = [new(0, 2)];
        bool rejected = false;
        try { _ = await TimelineSilence.PrepareAsync([safe, full], 1, 1); } catch (IOException) { rejected = true; }
        Check(rejected && safe.Patches.Length == 0 && Audio(safe, .1f, .2f) && full.Frames == 2,
            "all source-extent bounds are preflighted atomically before any row changes");
        var nearEnd = Track(.1f); nearEnd.Offset = SelectionStore.MaxTimelineFrame - 1; rejected = false;
        try { _ = await TimelineSilence.PrepareAsync([nearEnd], 0, 2); } catch (IOException) { rejected = true; }
        Check(rejected && nearEnd.Offset == SelectionStore.MaxTimelineFrame - 1, "shifting later audio beyond the timeline limit is rejected without mutation");
        var largeSamples = new float[AudioFiles.MaxSessionBytes / (128 * sizeof(float))]; var largePeaks = new PeakPyramid(largeSamples);
        var sessionLimit = Enumerable.Range(0, 128).Select(index => new AudioTrack { Path = "Shared budget fixture " + index + ".wav", Samples = largeSamples, Peaks = largePeaks }).ToArray();
        rejected = false;
        try { _ = await TimelineSilence.PrepareAsync(sessionLimit, 1, 1); } catch (IOException) { rejected = true; }
        Check(rejected && sessionLimit.All(track => track.Patches.Length == 0), "the 512 MiB session budget counts even sparse patch bytes before building previews");
        foreach (var request in new[] { (Position: 1L, Duration: 0L), (Position: 0L, Duration: -1L), (Position: SelectionStore.MaxTimelineFrame, Duration: 1L) })
        {
            rejected = false;
            try { _ = await TimelineSilence.PrepareAsync([safe], request.Position, request.Duration); } catch (Exception error) when (error is ArgumentException or IOException) { rejected = true; }
            Check(rejected && safe.Patches.Length == 0, "invalid silence duration or insertion bounds leave the source unchanged");
        }
        rejected = false;
        try { _ = await TimelineSilence.PrepareAsync([], 0, 1); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "an empty session reports that audio must be loaded first");
        rejected = false;
        try { _ = await TimelineSilence.PrepareAsync([safe, safe], 1, 1); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "duplicate row identities cannot produce conflicting transaction states");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); rejected = false;
            try { _ = await TimelineSilence.PrepareAsync([safe], 1, 1, cancelled.Token); } catch (OperationCanceledException) { rejected = true; }
            Check(rejected && safe.Patches.Length == 0, "cancelled silence preparation exits without source writes");
        }
        var sparse = Track(.1f, .2f); var big = await TimelineSilence.PrepareAsync([sparse], 1, 1_000_000);
        Check(big.PlaybackTracks[0].Frames == 1_000_002 && big.PlaybackTracks[0].Patches.Sum(patch => patch.Samples.Length) == 2
            && big.PlaybackTracks[0].Source.ReadVisibleSamples(999_999, 1_000_001).All(value => value == 0), "long silence consumes constant patch storage while its full sparse range reads as zero");

        string folder = Path.Combine(Path.GetTempPath(), "loopsmith-silence-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder); string sourcePath = Path.Combine(folder, "Original.wav"), sessionPath = Path.Combine(folder, "Silence.orchardsound");
            float[] source = [.1f, -.05f, .2f, -.1f, .3f, -.15f, .4f, -.2f];
            using (var writer = new WaveFileWriter(sourcePath, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2))) writer.WriteSamples(source, 0, source.Length);
            byte[] hash = SHA256.HashData(await File.ReadAllBytesAsync(sourcePath)); var loaded = await AudioFiles.LoadAsync(sourcePath);
            Commit(await TimelineSilence.PrepareAsync([loaded], 2, 100_000));
            var store = new SelectionStore(); store.SavePoint("Later", 3); store.ApplyInsert(2, 100_000);
            await SessionFiles.SaveAsync(sessionPath, [loaded], store); var restored = await SessionFiles.LoadAsync(sessionPath);
            var row = restored.Tracks[0];
            Check(row.Frames == 100_004 && row.Patches.Length == 1 && row.Patches[0].Samples.Length == 2
                && row.ReadVisibleSamples(0, 2).SequenceEqual(source.Take(4)) && row.ReadVisibleSamples(100_002, 100_004).SequenceEqual(source.Skip(4))
                && restored.Selections.Single().Start == 100_003, "sessions retain sparse silence, original stereo spans and shifted saved cues");
            Check(new FileInfo(sessionPath).Length < 20_000 && Directory.GetFiles(folder).Length == 2
                && SHA256.HashData(await File.ReadAllBytesAsync(sourcePath)).SequenceEqual(hash), "silence saves compactly without creating an audio file or changing original file bytes");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        Console.WriteLine($"{checks} silence checks passed.");
    }
}
