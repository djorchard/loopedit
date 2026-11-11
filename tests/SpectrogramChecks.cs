using System.Diagnostics;
using System.Reflection;
using OrchardSound;

public static class SpectrogramChecks
{
    static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static float[] Tone(int frames, double leftHz, double rightHz, bool invertRight = false, float amplitude = .5f)
    {
        var samples = new float[frames * 2];
        for (int frame = 0; frame < frames; frame++)
        {
            samples[frame * 2] = amplitude * (float)Math.Sin(2 * Math.PI * leftHz * frame / AudioTrack.Rate);
            samples[frame * 2 + 1] = (invertRight ? -1 : 1) * amplitude * (float)Math.Sin(2 * Math.PI * rightHz * frame / AudioTrack.Rate);
        }
        return samples;
    }
    static AudioTrack Track(string name, float[] samples) => new() { Path = name, Samples = samples, Peaks = new PeakPyramid(samples) };
    static IEnumerable<byte> Levels(SpectrogramData data)
    { for (int column = 0; column < data.Columns; column++) for (int band = 0; band < SpectrogramData.Bands; band++) yield return data[column, band]; }
    static double BandNote(int band) => SpectrogramData.LowestNote + (SpectrogramData.HighestNote - SpectrogramData.LowestNote) * (band + .5) / SpectrogramData.Bands;
    static int NoteLevel(SpectrogramData data, double note, int from, int to)
        => Enumerable.Range(from, to - from).SelectMany(column => Enumerable.Range(0, SpectrogramData.Bands).Where(band => Math.Abs(BandNote(band) - note) <= 1).Select(band => (int)data[column, band])).DefaultIfEmpty(0).Max();
    static double DominantNote(SpectrogramData data)
    {
        int from = Math.Min(4, data.Columns - 1), to = Math.Max(from + 1, data.Columns - 4);
        return BandNote(Enumerable.Range(0, SpectrogramData.Bands).OrderByDescending(band => Enumerable.Range(from, to - from).Sum(column => (int)data[column, band])).First());
    }

