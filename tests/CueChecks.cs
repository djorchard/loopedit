using System.Reflection;
using NAudio.Wave;
using OrchardSound;

public static class CueChecks
{
    public static async Task Run()
    {
        int checks = 0;
        void Check(bool result, string name) { if (!result) throw new Exception("FAILED CUE: " + name); checks++; Console.WriteLine("PASS CUE " + name); }
        static void Wav(string path, int rate, int frames)
        { using var writer = new WaveFileWriter(path, new WaveFormat(rate, 16, 2)); writer.Write(new byte[frames * 4], 0, frames * 4); }
        string temporary = Path.GetFullPath(Path.GetTempPath());
        string folder = Path.GetFullPath(Path.Combine(temporary, "Loopsmith-cues-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(folder);
        try
        {
            string source = Path.Combine(folder, "Cue source.wav"); Wav(source, AudioTrack.Rate, 9600);
            WavCueFile.Append(source, [new(9600, "End"), new(0, "Even"), new(1200, "Odd"), new(2400, "Café"), new(1200, "Duplicate"), new(-1, "Invalid")]);
            var cues = WavCueFile.Read(source);
            Check(cues.SequenceEqual(new CuePoint[] { new(0, "Even"), new(1200, "Odd"), new(2400, "Café"), new(9600, "End") }),
                "RIFF cue and adtl labels round-trip in frame order with even, odd and UTF-8 label sizes");
            using (var input = File.OpenRead(source))
            using (var reader = new BinaryReader(input))
            { input.Position = 4; Check(reader.ReadUInt32() == input.Length - 8, "cue append updates the RIFF length to include metadata chunks"); }
            using (var reader = new WaveFileReader(source))
                Check(reader.Length == 9600 * 4 && reader.WaveFormat.SampleRate == AudioTrack.Rate, "metadata append preserves the PCM payload length and format");
            string bounded = Path.Combine(folder, "Bounded cues.wav"); Wav(bounded, AudioTrack.Rate, 9600);
            WavCueFile.Append(bounded, Enumerable.Range(0, 5000).Select(frame => new CuePoint(frame, "Cue " + frame)));
            Check(WavCueFile.Read(bounded).Length == 4096, "WAV metadata retains a bounded maximum of 4096 cue entries");
            var loaded = await AudioFiles.LoadAsync(source);
            Check(loaded.ImportedCues.SequenceEqual(cues) && loaded.Frames == 9600, "audio loading imports WAV cues without adding audio samples");

            string resampled = Path.Combine(folder, "44100 source.wav"); Wav(resampled, 44100, 8820);
            WavCueFile.Append(resampled, [new(4410, "100 ms"), new(8820, "File end")]);
            Check(WavCueFile.Read(resampled).SequenceEqual(new CuePoint[] { new(4800, "100 ms"), new(9600, "File end") }),
                "source-rate cue offsets are converted from 44100 Hz to the 48000 Hz editor timeline");
            var resampledTrack = await AudioFiles.LoadAsync(resampled);
            Check(resampledTrack.ImportedCues.SequenceEqual(new CuePoint[] { new(4800, "100 ms"), new(9600, "File end") }) && resampledTrack.Frames == 9600,
                "decoded resampling and imported cue offsets agree at the file end");

            var export = await AudioFiles.ExportAsync([loaded.Snapshot()], 1000, 4000, folder, "Selected cues", ".wav", cues:
                [new(900, "Before"), new(1000, "Start"), new(1500, "Middle café"), new(4000, "End"), new(4100, "After")]);
            var selectedCues = WavCueFile.Read(export.Path);
            Check(selectedCues.SequenceEqual(new CuePoint[] { new(0, "Start"), new(500, "Middle café"), new(3000, "End") }),
                "WAV export excludes outside cues and rebases selected start, middle and inclusive end cues");
            var exported = await AudioFiles.LoadAsync(export.Path);
            Check(exported.Frames == 3000 && exported.ImportedCues.SequenceEqual(selectedCues), "reloading an exported clip retains its cue at the exact audio end");
            Check(!Directory.GetFiles(folder, ".orchard-*").Any(), "cue export removes its temporary audio file");

            var store = new SelectionStore(); store.SavePoint("Opening", 500); store.SavePoint("Ending", 3000);
            string session = Path.Combine(folder, "Cue points.orchardsound"); await SessionFiles.SaveAsync(session, [loaded], store);
            var restored = await SessionFiles.LoadAsync(session);
            Check(restored.Selections.SequenceEqual(store.Capture()) && restored.Selections.All(s => s.Start == s.End),
                "sessions preserve named zero-duration cue points without converting them to selections");
            bool duplicateRejected = false; try { store.SavePoint("opening", 1000); } catch (ArgumentException) { duplicateRejected = true; }
            Check(duplicateRejected && store.Items.Count == 2, "cue names remain unique without changing existing points after a rejected duplicate");
            var maximumStore = new SelectionStore();
            for (int i = 0; i < SelectionStore.MaxSelections; i++) maximumStore.SavePoint("Cue " + i, i);
            bool maximumRejected = false; try { maximumStore.SavePoint("Overflow", 2000); } catch (InvalidOperationException) { maximumRejected = true; }
            Check(maximumRejected && maximumStore.Items.Count == SelectionStore.MaxSelections, "session cue limits reject overflow without losing existing points");
            var edits = new SelectionStore(); edits.SavePoint("Before", 100); edits.SavePoint("At cut", 500); edits.SavePoint("Removed", 700); edits.SavePoint("After", 1100);
            edits.ApplyDelete(500, 1000);
            Check(edits.Items.Select(p => p.Start).SequenceEqual(new long[] { 100, 500, 500, 600 }) && edits.Items.All(p => p.Start == p.End),
                "cuts preserve earlier points, collapse removed points and shift later points with the surviving audio");
            edits.ApplyTrim(200, 800); edits.ApplyInsert(300, 100);
            Check(edits.Items.Select(p => p.Start).SequenceEqual(new long[] { 0, 300, 300, 500 }),
                "trim and insertion keep cue points rebased with their timeline positions");

            Exception? uiFailure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    using var canvas = new WaveCanvas { Width = 1800, SmartSnap = true }; canvas.SetViewportHeight(900);
                    canvas.Slots[0].Track = restored.Tracks.Single();
                    canvas.CuePoints.AddRange(restored.Selections.Select(s => new CuePoint(s.Start, s.Name)));
                    Check(canvas.SnapToBeat(510) == 500 && canvas.SnapToBeat(1800) == 1800, "smart snap chooses nearby saved cue points and preserves distant positions");
                    Check(canvas.SnapToBeat(510, bypass: true) == 510, "explicit manual bypass preserves a position near a saved cue");
                    canvas.SmartSnap = false; Check(canvas.SnapToBeat(510) == 510, "turning smart snap off also disables cue-point snapping");
                    var paint = typeof(WaveCanvas).GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var builds = typeof(WaveCanvas).GetField("renderCacheBuilds", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    using var image = new Bitmap(canvas.Width, canvas.Height); using var graphics = Graphics.FromImage(image);
                    void Paint()
                    {
                        graphics.Clear(AppTheme.Window); var state = graphics.Save();
                        try { paint.Invoke(canvas, [new PaintEventArgs(graphics, new Rectangle(Point.Empty, image.Size))]); }
                        finally { graphics.Restore(state); }
                    }
                    Paint();
                    long before = (long)builds.GetValue(canvas)!; canvas.CuePoints.Add(new(2000, "New point"));
                    Paint();
                    Check((long)builds.GetValue(canvas)! == before, "cue overlays update without rebuilding cached waveform bases");

                    const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
                    using (var editor = new EditorForm([]))
                    {
                        var editorCanvas = (WaveCanvas)typeof(EditorForm).GetField("canvas", hidden)!.GetValue(editor)!;
                        resampledTrack.Offset = -500; editorCanvas.Slots[0].Track = resampledTrack;
                        typeof(EditorForm).GetMethod("ImportTrackCues", hidden)!.Invoke(editor, [resampledTrack]);
                        Check(editorCanvas.CuePoints.Select(c => c.Frame).SequenceEqual(new long[] { 4300, 9100 }),
                            "imported source-rate cues also follow their track's global timeline offset");
                        editorCanvas.SelectionStart = 1000; editorCanvas.SelectionEnd = 3000;
                        typeof(EditorForm).GetMethod("SaveSelection", hidden)!.Invoke(editor, null);
                        Check(editorCanvas.CuePoints.Select(c => c.Frame).SequenceEqual(new long[] { 1000, 3000, 4300, 9100 }),
                            "Save section cue points marks both selection boundaries without replacing imported cues");
                    }
                    using (var editor = new EditorForm([]))
                    {
                        var editorCanvas = (WaveCanvas)typeof(EditorForm).GetField("canvas", hidden)!.GetValue(editor)!;
                        var duplicateTrack = new AudioTrack { Path = loaded.Path, Samples = loaded.Samples, Peaks = loaded.Peaks };
                        editorCanvas.Slots[0].Track = duplicateTrack; editorCanvas.SelectionStart = 1000; editorCanvas.SelectionEnd = 3000;
                        typeof(EditorForm).GetMethod("DuplicateSelection", hidden)!.Invoke(editor, null);
                        Check(duplicateTrack.Frames == 11600 && editorCanvas.SelectionStart == 3000 && editorCanvas.SelectionEnd == 5000
                            && editorCanvas.CuePoints.Select(c => c.Frame).SequenceEqual(new long[] { 1000, 3000, 5000 })
                            && typeof(EditorForm).GetField("output", hidden)!.GetValue(editor) == null,
                            "immediate duplication marks the original start, join and copied end without starting playback");
                        typeof(EditorForm).GetMethod("Undo", hidden)!.Invoke(editor, null);
                        bool undone = duplicateTrack.Frames == 9600 && editorCanvas.CuePoints.Count == 0 && editorCanvas.SelectionStart == 1000 && editorCanvas.SelectionEnd == 3000;
                        typeof(EditorForm).GetMethod("Redo", hidden)!.Invoke(editor, null);
                        Check(undone && duplicateTrack.Frames == 11600 && editorCanvas.CuePoints.Select(c => c.Frame).SequenceEqual(new long[] { 1000, 3000, 5000 }),
                            "one Undo and Redo restore duplication audio, selection and automatic cue points together");
                    }
                    using (var editor = new EditorForm([]))
                    {
                        var editorCanvas = (WaveCanvas)typeof(EditorForm).GetField("canvas", hidden)!.GetValue(editor)!;
                        var points = (SelectionStore)typeof(EditorForm).GetField("selections", hidden)!.GetValue(editor)!;
                        editorCanvas.Slots[0].Track = loaded;
                        for (int i = 0; i < SelectionStore.MaxSelections - 1; i++) points.SavePoint("Existing " + i, i);
                        typeof(EditorForm).GetMethod("SyncCuePoints", hidden)!.Invoke(editor, null);
                        editorCanvas.SelectionStart = 1000; editorCanvas.SelectionEnd = 3000;
                        typeof(EditorForm).GetMethod("SaveSelection", hidden)!.Invoke(editor, null);
                        var history = (System.Collections.ICollection)typeof(EditorForm).GetField("undo", hidden)!.GetValue(editor)!;
                        Check(points.Items.Count == SelectionStore.MaxSelections - 1 && editorCanvas.CuePoints.Count == points.Items.Count && history.Count == 0,
                            "saving two section cues at the session limit is atomic and creates no partial Undo entry");
                    }
                }
                catch (Exception error) { uiFailure = error; }
            });
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(20))) throw new TimeoutException("Cue control fixture deadline exceeded.");
            if (uiFailure != null) throw new Exception("Cue control fixture failed", uiFailure);
            Console.WriteLine($"{checks} cue checks passed. No native window or audio hardware was used.");
        }
        finally
        {
            string cleanup = Path.GetFullPath(folder);
            if (!cleanup.StartsWith(temporary.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Cue fixture cleanup path is outside the temporary folder.");
            Directory.Delete(cleanup, true);
        }
    }
}
