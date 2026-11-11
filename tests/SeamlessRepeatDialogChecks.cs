using System.Globalization;
using System.Reflection;
using OrchardSound;

public static class SeamlessRepeatDialogChecks
{
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                int checks = 0;
                void Check(bool result, string name) { if (!result) throw new Exception("FAILED SEAMLESS TOOL: " + name); checks++; Console.WriteLine("PASS SEAMLESS TOOL " + name); }
                IEnumerable<Control> All(Control parent) => parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(All(child)));
                using var dialog = new EffectDialog(EffectKind.SeamlessRepeat);
                NumericUpDown Number(string name) => All(dialog).OfType<NumericUpDown>().Single(input => input.AccessibleName == name);
                TrackBar Slider(string name) => All(dialog).OfType<TrackBar>().Single(input => input.AccessibleName == name + " slider");
                CheckBox Toggle(string name) => All(dialog).OfType<CheckBox>().Single(input => input.AccessibleName == name);
                Check(dialog.Text == "Seamless Repeat" && dialog.Settings.SeamlessRepeat is { RepeatCount: 10, CrossfadeMilliseconds: 20 },
                    "Seamless Repeat starts with ten total repetitions and a requested 20 ms crossfade");
                Check(!dialog.Settings.AutomationEnabled && !All(dialog).OfType<CheckBox>().Any(input => input.AccessibleName == "Automate effect parameters")
                    && !All(dialog).OfType<NumericUpDown>().Any(input => (input.AccessibleName ?? "").StartsWith("End ")),
                    "timeline repeat controls omit generic start/end automation");
                Check(All(dialog).OfType<NumericUpDown>().Count() == 2 && All(dialog).OfType<NumericUpDown>().All(input => input is LiveNumericUpDown)
                    && All(dialog).OfType<TrackBar>().Count() == 2 && All(dialog).OfType<TrackBar>().All(input => input is ThemedSlider),
                    "both seamless controls have live numeric fields and matching themed sliders");
                Check(Number("Total repetitions").Minimum == 1 && Number("Total repetitions").Maximum == 64 && Number("Total repetitions").DecimalPlaces == 0
                    && Number("Crossfade ms").Minimum == 0 && Number("Crossfade ms").Maximum == 2000 && Number("Crossfade ms").DecimalPlaces == 1,
                    "repeat and crossfade controls expose bounded counts and fractional milliseconds");
                Check(Toggle("Smart seam").Checked && Toggle("Adaptive crossfade").Checked
                    && dialog.Settings.SeamlessRepeat is { SmartSeam: true, AdaptiveCrossfade: true },
                    "the two approved optional seam helpers start enabled");
                Check(Toggle("Loop end back to start").Checked && dialog.Settings.SeamlessRepeat is { LoopEndToStart: true },
                    "the completed repeat output closes back to its start by default");
                Check(Toggle("Loop end back to start").Top > Slider("Crossfade ms").Bottom
                    && Toggle("Loop end back to start").Top < Toggle("Smart seam").Top,
                    "the closing loop option appears after crossfade and before the seam refinements");
                Check(!All(dialog).Any(control => (control.Text ?? "").Contains("Preserve beat", StringComparison.OrdinalIgnoreCase)),
                    "the dialog does not introduce an unapproved beat-preservation mode");
                int changes = 0; dialog.SettingsChanged += () => changes++;
                int before = changes; Number("Total repetitions").Text = "12";
                Check(changes == before + 1 && dialog.Settings.SeamlessRepeat!.RepeatCount == 12 && Slider("Total repetitions").Value == 11,
                    "typing a total repetition count updates settings and slider with one preview refresh");
                before = changes; Slider("Total repetitions").Value = 63;
                Check(changes == before + 1 && Number("Total repetitions").Value == 64 && dialog.Settings.SeamlessRepeat!.RepeatCount == 64,
                    "the repeat slider reaches the maximum total without adding another original copy");
                Slider("Total repetitions").Value = 0;
                Check(dialog.Settings.SeamlessRepeat!.RepeatCount == 1 && Number("Total repetitions").Value == 1,
                    "one total repetition remains available without adding an extra original copy");
                before = changes; Number("Crossfade ms").Text = 12.5m.ToString(CultureInfo.CurrentCulture);
                Check(changes == before + 1 && dialog.Settings.SeamlessRepeat!.CrossfadeMilliseconds == 12.5 && Slider("Crossfade ms").Value == 125,
                    "typing fractional crossfade milliseconds updates the live preview immediately");
                before = changes; Slider("Crossfade ms").Value = 300;
                Check(changes == before + 1 && Number("Crossfade ms").Value == 30 && dialog.Settings.SeamlessRepeat!.CrossfadeMilliseconds == 30,
                    "crossfade slider units stay synchronized with milliseconds");
                Number("Crossfade ms").Value = 0; bool zero = dialog.Settings.SeamlessRepeat!.CrossfadeMilliseconds == 0;
                Number("Crossfade ms").Value = 2000;
                Check(zero && dialog.Settings.SeamlessRepeat!.CrossfadeMilliseconds == 2000, "requested crossfade values preserve zero and the full 2000 ms UI bound for backend clamping");
                before = changes; Toggle("Loop end back to start").Checked = false;
                Check(changes == before + 1 && dialog.Settings.SeamlessRepeat is { LoopEndToStart: false, SmartSeam: true, AdaptiveCrossfade: true, RepeatCount: 1, CrossfadeMilliseconds: 2000 },
                    "opening the loop changes only its setting and refreshes the preview once");
                before = changes; Toggle("Loop end back to start").Checked = true;
                Check(changes == before + 1 && dialog.Settings.SeamlessRepeat is { LoopEndToStart: true, SmartSeam: true, AdaptiveCrossfade: true, RepeatCount: 1, CrossfadeMilliseconds: 2000 },
                    "closing the loop again preserves all original parameters and refreshes once");
                before = changes; Toggle("Smart seam").Checked = false;
                Check(changes == before + 1 && dialog.Settings.SeamlessRepeat is { LoopEndToStart: true, SmartSeam: false, AdaptiveCrossfade: true },
                    "Smart seam can be changed independently with one preview refresh");
                before = changes; Toggle("Adaptive crossfade").Checked = false;
                Check(changes == before + 1 && dialog.Settings.SeamlessRepeat is { LoopEndToStart: true, SmartSeam: false, AdaptiveCrossfade: false },
                    "Adaptive crossfade has its own independent preview setting");
                Check(dialog.PreviewEnabled, "live preview starts enabled for the seam tool");
                before = changes; Toggle("Live preview").Checked = false;
                Check(changes == before + 1 && !dialog.PreviewEnabled && dialog.Settings.SeamlessRepeat is { LoopEndToStart: true, SmartSeam: false, AdaptiveCrossfade: false, RepeatCount: 1, CrossfadeMilliseconds: 2000 },
                    "turning preview off preserves the proposed repetition settings");
                before = changes; Toggle("Loop end back to start").Checked = false;
                Check(changes == before + 1 && !dialog.PreviewEnabled && dialog.Settings.SeamlessRepeat is { LoopEndToStart: false, SmartSeam: false, AdaptiveCrossfade: false, RepeatCount: 1, CrossfadeMilliseconds: 2000 },
                    "the closing loop setting remains editable for Apply when live preview is off");
                var explanation = All(dialog).OfType<Label>().Single(label => label.AccessibleName == "Seamless repeat explanation");
                Check(explanation.Text.Contains("original copy") && explanation.Text.Contains("Overlaps shorten the result")
                    && explanation.Text.Contains("one closing overlap") && explanation.Text.Contains("refines the match") && explanation.Text.Contains("volume steady")
                    && !explanation.Text.Contains("perfect", StringComparison.OrdinalIgnoreCase),
                    "the explanation describes overlapping joins and the optional refinements without guaranteeing a perfect seam");
                var explanationSize = TextRenderer.MeasureText(explanation.Text, explanation.Font,
                    new Size(explanation.ClientSize.Width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                Check(explanationSize.Height <= explanation.ClientSize.Height,
                    "the complete seam explanation fits its current-DPI label without clipping");
                int applies = 0, cancels = 0; dialog.ApplyRequested += () => applies++; dialog.CancelRequested += () => cancels++;
                var apply = All(dialog).OfType<ThemeButton>().Single(button => button.AccessibleName == "Apply effect");
                var cancel = All(dialog).OfType<ThemeButton>().Single(button => button.AccessibleName == "Cancel effect");
                const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(Control).GetMethod("OnClick", hidden)!.Invoke(apply, [EventArgs.Empty]); typeof(Control).GetMethod("OnClick", hidden)!.Invoke(cancel, [EventArgs.Empty]);
                Check(applies == 1 && cancels == 1 && !dialog.IsDisposed && apply.Role == ButtonRole.Primary && cancel.Role == ButtonRole.Secondary,
                    "Apply and Cancel remain explicit modeless actions with the shared button hierarchy");
                int stops = 0, restarts = 0, cursorStarts = 0;
                dialog.StopPlaybackRequested += () => stops++; dialog.RestartPlaybackRequested += () => restarts++; dialog.CursorPlaybackRequested += () => cursorStarts++;
                var command = typeof(EffectDialog).GetMethod("ProcessCmdKey", hidden)!;
                command.Invoke(dialog, [Message.Create(IntPtr.Zero, 0x100, IntPtr.Zero, IntPtr.Zero), Keys.Space]);
                bool spaceStartsCursorOnly = restarts == 0 && cursorStarts == 1 && stops == 0;
                command.Invoke(dialog, [Message.Create(IntPtr.Zero, 0x100, IntPtr.Zero, IntPtr.Zero), Keys.Shift | Keys.Space]);
                bool shiftRestartsOnly = restarts == 1 && cursorStarts == 1 && stops == 0;
                command.Invoke(dialog, [Message.Create(IntPtr.Zero, 0x100, IntPtr.Zero, IntPtr.Zero), Keys.Control | Keys.Space]);
                Check(spaceStartsCursorOnly && shiftRestartsOnly && restarts == 1 && cursorStarts == 1 && stops == 1,
                    "Space starts at cursor, Shift+Space restarts selection, and Ctrl+Space only stops in the seam tool");
                var shortcutHelp = All(dialog).OfType<Label>().Single(label => label.AccessibleName == "Effect playback shortcuts");
                var shortcutSize = TextRenderer.MeasureText(shortcutHelp.Text, shortcutHelp.Font,
                    new Size(shortcutHelp.ClientSize.Width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                Check(shortcutHelp.Text.Contains("Space: cursor") && shortcutHelp.Text.Contains("Shift+Space: restart selection")
                    && shortcutHelp.Text.Contains("Ctrl+Space: stop") && shortcutSize.Height <= shortcutHelp.ClientSize.Height,
                    "the effect tool shows all three current transport shortcuts without clipping");
                Check(dialog.DispatchHoverCommand(Keys.Space) && dialog.DispatchHoverCommand(Keys.Shift | Keys.Space)
                    && dialog.DispatchHoverCommand(Keys.Control | Keys.Space) && cursorStarts == 2 && restarts == 2 && stops == 2,
                    "inactive hovered effect tools dispatch the same three transport actions without focusing a window");
                Check(!dialog.DispatchHoverCommand(Keys.Control | Keys.Z) && cursorStarts == 2 && restarts == 2 && stops == 2,
                    "hover transport dispatch leaves unrelated editor commands untouched");
                float scale = dialog.DeviceDpi / 96f; dialog.Height = Math.Max(dialog.MinimumSize.Height, (int)(300 * scale));
                dialog.PerformLayout(); foreach (var control in All(dialog).Reverse()) control.PerformLayout();
                var content = (Panel)Number("Total repetitions").Parent!; var body = (Panel)content.Parent!;
                Check(body.AutoScroll && content.Height > body.ClientSize.Height && apply.Parent != content
                    && dialog.FormBorderStyle == FormBorderStyle.SizableToolWindow && !dialog.Modal && !dialog.ShowInTaskbar,
                    "the seam tool keeps its controls scrollable and footer fixed in a compact movable window");
                Check(All(dialog).OfType<NumericUpDown>().All(input => input.Left >= 0 && input.Right <= content.ClientSize.Width)
                    && All(dialog).OfType<TrackBar>().All(input => input.Left >= 0 && input.Right <= content.ClientSize.Width),
                    "numeric and slider controls stay inside the current-DPI content width after resizing");
                Console.WriteLine($"{checks} seamless repeat dialog checks passed. No input device, playback or visible window was used.");
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("Seamless repeat dialog fixture deadline exceeded.");
        if (failure != null) throw new Exception("Seamless repeat dialog fixture failed", failure);
    }
}
