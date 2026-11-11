using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using OrchardSound;

public static class NestedPrecisionChecks
{
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                int checks = 0;
                void Check(bool condition, string name)
                { if (!condition) throw new Exception("FAILED NESTED PRECISION: " + name); checks++; Console.WriteLine("PASS NESTED PRECISION " + name); }
                static Bitmap Render(WaveCanvas canvas)
                {
                    var image = new Bitmap(canvas.Width, canvas.Height, PixelFormat.Format32bppPArgb);
                    using var graphics = Graphics.FromImage(image); graphics.Clear(AppTheme.Window);
                    typeof(WaveCanvas).GetMethod("OnPaint", Hidden)!.Invoke(canvas, [new PaintEventArgs(graphics, new Rectangle(Point.Empty, image.Size))]);
                    return image;
                }
                static int Count(WaveCanvas canvas, Bitmap image, Rectangle bounds, Func<Color, bool> matches)
                {
                    bounds = canvas.ToDevice(bounds); bounds.Intersect(new Rectangle(Point.Empty, image.Size));
                    if (bounds.IsEmpty) return 0;
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
                static long Counter(WaveCanvas canvas, string name) => (long)typeof(WaveCanvas).GetField(name, Hidden)!.GetValue(canvas)!;
                var samples = new float[AudioTrack.Rate * 4];
                for (int frame = 0; frame < samples.Length / 2; frame++)
                { samples[frame * 2] = frame % 2 == 0 ? .6f : -.6f; samples[frame * 2 + 1] = frame % 2 == 0 ? .2f : -.2f; }
                var track = new AudioTrack { Path = "Nested fixture.wav", Samples = samples, Peaks = new(samples) };
                using var canvas = new WaveCanvas { Width = 1800, AutoMagnify = false, Magnification = 8 };
                canvas.Slots[0].Track = track; canvas.SetViewportHeight(1500);
                typeof(WaveCanvas).GetField("lensCenter", Hidden)!.SetValue(canvas, 48000L);
                var wave = canvas.GetWaveBounds(0); var originalRange = canvas.PrecisionViewRange;
                Check(canvas.PrecisionMagnifierEnabled && canvas.NestedPrecisionAvailable && !canvas.NestedPrecisionVisible && canvas.GetNestedPrecisionBounds().IsEmpty,
                    "nested magnification starts enabled but waits for a pointer over precision");
                var policy = typeof(WaveCanvas).GetMethod("AllowsNestedPrecision", BindingFlags.Static | BindingFlags.NonPublic)!;
                bool Allowed(bool enabled, int height, double zoom) => (bool)policy.Invoke(null, [enabled, height, zoom])!;
                Check(!Allowed(true, 300, 12) && Allowed(true, 301, 99.9) && !Allowed(true, 301, 100) && !Allowed(false, 900, 12),
                    "nested eligibility uses strict300-device-pixel and100-times thresholds plus the View toggle");
                int blueY = wave.Top + wave.Height * 2 / 3;
                int x = wave.Left + wave.Width * 3 / 5;
                canvas.PrecisionPointer(new(x, blueY), false); canvas.CursorFrame = canvas.PointerAudioFrame(true);
                var nested = canvas.NestedPrecisionRange; var overlay = canvas.GetNestedPrecisionBounds();
                Check(canvas.NestedPrecisionVisible && overlay.Top == wave.Top && overlay.Left == wave.Left && overlay.Width == wave.Width && overlay.Height == wave.Height / 3,
                    "hovering precision exposes a purple overlay across exactly its top third");
                Check(nested.Max - nested.Min == (originalRange.Max - originalRange.Min) / 10
                    && nested.Min >= originalRange.Min && nested.Max <= originalRange.Max,
                    "the nested view is ten times closer within the existing precision range");
                var marker = canvas.GetNestedPrecisionContextBounds();
                Check(Math.Abs(marker.Width / wave.Width - .1) < .001 && marker.Left >= wave.Left && marker.Right <= wave.Right,
                    "the purple source marker shows the exact tenth of the blue precision waveform");
                using (var initial = Render(canvas))
                {
                    Check(Count(canvas, initial, overlay, color => color == WaveCanvas.NestedPrecisionAmplitudeColor(.6f)) > 100
                        && Count(canvas, initial, new(wave.Left, overlay.Bottom + 3, wave.Width, wave.Bottom - overlay.Bottom - 3), color => color == WaveCanvas.PrecisionAmplitudeColor(.6f)) > 100,
                        "actual painting keeps the top overlay purple and the remaining precision waveform blue");
                }
                long baseWarm = Counter(canvas, "renderCacheBuilds"), contextWarm = Counter(canvas, "nestedContextBuilds"), viewWarm = Counter(canvas, "nestedViewBuilds");
                canvas.PrecisionPointer(new(wave.Left + wave.Width / 4, overlay.Top + overlay.Height / 2), false);
                long expected = nested.Min + (long)Math.Round(.25 * (nested.Max - nested.Min));
                Check(canvas.NestedPrecisionRange == nested && Math.Abs(canvas.PointerAudioFrame(true) - expected) <= 2 && canvas.PrecisionViewRange == originalRange,
                    "entering purple freezes its anchor and maps directly into its ten-times range");
                canvas.PrecisionPointer(new(wave.Left + wave.Width * 3 / 4, overlay.Top + overlay.Height / 2), false);
                canvas.CursorFrame = canvas.PointerAudioFrame(true);
                using (var inside = Render(canvas))
                {
                    int guideX = (int)Math.Round(canvas.PrecisePointerX);
                    Check(canvas.NestedPrecisionRange == nested && Counter(canvas, "renderCacheBuilds") == baseWarm
                        && Counter(canvas, "nestedContextBuilds") == contextWarm && Counter(canvas, "nestedViewBuilds") == viewWarm,
                        "movement inside purple reuses every static bitmap instead of chasing the cursor");
                    Check(Count(canvas, inside, new(guideX - 2, overlay.Top + 4, 5, overlay.Height - 8), color => color.ToArgb() == Color.White.ToArgb()) > 30,
                        "the nested vertical white guide remains directly under the pointer");
                }
                canvas.PrecisionPointer(new(wave.Left + wave.Width * 4 / 5, blueY), false); canvas.CursorFrame = canvas.PointerAudioFrame(true);
                using (var moved = Render(canvas))
                    Check(canvas.NestedPrecisionRange != nested && Counter(canvas, "renderCacheBuilds") == baseWarm
                        && Counter(canvas, "nestedContextBuilds") == contextWarm && Counter(canvas, "nestedViewBuilds") == viewWarm + 1,
                        "blue-area movement retargets only the bounded purple viewport image");
                canvas.Slots[0].SplitChannels = true;
                using (var stereo = Render(canvas))
                {
                    var lanes = canvas.GetNestedChannelLaneBounds();
                    Check(lanes.Count == 2 && Count(canvas, stereo, lanes[0], color => color == WaveCanvas.NestedPrecisionAmplitudeColor(.6f)) > 50
                        && Count(canvas, stereo, lanes[1], color => color == WaveCanvas.NestedPrecisionAmplitudeColor(.2f)) > 50,
                        "the purple magnifier preserves distinct left and right channel data");
                }
                nested = canvas.NestedPrecisionRange;
                canvas.SelectionStart = nested.Min + (nested.Max - nested.Min) / 4; canvas.SelectionEnd = nested.Min + (nested.Max - nested.Min) * 3 / 4;
                var handle = canvas.GetNestedBoundaryHandleBounds(true);
                object? hit = typeof(WaveCanvas).GetMethod("BoundaryAt", Hidden)!.Invoke(canvas, [new Point(handle.Left + handle.Width / 2, handle.Top + handle.Height / 2)]);
                Check(!handle.IsEmpty && overlay.Contains(handle) && hit is ValueTuple<bool, int> boundary && boundary.Item1 && boundary.Item2 == 0
                    && ReferenceEquals(track.Samples, samples) && track.Offset == 0,
                    "nested selection handles use their own range and remain precision picks rather than audio moves");
                canvas.Magnification = 100;
                using (var hidden = Render(canvas))
                    Check(!canvas.NestedPrecisionAvailable && !canvas.NestedPrecisionVisible && canvas.GetNestedPrecisionBounds().IsEmpty
                        && typeof(WaveCanvas).GetField("nestedViewCache", Hidden)!.GetValue(canvas) == null,
                        "at100-times base zoom the extra overlay hides and releases its image");
                canvas.Magnification = 8; canvas.PrecisionMagnifierEnabled = false;
                Check(canvas.GetNestedPrecisionBounds().IsEmpty && !canvas.NestedPrecisionVisible,
                    "the View toggle removes nested magnification without changing audio or the base zoom");
                canvas.PrecisionMagnifierEnabled = true;
                canvas.PrecisionPointer(new(wave.Left, blueY), false);
                Check(canvas.NestedPrecisionRange.Min == canvas.PrecisionViewRange.Min,
                    "nested context clamps cleanly at the beginning of the blue precision range");
                canvas.PrecisionPointer(new(wave.Right - 1, blueY), false);
                Check(canvas.NestedPrecisionRange.Max == canvas.PrecisionViewRange.Max,
                    "nested context clamps cleanly at the end of the blue precision range");

                using var follow = new WaveCanvas { Width = 1800, Magnification = 8, PrecisionMagnifierEnabled = false };
                follow.Slots[0].Track = track; follow.SetViewportHeight(1500); follow.CursorFrame = 48000;
                Check(!follow.FollowCursor, "precision follow starts off to preserve the normal direct mapping");
                follow.FollowCursor = true;
                Check(follow.PrecisionViewRange == (42000L, 54000L), "Follow cursor centers the view on the current white guide");
                var fw = follow.GetWaveBounds(0); int fy = fw.Top + fw.Height * 2 / 3;
                follow.PrecisionPointer(new(fw.Left + fw.Width * 3 / 4, fy), false); follow.CursorFrame = follow.PointerAudioFrame(true);
                long cursor = follow.CursorFrame.Value; var followed = follow.PrecisionViewRange;
                follow.PrecisionPointer(new(fw.Left + fw.Width * 3 / 4, fy + 1), false); follow.CursorFrame = follow.PointerAudioFrame(true);
                Check(follow.CursorFrame == cursor && follow.PrecisionViewRange == followed
                    && followed.Min + (followed.Max - followed.Min) / 2 == cursor,
                    "a stationary-X or vertical-only pointer does not chase itself as Follow cursor recenters");
                follow.SelectionStart = 10000; follow.SelectionEnd = 20000;
                Check(follow.ZoomToSelection() && follow.FollowCursor && follow.PrecisionViewRange == (9000L, 21000L),
                    "Alt-fit temporarily centers the loop without unchecking Follow cursor");
                follow.CursorFrame = cursor + 100;
                var resumed = follow.PrecisionViewRange;
                Check(resumed.Min + (resumed.Max - resumed.Min) / 2 == cursor + 100,
                    "the next cursor change resumes precision following after Alt-fit");
                var frozen = follow.PrecisionViewRange; follow.FollowCursor = false; follow.CursorFrame += 1000;
                Check(follow.PrecisionViewRange == frozen, "disabling Follow cursor freezes the current view instead of jumping back");
                follow.FollowCursor = true; follow.CursorFrame = follow.Range.Min;
                var startView = follow.PrecisionViewRange;
                Check(startView.Min < follow.Range.Min && startView.Min + (startView.Max - startView.Min) / 2 == follow.CursorFrame,
                    "Follow cursor keeps the exact timeline start centered with blank context before the audio");
                follow.CursorFrame = follow.Range.Max; var endView = follow.PrecisionViewRange;
                Check(endView.Max > follow.Range.Max && endView.Min + (endView.Max - endView.Min) / 2 == follow.CursorFrame,
                    "Follow cursor keeps the exact timeline end centered with blank context after the audio");
                follow.MoveSelectionBoundary(true, follow.Range.Min - 1000, true);
                follow.MoveSelectionBoundary(false, follow.Range.Max + 1000, true);
                Check(follow.SelectionStart == follow.Range.Min && follow.SelectionEnd == follow.Range.Max && follow.CursorFrame == follow.Range.Max
                    && follow.ClampCursorToTrack(follow.Range.Min - 1000, 0) == track.Offset,
                    "blank follow context does not allow cursor picks or selection bounds outside valid audio time");
                follow.PrecisionMagnifierEnabled = true; follow.FollowCursor = true; follow.Magnification = 8; follow.CursorFrame = 48000;
                follow.PrecisionPointer(new(fw.Left, fy), false);
                var edgeNested = follow.NestedPrecisionRange; var purple = follow.GetNestedPrecisionBounds();
                follow.PrecisionPointer(new(fw.Left + fw.Width * 3 / 4, purple.Top + purple.Height / 2), false);
                follow.CursorFrame = follow.PointerAudioFrame(true); long nestedCursor = follow.CursorFrame.Value;
                follow.PrecisionPointer(new(fw.Left + fw.Width * 3 / 4, purple.Top + purple.Height / 2 + 1), false);
                follow.CursorFrame = follow.PointerAudioFrame(true);
                Check(follow.NestedPrecisionRange == edgeNested && follow.CursorFrame == nestedCursor,
                    "Follow cursor and an edge-clamped nested lens retain a stationary mapping when the blue view recenters");
                using (var axes = Render(canvas))
                {
                    var lane = canvas.GetChannelLaneBounds(1)[0];
                    Check(Count(canvas, axes, new(lane.Left - WaveCanvas.AmplitudeGutter, lane.Top, WaveCanvas.AmplitudeGutter - 7, lane.Height), color => color.R > 100 && color.G > 100 && color.B > 100) > 20
                        && Count(canvas, axes, new(lane.Right + 7, lane.Top, WaveCanvas.AmplitudeGutter - 7, lane.Height), color => color.R > 100 && color.G > 100 && color.B > 100) > 20,
                        "waveform lane amplitude labels are painted in clean left and right gutters");
                }
                canvas.ResetRenderCache();
                Check(typeof(WaveCanvas).GetField("nestedViewCache", Hidden)!.GetValue(canvas) == null
                    && typeof(WaveCanvas).GetField("nestedContextCache", Hidden)!.GetValue(canvas) == null,
                    "render reset releases both bounded purple image layers");
                Console.WriteLine($"{checks} nested precision and follow checks passed. No visible window, native input or audio was used.");
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(45))) throw new TimeoutException("Nested precision fixture deadline exceeded.");
        if (failure != null) throw new Exception("Nested precision fixture failed", failure);
    }
}
