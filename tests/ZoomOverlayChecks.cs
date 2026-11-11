using System.Collections;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using OrchardSound;

public static class ZoomOverlayChecks
{
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly MethodInfo Paint = typeof(WaveCanvas).GetMethod("OnPaint", Hidden)!;
    static readonly FieldInfo Hover = typeof(WaveCanvas).GetField("hoveredSlot", Hidden)!;
    static readonly FieldInfo Center = typeof(WaveCanvas).GetField("lensCenter", Hidden)!;
    static readonly FieldInfo Builds = typeof(WaveCanvas).GetField("renderCacheBuilds", Hidden)!;
    static readonly FieldInfo ContextBuilds = typeof(WaveCanvas).GetField("zoomContextBuilds", Hidden)!;
    static readonly FieldInfo ContextCache = typeof(WaveCanvas).GetField("zoomContextCache", Hidden)!;

    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                int checks = 0;
                void Check(bool condition, string name)
                { if (!condition) throw new Exception("FAILED ZOOM CONTEXT: " + name); checks++; Console.WriteLine("PASS ZOOM CONTEXT " + name); }
                static AudioTrack Track(string name, float quiet, float loud, long offset = 0)
                {
                    var samples = new float[AudioTrack.Rate * 2];
                    for (int frame = 0; frame < AudioTrack.Rate; frame++)
                    { float level = frame < AudioTrack.Rate / 2 ? quiet : loud; samples[frame * 2] = level; samples[frame * 2 + 1] = -level; }
                    return new() { Path = name + ".wav", Samples = samples, Peaks = new(samples), Offset = offset };
                }
                static Bitmap Render(WaveCanvas canvas)
                {
                    var image = new Bitmap(canvas.Width, canvas.Height, PixelFormat.Format32bppPArgb);
                    image.SetResolution(canvas.DeviceDpi, canvas.DeviceDpi);
                    using var graphics = Graphics.FromImage(image); graphics.Clear(AppTheme.Window);
                    Paint.Invoke(canvas, [new PaintEventArgs(graphics, new Rectangle(Point.Empty, image.Size))]);
                    return image;
                }
                static Rectangle Device(WaveCanvas canvas, RectangleF bounds) => Rectangle.FromLTRB(
                    (int)Math.Floor(bounds.Left * canvas.DpiScale), (int)Math.Floor(bounds.Top * canvas.DpiScale),
                    (int)Math.Ceiling(bounds.Right * canvas.DpiScale), (int)Math.Ceiling(bounds.Bottom * canvas.DpiScale));
                static int Count(Bitmap image, Rectangle bounds, Func<Color, bool> matches)
                {
                    bounds.Intersect(new Rectangle(Point.Empty, image.Size)); if (bounds.IsEmpty) return 0;
                    var data = image.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                    try
                    {
                        int count = 0; var row = new byte[bounds.Width * 4];
                        for (int y = 0; y < bounds.Height; y++)
                        {
                            Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                            for (int x = 0; x < row.Length; x += 4) if (matches(Color.FromArgb(row[x + 3], row[x + 2], row[x + 1], row[x]))) count++;
                        }
                        return count;
                    }
                    finally { image.UnlockBits(data); }
                }
                static bool EqualRegion(Bitmap first, Bitmap second, Rectangle bounds)
                {
                    bounds.Intersect(new Rectangle(Point.Empty, first.Size));
                    var a = first.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                    var b = second.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                    try
                    {
                        var rowA = new byte[bounds.Width * 4]; var rowB = new byte[rowA.Length];
                        for (int y = 0; y < bounds.Height; y++)
                        {
                            Marshal.Copy(IntPtr.Add(a.Scan0, y * a.Stride), rowA, 0, rowA.Length);
                            Marshal.Copy(IntPtr.Add(b.Scan0, y * b.Stride), rowB, 0, rowB.Length);
                            if (!rowA.AsSpan().SequenceEqual(rowB)) return false;
                        }
                        return true;
                    }
                    finally { first.UnlockBits(a); second.UnlockBits(b); }
                }
                static long BaseCount(WaveCanvas canvas) => (long)Builds.GetValue(canvas)!;
                static long BlueCount(WaveCanvas canvas) => (long)ContextBuilds.GetValue(canvas)!;
                static bool Near(Color actual, Color expected) => Math.Abs(actual.R - expected.R) <= 2 && Math.Abs(actual.G - expected.G) <= 2 && Math.Abs(actual.B - expected.B) <= 2;
                static Color Tinted(Color color)
                {
                    static int Blend(int source, int overlay) => (int)Math.Round((source * 236 + overlay * 19) / 255d);
                    return Color.FromArgb(Blend(color.R, 64), Blend(color.G, 137), Blend(color.B, 226));
                }
                bool CorrectBounds(WaveCanvas canvas, int pane)
                {
                    var actual = canvas.GetZoomContextBounds(pane); var wave = canvas.GetWaveBounds(pane);
                    var range = canvas.Range; var lens = canvas.PrecisionViewRange;
                    double left = wave.Left + (lens.Min - range.Min) / (double)(range.Max - range.Min) * wave.Width;
                    double right = wave.Left + (lens.Max - range.Min) / (double)(range.Max - range.Min) * wave.Width;
                    return !actual.IsEmpty && Math.Abs(actual.Left - left) < .001 && Math.Abs(actual.Right - right) < .001
                        && actual.Top == wave.Top && actual.Bottom == wave.Bottom;
                }

