using System.Reflection;
using OrchardSound;

public static class SpectralHoverChecks
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void Run()
    {
        int checks = 0;
        void Check(bool value, string name)
        { if (!value) throw new Exception("FAILED SPECTRAL HOVER: " + name); checks++; Console.WriteLine("PASS SPECTRAL HOVER " + name); }
        var lane = new Rectangle(20, 30, 800, 1201);
        Check(WaveCanvas.SpectralHoverMidiNote(lane, lane.Top) == 132 && WaveCanvas.SpectralHoverMidiNote(lane, lane.Bottom - 1) == 12
            && WaveCanvas.SpectralHoverMidiNote(lane, lane.Top + 630) == 69, "hover geometry follows the C0 toC10 scale with high notes above low notes");
        var band = WaveCanvas.SpectralHoverNoteBounds(lane, 69);
        Check(band.Width == lane.Width && band.Contains(lane.Left, lane.Top + 630) && band.Height <= 12 && lane.Contains(band),
            "one hovered semitone produces a bounded note row instead of lightening a whole channel");
        var baseColor = Color.FromArgb(25, 80, 110);
        var quiet = WaveCanvas.SpectralHoverColor(baseColor, 32); var loud = WaveCanvas.SpectralHoverColor(baseColor, 240);
        Check(WaveCanvas.SpectralHoverColor(baseColor, 0) == baseColor && quiet.R > baseColor.R && loud.R > quiet.R && loud.B < 255,
            "hover whitening follows spectral magnitude and leaves silence unchanged");
        Check(WaveCanvas.SpectralHoverColor(baseColor, 255) == Color.FromArgb(255, 255, 255)
            && quiet.R == quiet.G && quiet.G == quiet.B && loud.R == loud.G && loud.G == loud.B,
            "full-strength note centers become true white while quieter magnitudes remain distinguishable grayscale");
        Check(WaveCanvas.SpectralHoverColor(baseColor, 255, 0) == baseColor && WaveCanvas.SpectralHoverColor(baseColor, 255, .5).R < WaveCanvas.SpectralHoverColor(baseColor, 255).R,
            "vertical taper fades the highlight at the semitone edges");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var canvas = new WaveCanvas { Width = 1400, Magnification = 8, PrecisionViewMode = AudioViewMode.Spectrogram };
                canvas.SetViewportHeight((int)(1600 * canvas.DpiScale));
                var samples = new float[AudioTrack.Rate * 4];
                for (int frame = 0; frame < samples.Length / 2; frame++)
                {
                    double time = frame / (double)AudioTrack.Rate, amount = time < 1 ? .7 : time < 1.75 ? .035 : 0;
                    samples[frame * 2] = (float)(amount * Math.Sin(2 * Math.PI * 440 * time));
                    samples[frame * 2 + 1] = (float)(amount * Math.Sin(2 * Math.PI * 880 * time));
                }
                var track = new AudioTrack { Path = "Spectral hover fixture.wav", Samples = samples, Peaks = new(samples) };
                canvas.Slots[0].Track = track; canvas.Slots[0].SplitChannels = true; canvas.Slots[0].ViewMode = AudioViewMode.Spectrogram;
                var wave = canvas.GetWaveBounds(1); var lanes = canvas.GetChannelLaneBounds(1);
                Point NotePoint(Rectangle row, int note) => new(row.Left + row.Width / 3,
                    row.Top + (int)Math.Round((132 - note) / 120d * (row.Height - 1)));
                var point = NotePoint(lanes[0], 69); var target = canvas.GetSpectralNoteHover(1, point);
                Check(target.HasValue && target.Value.Channel == 0 && target.Value.MidiNote == 69 && lanes[0].Contains(target.Value.NoteBounds),
                    "a lower left-channel note hover targets the exact left lane");
                var rightPoint = NotePoint(lanes[1], 81); var rightTarget = canvas.GetSpectralNoteHover(1, rightPoint);
                Check(rightTarget.HasValue && rightTarget.Value.Channel == 1 && rightTarget.Value.MidiNote == 81 && lanes[1].Contains(rightTarget.Value.NoteBounds),
                    "the right channel has an independent semitone hover scale");
                Check(canvas.GetSpectralNoteHover(1, new(wave.Left, wave.Top - 1)) == null && canvas.GetSpectralNoteHover(0, point) == null,
                    "a pointer outside a pane never highlights that pane or another row");

                typeof(WaveCanvas).GetMethod("PrepareSpectrogramCache", Private)!.Invoke(canvas, null);
                var cache = typeof(WaveCanvas).GetField("spectralCache", Private)!.GetValue(canvas)!; var cacheType = cache.GetType();
                SpectrogramData? Read(int channel)
                { object?[] values = [track, channel, null]; return (SpectrogramData?)cacheType.GetMethod("GetChannel")!.Invoke(cache, values); }
                if (!SpinWait.SpinUntil(() => Read(0) != null && Read(1) != null, TimeSpan.FromSeconds(5))) throw new TimeoutException("Hover spectrum cache deadline exceeded.");
                var leftData = Read(0); var rightData = Read(1); long revision = (long)cacheType.GetProperty("Revision")!.GetValue(cache)!;
                void Pointer(Point value)
                {
                    typeof(WaveCanvas).GetField("previousRawPointer", Private)!.SetValue(canvas, value);
                    typeof(WaveCanvas).GetField("pointerOverWave", Private)!.SetValue(canvas, true);
                }
                Bitmap Paint(Rectangle bounds, int index, bool nested = false)
                {
                    var bitmap = new Bitmap(canvas.Width, canvas.Height);
                    using var graphics = Graphics.FromImage(bitmap); graphics.Clear(AppTheme.Surface);
                    typeof(WaveCanvas).GetMethod("DrawSpectralNoteHover", Private)!.Invoke(canvas, [graphics, bounds, 0L, track.Frames, track, index, nested]);
                    return bitmap;
                }
                Bitmap PaintWithLabels(Rectangle bounds, int pane, bool nested = false, bool drawBase = false)
                {
                    var bitmap = new Bitmap(canvas.Width, canvas.Height);
                    using var graphics = Graphics.FromImage(bitmap);
                    graphics.Clear(AppTheme.Window);
                    if (drawBase)
                        typeof(WaveCanvas).GetMethod("DrawSpectrogramBase", Private)!.Invoke(canvas, [graphics, bounds, 0L, track.Frames, track, pane]);
                    typeof(WaveCanvas).GetMethod("DrawWaveOverlaysCore", Private)!.Invoke(canvas, [graphics, bounds, 0L, track.Frames, track, Math.Max(0, pane - 1), nested]);
                    return bitmap;
                }
                int LabelInkWidth(Bitmap image, Rectangle laneBounds, int noteY, bool right)
                {
                    int left = right ? laneBounds.Right : laneBounds.Left - WaveCanvas.AmplitudeGutter;
                    int top = Math.Max(laneBounds.Top, noteY - 10);
                    int bottom = Math.Min(laneBounds.Bottom, noteY + 11);
                    int first = int.MaxValue, last = -1;
                    for (int x = left; x < left + WaveCanvas.AmplitudeGutter; x++)
                        for (int y = top; y < bottom; y++)
                            if (image.GetPixel(x, y).R >= 190)
                            {
                                first = Math.Min(first, x);
                                last = Math.Max(last, x);
                            }
                    return last < 0 ? 0 : last - first + 1;
                }
                int Light(Bitmap image, int x, Rectangle region)
                { int peak = 0; for (int y = region.Top; y < region.Bottom; y++) peak = Math.Max(peak, image.GetPixel(x, y).R); return peak; }
                Pointer(point); using (var painted = Paint(wave, 0))
                {
                    var row = target!.Value.NoteBounds;
                    int bright = Light(painted, wave.Left + wave.Width / 4, row), soft = Light(painted, wave.Left + wave.Width * 2 / 3, row), zero = Light(painted, wave.Left + wave.Width * 15 / 16, row);
                    Check(bright > soft && soft > AppTheme.Surface.R && zero == AppTheme.Surface.R, "rendered hover brightens strong note energy more than quiet energy and preserves silent time columns");
                    Check(Light(painted, wave.Left + wave.Width / 4, rightTarget!.Value.NoteBounds) == AppTheme.Surface.R
                        && painted.GetPixel(wave.Left + wave.Width / 4, row.Bottom + 4).ToArgb() == AppTheme.Surface.ToArgb(),
                        "hover rendering leaves the other stereo lane and adjacent semitones unchanged");
                }
                var exactBounds = new Rectangle(wave.Left, wave.Top, wave.Width, 486);
                var exactLane = new Rectangle(wave.Left, wave.Top, wave.Width, 241);
                var exactPoint = NotePoint(exactLane, 69); Pointer(exactPoint);
                using (var exact = Paint(exactBounds, 0))
                {
                    var strongCenter = exact.GetPixel(wave.Left + wave.Width / 4, exactPoint.Y);
                    var quietCenter = exact.GetPixel(wave.Left + wave.Width * 2 / 3, exactPoint.Y);
                    Check(strongCenter.R == strongCenter.G && strongCenter.G == strongCenter.B
                        && quietCenter.R == quietCenter.G && quietCenter.G == quietCenter.B && strongCenter.R > quietCenter.R,
                        "actual integer note-center pixels are grayscale and preserve the difference between strong and quiet energy");
                }
                Pointer(point);
                track.Gain = 0; using (var muted = Paint(wave, 0))
                    Check(Light(muted, wave.Left + wave.Width / 4, target!.Value.NoteBounds) == AppTheme.Surface.R, "a zero-volume view does not invent spectral highlight intensity");
                track.Gain = 10; using (var boosted = Paint(wave, 0))
                    Check(Light(boosted, wave.Left + wave.Width * 15 / 16, target!.Value.NoteBounds) == AppTheme.Surface.R, "gain boosts do not brighten a cached zero-energy noise floor");
                track.Gain = 1;
                Check(ReferenceEquals(Read(0), leftData) && ReferenceEquals(Read(1), rightData) && (long)cacheType.GetProperty("Revision")!.GetValue(cache)! == revision,
                    "hover movement and display gain reuse the existing left/right FFT data without cache rebuilds");

                var sharp = NotePoint(lanes[0], 61);
                var natural = NotePoint(lanes[0], 60);
                var normalTicks = canvas.GetNoteTicks(1, 0);
                Check(normalTicks.Any(tick => tick.MidiNote == 60) && normalTicks.All(tick => tick.MidiNote != 61),
                    "the hover fixture uses C sharp4, a note omitted from the ordinary axis labels");
                track.Gain = 0;
                Pointer(natural);
                using var naturalLabel = PaintWithLabels(wave, 1, drawBase: true);
                Pointer(sharp);
                using var sharpLabel = PaintWithLabels(wave, 1, drawBase: true);
                int naturalLeft = LabelInkWidth(naturalLabel, lanes[0], natural.Y, false);
                int naturalRight = LabelInkWidth(naturalLabel, lanes[0], natural.Y, true);
                Check(naturalLeft > 0 && naturalRight > 0
                    && LabelInkWidth(sharpLabel, lanes[0], sharp.Y, false) > naturalLeft
                    && LabelInkWidth(sharpLabel, lanes[0], sharp.Y, true) > naturalRight,
                    "both axes paint the hovered accidental as a distinct exact label even when the row is silent");
                track.Gain = 1;

                var cacheField = typeof(WaveCanvas).GetField("spectralCache", Private)!;
                cacheField.SetValue(canvas, null);
                try
                {
                    using var pending = PaintWithLabels(wave, 1);
                    Check(LabelInkWidth(pending, lanes[0], sharp.Y, false) > 0 && LabelInkWidth(pending, lanes[0], sharp.Y, true) > 0,
                        "both note labels appear while the spectrogram data is pending");
                    Pointer(new(wave.Left, wave.Top - 1));
                    using var outside = PaintWithLabels(wave, 1);
                    Check(LabelInkWidth(outside, lanes[0], sharp.Y, false) == 0 && LabelInkWidth(outside, lanes[0], sharp.Y, true) == 0,
                        "leaving the spectrogram clears both hover labels");
                }
                finally { cacheField.SetValue(canvas, cache); }

                canvas.Slots[0].SplitChannels = false;
                Check(canvas.GetSpectralNoteHover(1, NotePoint(wave, 69))?.Channel == -1, "combined-channel display uses its combined spectral lane");
                canvas.Slots[0].SplitChannels = true;
                typeof(WaveCanvas).GetField("nestedPointerOverPrecision", Private)!.SetValue(canvas, true);
                var nested = canvas.GetNestedPrecisionBounds(); var nestedPoint = new Point(nested.Left + nested.Width / 3, nested.Bottom - 4);
                var nestedTarget = canvas.GetSpectralNoteHover(0, nestedPoint, true);
                Check(canvas.NestedPrecisionVisible && nestedTarget.HasValue && nestedTarget.Value.Channel == 1 && nested.Contains(nestedTarget.Value.NoteBounds),
                    "the nested precision spectrum resolves its own right lane and note geometry");
                Check(canvas.GetSpectralNoteHover(0, nestedPoint) == null, "the underlying precision view skips hover where the nested spectrum owns the pointer");
                var compactNested = new Rectangle(nested.Left, nested.Top, nested.Width, (WaveCanvas.MinPaneHeight - 8) / 3);
                var compactLanes = ((ValueTuple<Rectangle, int>[])typeof(WaveCanvas).GetMethod("WaveLanes", Private)!.Invoke(canvas, [0, compactNested])!);
                var compactLane = compactLanes[0].Item1;
                var compactPoint = NotePoint(compactLane, SpectrogramData.HighestNote);
                Pointer(compactPoint);
                using (var compactLabels = PaintWithLabels(compactNested, 0, nested: true, drawBase: true))
                    Check(LabelInkWidth(compactLabels, compactLane, compactPoint.Y, false) > 0
                        && LabelInkWidth(compactLabels, compactLane, compactPoint.Y, true) > 0,
                        "the smallest split nested spectrogram paints both edge note labels within the hovered lane");
                typeof(WaveCanvas).GetField("nestedPointerOverPrecision", Private)!.SetValue(canvas, false);
                var topLane = canvas.GetChannelLaneBounds(0)[1]; var topTarget = canvas.GetSpectralNoteHover(0, NotePoint(topLane, 81));
                Check(topTarget.HasValue && topTarget.Value.Channel == 1 && topTarget.Value.MidiNote == 81, "base precision uses the same independent stereo note mapping");
                canvas.Slots[0].ViewMode = AudioViewMode.Waveform;
                Check(canvas.GetSpectralNoteHover(1, point) == null, "waveform mode does not draw spectral note hover");
                Check(track.Samples.SequenceEqual(samples) && track.Patches.Length == 0 && track.Fades.Count == 0, "spectral hover remains a display-only operation");
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("Spectral hover fixture deadline exceeded.");
        if (failure != null) throw new Exception("Spectral hover fixture failed.", failure);
        Console.WriteLine($"{checks} spectral hover checks passed. No native window or audio hardware was used.");
    }
}