    public static void Run()
    {
        int checks = 0;
        void Check(bool condition, string name)
        { if (!condition) throw new Exception("FAILED SPECTROGRAM: " + name); checks++; Console.WriteLine("PASS SPECTROGRAM " + name); }
        var original = Tone(AudioTrack.Rate, 440, 440); var originalCopy = original.ToArray();
        var a4 = Track("A4.wav", original); var a4Data = SpectrogramData.Compute(a4.Snapshot());
        Check(Math.Abs(DominantNote(a4Data) - 69) <= 1.1, "a 440 Hz A4 tone appears within one semitone of its calibrated note");
        var c5 = Track("C5.wav", Tone(AudioTrack.Rate, SpectrogramData.NoteFrequency(72), SpectrogramData.NoteFrequency(72)));
        Check(Math.Abs(DominantNote(SpectrogramData.Compute(c5.Snapshot())) - 72) <= 1.1, "a C5 tone appears within the FFT's semitone resolution");
        Check(a4Data.Columns == (AudioTrack.Rate + SpectrogramData.HopFrames - 1) / SpectrogramData.HopFrames && a4Data.ByteSize == a4Data.Columns * SpectrogramData.Bands && a4Data.ByteSize <= 64L * 1024 * 1024, "spectral time columns and byte storage follow the bounded hop and band dimensions");
        var anti = SpectrogramData.Compute(Track("Antiphase.wav", Tone(AudioTrack.Rate, 440, 440, true)).Snapshot());
        Check(Levels(anti).SequenceEqual(Levels(a4Data)) && NoteLevel(anti, 69, 4, anti.Columns - 4) > 180, "opposite stereo phases retain their spectral power instead of cancelling");
        var dual = SpectrogramData.Compute(Track("Two notes.wav", Tone(AudioTrack.Rate, 440, SpectrogramData.NoteFrequency(72))).Snapshot());
        Check(NoteLevel(dual, 69, 4, dual.Columns - 4) > 180 && NoteLevel(dual, 72, 4, dual.Columns - 4) > 180, "independent stereo notes both remain visible");
        var silence = SpectrogramData.Compute(Track("Silence.wav", new float[4800]).Snapshot());
        var empty = SpectrogramData.Compute(Track("Empty.wav", []).Snapshot());
        Check(Levels(silence).All(level => level == 0) && empty.Columns == 1 && Levels(empty).All(level => level == 0), "silent and empty audio produce safe dark spectral data");
        a4.Gain = .01f;
        Check(Levels(SpectrogramData.Compute(a4.Snapshot())).SequenceEqual(Levels(a4Data)), "source spectral data ignores the display gain");
        Check(original.SequenceEqual(originalCopy) && a4.Patches.Length == 0 && a4.Fades.Count == 0, "analysis preserves the original samples and edit state");
        var faded = Track("Faded.wav", Tone(AudioTrack.Rate, 440, 440)); faded.Fades.Add(new(0, faded.Frames, true));
        var fadedData = SpectrogramData.Compute(faded.Snapshot());
        Check(NoteLevel(fadedData, 69, fadedData.Columns - 5, fadedData.Columns - 2) > NoteLevel(fadedData, 69, 0, 3) + 20, "fade envelopes alter spectral energy across the visible timeline");
        var patched = Track("Patched silence.wav", new float[AudioTrack.Rate * 2]);
        patched.WriteVisibleSamples(12000, Tone(12000, SpectrogramData.NoteFrequency(72), SpectrogramData.NoteFrequency(72)));
        var patchedData = SpectrogramData.Compute(patched.Snapshot());
        Check(NoteLevel(patchedData, 72, 17, 20) > 180 && NoteLevel(patchedData, 72, 0, 3) == 0 && NoteLevel(patchedData, 72, 39, 42) == 0 && patched.Samples.All(sample => sample == 0), "processed patches appear only in their own visible time range without changing the raw source");
        var cutSamples = Tone(24000, 440, 440).Concat(Tone(24000, SpectrogramData.NoteFrequency(72), SpectrogramData.NoteFrequency(72))).ToArray();
        var cut = Track("Cut notes.wav", cutSamples); TimelineEditing.Delete([cut], 0, 24000);
        var cutData = SpectrogramData.Compute(cut.Snapshot());
        Check(cutData.Columns == (24000 + SpectrogramData.HopFrames - 1) / SpectrogramData.HopFrames && Math.Abs(DominantNote(cutData) - 72) <= 1.1, "timeline cuts analyze the retained visible segments and their new duration");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); bool rejected = false;
            try { SpectrogramData.Compute(a4.Snapshot(), cancelled.Token); } catch (OperationCanceledException) { rejected = true; }
            Check(rejected, "cancellation stops analysis before allocating spectral work");
        }
        long oversizedFrames = (64L * 1024 * 1024 / SpectrogramData.Bands + 1) * SpectrogramData.HopFrames;
        bool oversizedRejected = false;
        try { SpectrogramData.Compute(a4.Snapshot() with { Segments = [new(0, oversizedFrames)] }); } catch (IOException) { oversizedRejected = true; }
        Check(oversizedRejected, "oversized spectral matrices are rejected before allocation");
        Check(SpectrogramData.NoteName(12) == "C0" && SpectrogramData.NoteName(69) == "A4" && SpectrogramData.NoteName(72) == "C5" && SpectrogramData.NoteName(132) == "C10" && Math.Abs(SpectrogramData.NoteFrequency(69) - 440) < .000001 && Math.Abs(SpectrogramData.NoteFrequency(81) - 880) < .000001, "MIDI labels and octave frequencies use the standard A4 calibration");

        CacheChecks(Check);
        ViewChecks(Check);
        Console.WriteLine($"{checks} spectrogram checks passed. No native window or audio hardware was used.");
    }

    static void CacheChecks(Action<bool, string> check)
    {
        var cacheType = typeof(WaveCanvas).Assembly.GetType("OrchardSound.SpectrogramCache", true)!;
        object Create(Action changed, long budget) => Activator.CreateInstance(cacheType, [changed, budget])!;
        SpectrogramData? Get(object cache, AudioTrack track, out string? error)
        { object?[] args = [track, null]; var result = (SpectrogramData?)cacheType.GetMethod("Get")!.Invoke(cache, args); error = (string?)args[1]; return result; }
        SpectrogramData Await(object cache, AudioTrack track)
        {
            SpectrogramData? data = null; string? error = null;
            if (!SpinWait.SpinUntil(() => { data = Get(cache, track, out error); return data != null || error != null; }, TimeSpan.FromSeconds(5))) throw new TimeoutException("Spectral cache did not finish.");
            return data ?? throw new Exception(error);
        }
        long Bytes(object cache) => (long)cacheType.GetProperty("CachedBytes")!.GetValue(cache)!;
        using (var cache = (IDisposable)Create(() => { }, 2 * SpectrogramData.Bands))
        {
            var first = Track("Cache A.wav", new float[2]); var second = Track("Cache B.wav", new float[2]); var third = Track("Cache C.wav", new float[2]);
            Await(cache, first); Await(cache, second); _ = Get(cache, first, out _); Await(cache, third);
            check(Bytes(cache) <= 2 * SpectrogramData.Bands && Get(cache, first, out _) != null && Get(cache, second, out _) == null, "the shared spectral cache evicts the least recently used data within its byte budget");
        }
        using (var cache = (IDisposable)Create(() => throw new InvalidOperationException("Synthetic closed view"), SpectrogramData.Bands))
        {
            var track = Track("Callback.wav", new float[2]); _ = Await(cache, track);
            bool contained = true;
            try { cacheType.GetMethod("Notify", Private)!.Invoke(cache, null); } catch (TargetInvocationException) { contained = false; }
            check(contained && Get(cache, track, out var error) != null && error == null, "a closed-view callback cannot corrupt completed spectral data");
        }
        using (var cache = (IDisposable)Create(() => { }, SpectrogramData.Bands))
        {
            var track = Track("Beyond tiny budget.wav", new float[(SpectrogramData.HopFrames + 1) * 2]); string? error = null;
            bool finished = SpinWait.SpinUntil(() => { _ = Get(cache, track, out error); return error != null; }, TimeSpan.FromSeconds(5));
            check(finished && Bytes(cache) == 0 && error!.Contains("memory limit"), "an individual result larger than the cache budget is reported without retaining its data");
        }
    }

    static void ViewChecks(Action<bool, string> check)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var canvas = new WaveCanvas { Size = new Size(1800, 900) }; canvas.SetViewportHeight((int)(900 * canvas.DpiScale));
                check(canvas.GetPaneViewMode(0) == AudioViewMode.Waveform && canvas.GetPaneViewMode(1) == AudioViewMode.Waveform, "precision and channels start in waveform mode");
                canvas.Slots[0].ViewMode = AudioViewMode.Spectrogram;
                bool rowIndependent = canvas.GetPaneViewMode(1) == AudioViewMode.Spectrogram && canvas.GetPaneViewMode(0) == AudioViewMode.Waveform;
                canvas.PrecisionViewMode = AudioViewMode.Spectrogram; canvas.Slots[0].ViewMode = AudioViewMode.Waveform;
                check(rowIndependent && canvas.GetPaneViewMode(0) == AudioViewMode.Spectrogram && canvas.GetPaneViewMode(1) == AudioViewMode.Waveform, "precision and channel display modes remain independent");
                using var notes = new WaveCanvas { Width = (int)(1200 * canvas.DpiScale) }; notes.SetViewportHeight((int)(5000 * notes.DpiScale));
                var ticks = notes.GetNoteTicks(1); var bounds = notes.GetWaveBounds(1); var a4 = ticks.Single(tick => tick.MidiNote == 69); var c5 = ticks.Single(tick => tick.MidiNote == 72);
                check(ticks.Single(tick => tick.MidiNote == 132).Y == bounds.Top && ticks.Single(tick => tick.MidiNote == 12).Y == bounds.Bottom - 1 && a4.Label == "A4" && c5.Label == "C5" && Math.Abs(a4.Frequency - 440) < .000001 && a4.Y > c5.Y && Math.Abs(a4.Y - (bounds.Top + (132 - 69) / 120d * (bounds.Height - 1))) < .001, "the note grid maps high notes to the top and calibrates A4 and C5 vertically");

                canvas.PrecisionViewMode = AudioViewMode.Waveform; canvas.Slots[0].ViewMode = AudioViewMode.Spectrogram;
                var track = Track("Paint A4.wav", Tone(AudioTrack.Rate, 440, 440, amplitude: .8f));
                Bitmap Paint()
                {
                    var bitmap = new Bitmap(canvas.Width, canvas.Height); using var graphics = Graphics.FromImage(bitmap); graphics.Clear(canvas.BackColor);
                    typeof(WaveCanvas).GetMethod("OnPaint", Private)!.Invoke(canvas, [new PaintEventArgs(graphics, new Rectangle(Point.Empty, bitmap.Size))]); return bitmap;
                }
                bool Warm(Color color) => color.GetSaturation() > .25f && color.GetHue() < 120 && color.GetBrightness() > .3f;
                int WarmPixels(Bitmap bitmap, Rectangle region)
                {
                    region.Intersect(new Rectangle(Point.Empty, bitmap.Size)); int count = 0;
                    for (int y = region.Top; y < region.Bottom; y++) for (int x = region.Left; x < region.Right; x += 3) if (Warm(bitmap.GetPixel(x, y))) count++;
                    return count;
                }
                var cacheType = typeof(WaveCanvas).Assembly.GetType("OrchardSound.SpectrogramCache", true)!;
                var workers = (SemaphoreSlim)cacheType.GetField("workers", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                bool firstPermit = false, secondPermit = false; Bitmap? pending = null; int invalidations = 0, beforeReady = 0; canvas.Invalidated += (_, _) => invalidations++;
                try
                {
                    firstPermit = workers.Wait(TimeSpan.FromSeconds(2)); secondPermit = firstPermit && workers.Wait(TimeSpan.FromSeconds(2));
                    if (!secondPermit) throw new TimeoutException("Spectral workers did not become available for the pending-view fixture.");
                    canvas.Slots[0].Track = track; _ = canvas.Handle;
                    pending = Paint();
                    var pendingCache = typeof(WaveCanvas).GetField("spectralCache", Private)!.GetValue(canvas)!;
                    long pendingRevision = (long)cacheType.GetProperty("Revision")!.GetValue(pendingCache)!;
                    var pendingWave = canvas.GetWaveBounds(1);
                    var pendingRegion = canvas.ToDevice(new Rectangle(pendingWave.Left + pendingWave.Width / 10, pendingWave.Top, pendingWave.Width / 5, pendingWave.Height));
                    int pendingEnergy = WarmPixels(pending, pendingRegion);
                    if (pendingRevision != 0 || pendingEnergy != 0) Console.WriteLine($"Pending spectrum: revision {pendingRevision}, energy-colored pixels {pendingEnergy}.");
                    check(pendingRevision == 0 && pendingEnergy == 0, "pending spectral analysis paints a safe placeholder before worker completion");
                    Application.DoEvents(); beforeReady = invalidations;
                }
                finally { if (secondPermit) workers.Release(); if (firstPermit) workers.Release(); pending?.Dispose(); }
                var cache = typeof(WaveCanvas).GetField("spectralCache", Private)!.GetValue(canvas)!;
                long Revision() => (long)cacheType.GetProperty("Revision")!.GetValue(cache)!;
                var deadline = Stopwatch.StartNew();
                while ((Revision() == 0 || invalidations == beforeReady) && deadline.Elapsed < TimeSpan.FromSeconds(5)) { Application.DoEvents(); Thread.Sleep(5); }
                check(Revision() > 0 && invalidations > beforeReady, "completed background analysis requests a managed repaint without showing a window");
                using var ready = Paint(); var wave = canvas.GetWaveBounds(1);
                int noteY = (int)Math.Round(wave.Top + (132 - 69) / 120d * (wave.Height - 1));
                var noteBand = canvas.ToDevice(new Rectangle(wave.Left + wave.Width / 4, noteY - 8, wave.Width / 2, 17));
                check(WarmPixels(ready, noteBand) > 20, "the ready spectrogram paints visible A4 energy beside its calibrated note grid");
                long readyRevision = Revision(); track.Gain = 0;
                using var muted = Paint();
                check(WarmPixels(muted, canvas.ToDevice(wave)) == 0 && Revision() == readyRevision, "display gain changes spectral colors without recomputing the source data");
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(20))) throw new TimeoutException("Spectrogram view fixture timed out.");
        if (failure != null) throw new Exception("Spectrogram view fixture failed.", failure);
    }
}
