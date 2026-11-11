using System.Collections;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using OrchardSound;

public static class RenderingChecks
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly MethodInfo Bounds = typeof(WaveCanvas).GetMethod("WaveBounds", Private)!;
    static readonly MethodInfo Cached = typeof(WaveCanvas).GetMethod("DrawWave", Private)!;
    static readonly MethodInfo Base = typeof(WaveCanvas).GetMethod("DrawWaveBase", Private)!;
    static readonly MethodInfo Overlays = typeof(WaveCanvas).GetMethod("DrawWaveOverlays", Private)!;
    static readonly FieldInfo Builds = typeof(WaveCanvas).GetField("renderCacheBuilds", Private)!;
    static readonly FieldInfo Cache = typeof(WaveCanvas).GetField("renderCache", Private)!;

    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                int checks = 0;
                void Check(bool result, string name) { if (!result) throw new Exception("FAILED RENDERING: " + name); checks++; Console.WriteLine("PASS RENDERING " + name); }
                static AudioTrack Track(int frames)
                {
                    var samples = new float[frames * 2];
                    for (int frame = 0; frame < frames; frame++) { float value = (float)(Math.Sin(frame * .039) * (.1 + .7 * frame / frames)); samples[frame * 2] = value; samples[frame * 2 + 1] = -value; }
                    return new() { Path = "Rendering fixture.wav", Samples = samples, Peaks = new(samples) };
                }
                static long BuildCount(WaveCanvas canvas) => (long)Builds.GetValue(canvas)!;
                static int EntryCount(WaveCanvas canvas) => ((ICollection)Cache.GetValue(canvas)!).Count;
                static void Draw(WaveCanvas canvas, Graphics graphics, bool cached)
                {
                    graphics.Clear(AppTheme.Window); var state = graphics.Save(); graphics.ScaleTransform(canvas.DpiScale, canvas.DpiScale);
                    for (int pane = 0; pane <= canvas.Slots.Count; pane++)
                    {
                        int index = pane == 0 ? 0 : pane - 1; var track = canvas.Slots[index].Track;
                        var bounds = (Rectangle)Bounds.Invoke(canvas, [pane])!; var range = pane == 0 ? canvas.PrecisionViewRange : canvas.Range;
                        object?[] args = [graphics, bounds, range.Min, range.Max, track, index];
                        if (cached) Cached.Invoke(canvas, args); else { Base.Invoke(canvas, args); Overlays.Invoke(canvas, args); }
                    }
                    graphics.Restore(state);
                }
                static Bitmap Render(WaveCanvas canvas, bool cached)
                {
                    var image = new Bitmap(canvas.Width, canvas.Height, PixelFormat.Format32bppPArgb); image.SetResolution(canvas.DeviceDpi, canvas.DeviceDpi);
                    using var graphics = Graphics.FromImage(image); Draw(canvas, graphics, cached); return image;
                }
                static bool Equal(Bitmap first, Bitmap second)
                {
                    if (first.Size != second.Size) return false;
                    var a = first.LockBits(new Rectangle(Point.Empty, first.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                    var b = second.LockBits(new Rectangle(Point.Empty, second.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                    try
                    {
                        int length = first.Width * 4; var rowA = new byte[length]; var rowB = new byte[length];
                        for (int y = 0; y < first.Height; y++)
                        {
                            Marshal.Copy(IntPtr.Add(a.Scan0, y * a.Stride), rowA, 0, length); Marshal.Copy(IntPtr.Add(b.Scan0, y * b.Stride), rowB, 0, length);
                            if (rowA.AsSpan().SequenceEqual(rowB)) continue;
                            int x = Enumerable.Range(0, length).First(i => rowA[i] != rowB[i]) / 4;
                            int byteAt = x * 4;
                            Console.WriteLine($"Rendering pixel mismatch at {x},{y} (DPI image {first.HorizontalResolution}): cached BGRA {string.Join(",", rowA.Skip(byteAt).Take(4))}, direct BGRA {string.Join(",", rowB.Skip(byteAt).Take(4))}."); return false;
                        }
                        return true;
                    }
                    finally { first.UnlockBits(a); second.UnlockBits(b); }
                }
                void MatchesDirect(WaveCanvas canvas, string name)
                { using var cached = Render(canvas, true); using var direct = Render(canvas, false); Check(Equal(cached, direct), name); }

                using var canvas = new WaveCanvas { Width = 1800 }; canvas.SetViewportHeight(900);
                var track = Track(AudioTrack.Rate * 10); canvas.Slots[0].Track = track;
                MatchesDirect(canvas, "cold cached base and uncached reference have identical waveform, grid and amplitude-label pixels");
                long warm = BuildCount(canvas);
                canvas.SelectionStart = 10000; canvas.SelectionEnd = 60000; canvas.CursorFrame = 24000; canvas.Playhead = 40000;
                MatchesDirect(canvas, "dynamic selection, cursor, playhead and handles preserve reference pixels over a cached base");
                Check(BuildCount(canvas) == warm, "cursor, selection and playback overlays reuse the existing pane bitmaps");
                canvas.AutoMagnify = false; MatchesDirect(canvas, "auto magnification changes invalidate the base image and label scale");
                warm = BuildCount(canvas); track.Gain = .5f; MatchesDirect(canvas, "gain changes invalidate cached waveform height and amplitude shading");
                Check(BuildCount(canvas) == warm + 2, "one gain edit rebuilds exactly the precision and channel panes");
                track.Fades.Add(new(0, track.Frames, true)); MatchesDirect(canvas, "adding a fade invalidates cached envelope pixels");
                warm = BuildCount(canvas); track.Fades[0] = new(1000, track.Frames - 1000, false); MatchesDirect(canvas, "replacing an existing fade invalidates its semantic state");
                Check(BuildCount(canvas) == warm + 2, "fade replacement rebuilds each affected pane once");
                track.Segments = [new(1000, 100000), new(300000, 100000)]; MatchesDirect(canvas, "timeline cuts invalidate visible segment mapping");
                track.WriteVisibleSamples(2000, Enumerable.Repeat(.9f, 2000).ToArray()); MatchesDirect(canvas, "processed audio patches invalidate cached waveform peaks");
                track.Enabled = false; MatchesDirect(canvas, "unticked channels invalidate cached dimmed waveform colors");
                track.Enabled = true; track.Offset = -10000; MatchesDirect(canvas, "track moves invalidate range and waveform placement");
                track.Rhythm = new(120, [2000, 12000, 40000], [4000, 20000]); canvas.ShowBeats = true; canvas.ShowKicks = true;
                MatchesDirect(canvas, "beat and kick visibility invalidates static marker pixels");
                warm = BuildCount(canvas); track.Rhythm.BeatFrames[0] = 6000; track.Rhythm.KickFrames[0] = 8000;
                MatchesDirect(canvas, "in-place rhythm marker edits invalidate copied semantic cache keys");
                Check(BuildCount(canvas) == warm + 2, "rhythm marker edits rebuild the affected panes");
                var preview = Track((int)track.SourceFrames); preview.Segments = track.GetSegments(); preview.Offset = track.Offset; preview.Gain = .8f;
                canvas.PreviewSources[track] = preview; MatchesDirect(canvas, "effect preview source changes invalidate the displayed source");
                warm = BuildCount(canvas);
                canvas.PreviewSources[track] = new() { Path = preview.Path, Samples = preview.Samples, Peaks = preview.Peaks, Segments = preview.GetSegments(), Offset = preview.Offset, Gain = preview.Gain };
                MatchesDirect(canvas, "replacement preview instances are rendered from their current semantic state");
                Check(BuildCount(canvas) == warm + 2, "preview source identity changes invalidate both displayed panes");
                canvas.PreviewSources.Clear(); MatchesDirect(canvas, "closing a preview restores cached rendering to the underlying track");
                canvas.Width += 101; MatchesDirect(canvas, "resizing recreates physical bitmaps with the correct logical extent");
                canvas.ResizeBoundary(0, 19); MatchesDirect(canvas, "divider changes preserve fractional-DPI pixel phase in moved panes");
                warm = BuildCount(canvas); canvas.Magnification *= 2; MatchesDirect(canvas, "precision zoom invalidates only its changed range");
                Check(BuildCount(canvas) == warm + 1, "precision zoom leaves the unchanged channel bitmap warm");
                using (var target = new Bitmap(canvas.Width, canvas.Height, PixelFormat.Format32bppPArgb))
                using (var graphics = Graphics.FromImage(target))
                {
                    var center = typeof(WaveCanvas).GetField("lensCenter", Private)!;
                    for (int i = 0; i < 20; i++) { center.SetValue(canvas, (long)i * 5000); Draw(canvas, graphics, true); }
                    Check(EntryCount(canvas) == canvas.Slots.Count + 1, "repeated precision panning keeps at most one base bitmap per pane");
                    Draw(canvas, graphics, true); warm = BuildCount(canvas);
                    const int frames = 20; var clock = Stopwatch.StartNew();
                    for (int i = 0; i < frames; i++) Draw(canvas, graphics, true);
                    double cachedMs = clock.Elapsed.TotalMilliseconds; clock.Restart();
                    for (int i = 0; i < frames; i++) Draw(canvas, graphics, false);
                    double uncachedMs = clock.Elapsed.TotalMilliseconds;
                    Check(BuildCount(canvas) == warm, "warm-cache benchmark performs no hidden base rebuilds");
                    Console.WriteLine($"RENDER BENCH {frames} detached paints at {canvas.DeviceDpi} DPI: cached {cachedMs:F1} ms, uncached {uncachedMs:F1} ms. This is not native FPS evidence.");
                }
                canvas.Slots.Add(new() { Track = Track(30000) }); canvas.FitHeight(); MatchesDirect(canvas, "additional channels receive independent bounded cache entries");
                canvas.Slots.RemoveAt(1); canvas.FitHeight(); using (var redraw = Render(canvas, true)) { }
                Check(EntryCount(canvas) == canvas.Slots.Count + 1, "removing a channel disposes and removes its stale cache entry");
                canvas.ResetRenderCache(); Check(EntryCount(canvas) == 0, "explicit cache reset releases all stored pane images");
                using (var redraw = Render(canvas, true)) { }
                canvas.Dispose(); Check(EntryCount(canvas) == 0, "disposing the canvas releases its cached bitmap resources");
                Console.WriteLine($"{checks} rendering checks passed. No native window was shown.");
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(45))) throw new TimeoutException("Rendering fixture deadline exceeded.");
        if (failure != null) throw new Exception("Rendering fixture failed", failure);
    }
}