                using var canvas = new WaveCanvas { Width = 1440, AutoMagnify = false, Magnification = 8 };
                var first = Track("First", .2f, .8f); var second = Track("Second", .45f, .45f, 12000);
                canvas.Slots[0].Track = first; canvas.Slots.Add(new() { Track = second }); canvas.SetViewportHeight(1200);
                Hover.SetValue(canvas, 0); Center.SetValue(canvas, 12000L);
                var quietBlue = WaveCanvas.PrecisionAmplitudeColor(.2f); var loudBlue = WaveCanvas.PrecisionAmplitudeColor(.8f);
                Check(quietBlue.B > quietBlue.G && quietBlue.G > quietBlue.R && loudBlue.R > quietBlue.R && loudBlue.G > quietBlue.G && loudBlue.B > quietBlue.B,
                    "precision blue retains darker quiet peaks and brighter loud peaks");
                Check(WaveCanvas.PrecisionAmplitudeColor(float.NaN) == WaveCanvas.PrecisionAmplitudeColor(0)
                    && WaveCanvas.PrecisionAmplitudeColor(2) == WaveCanvas.PrecisionAmplitudeColor(1),
                    "blue amplitude shading keeps finite clamped color bounds");
                Check(CorrectBounds(canvas, 1) && canvas.GetZoomContextBounds(0).IsEmpty && canvas.GetZoomContextBounds(2).IsEmpty,
                    "only the active lower track exposes the exact magnified timeline window");
                using var initial = Render(canvas);
                var top = Device(canvas, canvas.GetWaveBounds(0)); var lower = canvas.GetWaveBounds(1);
                var context = canvas.GetZoomContextBounds(1); var contextPixels = Device(canvas, context);
                Check(Count(initial, top, color => color == quietBlue) > 100
                    && Count(initial, top, color => color == WaveCanvas.AmplitudeColor(0, .2f)) == 0,
                    "the actual precision paint uses blue waveform ink instead of the source track's green");
                Rectangle inside = contextPixels; inside.Inflate(-3, -3);
                Check(Count(initial, inside, color => Near(color, Tinted(quietBlue))) > 30,
                    "the highlighted lower span actually recolors waveform strokes blue");
                var outside = Device(canvas, RectangleF.FromLTRB(lower.Left, lower.Top, context.Left - 3, lower.Bottom));
                Check(Count(initial, outside, color => color == WaveCanvas.AmplitudeColor(0, .2f)) > 100,
                    "the lower waveform retains its original green outside the zoom window");
                Check(Count(initial, new(contextPixels.Left, contextPixels.Top, contextPixels.Width, 3), color => color.B > color.G + 20 && color.G > color.R + 20) > 10,
                    "the context has a visible thin blue border without covering waveform detail");
                long warmBase = BaseCount(canvas), warmBlue = BlueCount(canvas); object? blueEntry = ContextCache.GetValue(canvas);
                Center.SetValue(canvas, 16000L);
                using var moved = Render(canvas);
                Check(canvas.GetZoomContextBounds(1).Left > context.Left && CorrectBounds(canvas, 1),
                    "the blue window follows precision panning over the same source track");
                Check(BaseCount(canvas) == warmBase + 1 && BlueCount(canvas) == warmBlue && ReferenceEquals(blueEntry, ContextCache.GetValue(canvas)),
                    "moving the lens rebuilds only precision and reuses lower waveform and blue geometry");
                Check(EqualRegion(initial, moved, Device(canvas, canvas.GetPaneBounds(2))),
                    "lens movement leaves every pixel of the other lower track unchanged");
                float previousWidth = canvas.GetZoomContextBounds(1).Width; warmBase = BaseCount(canvas); canvas.Magnification = 16;
                using (var zoomed = Render(canvas))
                    Check(Math.Abs(canvas.GetZoomContextBounds(1).Width * 2 - previousWidth) < .001
                        && BaseCount(canvas) == warmBase + 1 && BlueCount(canvas) == warmBlue,
                        "changing zoom resizes the context while keeping lower cached geometry warm");
                for (int i = 0; i < 12; i++) { Center.SetValue(canvas, 8000L + i * 700); using var image = Render(canvas); }
                Check(BlueCount(canvas) == warmBlue && ReferenceEquals(blueEntry, ContextCache.GetValue(canvas))
                    && ((ICollection)typeof(WaveCanvas).GetField("renderCache", Hidden)!.GetValue(canvas)!).Count == canvas.Slots.Count + 1,
                    "repeated panning keeps one blue layer and one normal bitmap per pane");
                Hover.SetValue(canvas, 1); Center.SetValue(canvas, 30000L);
                using (var switched = Render(canvas))
                    Check(canvas.GetZoomContextBounds(1).IsEmpty && CorrectBounds(canvas, 2) && BlueCount(canvas) == warmBlue + 1
                        && Count(switched, Device(canvas, canvas.GetWaveBounds(1)), color => color == WaveCanvas.AmplitudeColor(0, .2f)) > 100,
                        "hovering another track moves the context and restores the former track's original ink");
                second.Offset = -9000; warmBlue = BlueCount(canvas);
                using (var offset = Render(canvas))
                    Check(canvas.Range.Min == -9000 && CorrectBounds(canvas, 2) && BlueCount(canvas) == warmBlue + 1,
                        "negative track offsets keep the window aligned to the global timeline");
                var preview = Track("Preview", .6f, .6f, 18000); canvas.PreviewSources[second] = preview; warmBlue = BlueCount(canvas);
                using (var previewed = Render(canvas))
                    Check(CorrectBounds(canvas, 2) && BlueCount(canvas) == warmBlue + 1
                        && Count(previewed, Device(canvas, canvas.GetWaveBounds(0)), color => color == WaveCanvas.PrecisionAmplitudeColor(.6f)) > 100,
                        "effect preview identities and offsets update both the blue source and its context");
                preview.Gain = .5f; warmBlue = BlueCount(canvas);
                using (var gained = Render(canvas))
                    Check(BlueCount(canvas) == warmBlue + 1 && Count(gained, Device(canvas, canvas.GetWaveBounds(0)), color => color == WaveCanvas.PrecisionAmplitudeColor(.3f)) > 100,
                        "gain edits invalidate the blue layer and retain actual audio-level shading");
                warmBlue = BlueCount(canvas); preview.Fades.Add(new(0, preview.Frames, true)); using (var faded = Render(canvas)) { }
                bool fadeUpdated = BlueCount(canvas) == warmBlue + 1;
                warmBlue = BlueCount(canvas); preview.WriteVisibleSamples(12000, Enumerable.Repeat(.8f, 1000).ToArray()); using (var patched = Render(canvas)) { }
                bool patchUpdated = BlueCount(canvas) == warmBlue + 1;
                warmBlue = BlueCount(canvas); preview.Segments = [new(1000, 20000), new(30000, 15000)]; using (var cut = Render(canvas)) { }
                Check(fadeUpdated && patchUpdated && BlueCount(canvas) == warmBlue + 1,
                    "fades, processed patches and timeline cuts invalidate blue geometry semantically");
                Hover.SetValue(canvas, 0); Center.SetValue(canvas, 12000L);
                var lens = canvas.PrecisionViewRange;
                canvas.SelectionStart = lens.Min + (lens.Max - lens.Min) / 4; canvas.SelectionEnd = lens.Max - (lens.Max - lens.Min) / 4;
                canvas.CursorFrame = (lens.Min + lens.Max) / 2;
                using (var selected = Render(canvas))
                {
                    bool GuideAt(int pane)
                    {
                        var wave = canvas.GetWaveBounds(pane); var range = pane == 0 ? canvas.PrecisionViewRange : canvas.Range;
                        float x = wave.Left + (float)((canvas.CursorFrame.Value - range.Min) / (double)(range.Max - range.Min) * wave.Width);
                        return Count(selected, Device(canvas, new RectangleF(x - 2, wave.Top + 6, 5, wave.Height - 12)), color => color.ToArgb() == Color.White.ToArgb()) > 40;
                    }
                    Check(GuideAt(0) && GuideAt(1), "the vertical white cursor guide remains visible above blue context and selection shading");
                    Check(Count(selected, Device(canvas, canvas.GetWaveBounds(1)), color => color == Color.FromArgb(237, 184, 110)) > 20,
                        "the selection end stays distinct above the zoom context");
                }
                canvas.Slots[0].ViewMode = AudioViewMode.Spectrogram;
                using (var spectrum = new Bitmap(canvas.Width, canvas.Height, PixelFormat.Format32bppPArgb))
                using (var graphics = Graphics.FromImage(spectrum))
                {
                    graphics.Clear(Color.FromArgb(200, 120, 45)); graphics.ScaleTransform(canvas.DpiScale, canvas.DpiScale);
                    var wave = canvas.GetWaveBounds(1); var range = canvas.Range;
                    typeof(WaveCanvas).GetMethod("DrawZoomContext", Hidden)!.Invoke(canvas, [graphics, wave, range.Min, range.Max, first, 0]);
                    var middle = Device(canvas, canvas.GetZoomContextBounds(1)); middle.Inflate(-4, -4);
                    Check(Count(spectrum, middle, color => color.R > color.G + 30 && color.G > color.B + 30) > 50 && ContextCache.GetValue(canvas) == null,
                        "spectrogram context keeps the underlying heatmap instead of replacing it with waveform ink");
                }
                canvas.Slots[0].ViewMode = AudioViewMode.Waveform; using (var restored = Render(canvas)) { }
                canvas.Slots[0].Track = null; using (var empty = Render(canvas)) { }
                Check(canvas.GetZoomContextBounds(1).IsEmpty && ContextCache.GetValue(canvas) == null,
                    "an empty active track has no blue window and releases its cached context source");
                canvas.Slots[0].Track = first; using (var populated = Render(canvas)) { }
                canvas.ResetRenderCache();
                Check(ContextCache.GetValue(canvas) == null && ((ICollection)typeof(WaveCanvas).GetField("renderCache", Hidden)!.GetValue(canvas)!).Count == 0,
                    "explicit render reset releases both normal and blue layer resources");
                using (var populated = Render(canvas)) { }
                canvas.Dispose(); Check(ContextCache.GetValue(canvas) == null, "disposing the canvas releases the blue context image");
                Console.WriteLine($"{checks} zoom context checks passed at the current process DPI. No native input, visible window or audio was used.");
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(45))) throw new TimeoutException("Zoom context fixture deadline exceeded.");
        if (failure != null) throw new Exception("Zoom context fixture failed", failure);
    }
}
