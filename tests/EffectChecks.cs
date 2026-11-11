using OrchardSound;

public static class EffectChecks
{
    static IEnumerable<Control> All(Control root) => root.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(All(child)));
    public static void Run()
    {
        const int rate = 48000; int checks = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception("FAILED EFFECT: " + name); checks++; Console.WriteLine("PASS EFFECT " + name); }
        static bool Near(float a, float b, float tolerance = .0001f) => Math.Abs(a - b) <= tolerance;
        static float[] Tone(int frames, double leftHz, double rightHz = 0)
        {
            var data = new float[frames * 2];
            for (int f = 0; f < frames; f++) { data[f * 2] = .2f * (float)Math.Sin(2 * Math.PI * leftHz * f / rate); data[f * 2 + 1] = .2f * (float)Math.Sin(2 * Math.PI * rightHz * f / rate); }
            return data;
        }
        static double Rms(float[] samples, int channel, int from, int to)
        { double sum = 0; for (int f = from; f < to; f++) sum += samples[f * 2 + channel] * samples[f * 2 + channel]; return Math.Sqrt(sum / (to - from)); }
        static double Frequency(float[] samples, int channel, int from, int to)
        { int crossings = 0; for (int f = from + 1; f < to; f++) if (samples[(f - 1) * 2 + channel] <= 0 && samples[f * 2 + channel] > 0) crossings++; return crossings * rate / (double)(to - from); }

        float[] original = [.1f, -.2f, .05f, -.1f, 0, 0];
        var maximized = AudioEffects.Process(original, rate, new(EffectKind.Maximize));
        Check(Near(maximized.Max(v => Math.Abs(v)), .98f) && Near(maximized[0] / maximized[1], -.5f), "maximize reaches its target peak and preserves stereo ratios");
        Check(original.SequenceEqual(new[] { .1f, -.2f, .05f, -.1f, 0, 0 }), "effects preserve the supplied source buffer");
        float[] scopeSamples = [.5f, -.25f, .1f, -.05f, .2f, -.1f, .4f, -.2f];
        float[] otherScopeSamples = [.25f, -.125f, .125f, -.0625f];
        var scopeTrack = new AudioTrack { Path = "Maximize scope.wav", Samples = scopeSamples, Peaks = new PeakPyramid(scopeSamples), Offset = 10 };
        var otherScopeTrack = new AudioTrack { Path = "Outside loop.wav", Samples = otherScopeSamples, Peaks = new PeakPyramid(otherScopeSamples), Offset = 100 };
        var selectedMaximize = EffectPreview.PrepareAsync([scopeTrack, otherScopeTrack], [scopeTrack, otherScopeTrack], 11, 13, new(EffectKind.Maximize) { TargetPeak = 1 }).GetAwaiter().GetResult();
        var selectedChange = selectedMaximize.Changes.Single();
        Check(new EffectSettings(EffectKind.Maximize).SelectionOnly && selectedChange.VisibleStart == 1 && selectedChange.Samples.Length == 4 && Near(selectedChange.Samples[0], .5f) && Near(selectedChange.Samples[2], 1), "programmatic maximize retains selection-only bounds and derives gain from that range");
        Check(selectedMaximize.PlaybackTracks[0].Source.ReadVisibleSamples(0, 1).SequenceEqual(scopeSamples.Take(2)) && selectedMaximize.PlaybackTracks[0].Source.ReadVisibleSamples(3, 4).SequenceEqual(scopeSamples.Skip(6)), "selection-only maximize preserves audio outside the selected interval");
        var wholeMaximize = EffectPreview.PrepareAsync([scopeTrack, otherScopeTrack], [scopeTrack, otherScopeTrack], 11, 13, new(EffectKind.Maximize) { TargetPeak = 1, SelectionOnly = false }).GetAwaiter().GetResult();
        var wholeChange = wholeMaximize.Changes.Single(change => ReferenceEquals(change.Track, scopeTrack));
        var outsideChange = wholeMaximize.Changes.Single(change => ReferenceEquals(change.Track, otherScopeTrack));
        Check(wholeChange.VisibleStart == 0 && wholeChange.Samples.Length == scopeSamples.Length && Near(wholeChange.Samples[0], 1) && Near(wholeChange.Samples[2], .2f), "whole-track maximize derives one consistent gain from the entire visible track");
        Check(outsideChange.VisibleStart == 0 && outsideChange.Samples.Length == otherScopeSamples.Length && Near(outsideChange.Samples[0], 1), "whole-track maximize includes targeted tracks outside the playback selection and normalizes them independently");
        var selectedLoop = new MixProvider(wholeMaximize.PlaybackTracks, 11, 13, true); var loopSamples = new float[8]; selectedLoop.Read(loopSamples, 0, loopSamples.Length);
        Check(loopSamples.Zip(new[] { .2f, -.1f, .4f, -.2f, .2f, -.1f, .4f, -.2f }).All(pair => Near(pair.First, pair.Second)), "whole-track maximize preview still plays only the selected loop");
        Check(scopeTrack.Patches.Length == 0 && otherScopeTrack.Patches.Length == 0 && scopeSamples.SequenceEqual(new[] { .5f, -.25f, .1f, -.05f, .2f, -.1f, .4f, -.2f }), "maximize previews leave the original tracks unchanged until Apply");
        foreach (var change in wholeMaximize.Changes) change.Track.WriteVisibleSamples(change.VisibleStart, change.Samples);
        Check(Near(scopeTrack.ReadVisibleSamples(0, scopeTrack.Frames).Max(sample => Math.Abs(sample)), 1) && Near(otherScopeTrack.ReadVisibleSamples(0, otherScopeTrack.Frames).Max(sample => Math.Abs(sample)), 1), "all prepared whole-track maximize changes can be committed together");
        float[] cutScopeSamples = [1, 0, .1f, 0, .2f, 0];
        var cutScopeTrack = new AudioTrack { Path = "Cut maximize.wav", Samples = cutScopeSamples, Peaks = new PeakPyramid(cutScopeSamples), Segments = [new(1, 2)] };
        var cutMaximize = EffectPreview.PrepareAsync([cutScopeTrack], [cutScopeTrack], 0, 1, new(EffectKind.Maximize) { TargetPeak = 1, SelectionOnly = false }).GetAwaiter().GetResult();
        Check(cutMaximize.Changes.Single().Samples.Length == 4 && Near(cutMaximize.Changes.Single().Samples[0], .5f) && Near(cutMaximize.Changes.Single().Samples[2], 1), "whole-track maximize uses retained visible audio rather than a peak removed by an earlier cut");
        var reversed = AudioEffects.Process(original, rate, new(EffectKind.Reverse));
        Check(reversed.SequenceEqual(new[] { 0f, 0f, .05f, -.1f, .1f, -.2f }), "reverse changes frame order without swapping stereo channels");
        Check(AudioEffects.Process(reversed, rate, new(EffectKind.Reverse)).SequenceEqual(original), "reversing twice restores the original audio");
        float[] constant = [.5f, -.25f, .5f, -.25f, .5f, -.25f];
        var fadeIn = AudioEffects.Process(constant, rate, new(EffectKind.FadeIn));
        Check(fadeIn.SequenceEqual(new[] { 0f, 0f, .25f, -.125f, .5f, -.25f }), "fade in ramps the selected stereo samples from silence to full level");
        var fadeOut = AudioEffects.Process(constant, rate, new(EffectKind.FadeOut) { FadeStrength = .5f });
        Check(fadeOut.SequenceEqual(new[] { .5f, -.25f, .375f, -.1875f, .25f, -.125f }), "fade strength blends the fade with the original level");
        Check(AudioEffects.Process(constant, rate, new(EffectKind.FadeIn) { FadeStrength = 0 }).SequenceEqual(constant), "zero-strength fade is an exact bypass");

        var impulse = new float[rate * 2]; impulse[0] = 1;
        var delay = AudioEffects.Process(impulse, rate, new(EffectKind.Delay) { DelaySeconds = .1, Wet = .3f, Feedback = .35f });
        Check(Near(delay[0], .7f) && Near(delay[4800 * 2], .3f) && Near(delay[9600 * 2], .105f), "delay places and decays repeated echoes at the requested interval");
        Check(delay.Where((_, i) => i % 2 == 1).All(v => v == 0), "delay does not leak a mono impulse into the silent channel");
        Check(AudioEffects.Process(impulse, rate, new(EffectKind.Delay) { Wet = 0 }).SequenceEqual(impulse), "zero-wet delay preserves the sound exactly");
        var reverb = AudioEffects.Process(impulse, rate, new(EffectKind.Reverb) { Wet = .4f });
        Check(reverb.Length == impulse.Length && Rms(reverb, 0, 960, 9600) > 0 && Rms(reverb, 0, 9600, 19200) > Rms(reverb, 0, 38400, 48000), "room reverb adds a decaying tail inside the selected duration");
        Check(AudioEffects.Process(impulse, rate, new(EffectKind.Reverb) { Wet = 0 }).SequenceEqual(impulse), "zero-wet reverb preserves the sound exactly");

        var separated = Tone(rate, 100, 6000);
        var lowPass = AudioEffects.Process(separated, rate, new(EffectKind.Filter) { StartHz = 1000, EndHz = 1000 });
        Check(Rms(lowPass, 0, 4800, rate) > .12 && Rms(lowPass, 1, 4800, rate) < .006, "low-pass filter retains low tones and removes high tones");
        var highPass = AudioEffects.Process(separated, rate, new(EffectKind.Filter) { StartHz = 1000, EndHz = 1000, HighPass = true });
        Check(Rms(highPass, 0, 4800, rate) < .006 && Rms(highPass, 1, 4800, rate) > .12, "high-pass filter retains high tones and removes low tones");
        var sweep = AudioEffects.Process(Tone(rate, 4000), rate, new(EffectKind.Filter) { StartHz = 100, EndHz = 10000 });
        Check(Rms(sweep, 0, 36000, 46000) > Rms(sweep, 0, 1000, 11000) * 3 && Rms(sweep, 0, 36000, 46000) > .1, "filter sweep moves from a rejected high tone to an audible end frequency");

        var tone = Tone(rate, 440);
        Check(AudioEffects.Process(tone, rate, new(EffectKind.PitchShift)).SequenceEqual(tone), "zero-semitone pitch preserves every original sample");
        var pitched = AudioEffects.Process(tone, rate, new(EffectKind.PitchShift) { StartSemitones = 12, EndSemitones = 12 });
        double shiftedHz = Frequency(pitched, 0, 12000, 36000);
        Check(pitched.Length == tone.Length && shiftedHz > 850 && shiftedHz < 910, $"pitch raises a tone by an octave while preserving duration (actual {shiftedHz:0.0} Hz)");
        Check(pitched.Where((_, i) => i % 2 == 1).All(v => v == 0), "pitch processing preserves a silent stereo channel");
        var pitchSweep = AudioEffects.Process(tone, rate, new(EffectKind.PitchShift) { StartSemitones = 0, EndSemitones = 12 });
        double early = Frequency(pitchSweep, 0, 9600, 19200), late = Frequency(pitchSweep, 0, 33600, 43200);
        Check(early > 470 && early < 610 && late > early * 1.25 && late < 860, $"pitch sweep moves continuously toward its end pitch ({early:0.0} to {late:0.0} Hz)");
        foreach (EffectKind kind in Enum.GetValues<EffectKind>().Where(kind => kind is not EffectKind.SeamlessRepeat))
        {
            var silent = AudioEffects.Process(new float[9600], rate, new(kind));
            Check(silent.All(v => v == 0), kind + " handles silence without invalid audio");
        }
        float[] voices = [.1f, -.1f, .2f, -.2f];
        var stacked = AudioEffects.Process(voices, rate, new(EffectKind.Multiply) { Multiply = new() { CopyCount = 3 } });
        Check(stacked.Length == voices.Length && stacked.Zip(voices).All(pair => Near(pair.First, pair.Second * 3))
            && voices.SequenceEqual(new[] { .1f, -.1f, .2f, -.2f }), "multiply stacks simultaneous voices at the original duration without modifying supplied samples");
        bool seamlessNeedsTimeline = false;
        try { AudioEffects.Process(new float[2], rate, new(EffectKind.SeamlessRepeat)); } catch (NotSupportedException) { seamlessNeedsTimeline = true; }
        Check(seamlessNeedsTimeline, "seamless repeat requires the dedicated timeline preview path rather than fixed-length DSP processing");
        bool invalidPitch = false; try { AudioEffects.Process(tone, rate, new(EffectKind.PitchShift) { StartSemitones = 25 }); } catch (ArgumentException) { invalidPitch = true; }
        Check(invalidPitch, "unsupported pitch is rejected before modifying a sound");
        bool invalidFilter = false; try { AudioEffects.Process(tone, rate, new(EffectKind.Filter) { EndHz = 24000 }); } catch (ArgumentException) { invalidFilter = true; }
        Check(invalidFilter, "filter frequencies must remain below Nyquist");
        bool invalidDelay = false; try { AudioEffects.Process(tone, rate, new(EffectKind.Delay) { Feedback = 1 }); } catch (ArgumentException) { invalidDelay = true; }
        Check(invalidDelay, "unstable delay feedback is rejected");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); bool cancelled = false;
        try { AudioEffects.Process(tone, rate, new(EffectKind.PitchShift) { EndSemitones = 12 }, cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && tone.SequenceEqual(Tone(rate, 440)), "cancelled processing leaves the original selection intact");
        Console.WriteLine($"{checks} effect checks passed.");
        DialogChecks();
    }

    static void DialogChecks()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                int checks = 0;
                void Check(bool condition, string name) { if (!condition) throw new Exception("FAILED EFFECT TOOL: " + name); checks++; Console.WriteLine("PASS EFFECT TOOL " + name); }
                const System.Reflection.BindingFlags privateInstance = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var visibleState = Enum.Parse(typeof(Control).GetNestedType("States", System.Reflection.BindingFlags.NonPublic)!, "Visible");
                bool LocallyVisible(Control control) => (bool)typeof(Control).GetMethod("GetState", privateInstance)!.Invoke(control, [visibleState])!;
                void Click(Button button) => typeof(Control).GetMethod("OnClick", privateInstance)!.Invoke(button, [EventArgs.Empty]);
                void Closing(EffectDialog dialog) => typeof(Form).GetMethod("OnFormClosing", privateInstance)!.Invoke(dialog, [new FormClosingEventArgs(CloseReason.UserClosing, false)]);
                foreach (EffectKind kind in Enum.GetValues<EffectKind>())
                {
                    using var dialog = new EffectDialog(kind);
                    Check(dialog.Settings.Kind == kind && dialog.PreviewEnabled && dialog.FormBorderStyle == FormBorderStyle.SizableToolWindow && !dialog.ShowInTaskbar, kind + " opens as a movable preview tool with valid settings");
                    Check(All(dialog).OfType<NumericUpDown>().All(n => n.Value >= n.Minimum && n.Value <= n.Maximum), kind + " numeric defaults remain inside their bounds");
                    int settings = 0, applied = 0, cancelled = 0, stop = 0, restart = 0, cursorPlay = 0;
                    dialog.SettingsChanged += () => settings++; dialog.ApplyRequested += () => applied++; dialog.CancelRequested += () => cancelled++;
                    dialog.StopPlaybackRequested += () => stop++; dialog.RestartPlaybackRequested += () => restart++;
                    dialog.CursorPlaybackRequested += () => cursorPlay++;
                    var preview = All(dialog).OfType<CheckBox>().Single(c => c.AccessibleName == "Live preview");
                    preview.Checked = false; Check(settings == 1 && !dialog.PreviewEnabled, kind + " preview toggle requests a live refresh");
                    preview.Checked = true;
                    var numeric = All(dialog).OfType<NumericUpDown>().FirstOrDefault();
                    if (numeric != null)
                    {
                        int before = settings; numeric.Value = numeric.Value < numeric.Maximum ? numeric.Value + 1 : numeric.Value - 1;
                        Check(settings == before + 1, kind + " parameter changes request a live refresh");
                    }
                    var numericInputs = All(dialog).OfType<NumericUpDown>().ToArray();
                    var rangeSliders = All(dialog).OfType<TrackBar>().ToArray();
                    int margin = (int)Math.Round(24 * dialog.DeviceDpi / 96f);
                    Check(rangeSliders.Length == numericInputs.Length && numericInputs.All(input =>
                    {
                        var slider = rangeSliders.SingleOrDefault(candidate => candidate.AccessibleName == input.AccessibleName + " slider");
                        if (slider == null) return false;
                        int width = slider.Parent!.ClientSize.Width, middle = width / 2;
                        bool fullWidth = slider.Left == margin && slider.Right == width - margin;
                        bool leftColumn = slider.Left == margin && Math.Abs(slider.Right - (middle - margin)) <= 1;
                        bool rightColumn = Math.Abs(slider.Left - (middle + margin)) <= 1 && slider.Right == width - margin;
                        return slider.Top >= input.Bottom && slider.Right == input.Right && (fullWidth || leftColumn || rightColumn);
                    }), kind + " numeric ranges have paired sliders underneath with equal margins inside their full row or paired column");
                    foreach (var input in numericInputs)
                    {
                        var slider = rangeSliders.Single(candidate => candidate.AccessibleName == input.AccessibleName + " slider");
                        decimal saved = input.Value; int before = settings;
                        input.Value = input.Minimum;
                        bool typedMinimum = slider.Value == slider.Minimum && settings == before + (saved == input.Minimum ? 0 : 1);
                        before = settings; input.Value = input.Maximum;
                        bool typedMaximum = slider.Value == slider.Maximum && settings == before + 1;
                        before = settings; slider.Value = slider.Minimum;
                        bool draggedMinimum = input.Value == input.Minimum && settings == before + 1;
                        before = settings; slider.Value = slider.Maximum;
                        bool draggedMaximum = input.Value == input.Maximum && settings == before + 1;
                        input.Value = saved;
                        Check(typedMinimum && typedMaximum && draggedMinimum && draggedMaximum, kind + " " + input.AccessibleName + " typing and slider endpoints synchronize with one live refresh per change");
                    }
                    var automationOptions = All(dialog).OfType<CheckBox>().Where(checkbox => checkbox.AccessibleName == "Automate effect parameters").ToArray();
                    var endInputs = numericInputs.Where(input => input.AccessibleName?.StartsWith("End ") == true).ToArray();
                    if (kind is EffectKind.Reverse or EffectKind.Multiply or EffectKind.SeamlessRepeat)
                        Check(automationOptions.Length == 0 && !dialog.Settings.AutomationEnabled, kind + " has no global numeric automation toggle");
                    else
                    {
                        var automation = automationOptions.Single();
                        Check(!automation.Checked && !dialog.Settings.AutomationEnabled && endInputs.Length * 2 == numericInputs.Length && endInputs.All(input => !LocallyVisible(input)), kind + " automation starts off with an end value for every numeric parameter hidden");
                        int before = settings; automation.Checked = true;
                        Check(dialog.Settings.AutomationEnabled && endInputs.All(LocallyVisible) && settings == before + 1, kind + " enabling automation reveals endpoint values with one live refresh");
                        var retainedEnds = endInputs.Select(input => input.Value).ToArray();
                        before = settings; automation.Checked = false;
                        bool disabled = !dialog.Settings.AutomationEnabled && endInputs.All(input => !LocallyVisible(input)) && settings == before + 1;
                        automation.Checked = true;
                        Check(disabled && endInputs.Select(input => input.Value).SequenceEqual(retainedEnds), kind + " automation can be disabled and restored without losing endpoint settings");
                        automation.Checked = false;
                    }
                    var tailOptions = All(dialog).OfType<CheckBox>().Where(checkbox => checkbox.AccessibleName == "Cut tail at selection end").ToArray();
                    if (kind is EffectKind.Delay or EffectKind.Reverb)
                    {
                        var tail = tailOptions.Single();
                        Check(tail.Checked && dialog.Settings.CutTail, kind + " cuts its effect tail at the selected end by default");
                        int before = settings; tail.Checked = false;
                        Check(!dialog.Settings.CutTail && settings == before + 1, kind + " tail checkbox can enable bleed with one live refresh");
                    }
                    else Check(tailOptions.Length == 0, kind + " does not expose an irrelevant tail option");
                    var selectionOptions = All(dialog).OfType<CheckBox>().Where(checkbox => checkbox.AccessibleName == "Apply to selection only").ToArray();
                    if (kind == EffectKind.Maximize)
                    {
                        Check(selectionOptions.Length == 1 && !selectionOptions[0].Checked && !dialog.Settings.SelectionOnly, "maximize defaults to whole-track processing with selection-only unchecked");
                        int before = settings; selectionOptions[0].Checked = true;
                        Check(dialog.Settings.SelectionOnly && settings == before + 1, "enabling selection-only maximize refreshes its live preview");
                        selectionOptions[0].Checked = false;
                        Check(!dialog.Settings.SelectionOnly && settings == before + 2, "disabling selection-only maximize refreshes its whole-track preview");
                    }
                    else Check(selectionOptions.Length == 0 && dialog.Settings.SelectionOnly, kind + " retains selection processing without a maximize-only option");
                    typeof(EffectDialog).GetMethod("ProcessCmdKey", privateInstance)!.Invoke(dialog, [new Message(), Keys.Space]);
                    typeof(EffectDialog).GetMethod("ProcessCmdKey", privateInstance)!.Invoke(dialog, [new Message(), Keys.Shift | Keys.Space]);
                    typeof(EffectDialog).GetMethod("ProcessCmdKey", privateInstance)!.Invoke(dialog, [new Message(), Keys.Control | Keys.Space]);
                    Check(stop == 1 && restart == 1 && cursorPlay == 1, kind + " forwards stop selection-restart and cursor shortcuts without starting another player");
                    var apply = All(dialog).OfType<Button>().Single(b => b.AccessibleName == "Apply effect");
                    Click(apply); Check(applied == 1 && !dialog.IsDisposed && apply.DialogResult == DialogResult.None, kind + " Apply is an explicit commit request without modal auto-close");
                    var cancel = All(dialog).OfType<Button>().Single(b => b.AccessibleName == "Cancel effect");
                    Click(cancel); Closing(dialog);
                    Check(cancelled == 1 && cancel.DialogResult == DialogResult.None, kind + " Cancel and window close request one restoration");
                }
                using (var dialog = new EffectDialog(EffectKind.PitchShift))
                {
                    All(dialog).OfType<CheckBox>().Single(checkbox => checkbox.AccessibleName == "Automate effect parameters").Checked = true;
                    var start = All(dialog).OfType<NumericUpDown>().Single(n => n.AccessibleName == "Start pitch");
                    var end = All(dialog).OfType<NumericUpDown>().Single(n => n.AccessibleName == "End pitch");
                    var units = All(dialog).OfType<ComboBox>().Single();
                    start.Value = 24; end.Value = -24; int changes = 0; dialog.SettingsChanged += () => changes++;
                    units.SelectedIndex = 1;
                    Check(start.Value == 400 && end.Value == 25 && Math.Abs(dialog.Settings.StartSemitones - 24) < .001 && Math.Abs(dialog.Settings.EndSemitones + 24) < .001 && changes == 1, "pitch units convert extreme values once without invalid intermediate settings");
                    units.SelectedIndex = 0; Check(start.Value == 24 && end.Value == -24 && changes == 2, "pitch units restore canonical semitones without changing the effect");
                    var startSlider = All(dialog).OfType<TrackBar>().Single(slider => slider.AccessibleName == "Start pitch slider");
                    start.Value = .01m;
                    Check(startSlider.Value == 2401 && startSlider.Maximum == 4800, "pitch slider retains one-hundredth-semitone resolution after switching units");
                    startSlider.Value = 2399;
                    Check(start.Value == -.01m, "pitch slider maps fractional semitones without integer rounding");
                    units.SelectedIndex = 1;
                    Check(startSlider.Maximum == 37500 && startSlider.Value == (int)decimal.Round((start.Value - 25) * 100), "pitch percent units rebuild the slider range and retain the current pitch");
                    startSlider.Value = 7501;
                    Check(start.Value == 100.01m && Math.Abs(dialog.Settings.StartSemitones - 12 * Math.Log2(1.0001)) < .000001, "pitch percent slider supports hundredths of a percent with the matching audio ratio");
                    units.SelectedIndex = 0;
                    var text = start.Controls.OfType<TextBox>().Single(); text.SelectAll();
                    string typedPitch = (-1.25m).ToString(System.Globalization.CultureInfo.CurrentCulture); bool preserved = true;
                    foreach (char digit in typedPitch)
                    {
                        text.SelectedText = digit.ToString(); string partial = text.Text; int caret = text.SelectionStart;
                        _ = dialog.Settings;
                        preserved &= text.Text == partial && text.SelectionStart == caret;
                    }
                    Check(preserved && text.Text == typedPitch && text.SelectionStart == typedPitch.Length && start.Value == -1.25m && dialog.Settings.StartSemitones == -1.25 && startSlider.Value == 2275, "incremental negative pitch typing survives live settings reads and updates its paired slider");
                }
                using (var dialog = new EffectDialog(EffectKind.Maximize))
                {
                    var target = All(dialog).OfType<NumericUpDown>().Single(input => input.AccessibleName == "Target peak %"); target.Value = 50;
                    var automation = All(dialog).OfType<CheckBox>().Single(checkbox => checkbox.AccessibleName == "Automate effect parameters"); automation.Checked = true;
                    var end = All(dialog).OfType<NumericUpDown>().Single(input => input.AccessibleName == "End target peak %");
                    Check(dialog.Settings.TargetPeak == .5f && end.Value == 50 && dialog.Settings.EndTargetPeak == .5f, "first enabling maximize automation copies the current start target without a sudden change");
                    end.Value = 90; Check(dialog.Settings.EndTargetPeak == .9f && !dialog.Settings.SelectionOnly, "maximize supports an independent end target while retaining whole-track scope");
                }
                using (var dialog = new EffectDialog(EffectKind.FadeOut))
                { All(dialog).OfType<NumericUpDown>().Single(input => input.AccessibleName == "Fade strength %").Value = 25; Check(dialog.Settings.FadeStrength == .25f, "fade amount is an editable percentage"); }
                using (var dialog = new EffectDialog(EffectKind.Delay))
                {
                    var timing = All(dialog).OfType<ComboBox>().Single(); int changes = 0; dialog.SettingsChanged += () => changes++;
                    timing.SelectedItem = "BPM";
                    var start = All(dialog).OfType<NumericUpDown>().Single(input => input.AccessibleName == "Delay BPM");
                    var startSlider = All(dialog).OfType<TrackBar>().Single(slider => slider.AccessibleName == "Delay BPM slider");
                    Check(dialog.Settings.DelayInBpm && Math.Abs(dialog.Settings.DelayBpm - 240) < .00001 && start.Value == 240 && changes == 1, "delay timing converts 250 milliseconds to 240 BPM with one live refresh");
                    start.Value = 120;
                    All(dialog).OfType<CheckBox>().Single(checkbox => checkbox.AccessibleName == "Automate effect parameters").Checked = true;
                    var end = All(dialog).OfType<NumericUpDown>().Single(input => input.AccessibleName == "End delay BPM"); end.Value = 60;
                    Check(dialog.Settings.DelayBpm == 120 && dialog.Settings.EndDelayBpm == 60 && startSlider.Value == 10800, "BPM automation and its slider retain independent start and end tempos");
                    int before = changes; timing.SelectedItem = "Milliseconds";
                    Check(!dialog.Settings.DelayInBpm && Math.Abs(dialog.Settings.DelaySeconds - .5) < .00001 && dialog.Settings.EndDelaySeconds == 1 && dialog.Settings.EndDelayBpm == null && changes == before + 1, "delay timing switches both endpoints back to milliseconds without duplicate refreshes");
                    Check(startSlider.Maximum == 499900 && startSlider.AccessibleName == "Delay ms slider", "delay timing updates the slider's units and full numeric range");
                    var contentPanel = (Panel)start.Parent!; var scrollPanel = (Panel)contentPanel.Parent!;
                    dialog.Height = dialog.MinimumSize.Height; dialog.PerformLayout();
                    Check(scrollPanel.AutoScroll && contentPanel.Height > scrollPanel.ClientSize.Height && dialog.Height < (Screen.PrimaryScreen?.WorkingArea.Height ?? int.MaxValue), "automated delay fields scroll inside a bounded tool window");
                }
                using (var dialog = new EffectDialog(EffectKind.Bitcrush))
                {
                    NumericUpDown Number(string name) => All(dialog).OfType<NumericUpDown>().Single(input => input.AccessibleName == name);
                    Check(dialog.Settings.BitDepth == 8 && dialog.Settings.CrushSampleRate == 12000 && dialog.Settings.Wet == 1 && !dialog.Settings.AutomationEnabled, "bitcrush opens at eight bits, 12 kHz and full mix without automation");
                    Number("Bit depth").Text = "4"; Number("Sample rate Hz").Value = 16000;
                    All(dialog).OfType<CheckBox>().Single(checkbox => checkbox.AccessibleName == "Automate effect parameters").Checked = true;
                    Number("End bit depth").Value = 12; Number("End sample rate Hz").Value = 4000; Number("End mix %").Value = 50;
                    Check(dialog.Settings.BitDepth == 4 && dialog.Settings.EndBitDepth == 12 && dialog.Settings.CrushSampleRate == 16000 && dialog.Settings.EndCrushSampleRate == 4000 && dialog.Settings.EndWet == .5f && All(dialog).OfType<TrackBar>().Single(slider => slider.AccessibleName == "Bit depth slider").Value == 3, "bitcrush numeric and slider controls expose independent automated endpoints");
                }
                using (var dialog = new EffectDialog(EffectKind.Reverse) { Applied = true })
                { int cancelled = 0; dialog.CancelRequested += () => cancelled++; Closing(dialog); Check(cancelled == 0, "closing an applied tool does not cancel its committed change"); }
                using (var dialog = new EffectDialog(EffectKind.Reverse))
                {
                    int cancelled = 0; bool closingVisible = false;
                    dialog.CancelRequested += () => { cancelled++; closingVisible = dialog.IsClosing; };
                    Closing(dialog); Closing(dialog);
                    Check(closingVisible && dialog.IsClosing && cancelled == 1, "title-bar close exposes its closing guard before one cancellation callback");
                }
                Console.WriteLine($"{checks} effect-tool checks passed.");
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("Effect tool fixture deadline exceeded.");
        if (failure != null) throw new Exception("Effect tool fixture failed.", failure);
    }
}
