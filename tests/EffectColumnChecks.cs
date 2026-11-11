using System.Reflection;
using OrchardSound;

public static class EffectColumnChecks
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
                { if (!condition) throw new Exception("FAILED EFFECT COLUMNS: " + name); checks++; Console.WriteLine("PASS EFFECT COLUMNS " + name); }
                IEnumerable<Control> All(Control parent) => parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(All(child)));
                static bool Near(double first, double second) => Math.Abs(first - second) <= 1;
                const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
                var visibleState = Enum.Parse(typeof(Control).GetNestedType("States", BindingFlags.NonPublic)!, "Visible");
                bool LocallyVisible(Control control) => (bool)typeof(Control).GetMethod("GetState", hidden)!.Invoke(control, [visibleState])!;
                void Layout(EffectDialog dialog)
                {
                    dialog.PerformLayout();
                    foreach (Control child in All(dialog).Reverse()) child.PerformLayout();
                    dialog.PerformLayout();
                }
                NumericUpDown Number(EffectDialog dialog, string name) => All(dialog).OfType<NumericUpDown>().Single(input => input.AccessibleName == name);
                TrackBar Slider(EffectDialog dialog, string name) => All(dialog).OfType<TrackBar>().Single(input => input.AccessibleName == name + " slider");
                CheckBox Automation(EffectDialog dialog) => All(dialog).OfType<CheckBox>().Single(input => input.AccessibleName == "Automate effect parameters");
                bool PairGeometry(EffectDialog dialog, string startName, string endName)
                {
                    var start = Number(dialog, startName); var end = Number(dialog, endName);
                    var startSlider = Slider(dialog, startName); var endSlider = Slider(dialog, endName);
                    var content = (Panel)start.Parent!;
                    int middle = content.ClientSize.Width / 2;
                    int margin = (int)Math.Round(24 * dialog.DeviceDpi / 96d), gap = (int)Math.Round(16 * dialog.DeviceDpi / 96d);
                    int captionTop = start.Top + (int)Math.Round(4 * dialog.DeviceDpi / 96d);
                    var startLabel = content.Controls.OfType<Label>().SingleOrDefault(label => label.Left == margin && label.Top == captionTop);
                    var endLabel = content.Controls.OfType<Label>().SingleOrDefault(label => label.Left == middle + margin && label.Top == captionTop);
                    bool valid = ReferenceEquals(start.Parent, end.Parent) && start.Top == end.Top && startSlider.Top == endSlider.Top
                        && startSlider.Top >= start.Bottom && endSlider.Top >= end.Bottom
                        && start.Left >= margin && start.Right == middle - margin
                        && end.Left >= middle + margin && end.Right == content.ClientSize.Width - margin
                        && startSlider.Left == margin && startSlider.Right == middle - margin
                        && endSlider.Left == middle + margin && endSlider.Right == content.ClientSize.Width - margin
                        && startLabel != null && endLabel != null && startLabel.Right <= start.Left - gap && endLabel.Right <= end.Left - gap
                        && LocallyVisible(start) && LocallyVisible(end) && LocallyVisible(startSlider) && LocallyVisible(endSlider);
                    if (!valid) Console.WriteLine($"Column geometry {dialog.Text}/{startName}: DPI {dialog.DeviceDpi}, content {content.Size}, start {start.Bounds}, end {end.Bounds}, sliders {startSlider.Bounds}/{endSlider.Bounds}");
                    return valid;
                }
                bool SharedGeometry(EffectDialog dialog)
                {
                    var content = (Panel)All(dialog).OfType<NumericUpDown>().First().Parent!;
                    int margin = (int)Math.Round(24 * dialog.DeviceDpi / 96d);
                    var footer = All(dialog).OfType<Panel>().Single(panel => panel.AccessibleName == "Dialog actions");
                    var help = content.Controls.OfType<Label>().Single(label => label.Text.StartsWith("Space:"));
                    return footer.Width == dialog.ClientSize.Width && footer.Bottom == dialog.ClientSize.Height
                        && help.Left == margin && help.Right == content.ClientSize.Width - margin
                        && content.Controls.OfType<CheckBox>().All(input => input.Left == margin)
                        && content.Controls.OfType<ComboBox>().All(input => input.Right == content.ClientSize.Width - margin);
                }

                var cases = new (EffectKind Kind, (string Start, string End)[] Pairs)[]
                {
                    (EffectKind.Maximize, [("Target peak %", "End target peak %")]),
                    (EffectKind.FadeIn, [("Fade strength %", "End fade strength %")]),
                    (EffectKind.FadeOut, [("Fade strength %", "End fade strength %")]),
                    (EffectKind.Reverb, [("Room size %", "End room size %"), ("Mix %", "End mix %")]),
                    (EffectKind.Delay, [("Delay ms", "End delay ms"), ("Feedback %", "End feedback %"), ("Mix %", "End mix %")]),
                    (EffectKind.Filter, [("Start frequency Hz", "End frequency Hz")]),
                    (EffectKind.PitchShift, [("Start pitch", "End pitch")]),
                    (EffectKind.Bitcrush, [("Bit depth", "End bit depth"), ("Sample rate Hz", "End sample rate Hz"), ("Mix %", "End mix %")]),
                    (EffectKind.DeClick, [("Sensitivity %", "End sensitivity %"), ("Repair width ms", "End repair width ms")]),
                    (EffectKind.MultibandEq, new[] { "31 Hz", "63 Hz", "125 Hz", "250 Hz", "500 Hz", "1 kHz", "2 kHz", "4 kHz", "8 kHz", "16 kHz" }
                        .Select(band => (band + " gain dB", "End " + band + " gain dB")).ToArray())
                };
                foreach (var item in cases)
                {
                    using var dialog = new EffectDialog(item.Kind);
                    Layout(dialog);
                    int initialWidth = dialog.ClientSize.Width, initialHeight = dialog.ClientSize.Height;
                    var content = (Panel)Number(dialog, item.Pairs[0].Start).Parent!;
                    int initialContentHeight = content.Height;
                    Check(Near(initialWidth, 390 * dialog.DeviceDpi / 96d) && item.Pairs.All(pair => !LocallyVisible(Number(dialog, pair.End))),
                        item.Kind + " opens at one logical column with hidden end values");
                    int changes = 0; dialog.SettingsChanged += () => changes++;
                    Automation(dialog).Checked = true; Layout(dialog);
                    Check(dialog.ClientSize.Width == initialWidth * 2 && dialog.ClientSize.Height == initialHeight
                        && content.Height == initialContentHeight && changes == 1,
                        item.Kind + " automation doubles width without stacking rows or duplicating preview refreshes");
                    foreach (var pair in item.Pairs)
                        Check(PairGeometry(dialog, pair.Start, pair.End), item.Kind + " " + pair.Start + " and its end align in contained left and right columns");
                    Check(SharedGeometry(dialog), item.Kind + " shared controls and footer continue across the full window width");
                    var endInputs = item.Pairs.Select(pair => Number(dialog, pair.End)).ToArray();
                    foreach (var input in endInputs) input.Value = input.Value == input.Minimum ? input.Maximum : input.Minimum;
                    var retained = endInputs.Select(input => input.Value).ToArray(); int before = changes;
                    Automation(dialog).Checked = false; Layout(dialog);
                    bool narrow = dialog.ClientSize.Width == initialWidth && endInputs.All(input => !LocallyVisible(input)) && changes == before + 1;
                    Automation(dialog).Checked = true; Layout(dialog);
                    Check(narrow && dialog.ClientSize.Width == initialWidth * 2 && endInputs.Select(input => input.Value).SequenceEqual(retained)
                        && changes == before + 2,
                        item.Kind + " repeated toggles restore width and retain independent endpoint edits");
                }

                using (var dialog = new EffectDialog(EffectKind.Reverb))
                {
                    var automate = Automation(dialog);
                    int resizedWidth = dialog.ClientSize.Width + (int)Math.Round(80 * dialog.DeviceDpi / 96d);
                    dialog.ClientSize = new(resizedWidth, dialog.ClientSize.Height + 20);
                    int height = dialog.ClientSize.Height; automate.Checked = true; Layout(dialog);
                    Check(dialog.ClientSize.Width == resizedWidth * 2 && dialog.ClientSize.Height == height,
                        "automation doubles the user's resized width while preserving height");
                    int oddWidth = dialog.ClientSize.Width + 1;
                    dialog.ClientSize = new(oddWidth, height); Layout(dialog);
                    Check(PairGeometry(dialog, "Room size %", "End room size %") && PairGeometry(dialog, "Mix %", "End mix %"),
                        "a manually resized odd-width window keeps equal contained parameter columns");
                    automate.Checked = false; int narrowWidth = dialog.ClientSize.Width;
                    dialog.ClientSize = new(narrowWidth, dialog.ClientSize.Height + 7);
                    automate.Checked = true; Layout(dialog);
                    Check(dialog.ClientSize.Width == oddWidth,
                        "odd-pixel wide widths survive a narrow toggle and a height-only resize");
                    int changes = 0; dialog.SettingsChanged += () => changes++;
                    for (int i = 0; i < 8; i++) { automate.Checked = false; automate.Checked = true; }
                    Check(dialog.ClientSize.Width == oddWidth && changes == 16,
                        "repeated automation toggles do not accumulate width rounding or preview refreshes");
                    dialog.ClientSize = new(1, dialog.MinimumSize.Height); Layout(dialog);
                    if (dialog.ClientSize.Width < 780 * dialog.DeviceDpi / 96d - 1)
                        Console.WriteLine($"Minimum width mismatch: client {dialog.ClientSize}, window {dialog.Size}, minimum {dialog.MinimumSize}, DPI {dialog.DeviceDpi}");
                    Check(dialog.ClientSize.Width >= 780 * dialog.DeviceDpi / 96d - 1
                        && PairGeometry(dialog, "Room size %", "End room size %") && SharedGeometry(dialog),
                        "the wide minimum width protects both columns and the fixed footer");
                }

                using (var dialog = new EffectDialog(EffectKind.Multiply))
                {
                    Layout(dialog);
                    Check(Near(dialog.ClientSize.Width, 780 * dialog.DeviceDpi / 96d)
                        && !All(dialog).OfType<CheckBox>().Any(input => input.AccessibleName == "Automate effect parameters"),
                        "Multiply opens wide for its always-visible explicit endpoints");
                    foreach (var pair in new[] { ("Start pitch", "End pitch"), ("Start frequency Hz", "End frequency Hz"), ("Start room size %", "End room size %"), ("Start mix %", "End mix %") })
                        Check(PairGeometry(dialog, pair.Item1, pair.Item2), "Multiply " + pair.Item1 + " aligns left of its end value even while disabled");
                    var content = (Panel)Number(dialog, "Total copies").Parent!;
                    int margin = (int)Math.Round(24 * dialog.DeviceDpi / 96d);
                    var endpointLabels = content.Controls.OfType<Label>().Where(label => label.Text.StartsWith("Start ") || label.Text.StartsWith("End ") || label.Text == "Filter type").ToArray();
                    Check(endpointLabels.Length == 9 && endpointLabels.All(label => label.Enabled && label.ForeColor == AppTheme.Muted)
                        && !Number(dialog, "Start pitch").Enabled && !All(dialog).OfType<ComboBox>().Single().Enabled,
                        "inactive Multiply groups keep readable muted captions while their inputs remain disabled");
                    var pitch = All(dialog).OfType<CheckBox>().Single(input => input.AccessibleName == "Enable pitch variation");
                    pitch.Checked = true;
                    Check(endpointLabels.Where(label => label.Text is "Start pitch" or "End pitch").All(label => label.Enabled && label.ForeColor == AppTheme.Text)
                        && Number(dialog, "Start pitch").Enabled && !Number(dialog, "Start frequency Hz").Enabled,
                        "enabling a Multiply group restores its normal caption color and only its own inputs");
                    pitch.Checked = false;
                    Check(endpointLabels.All(label => label.Enabled && label.ForeColor == AppTheme.Muted) && !Number(dialog, "Start pitch").Enabled,
                        "disabling a Multiply group restores readable muted captions without enabling its inputs");
                    Check(Slider(dialog, "Total copies").Left == margin && Slider(dialog, "Total copies").Right == content.ClientSize.Width - margin
                        && Number(dialog, "Random variation %").Right == content.ClientSize.Width - margin && SharedGeometry(dialog),
                        "Multiply copy count, variation and shared controls span both parameter columns");
                    var explanation = All(dialog).OfType<Label>().Single(label => label.AccessibleName == "Multiply variation explanation");
                    Check(explanation.Text.StartsWith("All copies play together.") && !explanation.Text.Contains("shift", StringComparison.OrdinalIgnoreCase),
                        "Multiply describes simultaneous voices rather than timeline repetition");
                    dialog.Height = dialog.MinimumSize.Height; Layout(dialog);
                    var body = (Panel)content.Parent!;
                    Check(body.AutoScroll && content.Height > body.ClientSize.Height && SharedGeometry(dialog),
                        "the compact Multiply tool scrolls parameter rows while keeping its full-width action footer fixed");
                }
                using (var dialog = new EffectDialog(EffectKind.DeClick))
                {
                    var sensitivity = Number(dialog, "Sensitivity %"); var repair = Number(dialog, "Repair width ms");
                    Check(dialog.Text == "De-click" && dialog.Settings.Sensitivity == 50 && dialog.Settings.EndSensitivity == null
                        && dialog.Settings.RepairMilliseconds == 2 && dialog.Settings.EndRepairMilliseconds == null
                        && sensitivity.Minimum == 1 && sensitivity.Maximum == 100 && sensitivity.DecimalPlaces == 0
                        && repair.Minimum == .1m && repair.Maximum == 20 && repair.DecimalPlaces == 1,
                        "De-click opens with bounded sensitivity and fractional repair width without automation");
                    int changes = 0; dialog.SettingsChanged += () => changes++; sensitivity.Text = "75";
                    Check(dialog.Settings.Sensitivity == 75 && Slider(dialog, "Sensitivity %").Value == 74 && changes == 1,
                        "De-click sensitivity typing updates its synchronized slider and preview once");
                    Automation(dialog).Checked = true;
                    Number(dialog, "End sensitivity %").Value = 25;
                    Slider(dialog, "Repair width ms").Value = 34;
                    Number(dialog, "End repair width ms").Value = .5m;
                    Check(dialog.Settings.AutomationEnabled && dialog.Settings.Sensitivity == 75 && dialog.Settings.EndSensitivity == 25
                        && dialog.Settings.RepairMilliseconds == 3.5 && dialog.Settings.EndRepairMilliseconds == .5
                        && PairGeometry(dialog, "Sensitivity %", "End sensitivity %") && PairGeometry(dialog, "Repair width ms", "End repair width ms"),
                        "De-click retains independent automated endpoints for sensitivity and repair width");
                    Check(All(dialog).OfType<CheckBox>().Single(input => input.AccessibleName == "Live preview").Checked
                        && All(dialog).OfType<ThemeButton>().Single(input => input.AccessibleName == "Apply effect").Role == ButtonRole.Primary
                        && All(dialog).OfType<ThemeButton>().Single(input => input.AccessibleName == "Cancel effect").Role == ButtonRole.Secondary,
                        "De-click retains the standard live preview and Apply/Cancel workflow");
                }
                foreach (EffectKind kind in new[] { EffectKind.Reverse, EffectKind.SeamlessRepeat })
                {
                    using var dialog = new EffectDialog(kind);
                    Check(Near(dialog.ClientSize.Width, 390 * dialog.DeviceDpi / 96d),
                        kind + " keeps its original single-column width");
                }
                Console.WriteLine($"{checks} effect column checks passed at the current process DPI. No visible window or audio was used.");
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("Effect column fixture deadline exceeded.");
        if (failure != null) throw new Exception("Effect column fixture failed", failure);
    }
}
