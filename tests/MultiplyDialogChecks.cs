using System.Reflection;
using OrchardSound;

public static class MultiplyDialogChecks
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
                { if (!condition) throw new Exception("FAILED MULTIPLY TOOL: " + name); checks++; Console.WriteLine("PASS MULTIPLY TOOL " + name); }
                IEnumerable<Control> All(Control control) => control.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(All(child)));
                using var dialog = new EffectDialog(EffectKind.Multiply);
                NumericUpDown Number(string name) => All(dialog).OfType<NumericUpDown>().Single(input => input.AccessibleName == name);
                TrackBar Slider(string name) => All(dialog).OfType<TrackBar>().Single(input => input.AccessibleName == name + " slider");
                CheckBox Toggle(string name) => All(dialog).OfType<CheckBox>().Single(input => input.AccessibleName == name);
                var settings = dialog.Settings.Multiply!;
                Check(dialog.Text == "Multiply" && settings.CopyCount == 4 && settings.VariationPercent == 25 && dialog.Settings.Multiply!.Seed == settings.Seed, "multiply opens with four total copies, 25 percent variation and a stable window seed");
                Check(!settings.EnablePitch && !settings.EnableFilter && !settings.EnableReverb && !All(dialog).OfType<CheckBox>().Any(input => input.AccessibleName == "Automate effect parameters"), "multiply has independent disabled subeffects and explicit endpoints without global automation");
                Check(All(dialog).OfType<NumericUpDown>().Count() == 10 && All(dialog).OfType<NumericUpDown>().All(input => input is LiveNumericUpDown) && All(dialog).OfType<TrackBar>().Count() == 10 && All(dialog).OfType<TrackBar>().All(input => input is ThemedSlider), "every multiply parameter has live numeric input and a paired themed slider");
                Check(Number("Total copies").Enabled && Number("Random variation %").Enabled && Number("Total copies").Minimum == 1 && Number("Total copies").Maximum == 64 && Number("Random variation %").Minimum == 1 && Number("Random variation %").Maximum == 100, "total copies and random variation expose their intended limits");
                Check(!Number("Start pitch").Enabled && !Slider("End pitch").Enabled && !Number("Start frequency Hz").Enabled && !Number("Start room size %").Enabled, "disabled subeffect fields and sliders clearly remain inactive");
                var explanation = All(dialog).OfType<Label>().Single(label => label.AccessibleName == "Multiply variation explanation");
                Check(explanation.Text.Contains(.75.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture)) && explanation.Text.Contains(1.25.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture)), "variation explains the actual parameter multipliers");
                int changes = 0; dialog.SettingsChanged += () => changes++;
                int before = changes; Toggle("Enable pitch variation").Checked = true;
                Check(changes == before + 1 && Number("Start pitch").Enabled && Slider("End pitch").Enabled && !Number("Start frequency Hz").Enabled, "enabling pitch activates only that group and refreshes the preview once");
                Number("Start pitch").Text = "1"; Number("End pitch").Value = 2;
                Check(dialog.Settings.Multiply is { EnablePitch: true, StartSemitones: 1, EndSemitones: 2 }, "pitch variation retains independent semitone endpoints");
                before = changes; Toggle("Enable pitch variation").Checked = false;
                bool pitchDisabled = changes == before + 1 && !Number("Start pitch").Enabled;
                Toggle("Enable pitch variation").Checked = true;
                Check(pitchDisabled && Number("Start pitch").Value == 1 && Number("End pitch").Value == 2, "pitch can be disabled and restored without losing its endpoints");
                Toggle("Enable filter variation").Checked = true;
                var mode = All(dialog).OfType<ComboBox>().Single(); mode.SelectedItem = "High pass";
                Number("Start frequency Hz").Value = 300; Number("End frequency Hz").Value = 900;
                Check(mode.Enabled && dialog.Settings.Multiply is { EnableFilter: true, HighPass: true, StartHz: 300, EndHz: 900 } && Slider("Start frequency Hz").Value == 280, "filter variation exposes its mode, frequency endpoints and matching slider");
                Toggle("Enable reverb variation").Checked = true;
                Number("Start room size %").Value = 25; Number("End room size %").Value = 75;
                Number("Start mix %").Value = 20; Number("End mix %").Value = 80;
                settings = dialog.Settings.Multiply!;
                Check(settings.EnableReverb && settings.StartRoomSize == .25f && settings.EndRoomSize == .75f && settings.StartWet == .2f && settings.EndWet == .8f, "reverb variation maps independent room and mix percentages to audio settings");
                Number("Total copies").Text = "64";
                Check(dialog.Settings.Multiply!.CopyCount == 64 && Slider("Total copies").Value == 63, "typing the maximum total-copy count updates its slider immediately");
                Slider("Total copies").Value = 0;
                Check(Number("Total copies").Value == 1 && dialog.Settings.Multiply!.CopyCount == 1, "the copy slider supports one total copy without adding an extra original");
                Slider("Random variation %").Value = 99;
                Check(dialog.Settings.Multiply!.VariationPercent == 100 && explanation.Text.Contains("0 to 2"), "maximum variation updates the multiplier explanation and settings");
                var beforeReroll = dialog.Settings.Multiply!; before = changes;
                var reroll = All(dialog).OfType<Button>().Single(button => button.AccessibleName == "Reroll multiply variation");
                typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(reroll, [EventArgs.Empty]);
                var afterReroll = dialog.Settings.Multiply!;
                Check(changes == before + 1 && afterReroll.Seed != beforeReroll.Seed && (afterReroll with { Seed = beforeReroll.Seed }) == beforeReroll, "reroll changes only the seed and requests exactly one preview refresh");
                Check(dialog.Settings.Multiply!.Seed == afterReroll.Seed, "ordinary settings reads keep the rerolled variation stable");
                var content = (Panel)Number("Total copies").Parent!; var body = (Panel)content.Parent!;
                var apply = All(dialog).OfType<Button>().Single(button => button.AccessibleName == "Apply effect");
                Check(body.AutoScroll && content.Height > body.ClientSize.Height && !ReferenceEquals(apply.Parent, content) && dialog.Height < (Screen.PrimaryScreen?.WorkingArea.Height ?? int.MaxValue), "multiply keeps its long parameter list scrollable with a fixed action footer inside the work area");
                Console.WriteLine($"{checks} multiply dialog checks passed. No native window was shown.");
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("Multiply dialog fixture timed out.");
        if (failure != null) throw new Exception("Multiply dialog fixture failed.", failure);
    }
}
