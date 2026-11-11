using System.Reflection;
using System.Security.Cryptography;
using NAudio.Wave;
using OrchardSound;

public static class DuplicationChecks
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static async Task Run()
    {
        int checks = 0;
        void Check(bool condition, string name)
        { if (!condition) throw new Exception("FAILED DUPLICATION: " + name); checks++; Console.WriteLine("PASS DUPLICATION " + name); }
        static AudioTrack Track(params float[] values)
        {
            var samples = values.SelectMany(value => new[] { value, -value / 2 }).ToArray();
            return new() { Path = "Duplication fixture.wav", Samples = samples, Peaks = new PeakPyramid(samples) };
        }
        static bool Audio(AudioTrack track, params float[] left)
        {
            var samples = Editing.RenderSelection(track, 0, track.Frames);
            return samples.Length == left.Length * 2 && left.Select((value, frame) => Math.Abs(samples[frame * 2] - value) < .00001 && Math.Abs(samples[frame * 2 + 1] + value / 2) < .00001).All(value => value);
        }
        var a = Track(.1f, .2f, .3f, .4f); var b = Track(.5f, .6f, .7f); b.Offset = 1; b.Enabled = false;
        var later = Track(.8f); later.Offset = 5;
        var originals = new[] { a, b, later }; var before = originals.Select(track => track.Snapshot()).ToArray();
        TimelineDuplication.Duplicate(originals, 1, 3);
        Check(Audio(a, .1f, .2f, .3f, .2f, .3f, .4f), "selection duplicates immediately after its original while later audio shifts right");
        Check(Audio(b, .5f, .6f, .5f, .6f, .7f) && b.Offset == 1 && !b.Enabled, "all loaded rows duplicate in stereo while preserving offsets and inclusion state");
        Check(later.Offset == 7 && Audio(later, .8f) && later.Patches.Length == 0, "rows starting after insertion shift without copying their source buffers");
        Check(ReferenceEquals(a.Samples, before[0].Source.Samples) && a.Samples.Length == 8 && a.SourceFrames == 6, "duplication retains the original immutable source and adds only selected audio");
        var after = originals.Select(track => track.Snapshot()).ToArray();
        foreach (var state in before) state.ApplyToSource();
        Check(Audio(a, .1f, .2f, .3f, .4f) && Audio(b, .5f, .6f, .7f) && later.Offset == 5 && a.Patches.Length == 0, "snapshots undo duplication across every row");
        foreach (var state in after) state.ApplyToSource();
        Check(Audio(a, .1f, .2f, .3f, .2f, .3f, .4f) && later.Offset == 7, "snapshots redo inserted virtual spans and shifted rows");

        var faded = Track(.5f, .5f, .5f, .5f); faded.Gain = .25f; faded.Fades = [new(0, 4, true)];
        TimelineDuplication.Duplicate([faded], 1, 3);
        Check(Audio(faded, 0, .5f / 3, 1f / 3, .5f / 3, 1f / 3, .5f) && faded.Gain == .25f,
            "duplicated audio bakes fades once while the original fade resumes after insertion and gain stays separate");
        var partial = Track(.1f, .2f); partial.Offset = 2;
        TimelineDuplication.Duplicate([partial], 0, 3);
        Check(partial.Offset == 2 && Audio(partial, .1f, 0, 0, .1f, .2f), "partial row overlap retains leading silence inside the duplicated time range");
        var early = Track(.1f); TimelineDuplication.Duplicate([early], 2, 4);
        Check(Audio(early, .1f, 0, 0, 0, 0, 0), "short rows receive only the silence needed to align the inserted range");
        var negative = Track(.1f, .2f, .3f, .4f, .5f, .6f); negative.Offset = -3;
        TimelineDuplication.Duplicate([negative], -2, 1);
        Check(negative.Offset == -3 && Audio(negative, .1f, .2f, .3f, .4f, .2f, .3f, .4f, .5f, .6f), "negative timeline offsets preserve insertion alignment");
        var cut = Track(.1f, .2f, .3f, .4f, .5f, .6f); TimelineEditing.Delete([cut], 1, 3);
        TimelineDuplication.Duplicate([cut], 1, 3);
        Check(Audio(cut, .1f, .4f, .5f, .4f, .5f, .6f), "duplication copies retained audio across an earlier cut");
        var tail = Track(.1f, .2f); var retained = tail.GetSegments(); var extra = tail.AppendSourceSamples([.3f, -.15f, .4f, -.2f]); tail.Segments = [.. retained, extra];
        TimelineDuplication.Duplicate([tail], 2, 4);
        Check(Audio(tail, .1f, .2f, .3f, .4f, .3f, .4f), "existing virtual effect tails can be duplicated like original audio");

        var safe = Track(.1f, .2f); var full = Track(0); full.Patches = [new(AudioTrack.MaxSourceFrames - 1, [.1f, -.05f])]; full.Segments = [new(AudioTrack.MaxSourceFrames - 1, 1)];
        bool rejected = false;
        try { TimelineDuplication.Duplicate([safe, full], 0, 1); } catch (IOException) { rejected = true; }
        Check(rejected && Audio(safe, .1f, .2f) && safe.Patches.Length == 0 && full.Frames == 1, "all track bounds are preflighted before any row changes");
        rejected = false;
        try { TimelineDuplication.Duplicate([safe], 1, 1); } catch (ArgumentException) { rejected = true; }
        Check(rejected && safe.Patches.Length == 0, "empty duplicate ranges are rejected without changes");

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new EditorForm([]); _ = form.Handle;
                var canvas = (WaveCanvas)typeof(EditorForm).GetField("canvas", Private)!.GetValue(form)!;
                var selections = (SelectionStore)typeof(EditorForm).GetField("selections", Private)!.GetValue(form)!;
                var row = Track(.1f, .2f, .3f, .4f); canvas.Slots[0].Track = row;
                canvas.SelectionStart = 1; canvas.SelectionEnd = 3; selections.SavePoint("Later cue", 10);
                void Invoke(string name) => typeof(EditorForm).GetMethod(name, Private)!.Invoke(form, null);
                Invoke("DuplicateSelection");
                Check(Audio(row, .1f, .2f, .3f, .2f, .3f, .4f) && canvas.SelectionStart == 3 && canvas.SelectionEnd == 5,
                    "editor duplication selects the newly inserted copy on the same row");
                Check(canvas.CuePoints.Select(cue => cue.Frame).SequenceEqual(new long[] { 1, 3, 5, 12 }) && selections.Items.Single(item => item.Name == "Later cue").Start == 12,
                    "duplication creates three boundary cues and shifts later stored cues");
                Invoke("Undo");
                Check(Audio(row, .1f, .2f, .3f, .4f) && canvas.SelectionStart == 1 && canvas.SelectionEnd == 3 && selections.Items.Count == 1 && canvas.CuePoints.Single().Frame == 10,
                    "one Undo restores audio, selection and cue points together");
                Invoke("Redo");
                Check(Audio(row, .1f, .2f, .3f, .2f, .3f, .4f) && canvas.CuePoints.Count == 4, "one Redo restores the entire duplicate operation");
                int savedCount = selections.Items.Count; var savedAudio = row.ReadVisibleSamples(0, row.Frames);
                while (selections.Items.Count < SelectionStore.MaxSelections) selections.SavePoint("Capacity " + selections.Items.Count, 20 + selections.Items.Count);
                canvas.SelectionStart = 0; canvas.SelectionEnd = 1; Invoke("DuplicateSelection");
                Check(row.ReadVisibleSamples(0, row.Frames).SequenceEqual(savedAudio) && selections.Items.Count == SelectionStore.MaxSelections,
                    "cue-capacity rejection leaves the complete edit untouched");
                Check(savedCount == 4, "successful duplication adds only the required unique boundary cues");
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("Duplication editor fixture deadline exceeded.");
        if (failure != null) throw new Exception("Duplication editor fixture failed", failure);

        string root = Path.Combine(Path.GetTempPath(), "loopsmith-duplicate-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root); string sourcePath = Path.Combine(root, "Original.wav"), sessionPath = Path.Combine(root, "Duplicate.orchardsound");
            float[] data = [.8f, -.4f, 0, 0, 0, 0, 0, 0];
            using (var writer = new WaveFileWriter(sourcePath, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2))) writer.WriteSamples(data, 0, data.Length);
            byte[] hash = SHA256.HashData(await File.ReadAllBytesAsync(sourcePath));
            var source = await AudioFiles.LoadAsync(sourcePath);
            var prepared = await EffectPreview.PrepareAsync([source], [source], 0, 1, new(EffectKind.Delay) { DelaySeconds = .001, Feedback = 0, Wet = .5f, CutTail = false });
            foreach (var change in prepared.Changes) change.Track.WriteVisibleSamples(change.VisibleStart, change.Samples);
            long length = source.Frames; TimelineDuplication.Duplicate([source], 0, length);
            await SessionFiles.SaveAsync(sessionPath, [source], new SelectionStore()); var loaded = await SessionFiles.LoadAsync(sessionPath);
            Check(loaded.Tracks[0].Frames == length * 2 && loaded.Tracks[0].ReadVisibleSamples(0, length).SequenceEqual(loaded.Tracks[0].ReadVisibleSamples(length, length * 2)),
                "sessions roundtrip duplicated original audio and virtual tails as identical adjacent copies");
            Check(SHA256.HashData(await File.ReadAllBytesAsync(sourcePath)).SequenceEqual(hash) && Directory.GetFiles(root).Length == 2,
                "duplication and session saving preserve the original file without generated audio files");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine($"{checks} duplication checks passed.");
    }
}
