using System.Reflection;
using OrchardSound;

public static class ToolbarChecks
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static IEnumerable<Control> All(Control parent) => parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(All(child)));

    public static void Run()
    {
        int checks = 0; Exception? failure = null;
        void Check(bool condition, string name)
        { if (!condition) throw new Exception("FAILED TOOLBAR: " + name); checks++; Console.WriteLine("PASS TOOLBAR " + name); }
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new EditorForm([]); _ = form.Handle;
                object? Field(string name) => typeof(EditorForm).GetField(name, Private)!.GetValue(form);
                object? Call(string name, params object?[] args) => typeof(EditorForm).GetMethod(name, Private)!.Invoke(form, args.Length == 0 ? null : args);
                var toolbar = form.Controls.OfType<FlowLayoutPanel>().Single(); var canvas = (WaveCanvas)Field("canvas")!;
                var selection = (ContextMenuStrip)Field("selectionContextMenu")!;
                var view = (ContextMenuStrip)Field("viewContextMenu")!;
                var export = (ContextMenuStrip)Field("exportContextMenu")!;
                ToolStripMenuItem Item(ContextMenuStrip menu, string name) => menu.Items.OfType<ToolStripMenuItem>().Single(item => item.AccessibleName == name);
                IconButton Icon(string name) => All(toolbar).OfType<IconButton>().Single(button => button.AccessibleName == name);
                bool WantsVisible(Control control) => (bool)typeof(Control).GetProperty("DesiredVisibility", Private)!.GetValue(control)!;
                void Click(Control button) => typeof(Control).GetMethod("OnClick", Private)!.Invoke(button, [EventArgs.Empty]);
                Check(!All(toolbar).OfType<IconButton>().Any(button => button.AccessibleName == "Add sounds" || button.Icon == EditorIcon.Open)
                    && Icon("Add track").Shortcut == "Ctrl+=", "toolbar removes the global Open icon while retaining Add track and its shortcut");
                string[] selectionNames = ["Selection start", "Selection end", "Select all", "Clear selection", "Delete section", "Duplicate selection", "Trim to selection", "Save section cue points"];
                Check(selectionNames.All(name => Item(selection, name).Image != null) && selection.Items[3] is ToolStripSeparator
                    && !All(toolbar).OfType<IconButton>().Any(button => selectionNames.Contains(button.AccessibleName)),
                    "one Selection icon groups the former selection buttons into themed image menu commands");
                Check(Item(selection, "Selection start").ShortcutKeyDisplayString == "LMB / [" && Item(selection, "Selection end").ShortcutKeyDisplayString == "RMB / ]"
                    && Item(selection, "Clear selection").ShortcutKeyDisplayString == "Esc" && Item(selection, "Duplicate selection").ShortcutKeyDisplayString == "Ctrl+D"
                    && Item(selection, "Trim to selection").ShortcutKeyDisplayString == "Ctrl+T" && Item(selection, "Save section cue points").ShortcutKeyDisplayString == "Ctrl+B",
                    "selection menu preserves boundary edit clear and cue shortcuts without opening a popup");
                Check(Icon("Play from cursor").Shortcut == "Space" && Icon("Play loop").Shortcut == "Shift+Space" && Icon("Record").Shortcut == "Shift+R",
                    "separate cursor and selection play controls and recording show their current primary shortcuts");
                var tips = (ToolTip)Field("tips")!;
                Check(tips.GetToolTip(Icon("Play from cursor"))?.Contains("Ctrl+Space") == true && tips.GetToolTip(Icon("Play loop"))?.Contains("Ctrl+Space") == true
                    && All(toolbar).OfType<IconButton>().All(button => tips.GetToolTip(button)?.Length > 15),
                    "toolbar icons retain descriptive hover help and both play controls explain Stop");
                Check(!All(toolbar).OfType<CheckBox>().Any(box => box.AccessibleName is "Fit waveform height" or "Show beats and estimated BPM" or "Show likely kick markers"),
                    "waveform height and rhythm aids occupy the View menu rather than toolbar checkboxes");
                var fit = Item(view, "Fit waveform height"); var magnifier = Item(view, "Precision magnifier"); var follow = Item(view, "Follow cursor");
                Check(fit.Checked && canvas.AutoMagnify && magnifier.Checked && canvas.PrecisionMagnifierEnabled
                    && !follow.Checked && !canvas.FollowCursor && !Item(view, "Show beats and estimated BPM").Checked && !Item(view, "Show likely kick markers").Checked,
                    "View starts with height fitting and precision magnification enabled while rhythm overlays stay optional");
                fit.PerformClick(); magnifier.PerformClick(); follow.PerformClick();
                Check(!fit.Checked && !canvas.AutoMagnify && !magnifier.Checked && !canvas.PrecisionMagnifierEnabled,
                    "View menu checks control the actual waveform height and precision magnifier settings");
                Check(follow.Checked && canvas.FollowCursor && follow.ToolTipText?.Contains("Alt") == true, "Follow cursor View setting controls the precision range policy and explains temporary Alt fitting");
                fit.PerformClick(); magnifier.PerformClick(); follow.PerformClick();
                var scrub = (CheckBox)Field("scrub")!; var refine = (CheckBox)Field("refineEnd")!;
                Click(Icon("Scrub")); Click(Icon("Refine selection end"));
                Check(scrub.Checked && refine.Checked && Icon("Scrub").IsToggled && Icon("Refine selection end").IsToggled,
                    "Scrub and end refinement use mint toggle states backed by their existing option models");
                Click(Icon("Scrub")); Click(Icon("Refine selection end"));
                Check(!scrub.Checked && !refine.Checked && !Icon("Scrub").IsToggled && !Icon("Refine selection end").IsToggled,
                    "both option icons return to neutral when turned off");
                var exportButton = Icon("Export"); var options = All(toolbar).OfType<ThemeButton>().Single(button => button.AccessibleName == "Export options");
                Check(ReferenceEquals(exportButton.Parent, options.Parent) && exportButton.Top == options.Top && exportButton.Height == options.Height
                    && options.Left > exportButton.Right && options.Width < exportButton.Width && exportButton.Role == ButtonRole.Primary && options.Role == ButtonRole.Secondary,
                    "Export pairs a primary button with a narrow secondary caret of equal height");
                var format = (ComboBox)Field("format")!;
                Check(format.Parent == null && !format.IsHandleCreated && !All(toolbar).OfType<ComboBox>().Any(combo => combo.AccessibleName == "Export format")
                    && !All(toolbar).OfType<IconButton>().Any(button => button.AccessibleName == "Set export folder"),
                    "export format and folder choices no longer occupy separate toolbar controls");
                foreach (string extension in new[] { "WAV", "MP3", "FLAC", "OGG", "M4A" })
                {
                    Item(export, "Export format " + extension).PerformClick();
                    var formats = export.Items.OfType<ToolStripMenuItem>().Where(item => item.Tag is string).ToArray();
                    Check(form.SelectedExportExtension == "." + extension.ToLowerInvariant() && formats.Count(item => item.Checked) == 1
                        && Item(export, "Export format " + extension).Checked && formats.All(item => item.AccessibleRole == AccessibleRole.RadioButton),
                        extension + " radio choice controls the actual export extension exclusively");
                }
                Check(Item(export, "Set export folder").ShortcutKeyDisplayString == "Ctrl+Shift+S" && Item(export, "Set export folder").ToolTipText?.Contains("does not export") == true,
                    "export options keep choosing a folder separate from exporting audio");
                var wordmark = toolbar.Controls.OfType<WordmarkControl>().Single(); var originalSize = wordmark.Size;
                using var logicalWordmark = new WordmarkControl();
                foreach (int dpi in new[] { 96, 144, 192 })
                {
                    wordmark.Size = new((int)(logicalWordmark.Width * dpi / 96f), (int)(logicalWordmark.Height * dpi / 96f));
                    using var bitmap = new Bitmap(wordmark.Width, wordmark.Height); wordmark.DrawToBitmap(bitmap, wordmark.ClientRectangle);
                    int mintPixels = 0, copperPixels = 0;
                    for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++)
                    {
                        var pixel = bitmap.GetPixel(x, y);
                        if (pixel.G > pixel.R + 20 && pixel.G > pixel.B) mintPixels++;
                        if (pixel.R > pixel.G + 20 && pixel.G > pixel.B + 20) copperPixels++;
                    }
                    Check(bitmap.GetPixel(0, 0).ToArgb() == toolbar.BackColor.ToArgb() && bitmap.GetPixel(bitmap.Width - 1, bitmap.Height - 1).ToArgb() == toolbar.BackColor.ToArgb()
                        && mintPixels > 200 && copperPixels > 30,
                        "the native-hidden vector wordmark renders mint lettering copper tape and matching corners at simulated " + dpi + " DPI");
                }
                wordmark.Size = originalSize;
                var samples = new float[128]; var track = new AudioTrack { Path = "Toolbar fixture.wav", Samples = samples, Peaks = new PeakPyramid(samples), Rhythm = new(120, [], []) };
                canvas.Slots[0].Track = track; Call("RebuildTrackControls");
                var split = All(canvas).OfType<CheckBox>().Single(box => box.AccessibleName == "Split channels in Track 1");
                split.Checked = true;
                Check(canvas.Slots[0].SplitChannels && track.Offset == 0 && track.Frames == 64 && track.Gain == 1,
                    "Split channels changes the row display while preserving shared timing volume and source duration");
                split.Checked = false;
                var viewport = (Panel)Field("scroll")!; viewport.Dock = DockStyle.None;
                viewport.Size = new((int)(1600 * canvas.DpiScale), (int)(1000 * canvas.DpiScale));
                canvas.SetViewportHeight(viewport.ClientSize.Height); canvas.FitHeight(true); Call("LayoutTrackControls");
                var compact = (Control)Field("addTrackOverlay")!; var pane = canvas.ToDevice(canvas.GetPaneBounds(canvas.Slots.Count));
                Check(WantsVisible(compact) && pane.Contains(compact.Bounds) && compact.Right < WaveCanvas.LeftGutter * canvas.DpiScale
                    && ((Control)Field("status")!).Controls.Count == 0,
                    "the compact last-track add button stays in the left gutter and never becomes a footer-wide row");
                int before = canvas.Slots.Count; Click(compact);
                Check(canvas.Slots.Count == before + 1 && canvas.Slots[^1].Track == null, "the compact track button adds exactly one empty channel");
                before = canvas.Slots.Count;
                bool dispatched = (bool)Call("DispatchEditorCommand", Keys.Control | Keys.Oemplus)!;
                Check(dispatched && canvas.Slots.Count == before + 1, "the shared hover and keyboard dispatcher retains Ctrl+= track creation");
                Call("StopEditorPlayback");
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(45))) throw new TimeoutException("Toolbar fixture deadline exceeded.");
        if (failure != null) throw new Exception("Toolbar fixture failed.", failure);
        Console.WriteLine($"{checks} toolbar checks passed. Native controls were rendered hidden; no popup input or audio hardware was used.");
    }
}
