using System.Globalization;
using System.Reflection;
using OrchardSound;

public static class EqDialogChecks
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
                { if (!condition) throw new Exception("FAILED EQ TOOL: " + name); checks++; Console.WriteLine("PASS EQ TOOL " + name); }
                IEnumerable<Control> All(Control parent) => parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(All(child)));
                const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
                var visibleState = Enum.Parse(typeof(Control).GetNestedType("States", BindingFlags.NonPublic)!, "Visible");
                bool LocallyVisible(Control control) => (bool)typeof(Control).GetMethod("GetState", hidden)!.Invoke(control, [visibleState])!;
                void Click(Button button) => typeof(Control).GetMethod("OnClick", hidden)!.Invoke(button, [EventArgs.Empty]);
                void Layout(EffectDialog dialog)
                {
                    dialog.PerformLayout();
                    foreach (Control child in All(dialog).Reverse()) child.PerformLayout();
                    dialog.PerformLayout();
                }
                string[] names = ["31 Hz gain dB", "63 Hz gain dB", "125 Hz gain dB", "250 Hz gain dB", "500 Hz gain dB", "1 kHz gain dB", "2 kHz gain dB", "4 kHz gain dB", "8 kHz gain dB", "16 kHz gain dB"];
                double[] centers = [31.25, 62.5, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];
                using var dialog = new EffectDialog(EffectKind.MultibandEq);
                NumericUpDown Number(string name) => All(dialog).OfType<NumericUpDown>().Single(input => input.AccessibleName == name);
                TrackBar Slider(string name) => All(dialog).OfType<TrackBar>().Single(input => input.AccessibleName == name + " slider");
                var automate = All(dialog).OfType<CheckBox>().Single(input => input.AccessibleName == "Automate effect parameters");
                var preview = All(dialog).OfType<CheckBox>().Single(input => input.AccessibleName == "Live preview");
                var reset = All(dialog).OfType<ThemeButton>().Single(button => button.AccessibleName == "Reset EQ to flat");
                Layout(dialog);
                Check(dialog.Text == "Multiband EQ" && dialog.Settings.MultibandEq != null && !dialog.Settings.AutomationEnabled,
                    "the ten-band tool has a readable title and starts without automation");
                Check(MultibandEqSettings.CenterFrequencies.SequenceEqual(centers)
                    && names.Select((name, index) => Number(name).AccessibleDescription!.Contains(centers[index].ToString("0.##", CultureInfo.CurrentCulture) + " Hz")).All(value => value),
                    "compact band labels retain the exact octave centers in accessible descriptions");
                Check(All(dialog).OfType<NumericUpDown>().Count() == 20 && All(dialog).OfType<NumericUpDown>().All(input => input is LiveNumericUpDown)
                    && All(dialog).OfType<TrackBar>().Count() == 20 && All(dialog).OfType<TrackBar>().All(input => input is ThemedSlider),
                    "every band has independent live start and end inputs with themed sliders");
                Check(All(dialog).OfType<NumericUpDown>().All(input => input.Minimum == -24 && input.Maximum == 24 && input.DecimalPlaces == 1 && input.Increment == .1m && input.Value == 0)
                    && All(dialog).OfType<TrackBar>().All(input => input.Minimum == 0 && input.Maximum == 480 && input.Value == 240),
                    "all gain controls start flat and expose minus24 to plus24dB in tenth-dB steps");
                Check(Enumerable.Range(0, 10).All(index => dialog.Settings.MultibandEq!.BandAt(index) is { GainDb: 0, EndGainDb: null }),
                    "flat settings contain ten immutable zero-gain bands with no inactive endpoints");
                Check(Math.Abs(dialog.ClientSize.Width - 390 * dialog.DeviceDpi / 96d) <= 1
                    && names.All(name => LocallyVisible(Number(name)) && !LocallyVisible(Number("End " + name))),
                    "EQ opens in one column with its end controls hidden");
                Check(names.Select(name => Number(name).Top).Zip(names.Skip(1).Select(name => Number(name).Top), (first, second) => first < second).All(value => value),
                    "band rows run from bass to treble in frequency order");
                int changes = 0; dialog.SettingsChanged += () => changes++;
                Number(names[0]).Text = (-12.5m).ToString(CultureInfo.CurrentCulture);
                Check(changes == 1 && dialog.Settings.MultibandEq!.Hz31.GainDb == -12.5 && Slider(names[0]).Value == 115,
                    "typing a fractional bass cut updates its slider and preview once");
                int before = changes; Slider(names[0]).Value = 0;
                bool minimum = changes == before + 1 && Number(names[0]).Value == -24 && dialog.Settings.MultibandEq!.Hz31.GainDb == -24;
                before = changes; Slider(names[0]).Value = 480;
                Check(minimum && changes == before + 1 && Number(names[0]).Value == 24 && dialog.Settings.MultibandEq!.Hz31.GainDb == 24,
                    "gain slider extremes map to the full negative and positive dB range");
                for (int i = 0; i < names.Length; i++) Number(names[i]).Value = i - 5;
                var snapshot = dialog.Settings.MultibandEq!;
                Check(Enumerable.Range(0, 10).All(index => snapshot.BandAt(index).GainDb == index - 5 && snapshot.BandAt(index).EndGainDb == null),
                    "each frequency control maps to its own ordered gain setting");
                Number(names[5]).Value = 6.5m;
                Check(snapshot.Hz1000.GainDb == 0 && dialog.Settings.MultibandEq!.Hz1000.GainDb == 6.5
                    && Enumerable.Range(0, 10).Where(index => index != 5).All(index => snapshot.BandAt(index) == dialog.Settings.MultibandEq!.BandAt(index)),
                    "editing one band leaves prior immutable snapshots and other bands unchanged");
                using (var independent = new EffectDialog(EffectKind.MultibandEq))
                    Check(Enumerable.Range(0, 10).All(index => independent.Settings.MultibandEq!.BandAt(index) is { GainDb: 0, EndGainDb: null }),
                        "a second EQ window has independent flat settings");
                int narrowWidth = dialog.ClientSize.Width, height = dialog.ClientSize.Height;
                var content = (Panel)Number(names[0]).Parent!;
                int contentHeight = content.Height; before = changes; automate.Checked = true; Layout(dialog);
                Check(changes == before + 1 && dialog.Settings.AutomationEnabled && dialog.ClientSize.Width == narrowWidth * 2
                    && dialog.ClientSize.Height == height && content.Height == contentHeight,
                    "EQ automation doubles the width and pairs rows without adding height or refreshes");
                Check(Enumerable.Range(0, 10).All(index => dialog.Settings.MultibandEq!.BandAt(index).EndGainDb == dialog.Settings.MultibandEq!.BandAt(index).GainDb),
                    "first enabling automation starts each endpoint at its current band gain");
                int margin = (int)Math.Round(24 * dialog.DeviceDpi / 96d), middle = content.ClientSize.Width / 2;
                foreach (string name in names)
                {
                    var start = Number(name); var end = Number("End " + name); var leftSlider = Slider(name); var rightSlider = Slider("End " + name);
                    Check(start.Top == end.Top && leftSlider.Top == rightSlider.Top && leftSlider.Top >= start.Bottom && rightSlider.Top >= end.Bottom
                        && start.Right == middle - margin && end.Left >= middle + margin && end.Right == content.ClientSize.Width - margin
                        && leftSlider.Left == margin && leftSlider.Right == middle - margin
                        && rightSlider.Left == middle + margin && rightSlider.Right == content.ClientSize.Width - margin
                        && LocallyVisible(start) && LocallyVisible(end),
                        name + " aligns with its endpoint in contained Start and End columns");
                }
                for (int i = 0; i < names.Length; i++) Number("End " + names[i]).Value = 24 - i * 2;
                Check(Enumerable.Range(0, 10).All(index => dialog.Settings.MultibandEq!.BandAt(index).EndGainDb == 24 - index * 2)
                    && snapshot.Hz31 is { GainDb: -5, EndGainDb: null },
                    "all end gains remain independent while earlier snapshots retain their values");
                var endValues = names.Select(name => Number("End " + name).Value).ToArray(); before = changes;
                automate.Checked = false; Layout(dialog);
                bool disabled = changes == before + 1 && dialog.ClientSize.Width == narrowWidth
                    && Enumerable.Range(0, 10).All(index => dialog.Settings.MultibandEq!.BandAt(index).EndGainDb == null);
                automate.Checked = true; Layout(dialog);
                Check(disabled && changes == before + 2 && dialog.ClientSize.Width == narrowWidth * 2
                    && names.Select(name => Number("End " + name).Value).SequenceEqual(endValues),
                    "disabling and restoring automation preserves the edited endpoint gains and widths");
                before = changes; Click(reset);
                Check(changes == before + 1 && automate.Checked && dialog.ClientSize.Width == narrowWidth * 2
                    && All(dialog).OfType<NumericUpDown>().All(input => input.Value == 0)
                    && All(dialog).OfType<TrackBar>().All(input => input.Value == 240)
                    && Enumerable.Range(0, 10).All(index => dialog.Settings.MultibandEq!.BandAt(index) is { GainDb: 0, EndGainDb: 0 }),
                    "Reset to flat restores all start and end gains and sliders with one preview refresh");
                before = changes; Click(reset);
                Check(changes == before, "resetting an already-flat EQ does not rebuild an unchanged preview");
                Number("End " + names[9]).Value = -6; automate.Checked = false; Number(names[1]).Value = 3; before = changes; Click(reset);
                Check(changes == before + 1 && !automate.Checked && All(dialog).OfType<NumericUpDown>().All(input => input.Value == 0),
                    "Reset to flat also clears hidden endpoint edits without changing automation mode");
                automate.Checked = true; before = changes; preview.Checked = false;
                Check(changes == before + 1 && !dialog.PreviewEnabled && Enumerable.Range(0, 10).All(index => dialog.Settings.MultibandEq!.BandAt(index) is { GainDb: 0, EndGainDb: 0 }),
                    "turning off live preview preserves the complete proposed EQ settings");
                before = changes; Number(names[1]).Text = 1.5m.ToString(CultureInfo.CurrentCulture);
                Check(changes == before + 1 && !dialog.PreviewEnabled && dialog.Settings.MultibandEq!.Hz63.GainDb == 1.5,
                    "band settings remain editable for Apply while preview is off");
                int applies = 0, cancels = 0; dialog.ApplyRequested += () => applies++; dialog.CancelRequested += () => cancels++;
                var apply = All(dialog).OfType<ThemeButton>().Single(button => button.AccessibleName == "Apply effect");
                var cancel = All(dialog).OfType<ThemeButton>().Single(button => button.AccessibleName == "Cancel effect");
                Click(apply); Click(cancel);
                Check(applies == 1 && cancels == 1 && !dialog.IsDisposed && apply.Role == ButtonRole.Primary
                    && cancel.Role == ButtonRole.Secondary && reset.Role == ButtonRole.Secondary,
                    "EQ keeps explicit modeless Apply/Cancel actions and a secondary reset action");
                dialog.Height = dialog.MinimumSize.Height; Layout(dialog);
                var body = (Panel)content.Parent!; var footer = (Panel)apply.Parent!;
                Check(body.AutoScroll && content.Height > body.ClientSize.Height && footer.Width == dialog.ClientSize.Width
                    && footer.Bottom == dialog.ClientSize.Height && !ReferenceEquals(footer, content)
                    && All(dialog).OfType<NumericUpDown>().All(input => input.Left >= 0 && input.Right <= content.ClientSize.Width),
                    "the compact ten-band tool scrolls its rows with contained inputs and a fixed full-width footer");
                Console.WriteLine($"{checks} EQ dialog checks passed. No visible window, native input or audio was used.");
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("EQ dialog fixture deadline exceeded.");
        if (failure != null) throw new Exception("EQ dialog fixture failed", failure);
    }
}
