using System.Reflection;
using OrchardSound;

public static class RhythmUiChecks
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new EditorForm([]); _ = form.Handle;
                var canvas = (WaveCanvas)typeof(EditorForm).GetField("canvas", Private)!.GetValue(form)!;
                var status = (Label)typeof(EditorForm).GetField("status", Private)!.GetValue(form)!;
                var beats = (CheckBox)typeof(EditorForm).GetField("beats", Private)!.GetValue(form)!;
                var kicks = (CheckBox)typeof(EditorForm).GetField("kicks", Private)!.GetValue(form)!;
                var snap = (CheckBox)typeof(EditorForm).GetField("snap", Private)!.GetValue(form)!;
                var controls = (List<Control>)typeof(EditorForm).GetField("trackControls", Private)!.GetValue(form)!;
                int checks = 0;
                void Check(bool condition, string name)
                { if (!condition) throw new Exception("FAILED RHYTHM UI: " + name); checks++; Console.WriteLine("PASS RHYTHM UI " + name); }
                void Invoke(string name) => typeof(EditorForm).GetMethod(name, Private)!.Invoke(form, null);
                IEnumerable<Control> All(Control parent) => parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(All(child)));
                Panel Header(AudioTrack track) => controls.OfType<Panel>().Single(panel => panel.Tag is ChannelSlot slot && ReferenceEquals(slot.Track, track));
                Label Tempo(AudioTrack track) => All(Header(track)).OfType<Label>().Single(label => label.AccessibleName == "Estimated BPM");
                // The test form stays hidden. Check each child control's own visibility state.
                bool WantsVisible(Control control) => (bool)typeof(Control).GetProperty("DesiredVisibility", Private)!.GetValue(control)!;
                bool Fits(Label label)
                {
                    var parent = label.Parent!;
                    var text = TextRenderer.MeasureText(label.Text, label.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                    return parent.ClientRectangle.Contains(label.Bounds) && label.ClientSize.Width >= text.Width + label.Padding.Horizontal
                        && label.ClientSize.Height >= text.Height + label.Padding.Vertical;
                }
                bool WithinPane(AudioTrack track, int index, float scale)
                {
                    var panel = Header(track); var label = Tempo(track); var pane = canvas.GetPaneBounds(index + 1);
                    var paneDevice = new Rectangle((int)(pane.X * scale), (int)(pane.Y * scale), (int)(pane.Width * scale), (int)(pane.Height * scale));
                    var labelDevice = new Rectangle(panel.Left + label.Left, panel.Top + label.Top, label.Width, label.Height);
                    var slider = panel.Controls.OfType<TrackBar>().Single();
                    return paneDevice.Contains(labelDevice) && !label.Bounds.IntersectsWith(slider.Bounds);
                }
                var samples = new float[64];
                var known = new AudioTrack { Path = "Known tempo.wav", Samples = samples, Peaks = new PeakPyramid(samples), Rhythm = new RhythmAnalysis(123, [1, 20], []) };
                var unknown = new AudioTrack { Path = "No fixed tempo.wav", Samples = samples, Peaks = new PeakPyramid(samples), Rhythm = new RhythmAnalysis(null, [], []) };
                canvas.Slots[0].Track = known; canvas.Slots.Add(new() { Track = unknown });
                Invoke("RebuildTrackControls");
                Check(Tempo(known).Parent == Header(known) && Tempo(unknown).Parent == Header(unknown), "BPM assertions use the real nested track header labels");
                Check(WantsVisible(Tempo(known)) && WantsVisible(Tempo(unknown)) && Tempo(known).Text == "BPM: off (B)" && Tempo(unknown).Text == "BPM: off (B)", "disabled detection still shows where to enable BPM on every loaded row");

                foreach (var aid in new[] { beats, kicks, snap })
                {
                    aid.Checked = true;
                    Check(WantsVisible(Tempo(known)) && Tempo(known).Text.Contains("123") && Tempo(known).Text.Contains("BPM") && WantsVisible(Tempo(unknown)) && Tempo(unknown).Text == "No steady tempo",
                        aid.AccessibleName + " exposes the cached BPM or an explicit unavailable result");
                    Check(known.Rhythm?.Bpm == 123 && unknown.Rhythm != null, "seeded rhythm results avoid running analysis when a detection aid is toggled");
                    aid.Checked = false;
                }
                Check(Tempo(known).Text == "BPM: off (B)" && WantsVisible(Tempo(known)), "turning all detection aids off retains visible BPM guidance");

                beats.Checked = true;
                known.Rhythm = null; Invoke("UpdateRhythmLabels");
                Check(WantsVisible(Tempo(known)) && Tempo(known).Text == "Analyzing BPM...", "pending analysis has a visible BPM-specific state");
                Check(known.Rhythm == null, "updating pending text does not start background analysis");
                known.Rhythm = new RhythmAnalysis(null, [], []); Invoke("UpdateRhythmLabels");
                Check(WantsVisible(Tempo(known)) && Tempo(known).Text == "No steady tempo", "analysis without a tempo keeps a visible result");
                known.Rhythm = new RhythmAnalysis(123, [1, 20], []); Invoke("UpdateRhythmLabels");
                Check(Tempo(known).Text.Contains("123") && Tempo(known).Text.Contains("BPM"), "completed tempo replaces the pending or unavailable state");

                canvas.SetViewportHeight((int)(900 * canvas.DpiScale)); canvas.FitHeight(true); Invoke("LayoutTrackControls");
                Check(Fits(Tempo(known)) && Fits(Tempo(unknown)) && WithinPane(known, 0, canvas.DpiScale) && WithinPane(unknown, 1, canvas.DpiScale),
                    "BPM text fits its actual headers and avoids the volume sliders at the current DPI");
                canvas.SetViewportHeight((int)((3 * WaveCanvas.MinPaneHeight + 2 * WaveCanvas.SplitterHeight + 16) * canvas.DpiScale));
                canvas.FitHeight(true); Invoke("LayoutTrackControls");
                Check(canvas.GetPaneBounds(1).Height == WaveCanvas.MinPaneHeight && canvas.GetPaneBounds(2).Height == WaveCanvas.MinPaneHeight
                    && WithinPane(known, 0, canvas.DpiScale) && WithinPane(unknown, 1, canvas.DpiScale), "minimum-height multitrack panes retain both BPM labels inside their own rows");

                foreach (int dpi in new[] { 96, 144, 192 })
                {
                    Invoke("RebuildTrackControls");
                    var panel = Header(known); var label = Tempo(known); var originalFont = label.Font;
                    float ratio = dpi / (float)form.DeviceDpi, scale = dpi / 96f;
                    panel.Scale(new SizeF(ratio, ratio));
                    var pane = canvas.GetPaneBounds(1);
                    panel.Location = new((int)(18 * scale), (int)((pane.Top + 12) * scale));
                    using var scaledFont = new Font(originalFont.FontFamily, originalFont.SizeInPoints * ratio, originalFont.Style, GraphicsUnit.Point);
                    label.Font = scaledFont;
                    foreach (string text in new[] { "BPM: off (B)", "Analyzing BPM...", "123 BPM", "No steady tempo" })
                    {
                        label.Text = text;
                        Check(Fits(label) && WithinPane(known, 0, scale), $"real track BPM label fits '{text}' at simulated {dpi} DPI scale");
                    }
                    label.Font = originalFont;
                }
                Invoke("RebuildTrackControls");
                status.Text = "Rhythm analysis timed out. Toggle detection to retry.";
                var measuredStatus = TextRenderer.MeasureText(status.Text, status.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                Check(status.Height >= measuredStatus.Height + status.Padding.Vertical && status.TextAlign == ContentAlignment.MiddleLeft,
                    "status line reserves the scaled font height and vertically centers rhythm feedback");
                Check(known.Rhythm?.Bpm == 123 && unknown.Rhythm?.Bpm == null, "UI state and layout checks preserve the supplied analysis results");
                known.Rhythm = new(null, [], []) { Status = RhythmAnalysisStatus.Silence }; Invoke("UpdateRhythmLabels");
                Check(Tempo(known).Text == "BPM: silence" && Tempo(known).AccessibleDescription!.Contains("energy"), "silent audio has a distinct result and an accessible explanation");
                known.Rhythm = new(null, [], []) { Status = RhythmAnalysisStatus.TooShort }; Invoke("UpdateRhythmLabels");
                Check(Tempo(known).Text == "BPM: too short" && Tempo(known).AccessibleDescription!.Contains("four attacks"), "short audio explains what is needed instead of displaying a generic unavailable result");
                known.Rhythm = new(null, [], []); Invoke("UpdateRhythmLabels");
                Check(Tempo(known).Text == "No steady tempo" && Tempo(known).Cursor == Cursors.Hand, "a valid no-tempo result offers direct retry on its BPM label");

                const int rate = AudioTrack.Rate;
                var mappedSamples = new float[(int)(rate * 3.2) * 2];
                foreach (double start in new[] { 1.1, 1.6, 2.1, 2.6 })
                    for (int frame = 0; frame < rate * .15; frame++)
                    {
                        double seconds = frame / (double)rate; float value = (float)(.65 * Math.Min(1, seconds / .002) * Math.Exp(-seconds / .035) * Math.Cos(2 * Math.PI * 60 * seconds));
                        int at = ((int)Math.Round(start * rate) + frame) * 2; mappedSamples[at] = value; mappedSamples[at + 1] = -value;
                    }
                var mappedTrack = new AudioTrack { Path = "Mapped BPM fixture.wav", Samples = mappedSamples, Peaks = new(mappedSamples), Gain = 10,
                    Segments = [new(rate, (long)(rate * 2.2))], Fades = [new(0, (long)(rate * 2.2), true, 0, 0)] };
                var mappedRhythm = (RhythmAnalysis)typeof(EditorForm).GetMethod("AnalyzeVisibleRhythm", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [mappedTrack.Snapshot(), CancellationToken.None])!;
                mappedTrack.Rhythm = mappedRhythm;
                Check(mappedRhythm.Bpm.HasValue && Math.Abs(mappedRhythm.Bpm.Value - 120) < 1 && mappedRhythm.UsedFallback
                    && mappedRhythm.Confidence > 0 && mappedRhythm.Status == RhythmAnalysisStatus.Detected, "visible-to-source analysis preserves the fallback diagnostic and evidence metadata through a cut");
                Check(mappedRhythm.BeatFrames.All(frame => frame >= rate) && mappedTrack.VisibleRhythmFrames(false).Any(frame => Math.Abs(frame / (double)rate - .1) < .025)
                    && mappedTrack.Gain == 10 && mappedTrack.Patches.Length == 0, "beat markers map back to retained48kHz source frames independently of playback gain and fades");

                void Until(Func<bool> completed)
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    while (!completed()) { if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Rhythm diagnostic fixture deadline exceeded."); Application.DoEvents(); Thread.Sleep(1); }
                }
                void RunRefresh()
                {
                    var task = (Task)typeof(EditorForm).GetMethod("RefreshRhythmAsync", Private)!.Invoke(form, null)!;
                    Until(() => task.IsCompleted); task.GetAwaiter().GetResult();
                }
                var analyzer = typeof(EditorForm).GetField("rhythmAnalyzer", Private)!; var originalAnalyzer = analyzer.GetValue(form);
                var timeout = typeof(EditorForm).GetField("rhythmAnalysisTimeout", Private)!;
                analyzer.SetValue(form, (Func<TrackState, CancellationToken, RhythmAnalysis>)((state, token) =>
                    ReferenceEquals(state.Source, known) ? throw new IOException("Synthetic rhythm failure") : new(129, [], [])));
                known.Rhythm = null; unknown.Rhythm = null; RunRefresh();
                Check(Tempo(known).Text == "BPM: error" && Tempo(known).AccessibleDescription!.Contains("Synthetic rhythm failure")
                    && status.Text.Contains("Synthetic rhythm failure") && known.Rhythm == null, "an actual analysis exception remains visible with its real error detail");
                Check(Tempo(unknown).Text == "129 BPM" && unknown.Rhythm?.Bpm == 129, "one failed row does not prevent another row from completing analysis");
                int retries = 0;
                analyzer.SetValue(form, (Func<TrackState, CancellationToken, RhythmAnalysis>)((state, token) => { Interlocked.Increment(ref retries); return new(121, [], []) { UsedFallback = true, Confidence = .7 }; }));
                typeof(Control).GetMethod("OnClick", Private)!.Invoke(Tempo(known), [EventArgs.Empty]);
                Until(() => known.Rhythm?.Bpm == 121 && status.Text.Contains("121"));
                Check(retries == 1 && Tempo(known).Text == "121 BPM" && !status.Text.Contains("Synthetic rhythm failure"), "clicking an error label retries once and clears the stale failure after success");
                analyzer.SetValue(form, (Func<TrackState, CancellationToken, RhythmAnalysis>)((state, token) =>
                { token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); return new(null, [], []); }));
                timeout.SetValue(form, TimeSpan.FromMilliseconds(30)); known.Rhythm = null; RunRefresh();
                Check(Tempo(known).Text == "BPM: timed out" && status.Text.Contains("timed out") && known.Rhythm == null,
                    "the bounded deadline has its own timeout state rather than a false no-tempo result");
                timeout.SetValue(form, TimeSpan.FromMinutes(3)); analyzer.SetValue(form, originalAnalyzer);
                known.Rhythm = new(123, [], []); Invoke("UpdateRhythmLabels");
                Check(Tempo(known).Text == "123 BPM", "a valid completed result supersedes a stale failure state");
                Console.WriteLine($"{checks} rhythm UI checks passed.");
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("Rhythm UI fixture deadline exceeded.");
        if (failure != null) throw new Exception("Rhythm UI fixture failed", failure);
    }
}
