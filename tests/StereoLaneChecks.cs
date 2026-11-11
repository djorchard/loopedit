using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using OrchardSound;

public static class StereoLaneChecks
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
                { if (!condition) throw new Exception("FAILED STEREO LANES: " + name); checks++; Console.WriteLine("PASS STEREO LANES " + name); }
                static AudioTrack Track(string name, float[] samples) => new() { Path = name + ".wav", Samples = samples, Peaks = new(samples) };
                static (float Min, float Max) Brute(float[] samples, int channel, long start, long end)
                {
                    float min = 0, max = 0;
                    for (long frame = Math.Clamp(start, 0, samples.LongLength / 2); frame < Math.Clamp(end, 0, samples.LongLength / 2); frame++)
                    { float value = samples[checked((int)(frame * 2 + channel))]; min = Math.Min(min, value); max = Math.Max(max, value); }
                    return (min, max);
                }
                static Bitmap Render(WaveCanvas canvas)
                {
                    var image = new Bitmap(canvas.Width, canvas.Height, PixelFormat.Format32bppPArgb);
                    using var graphics = Graphics.FromImage(image); graphics.Clear(AppTheme.Window);
                    typeof(WaveCanvas).GetMethod("OnPaint", Hidden)!.Invoke(canvas, [new PaintEventArgs(graphics, new Rectangle(Point.Empty, image.Size))]);
                    return image;
                }
                static Rectangle Device(WaveCanvas canvas, Rectangle bounds) => canvas.ToDevice(bounds);
                static byte[] Pixels(Bitmap image, Rectangle bounds)
                {
                    bounds.Intersect(new Rectangle(Point.Empty, image.Size));
                    var data = image.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                    try
                    {
                        int stride = bounds.Width * 4; var pixels = new byte[stride * bounds.Height];
                        for (int row = 0; row < bounds.Height; row++) Marshal.Copy(IntPtr.Add(data.Scan0, row * data.Stride), pixels, row * stride, stride);
                        return pixels;
                    }
                    finally { image.UnlockBits(data); }
                }
                static int Count(Bitmap image, Rectangle bounds, Func<Color, bool> matches)
                {
                    byte[] data = Pixels(image, bounds); int count = 0;
                    for (int i = 0; i < data.Length; i += 4) if (matches(Color.FromArgb(data[i + 3], data[i + 2], data[i + 1], data[i]))) count++;
                    return count;
                }
                static double DominantNote(SpectrogramData data)
                {
                    int best = 0; long energy = -1;
                    for (int band = 0; band < SpectrogramData.Bands; band++)
                    {
                        long sum = 0; for (int column = 4; column < data.Columns - 4; column++) sum += data[column, band];
                        if (sum > energy) { energy = sum; best = band; }
                    }
                    return SpectrogramData.LowestNote + (best + .5) / SpectrogramData.Bands * (SpectrogramData.HighestNote - SpectrogramData.LowestNote);
                }
                var source = new float[6000 * 2];
                for (int frame = 0; frame < 6000; frame++)
                { source[frame * 2] = (float)Math.Sin(frame * .071) * .8f; source[frame * 2 + 1] = (float)Math.Sin(frame * .193) * .12f; }
                source[127 * 2] = .95f; source[128 * 2 + 1] = -.7f;
                var original = source.ToArray(); var edited = Track("Channel peaks", source);
                var ranges = new[] { (0L, 1L), (127L, 129L), (128L, 512L), (511L, 1023L), (-20L, 7000L) };
                Check(ranges.All(range => Enumerable.Range(0, 2).All(channel => edited.QueryPeaks(range.Item1, range.Item2, channel) == Brute(source, channel, range.Item1, range.Item2))),
                    "channel peak pyramids preserve exact independent transients at short and aligned ranges");
                var getPeaks = typeof(AudioTrack).GetMethod("GetChannelPeaks", BindingFlags.NonPublic | BindingFlags.Static)!;
                var leftPeaks = (PeakPyramid)getPeaks.Invoke(null, [source, 0])!;
                var rightPeaks = (PeakPyramid)getPeaks.Invoke(null, [source, 1])!;
                var samplesField = typeof(PeakPyramid).GetField("samples", Hidden)!;
                Check(ReferenceEquals(leftPeaks, getPeaks.Invoke(null, [source, 0])) && !ReferenceEquals(leftPeaks, rightPeaks)
                    && ReferenceEquals(samplesField.GetValue(leftPeaks), source) && ReferenceEquals(samplesField.GetValue(rightPeaks), source),
                    "left and right summaries are reused over the same original interleaved array without copying audio");
                edited.Segments = [new(100, 900), new(2000, 1500)];
                var patch = Enumerable.Range(0, 400).SelectMany(frame => new[] { frame % 2 == 0 ? .6f : -.6f, frame % 2 == 0 ? .05f : -.05f }).ToArray();
                edited.WriteVisibleSamples(500, patch); var visible = edited.ReadVisibleSamples(0, edited.Frames);
                Check(Enumerable.Range(0, 2).All(channel => edited.QueryPeaks(450, 1000, channel) == Brute(visible, channel, 450, 1000)),
                    "per-channel peaks follow visible cuts and processed patch mappings");
                var retained = edited.GetSegments(); var appended = edited.AppendSourceSamples(Enumerable.Range(0, 80).SelectMany(_ => new[] { .4f, -.2f }).ToArray());
                edited.Segments = [.. retained, appended]; visible = edited.ReadVisibleSamples(0, edited.Frames);
                Check(Enumerable.Range(0, 2).All(channel => edited.QueryPeaks(edited.Frames - 90, edited.Frames, channel) == Brute(visible, channel, visible.Length / 2 - 90, visible.Length / 2))
                    && source.SequenceEqual(original),
                    "channel summaries include virtual tails while preserving original source samples");
                bool invalid = false; try { _ = edited.QueryPeaks(0, 1, 2); } catch (ArgumentOutOfRangeException) { invalid = true; }
                Check(invalid, "channel peak queries reject invalid channel indices");

                var tones = new float[AudioTrack.Rate * 2];
                for (int frame = 0; frame < AudioTrack.Rate; frame++)
                { tones[frame * 2] = (float)Math.Sin(2 * Math.PI * 220 * frame / AudioTrack.Rate) * .8f; tones[frame * 2 + 1] = (float)Math.Sin(2 * Math.PI * 880 * frame / AudioTrack.Rate) * .1f; }
                var tone = Track("Different stereo notes", tones);
                var leftSpectrum = SpectrogramData.ComputeChannel(tone.Snapshot(), 0); var rightSpectrum = SpectrogramData.ComputeChannel(tone.Snapshot(), 1);
                Check(Math.Abs(DominantNote(leftSpectrum) - 57) < 2 && Math.Abs(DominantNote(rightSpectrum) - 81) < 2,
                    "separate spectral analysis locates220Hz left and880Hz right without duplicating their data");
                var silentRight = tones.ToArray(); for (int frame = 0; frame < AudioTrack.Rate; frame++) silentRight[frame * 2 + 1] = 0;
                var rightSilence = SpectrogramData.ComputeChannel(Track("Silent right", silentRight).Snapshot(), 1);
                Check(Enumerable.Range(0, rightSilence.Columns).All(column => Enumerable.Range(0, SpectrogramData.Bands).All(band => rightSilence[column, band] == 0)),
                    "a silent right channel remains spectrally silent beside a loud left channel");
                using (var cancel = new CancellationTokenSource())
                {
                    cancel.Cancel(); bool stopped = false;
                    try { _ = SpectrogramData.ComputeChannel(tone.Snapshot(), 0, cancel.Token); } catch (OperationCanceledException) { stopped = true; }
                    Check(stopped, "channel-specific spectral work honors cancellation before processing");
                }
                var cacheType = typeof(WaveCanvas).Assembly.GetType("OrchardSound.SpectrogramCache", true)!;
                using (var cache = (IDisposable)Activator.CreateInstance(cacheType, [(Action)(() => { }), (long)(leftSpectrum.ByteSize + rightSpectrum.ByteSize)])!)
                {
                    SpectrogramData Await(int channel)
                    {
                        SpectrogramData? data = null; string? error = null;
                        bool ready = SpinWait.SpinUntil(() =>
                        {
                            object?[] args = [tone, channel, null]; data = (SpectrogramData?)cacheType.GetMethod("GetChannel")!.Invoke(cache, args); error = (string?)args[2];
                            return data != null || error != null;
                        }, TimeSpan.FromSeconds(5));
                        return ready && data != null ? data : throw new Exception(error ?? "Stereo cache deadline exceeded.");
                    }
                    var left = Await(0); var right = Await(1);
                    Check(!ReferenceEquals(left, right) && Math.Abs(DominantNote(left) - 57) < 2 && Math.Abs(DominantNote(right) - 81) < 2
                        && (long)cacheType.GetProperty("CachedBytes")!.GetValue(cache)! == left.ByteSize + right.ByteSize,
                        "the shared spectral byte budget contains distinct left and right entries");
                    cacheType.GetMethod("KeepOnlyChannels")!.Invoke(cache, [new[] { (tone, 0) }]);
                    Check((long)cacheType.GetProperty("CachedBytes")!.GetValue(cache)! == left.ByteSize,
                        "inactive channel matrices are released when their display is no longer requested");
                }

                var laneSamples = new float[AudioTrack.Rate * 2];
                for (int frame = 0; frame < AudioTrack.Rate; frame++)
                { laneSamples[frame * 2] = frame % 2 == 0 ? .8f : -.8f; laneSamples[frame * 2 + 1] = frame % 2 == 0 ? .15f : -.15f; }
                var track = Track("Loud left quiet right", laneSamples);
                using var canvas = new WaveCanvas { Width = 1440, AutoMagnify = false };
                canvas.Slots[0].Track = track; canvas.Slots.Add(new() { Track = Track("Other", new float[AudioTrack.Rate * 2]) }); canvas.SetViewportHeight(1200);
                typeof(WaveCanvas).GetField("lensCenter", Hidden)!.SetValue(canvas, (long)AudioTrack.Rate / 2);
                var builds = typeof(WaveCanvas).GetField("renderCacheBuilds", Hidden)!;
                long BuildCount() => (long)builds.GetValue(canvas)!;
                using var combined = Render(canvas); long warm = BuildCount(); int paneHeight = canvas.GetPaneBounds(1).Height;
                var beforeMix = new float[64]; new MixProvider([track.Snapshot()], 0, 32, false).Read(beforeMix, 0, beforeMix.Length);
                canvas.Slots[0].SplitChannels = true;
                using var split = Render(canvas); var lanes = canvas.GetChannelLaneBounds(1); var topLanes = canvas.GetChannelLaneBounds(0);
                Check(lanes.Count == 2 && topLanes.Count == 2 && lanes[0].Bottom < lanes[1].Top && lanes[0].Width == lanes[1].Width
                    && lanes[0].Top == canvas.GetWaveBounds(1).Top && lanes[1].Bottom == canvas.GetWaveBounds(1).Bottom && canvas.GetPaneBounds(1).Height == paneHeight,
                    "Split channels adds two L/R display lanes inside the existing resizable pane");
                Check(BuildCount() == warm + 2 && Pixels(combined, Device(canvas, canvas.GetPaneBounds(2))).AsSpan().SequenceEqual(Pixels(split, Device(canvas, canvas.GetPaneBounds(2)))),
                    "splitting rebuilds the active pane and precision while leaving the other track's pixels unchanged");
                Color leftBlue = WaveCanvas.PrecisionAmplitudeColor(.8f), rightBlue = WaveCanvas.PrecisionAmplitudeColor(.15f);
                Check(Count(split, Device(canvas, topLanes[0]), color => color == leftBlue) > 100
                    && Count(split, Device(canvas, topLanes[1]), color => color == rightBlue) > 100
                    && Count(split, Device(canvas, topLanes[1]), color => color == leftBlue) < 5,
                    "both precision lanes are blue and show their own different channel amplitudes");
                Check(Count(split, Device(canvas, lanes[0]), color => color == WaveCanvas.AmplitudeColor(0, .8f)) > 100
                    && Count(split, Device(canvas, lanes[1]), color => color == WaveCanvas.AmplitudeColor(0, .15f)) > 100,
                    "lower L/R lanes retain independent amplitude shading outside the blue context");
                var context = canvas.GetZoomContextBounds(1);
                Check(context.Top == lanes[0].Top && context.Bottom == lanes[1].Bottom && canvas.GetZoomContextBounds(2).IsEmpty,
                    "the active blue zoom window spans both L/R lanes of only its source track");
                canvas.AutoMagnify = true;
                var leftRange = canvas.GetAmplitudeRange(1, 0); var rightRange = canvas.GetAmplitudeRange(1, 1);
                Check(Math.Abs(leftRange.Max - .8) < .00001 && Math.Abs(rightRange.Max - .15) < .00001
                    && canvas.GetAmplitudeTicks(1, 0).All(tick => tick.Y >= lanes[0].Top && tick.Y < lanes[0].Bottom)
                    && canvas.GetAmplitudeTicks(1, 1).All(tick => tick.Y >= lanes[1].Top && tick.Y < lanes[1].Bottom)
                    && Math.Abs(canvas.GetAmplitudeTicks(1, 1).Max(tick => tick.Percent) - 15) < .001,
                    "each magnified lane has its own truthful amplitude scale and contained grid labels");
                typeof(WaveCanvas).GetField("hoveredSlot", Hidden)!.SetValue(canvas, 1);
                Check(canvas.GetChannelLaneBounds(0).Count == 1 && canvas.GetChannelLaneBounds(1).Count == 2,
                    "precision inherits the currently hovered track's split setting independently of other panes");
                typeof(WaveCanvas).GetField("hoveredSlot", Hidden)!.SetValue(canvas, 0); canvas.Slots[0].SplitChannels = false;
                var afterMix = new float[64]; new MixProvider([track.Snapshot()], 0, 32, false).Read(afterMix, 0, afterMix.Length);
                Check(beforeMix.SequenceEqual(afterMix) && canvas.Slots.Count == 2 && ReferenceEquals(canvas.Slots[0].Track, track)
                    && ReferenceEquals(track.Samples, laneSamples) && track.Frames == AudioTrack.Rate && track.Offset == 0 && track.Gain == 1 && track.Patches.Length == 0,
                    "split display toggles preserve one shared track and exactly the same stereo mix samples");
                Check(canvas.GetChannelLaneBounds(0).Count == 1 && canvas.GetChannelLaneBounds(1).Count == 1,
                    "turning Split channels off restores the original combined display");

                canvas.Slots[0].Track = tone; canvas.Slots[0].SplitChannels = true;
                canvas.Slots[0].ViewMode = AudioViewMode.Spectrogram; canvas.PrecisionViewMode = AudioViewMode.Spectrogram;
                using (var pending = Render(canvas)) { }
                var spectralCache = typeof(WaveCanvas).GetField("spectralCache", Hidden)!.GetValue(canvas)!;
                if (!SpinWait.SpinUntil(() => (long)cacheType.GetProperty("Revision")!.GetValue(spectralCache)! >= 2, TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Split spectral render deadline exceeded.");
                using (var spectrum = Render(canvas))
                {
                    lanes = canvas.GetChannelLaneBounds(1);
                    int Energy(int channel, int midi)
                    {
                        var lane = lanes[channel]; int y = (int)Math.Round(lane.Top + (SpectrogramData.HighestNote - midi) / 120d * (lane.Height - 1));
                        return Count(spectrum, Device(canvas, new(lane.Left + lane.Width / 4, y - 3, lane.Width / 2, 7)), color => color.G > 115 && color.GetSaturation() > .2f);
                    }
                    Check(Energy(0, 57) > 50 && Energy(1, 81) > 50 && Energy(0, 81) < Energy(0, 57) / 4 && Energy(1, 57) < Energy(1, 81) / 4,
                        "the actual split spectrogram paints220Hz only in L and880Hz only in R");
                    Check(canvas.GetNoteTicks(1, 0).All(tick => tick.Y >= lanes[0].Top && tick.Y < lanes[0].Bottom)
                        && canvas.GetNoteTicks(1, 1).All(tick => tick.Y >= lanes[1].Top && tick.Y < lanes[1].Bottom),
                        "each spectral lane receives a separate contained musical-note grid");
                    Check(Count(spectrum, Device(canvas, new(lanes[0].Left - WaveCanvas.AmplitudeGutter, lanes[0].Top, WaveCanvas.AmplitudeGutter - 7, lanes[0].Height)), color => color.R > 100 && color.G > 100 && color.B > 100) > 20
                        && Count(spectrum, Device(canvas, new(lanes[0].Right + 7, lanes[0].Top, WaveCanvas.AmplitudeGutter - 7, lanes[0].Height)), color => color.R > 100 && color.G > 100 && color.B > 100) > 20,
                        "spectral note names are painted in separate left and right axis gutters");
                    Check((long)cacheType.GetProperty("CachedBytes")!.GetValue(spectralCache)! == leftSpectrum.ByteSize + rightSpectrum.ByteSize,
                        "precision and lower spectral lanes share the same two bounded channel analyses");
                }
                canvas.Slots[0].ViewMode = AudioViewMode.Waveform; canvas.PrecisionViewMode = AudioViewMode.Waveform;
                using (var restored = Render(canvas)) { }
                Check((long)cacheType.GetProperty("CachedBytes")!.GetValue(spectralCache)! == 0,
                    "leaving spectral display releases inactive L/R analysis matrices");
                Console.WriteLine($"{checks} stereo lane checks passed. No visible window, native input or audio output was used.");
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(45))) throw new TimeoutException("Stereo lane fixture deadline exceeded.");
        if (failure != null) throw new Exception("Stereo lane fixture failed", failure);
    }
}
