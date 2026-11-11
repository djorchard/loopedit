using System.Reflection;
using OrchardSound;

public static class SelectionZoomChecks
{
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                int checks = 0;
                void Check(bool condition, string name)
                { if (!condition) throw new Exception("FAILED SELECTION ZOOM: " + name); checks++; Console.WriteLine("PASS SELECTION ZOOM " + name); }
                using var canvas = new WaveCanvas { Width = 1440 };
                float[] samples = new float[AudioTrack.Rate * 2];
                var track = new AudioTrack { Path = "Selection fit.wav", Samples = samples, Peaks = new(samples), Gain = .5f };
                canvas.Slots[0].Track = track;
                const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
                var center = typeof(WaveCanvas).GetField("lensCenter", hidden)!; center.SetValue(canvas, 12000L);
                var before = canvas.PrecisionViewRange; double zoom = canvas.Magnification;
                Check(!canvas.ZoomToSelection() && canvas.PrecisionViewRange == before && canvas.Magnification == zoom,
                    "fitting without a selection leaves the existing view unchanged");
                canvas.CursorFrame = 15000; canvas.Playhead = 17500; canvas.SelectionStart = 10000; canvas.SelectionEnd = 20000;
                int selections = 0, cursors = 0; canvas.SelectionChanged += () => selections++; canvas.CursorMoved += _ => cursors++;
                Check(canvas.ZoomToSelection() && canvas.Magnification == 4 && canvas.PrecisionViewRange == (9000L, 21000L),
                    "a middle loop fits with ten percent of its duration visible on both sides");
                Check(canvas.SelectionStart == 10000 && canvas.SelectionEnd == 20000 && canvas.CursorFrame == 15000 && canvas.Playhead == 17500
                    && selections == 0 && cursors == 0 && ReferenceEquals(track.Samples, samples) && track.Gain == .5f && track.Offset == 0 && track.Patches.Length == 0,
                    "fitting changes only the view and preserves audio, selection, playhead and cursor");
                canvas.SelectionStart = 0; canvas.SelectionEnd = 10000;
                Check(canvas.ZoomToSelection() && canvas.PrecisionViewRange.Min == 0 && canvas.PrecisionViewRange.Max >= 11000,
                    "context near the file start clamps to the available timeline without cutting off the loop");
                canvas.SelectionStart = 38000; canvas.SelectionEnd = 48000;
                Check(canvas.ZoomToSelection() && canvas.PrecisionViewRange.Min <= 37000 && canvas.PrecisionViewRange.Max == 48000,
                    "context near the file end stays inside the available timeline");
                canvas.SelectionStart = 0; canvas.SelectionEnd = 48000;
                Check(canvas.ZoomToSelection() && canvas.Magnification == 1 && canvas.PrecisionViewRange == canvas.Range,
                    "a full-file loop can use the new one-times zoom minimum");
                canvas.SelectionStart = null; canvas.SelectionEnd = 24000;
                Check(canvas.ZoomToSelection() && canvas.SelectionStart == null && canvas.PrecisionViewRange.Min == 0 && canvas.PrecisionViewRange.Max >= 26400,
                    "an end-only loop fits its implicit timeline start without adding a start marker");
                track.Offset = -6000; canvas.SelectionStart = -6000; canvas.SelectionEnd = -5000;
                Check(canvas.ZoomToSelection() && canvas.PrecisionViewRange.Min == -6000 && canvas.PrecisionViewRange.Max >= -4900,
                    "negative track offsets use the same global-time fit and context rules");
                canvas.SelectionStart = 10000; canvas.SelectionEnd = 10001;
                Check(canvas.ZoomToSelection() && canvas.Magnification == 2048 && canvas.PrecisionViewRange.Min <= 9999 && canvas.PrecisionViewRange.Max >= 10002,
                    "a one-frame selection respects the maximum zoom while retaining its context");
                canvas.SelectionStart = 10000; canvas.SelectionEnd = 20000;
                Check(canvas.ZoomToSelection(0) && canvas.PrecisionViewRange.Min <= 10000 && canvas.PrecisionViewRange.Max >= 20000,
                    "zero context still contains both exact selection boundaries after zoom quantization");
                canvas.SelectionStart = 20000; canvas.SelectionEnd = 10000; before = canvas.PrecisionViewRange;
                Check(!canvas.ZoomToSelection() && canvas.PrecisionViewRange == before,
                    "an inverted selection is a safe no-op");
                var previewSamples = new float[AudioTrack.Rate * 4];
                canvas.PreviewSources[track] = new AudioTrack { Path = track.Path, Samples = previewSamples, Peaks = new(previewSamples), Offset = -6000 };
                canvas.SelectionStart = 60000; canvas.SelectionEnd = 80000;
                Check(canvas.ZoomToSelection() && canvas.PrecisionViewRange.Min <= 58000 && canvas.PrecisionViewRange.Max >= 82000
                    && ReferenceEquals(track.Samples, samples) && track.Frames == AudioTrack.Rate,
                    "preview-extended timelines fit their displayed loop without committing preview audio");
                Check(canvas.GetZoomContextBounds(1).Left >= canvas.GetWaveBounds(1).Left
                    && canvas.GetZoomContextBounds(1).Right <= canvas.GetWaveBounds(1).Right,
                    "fit selection keeps the blue context inside the lower waveform plot");
                Console.WriteLine($"{checks} selection zoom checks passed. No visible window, native input or audio was used.");
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("Selection zoom fixture deadline exceeded.");
        if (failure != null) throw new Exception("Selection zoom fixture failed", failure);
    }
}
