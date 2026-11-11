using System.Reflection;
using NAudio.Wave;
using OrchardSound;

public static class UiChecks
{
    static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void Run(string? renderPath)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                using var form = new EditorForm([]);
                _ = form.Handle;
                var canvas = (WaveCanvas)typeof(EditorForm).GetField("canvas", Private)!.GetValue(form)!;
                void Invoke(string name, params object[] values) => typeof(EditorForm).GetMethod(name, Private, null, values.Select(v => v.GetType()).ToArray(), null)!.Invoke(form, values);
                IEnumerable<Control> All(Control c) => c.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(All(child)));
                Button ActionButton(string name) => All(form).OfType<Button>().Single(b => b.AccessibleName == name);
                IEnumerable<ToolStripMenuItem> MenuItems(EditorForm owner) => new[] { "selectionContextMenu", "viewContextMenu", "exportContextMenu" }
                    .SelectMany(name => ((ContextMenuStrip)typeof(EditorForm).GetField(name, Private)!.GetValue(owner)!).Items.OfType<ToolStripMenuItem>());
                ToolStripMenuItem ActionItem(string name) => MenuItems(form).Single(item => item.AccessibleName == name);
                void InvokeAction(EditorForm owner, string name)
                {
                    var button = All(owner).OfType<Button>().SingleOrDefault(action => action.AccessibleName == name);
                    if (button != null) typeof(Control).GetMethod("OnClick", Private)!.Invoke(button, [EventArgs.Empty]);
                    else MenuItems(owner).Single(item => item.AccessibleName == name).PerformClick();
                }
                void Click(string name) => InvokeAction(form, name);
                int checks = 0;
                void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; Console.WriteLine("PASS UI " + name); }
                Bitmap Render(Control view)
                {
                    var bitmap = new Bitmap(view.Width, view.Height);
                    using var graphics = Graphics.FromImage(bitmap);
                    graphics.Clear(view.BackColor);
                    view.GetType().GetMethod("OnPaint", Private)!.Invoke(view, [new PaintEventArgs(graphics, new Rectangle(Point.Empty, bitmap.Size))]);
                    return bitmap;
                }
                bool HasContrast(Bitmap bitmap, Rectangle area)
                {
                    var background = bitmap.GetPixel(0, 0);
                    area.Intersect(new Rectangle(Point.Empty, bitmap.Size));
                    for (int y = area.Top; y < area.Bottom; y++)
                        for (int x = area.Left; x < area.Right; x++)
                        {
                            var pixel = bitmap.GetPixel(x, y);
                            if (Math.Abs(pixel.R - background.R) > 20 || Math.Abs(pixel.G - background.G) > 20 || Math.Abs(pixel.B - background.B) > 20) return true;
                        }
                    return false;
                }

                Check(form.Text == "LoopEdit" && typeof(EditorForm).Assembly.GetName().Name == "LoopEdit", "window and application assembly use the LoopEdit name");
                using (var iconStream = typeof(EditorForm).Assembly.GetManifestResourceStream("LoopEdit.AppIcon"))
                {
                    if (iconStream == null) throw new Exception("LoopEdit app icon resource is missing");
                    using var data = new MemoryStream(); iconStream.CopyTo(data); byte[] iconBytes = data.ToArray();
                    using var reader = new BinaryReader(new MemoryStream(iconBytes));
                    ushort reserved = reader.ReadUInt16(), kind = reader.ReadUInt16(), count = reader.ReadUInt16();
                    var sizes = new HashSet<int>();
                    for (int i = 0; i < count; i++)
                    { int width = reader.ReadByte(); _ = reader.ReadByte(); reader.BaseStream.Position += 14; sizes.Add(width == 0 ? 256 : width); }
                    Check(reserved == 0 && kind == 1 && count >= 4 && sizes.Contains(16) && sizes.Contains(32) && sizes.Contains(256), "embedded app icon includes desktop and small-window resolutions");
                    if (form.Icon == null) throw new Exception("LoopEdit window icon is missing");
                    using var expectedIcon = new Icon(new MemoryStream(iconBytes), form.Icon.Size);
                    using var expectedBitmap = expectedIcon.ToBitmap(); using var actualBitmap = form.Icon.ToBitmap();
                    bool matches = expectedBitmap.Size == actualBitmap.Size, visible = false;
                    for (int y = 0; y < actualBitmap.Height && matches; y++)
                        for (int x = 0; x < actualBitmap.Width; x++)
                        { Color pixel = actualBitmap.GetPixel(x, y); matches &= pixel.ToArgb() == expectedBitmap.GetPixel(x, y).ToArgb(); visible |= pixel.A != 0; }
                    Check(matches && visible, "window uses the generated LoopEdit icon");
                }
                Check(form.WindowState == FormWindowState.Maximized, "app starts maximized");
                Check(canvas.Slots.Count == 1 && canvas.Slots[0].Track == null && !canvas.Tracks.Any(), "session starts with one empty channel");
                Check(canvas.Slots[0].Enabled, "the initial empty channel is ticked by default");
                Check(All(canvas).OfType<IconButton>().Any(button => button.AccessibleName == "Open audio in Track 1"), "the empty channel offers an Open audio icon without opening a dialog");
                Check(All(canvas).OfType<IconButton>().Any(button => button.AccessibleName == "Open or replace audio in Track 1"), "the row header offers an audio open or replacement action");
                using (var emptyForm = new EditorForm([]))
                {
                    _ = emptyForm.Handle;
                    var emptyCanvas = (WaveCanvas)typeof(EditorForm).GetField("canvas", Private)!.GetValue(emptyForm)!;
                    var visibleState = Enum.Parse(typeof(Control).GetNestedType("States", BindingFlags.NonPublic)!, "Visible");
                    bool LocallyVisible(Control control) => (bool)typeof(Control).GetMethod("GetState", Private)!.Invoke(control, [visibleState])!;
                    bool EmptyActionsFit()
                    {
                        bool ClearAbove(Control action) => !emptyCanvas.Controls.Cast<Control>().Any(other => other != action && LocallyVisible(other)
                            && emptyCanvas.Controls.GetChildIndex(other) < emptyCanvas.Controls.GetChildIndex(action) && other.Bounds.IntersectsWith(action.Bounds));
                        for (int i = 0; i < emptyCanvas.Slots.Count; i++)
                        {
                            var open = All(emptyCanvas).OfType<IconButton>().Single(button => button.AccessibleName == "Open audio in Track " + (i + 1));
                            var paste = All(emptyCanvas).OfType<IconButton>().Single(button => button.AccessibleName == "Paste audio in Track " + (i + 1));
                            var plot = emptyCanvas.ToDevice(emptyCanvas.GetWaveBounds(i + 1));
                            int middle = plot.Top + plot.Height / 2, hintHalfHeight = (int)Math.Round(12 * emptyCanvas.DpiScale);
                            if (!LocallyVisible(open) || !LocallyVisible(paste) || !plot.Contains(open.Bounds) || !plot.Contains(paste.Bounds)
                                || open.Caption != "Open" || paste.Caption != "Paste" || open.Size != paste.Size
                                || open.Top < middle + hintHalfHeight || paste.Bottom > middle - hintHalfHeight
                                || Math.Abs(open.Left + open.Width / 2 - (plot.Left + plot.Width / 2)) > 1 || paste.Left != open.Left
                                || !ClearAbove(open) || !ClearAbove(paste)) return false;
                        }
                        return true;
                    }
                    emptyCanvas.SetViewportHeight((int)(900 * emptyCanvas.DpiScale));
                    typeof(EditorForm).GetMethod("LayoutTrackControls", Private)!.Invoke(emptyForm, null);
                    Check(EmptyActionsFit(), "empty-channel Paste and Open remain visible above and below the drop hint in regular panes");
                    emptyCanvas.SetViewportHeight((int)((2 * WaveCanvas.MinPaneHeight + WaveCanvas.SplitterHeight + 16) * emptyCanvas.DpiScale));
                    emptyCanvas.FitHeight(true);
                    Check(emptyCanvas.GetPaneBounds(1).Height == WaveCanvas.MinPaneHeight && EmptyActionsFit(), "minimum-height panes retain both empty-channel actions without clipping or covering the hint");
                    emptyCanvas.Slots.Add(new());
                    typeof(EditorForm).GetMethod("RebuildTrackControls", Private)!.Invoke(emptyForm, null);
                    emptyCanvas.SetViewportHeight((int)((3 * WaveCanvas.MinPaneHeight + 2 * WaveCanvas.SplitterHeight + 16) * emptyCanvas.DpiScale));
                    emptyCanvas.FitHeight(true);
                    Check(emptyCanvas.Slots.Count == 2 && emptyCanvas.GetPaneBounds(1).Height == WaveCanvas.MinPaneHeight && emptyCanvas.GetPaneBounds(2).Height == WaveCanvas.MinPaneHeight && EmptyActionsFit(), "every empty channel keeps its own visible actions when multiple rows have minimum height");
                    foreach (var action in All(emptyCanvas).OfType<IconButton>().Where(button => button.Caption is "Open" or "Paste"))
                    {
                        using var painted = Render(action);
                        int scaleY(int y) => (int)Math.Round(y * action.DeviceDpi / 96f);
                        Check(HasContrast(painted, new Rectangle(2, scaleY(2), painted.Width - 4, scaleY(25))) && HasContrast(painted, new Rectangle(2, scaleY(28), painted.Width - 4, painted.Height - scaleY(28))), action.Caption + " empty-channel action renders both a glyph and readable caption");
                    }
                    var addOverlay = All(emptyCanvas).OfType<Button>().Single(button => button.AccessibleName == "Add new track at bottom");
                    var footer = (Control)typeof(EditorForm).GetField("status", Private)!.GetValue(emptyForm)!;
                    var viewport = (Panel)typeof(EditorForm).GetField("scroll", Private)!.GetValue(emptyForm)!;
                    viewport.Dock = DockStyle.None; viewport.Size = new((int)(1600 * emptyCanvas.DpiScale), (int)(1200 * emptyCanvas.DpiScale));
                    emptyCanvas.SetViewportHeight(viewport.ClientSize.Height); emptyCanvas.FitHeight(true);
                    typeof(EditorForm).GetMethod("LayoutTrackControls", Private)!.Invoke(emptyForm, null);
                    var lastPane = emptyCanvas.ToDevice(emptyCanvas.GetPaneBounds(emptyCanvas.Slots.Count));
                    if (!LocallyVisible(addOverlay) || !lastPane.Contains(addOverlay.Bounds))
                        Console.WriteLine($"Compact action geometry: visible {LocallyVisible(addOverlay)}, action {addOverlay.Bounds}, last pane {lastPane}, viewport {viewport.ClientSize}, canvas {emptyCanvas.Bounds}, headers " + string.Join("; ", ((List<Control>)typeof(EditorForm).GetField("trackControls", Private)!.GetValue(emptyForm)!).Select(header => header.Bounds)));
                    Check(LocallyVisible(addOverlay) && lastPane.Contains(addOverlay.Bounds) && addOverlay.Width <= WaveCanvas.LeftGutter * emptyCanvas.DpiScale && addOverlay.Left == (int)(18 * emptyCanvas.DpiScale),
                        "the final track offers a compact left-gutter add action when its header and viewport leave room");
                    int rowsBefore = emptyCanvas.Slots.Count;
                    typeof(Control).GetMethod("OnClick", Private)!.Invoke(addOverlay, [EventArgs.Empty]);
                    Check(emptyCanvas.Slots.Count == rowsBefore + 1 && emptyCanvas.Slots[^1].Track == null, "the compact add action creates exactly one empty final track");
                    var beforeFooterHover = addOverlay.Bounds;
                    typeof(Control).GetMethod("OnMouseEnter", Private)!.Invoke(footer, [EventArgs.Empty]);
                    Check(addOverlay.Bounds == beforeFooterHover && footer.Controls.Count == 0, "the status footer neither hosts nor repositions an add-track row");
                    typeof(EditorForm).GetField("busy", Private)!.SetValue(emptyForm, true);
                    try
                    {
                        typeof(EditorForm).GetMethod("UpdateAddTrackOverlay", Private)!.Invoke(emptyForm, [Point.Empty]);
                        Check(!LocallyVisible(addOverlay), "busy operations hide the compact add-track action");
                    }
                    finally { typeof(EditorForm).GetField("busy", Private)!.SetValue(emptyForm, false); }
                    int scrollingRows = Math.Max(7, (int)Math.Ceiling(viewport.ClientSize.Height / (WaveCanvas.MinPaneHeight * emptyCanvas.DpiScale)) + 3);
                    while (emptyCanvas.Slots.Count < scrollingRows) emptyCanvas.Slots.Add(new());
                    typeof(EditorForm).GetMethod("RebuildTrackControls", Private)!.Invoke(emptyForm, null);
                    emptyCanvas.SetViewportHeight(viewport.ClientSize.Height); emptyCanvas.FitHeight(true);
                    _ = viewport.Handle; viewport.AutoScrollPosition = new Point(0, 120); viewport.PerformLayout();
                    // Hidden WinForms children need both scroll coordinates and child bounds set for an offscreen viewport.
                    typeof(ScrollableControl).GetMethod("SetDisplayRectLocation", Private)!.Invoke(viewport, [0, -120]);
                    emptyCanvas.Location = new Point(emptyCanvas.Left, -120);
                    typeof(EditorForm).GetMethod("UpdateAddTrackOverlay", Private)!.Invoke(emptyForm, [Point.Empty]);
                    Check(emptyCanvas.Height > viewport.ClientSize.Height && !LocallyVisible(addOverlay), "an offscreen last track does not show a floating full-width add row");
                }
                Check(All(form).OfType<NumericUpDown>().All(n => n.AccessibleName == "Precision zoom amount"), "empty editor has no numeric time fields");
                var precisionDisplay = All(canvas).OfType<ComboBox>().Single(combo => combo.AccessibleName == "Precision display mode");
                var trackDisplay = All(canvas).OfType<ComboBox>().Single(combo => combo.AccessibleName == "Track 1 display mode");
                Check(canvas.GetPaneViewMode(0) == AudioViewMode.Waveform && canvas.GetPaneViewMode(1) == AudioViewMode.Waveform && precisionDisplay.SelectedIndex == 0 && trackDisplay.SelectedIndex == 0, "precision and track selectors start in waveform mode");
                trackDisplay.SelectedIndex = 1;
                bool separateTrackMode = canvas.GetPaneViewMode(1) == AudioViewMode.Spectrogram && canvas.GetPaneViewMode(0) == AudioViewMode.Waveform;
                precisionDisplay.SelectedIndex = 1;
                Check(separateTrackMode && canvas.GetPaneViewMode(0) == AudioViewMode.Spectrogram && canvas.GetPaneViewMode(1) == AudioViewMode.Spectrogram, "track and precision display selectors control independent runtime modes");
                Invoke("RebuildTrackControls");
                trackDisplay = All(canvas).OfType<ComboBox>().Single(combo => combo.AccessibleName == "Track 1 display mode");
                Check(trackDisplay.SelectedIndex == 1 && canvas.Slots[0].ViewMode == AudioViewMode.Spectrogram && canvas.PrecisionViewMode == AudioViewMode.Spectrogram, "track control rebuilds retain the selected runtime display modes");
                trackDisplay.SelectedIndex = 0;
                All(canvas).OfType<ComboBox>().Single(combo => combo.AccessibleName == "Precision display mode").SelectedIndex = 0;
                using (var previewExtent = new WaveCanvas())
                {
                    var committedSamples = new float[200]; var expandedSamples = new float[400];
                    var committed = new AudioTrack { Path = "Preview extent.wav", Samples = committedSamples, Peaks = new PeakPyramid(committedSamples) };
                    previewExtent.Slots[0].Track = committed;
                    previewExtent.PreviewSources[committed] = new AudioTrack { Path = committed.Path, Samples = expandedSamples, Peaks = new PeakPyramid(expandedSamples) };
                    bool expanded = previewExtent.Range == (0L, 200L) && previewExtent.CommittedRange == (0L, 100L) && previewExtent.SelectionBounds == (0L, 100L);
                    previewExtent.PreviewSources.Clear();
                    Check(expanded && previewExtent.Range == previewExtent.CommittedRange, "expanded effect previews extend the visible timeline while default selections retain committed bounds");
                }
                var beatsToggle = ActionItem("Show beats and estimated BPM");
                var kicksToggle = ActionItem("Show likely kick markers");
                var snapToggle = All(form).OfType<CheckBox>().Single(c => c.AccessibleName == "Smart snap to detected beats");
                Check(!beatsToggle.Checked && !kicksToggle.Checked && !snapToggle.Checked && !canvas.ShowBeats && !canvas.ShowKicks && !canvas.SmartSnap, "rhythm overlays and smart snapping start switched off");
                beatsToggle.Checked = kicksToggle.Checked = snapToggle.Checked = true;
                Check(canvas.ShowBeats && canvas.ShowKicks && canvas.SmartSnap, "rhythm checkboxes enable their corresponding waveform options");
                beatsToggle.Checked = kicksToggle.Checked = snapToggle.Checked = false;
                Check(!canvas.ShowBeats && !canvas.ShowKicks && !canvas.SmartSnap, "rhythm checkboxes can turn all optional aids off again");
                Check(new[] { "Add track", "Play from cursor", "Play loop", "Selection", "View", "Scrub", "Refine selection end", "Fade in", "Fade out", "Undo", "Redo", "Export", "Export options", "Equal heights", "Save session and selections", "Open session and selections", "Record", "Keyboard shortcuts" }.All(name => ActionButton(name).AccessibleName == name)
                    && new[] { "Selection start", "Selection end", "Select all", "Clear selection", "Delete section", "Duplicate selection", "Trim to selection", "Save section cue points" }.All(name => ActionItem(name).AccessibleName == name), "toolbar and grouped menu actions have distinct accessible names");
                var finalToolbarAction = All(form).OfType<IconButton>().Last(button => button.Shortcut.Length > 0);
                Check(finalToolbarAction.AccessibleName == "Keyboard shortcuts" && finalToolbarAction.Shortcut == "F1", "the last toolbar icon offers keyboard help with its F1 hint");
                Check(!All(form).OfType<ComboBox>().Any(combo => combo.AccessibleName == "Saved selections"), "cue points replace the legacy saved-selection dropdown in the toolbar");
                var tooltips = (ToolTip)typeof(EditorForm).GetField("tips", Private)!.GetValue(form)!;
                string Tooltip(Control button) => tooltips.GetToolTip(button) ?? "";
                Check(All(form).OfType<IconButton>().Where(button => button.Shortcut.Length > 0).All(button => Tooltip(button).Length > 15 && Tooltip(button) != button.Text && Tooltip(button) != button.AccessibleName + " (" + button.Shortcut.Replace('\n', ' ') + ")"), "every top icon has a descriptive tooltip beyond its action name and shortcut");
                Check(ActionItem("Selection start").ToolTipText?.Contains('[') == true && ActionItem("Selection end").ToolTipText?.Contains(']') == true && Tooltip(ActionButton("Play from cursor")).Contains("Shift+Space"), "selection menu and playback tooltips retain their keyboard shortcuts");
                Check(ActionItem("Set export folder").ToolTipText?.Contains("choose", StringComparison.OrdinalIgnoreCase) == true, "the export-folder menu tooltip explains choosing the destination");
                Check(All(form).OfType<IconButton>().Where(button => button.Shortcut.Length > 0).All(button => button.Width >= 58 * button.DeviceDpi / 96 && button.Height >= 74 * button.DeviceDpi / 96), "toolbar buttons retain their physical size at the current DPI");
                var selectionIcon = ActionButton("Selection");
                using var iconRender = Render(selectionIcon);
                float iconScale = selectionIcon.DeviceDpi / 96f;
                bool HasInk(int top, int bottom)
                {
                    var background = iconRender.GetPixel(0, 0);
                    for (int y = (int)(top * iconScale); y < Math.Min(iconRender.Height, (int)(bottom * iconScale)); y++)
                        for (int x = (int)(4 * iconScale); x < iconRender.Width - (int)(4 * iconScale); x++)
                        {
                            var color = iconRender.GetPixel(x, y);
                            if (Math.Abs(color.R - background.R) > 20 || Math.Abs(color.G - background.G) > 20 || Math.Abs(color.B - background.B) > 20) return true;
                        }
                    return false;
                }
                Check(HasInk(5, 34) && !HasInk(35, 43) && HasInk(44, 71), "shortcut renders below the icon with a clear gap between them");
                var zoomAmount = All(form).OfType<NumericUpDown>().Single(n => n.AccessibleName == "Precision zoom amount");
                var zoomSlider = All(form).OfType<TrackBar>().Single(n => n.AccessibleName == "Precision zoom slider");
                zoomAmount.Text = 16.5m.ToString(System.Globalization.CultureInfo.CurrentCulture);
                Check(canvas.Magnification == 16.5 && zoomAmount.Value == 16.5m, "typing precision zoom updates the view without leaving the numeric field");
                var zoomText = zoomAmount.Controls.OfType<TextBox>().Single();
                zoomText.SelectAll(); var zoomTyping = new List<string>(); bool zoomReadsPreserved = true;
                string zoomInput = 16.5m.ToString(System.Globalization.CultureInfo.CurrentCulture);
                foreach (char digit in zoomInput)
                {
                    zoomText.SelectedText = digit.ToString(); string partial = zoomText.Text; int caret = zoomText.SelectionStart;
                    _ = zoomAmount.Value;
                    zoomReadsPreserved &= zoomText.Text == partial && zoomText.SelectionStart == caret;
                    zoomTyping.Add(zoomText.Text + "@" + zoomText.SelectionStart);
                }
                Console.WriteLine("Incremental zoom: " + string.Join(" | ", zoomTyping));
                Check(zoomReadsPreserved && zoomText.Text == zoomInput && zoomText.SelectionStart == zoomInput.Length && canvas.Magnification == 16.5, "incremental decimal zoom typing preserves partial text and caret through value reads");
                zoomAmount.Value = 32;
                Check(canvas.Magnification == 32 && zoomSlider.Value == 500, "precision zoom numeric input updates the waveform and slider");
                zoomSlider.Value = 600;
                Check(canvas.Magnification == 64 && zoomAmount.Value == 64, "precision zoom slider updates the waveform and numeric input");
                typeof(WaveCanvas).GetMethod("OnMouseWheel", Private)!.Invoke(canvas, [new MouseEventArgs(MouseButtons.None, 0, 0, 0, 120)]);
                Check(canvas.Magnification == 96 && zoomAmount.Value == 96 && zoomSlider.Value > 600 && zoomSlider.Value < 700, "mouse wheel keeps precision zoom controls synchronized");
                canvas.Magnification = 10000;
                bool upperClamped = canvas.Magnification == 2048 && zoomAmount.Value == 2048 && zoomSlider.Value == zoomSlider.Maximum;
                canvas.Magnification = 0;
                Check(upperClamped && canvas.Magnification == 1 && zoomAmount.Value == 1 && zoomSlider.Value == zoomSlider.Minimum, "precision zoom clamps both controls to its supported range");
                canvas.Magnification = 12;
                object[] restartArgs = [new Message(), Keys.Shift | Keys.Space];
                bool handledRestart = (bool)typeof(EditorForm).GetMethod("ProcessCmdKey", Private)!.Invoke(form, restartArgs)!;
                Check(handledRestart && typeof(EditorForm).GetField("output", Private)!.GetValue(form) == null, "Shift+Space is handled safely without a selection");
                bool handledRedo = true;
                foreach (Keys chord in new[] { Keys.Control | Keys.Y, Keys.Control | Keys.Shift | Keys.Z })
                    handledRedo &= (bool)typeof(EditorForm).GetMethod("ProcessCmdKey", Private)!.Invoke(form, [new Message(), chord])!;
                Check(handledRedo, "both redo shortcuts are handled safely with empty history");
                Check(ActionItem("Duplicate selection").ShortcutKeyDisplayString == "Ctrl+D" && ActionItem("Trim to selection").ShortcutKeyDisplayString == "Ctrl+T" && ((IconButton)ActionButton("Add track")).Shortcut == "Ctrl+=" && ((IconButton)ActionButton("Effects")).Shortcut == "Ctrl+E" && ActionItem("Clear selection").ShortcutKeyDisplayString == "Esc", "toolbar and menu hints match duplicate trim add-track effects and clear-selection bindings");
                using (var keyForm = new EditorForm([]))
                {
                    _ = keyForm.Handle;
                    var keyCanvas = (WaveCanvas)typeof(EditorForm).GetField("canvas", Private)!.GetValue(keyForm)!;
                    bool Key(Keys chord) => (bool)typeof(EditorForm).GetMethod("ProcessCmdKey", Private)!.Invoke(keyForm, [new Message(), chord])!;
                    Check(Key(Keys.Control | Keys.R) && Key(Keys.Control | Keys.T) && Key(Keys.Control | Keys.D) && keyForm.OwnedForms.Length == 0, "Reverse, Trim and Duplicate hotkeys are handled safely without loaded audio or visible dialogs");
                    bool added = true;
                    foreach (var chord in new[] { Keys.Control | Keys.Oemplus, Keys.Control | Keys.Shift | Keys.Oemplus, Keys.Control | Keys.Add })
                    { int count = keyCanvas.Slots.Count; added &= Key(chord) && keyCanvas.Slots.Count == count + 1; }
                    Check(added, "Ctrl+=, Ctrl+Plus and numeric Ctrl+Plus each add one empty track");
                    keyCanvas.SelectionStart = 1; keyCanvas.SelectionEnd = 2;
                    Check(Key(Keys.Escape) && keyCanvas.SelectionStart == null && keyCanvas.SelectionEnd == null, "Escape clears the loop through the keyboard route");
                }

                canvas.SetViewportHeight(900); canvas.FitHeight(true);
                Check(Math.Abs(canvas.GetPaneBounds(0).Height - canvas.GetPaneBounds(1).Height) <= 1, "precision and initial channel share height equally");
                var beforePrecision = canvas.GetPaneBounds(0); var beforeChannel = canvas.GetPaneBounds(1);
                canvas.ResizeBoundary(0, 30);
                Check(canvas.GetPaneBounds(0).Height == beforePrecision.Height + 30 && canvas.GetPaneBounds(1).Height == beforeChannel.Height - 30, "divider redistributes height between adjacent panes");
                canvas.ResizeBoundary(0, 100000);
                Check(canvas.GetPaneBounds(1).Height >= WaveCanvas.MinPaneHeight, "divider clamps the smallest channel height");
                canvas.SetViewportHeight(1200); canvas.FitHeight(true);
                Check(Math.Abs(canvas.GetPaneBounds(0).Height - canvas.GetPaneBounds(1).Height) <= 1, "resized viewport can restore equal pane heights");
                Click("Add track");
                Check(canvas.Slots.Count == 2 && canvas.Slots.All(slot => slot.Track == null), "Add track creates another empty channel");
                canvas.FitHeight(true);
                Check(Enumerable.Range(1, canvas.Slots.Count).All(i => Math.Abs(canvas.GetPaneBounds(i).Height - canvas.GetPaneBounds(0).Height) <= 1), "precision and all channels equalize together");

                float[] samples = new float[48000 * 2];
                for (int i = 0; i < samples.Length / 2; i++) samples[i * 2] = samples[i * 2 + 1] = .08f * (float)Math.Sin(i * .03);
                var track = new AudioTrack { Path = "Fixture.wav", Samples = samples, Peaks = new PeakPyramid(samples) };
                canvas.Slots[0].Track = track; Invoke("RebuildTrackControls");
                Check(canvas.Tracks.Count() == 1, "empty channels are excluded from loaded audio");
                var volume = All(canvas).OfType<NumericUpDown>().Single(n => n.AccessibleName == track.Name + " volume percent"); volume.Value = 1000;
                Check(track.Gain == 10 && All(canvas).OfType<TrackBar>().Single(n => n.AccessibleName == track.Name + " volume slider").Value == 1000, "1000 percent control updates actual gain and slider");
                volume.Text = "250";
                Check(track.Gain == 2.5f && All(canvas).OfType<TrackBar>().Single(n => n.AccessibleName == track.Name + " volume slider").Value == 250, "typing volume updates audio and slider without focus loss");
                volume.Text = "1000";
                var volumeText = volume.Controls.OfType<TextBox>().Single(); volumeText.SelectAll();
                foreach (char digit in "1000") volumeText.SelectedText = digit.ToString();
                Check(volumeText.Text == "1000" && volumeText.SelectionStart == 4 && track.Gain == 10, "incremental volume typing preserves all four digits and applies the final gain");
                ActionItem("Export format MP3").PerformClick();
                Check(form.SelectedExportExtension == ".mp3" && ActionItem("Export format MP3").Checked, "MP3 export menu controls the shared export format");
                ActionItem("Export format FLAC").PerformClick();
                Check(form.SelectedExportExtension == ".flac" && ActionItem("Export format FLAC").Checked && !ActionItem("Export format MP3").Checked, "export format radio menu keeps one selected format");

                canvas.SelectionStart = null; canvas.SelectionEnd = null; canvas.CursorFrame = 12000;
                Check(form.GetPlaybackBounds() == (0L, 48000L) && canvas.LastCursorFrame == 12000, "unselected playback retains full loop bounds and the hovered start position");
                canvas.SelectionEnd = 36000;
                Check(form.GetPlaybackBounds() == (0L, 36000L) && canvas.HasSelection, "end-only selection loops from timeline start to its end marker");
                canvas.SelectionStart = 0; canvas.SelectionEnd = 48000; Invoke("SelectionUpdated", false);
                Invoke("ApplyFade", true); Check(track.Fades.Count == 1 && track.Snapshot().Envelope(0) == 0, "applying Fade in changes the selected audio envelope");
                Invoke("ApplyFade", false); Check(track.Fades.Count == 2 && track.Snapshot().Envelope(47999) == 0, "applying Fade out compounds the envelope");
                Click("Undo"); Check(track.Fades.Count == 1, "Undo icon restores previous fade state");
                Click("Redo"); Check(track.Fades.Count == 2, "Redo icon reapplies the undone fade");
                Click("Undo");
                volume = All(canvas).OfType<NumericUpDown>().Single(n => n.AccessibleName == track.Name + " volume percent"); volume.Value = 0;
                Click("Undo"); Check(track.Gain == 10, "Undo restores volume and recreated controls");
                Click("Redo"); Check(track.Gain == 0, "Redo restores the undone volume change");
                Click("Undo");
                Invoke("Remember"); track.Offset = 1234;
                Click("Undo"); bool offsetUndone = track.Offset == 0;
                Click("Redo"); Check(offsetUndone && track.Offset == 1234, "Undo and Redo restore track timing changes");
                Click("Undo");
                volume = All(canvas).OfType<NumericUpDown>().Single(n => n.AccessibleName == track.Name + " volume percent"); volume.Value = 250;
                Click("Redo");
                Check(track.Gain == 2.5f && track.Offset == 0, "making a new edit invalidates the previous redo branch");
                Click("Undo");
                var beforeClear = track.Snapshot(); var samplesBeforeClear = track.Samples.ToArray();
                Click("Clear selection");
                Check(canvas.SelectionStart == null && canvas.SelectionEnd == null && track.Gain == beforeClear.Gain && track.Offset == beforeClear.Offset && track.Fades.SequenceEqual(beforeClear.Fades) && track.Samples.SequenceEqual(samplesBeforeClear), "clearing the loop removes markers without changing source audio or edits");

                track.Gain = .1f;
                var originalSamples = samples.ToArray();
                canvas.AutoMagnify = false;
                using var unscaled = Render(canvas);
                canvas.AutoMagnify = true;
                using var enlarged = Render(canvas);
                bool visuallyChanged = false;
                for (int y = 0; y < enlarged.Height && !visuallyChanged; y += 3)
                    for (int x = 0; x < enlarged.Width; x += 8)
                        if (unscaled.GetPixel(x, y) != enlarged.GetPixel(x, y)) { visuallyChanged = true; break; }
                Check(visuallyChanged, "vertical auto magnification enlarges a quiet waveform");
                Check(track.Gain == .1f && track.Samples.SequenceEqual(originalSamples), "vertical magnification preserves audio gain and samples");
                var darkAmplitude = WaveCanvas.AmplitudeColor(0, 0); var middleAmplitude = WaveCanvas.AmplitudeColor(0, .5f); var brightAmplitude = WaveCanvas.AmplitudeColor(0, 1);
                Check(darkAmplitude.GetBrightness() < middleAmplitude.GetBrightness() && middleAmplitude.GetBrightness() < brightAmplitude.GetBrightness(), "waveform amplitude shading progresses from dark to bright");
                Check(WaveCanvas.AmplitudeColor(0, -5) == darkAmplitude && WaveCanvas.AmplitudeColor(0, 5) == brightAmplitude && WaveCanvas.AmplitudeColor(1, 1) != brightAmplitude, "amplitude shading clamps its range and retains channel colors");
                using var gradientCanvas = new WaveCanvas { Size = new Size(1800, 900), PrecisionMagnifierEnabled = false };
                gradientCanvas.SetViewportHeight(900);
                var gradientSamples = new float[48000 * 2];
                for (int i = 0; i < gradientSamples.Length / 2; i++) gradientSamples[i * 2] = gradientSamples[i * 2 + 1] = (i % 1000) / 999f * (float)Math.Sin(i * .1);
                gradientCanvas.Slots[0].Track = new AudioTrack { Path = "Amplitude ramp.wav", Samples = gradientSamples, Peaks = new PeakPyramid(gradientSamples) };
                using var gradientRender = Render(gradientCanvas);
                int RenderedShadeCount(int pane)
                {
                    var logical = (Rectangle)typeof(WaveCanvas).GetMethod("WaveBounds", Private)!.Invoke(gradientCanvas, [pane])!;
                    var bounds = gradientCanvas.ToDevice(logical); var colors = new HashSet<int>();
                    float baseHue = (pane == 0 ? WaveCanvas.PrecisionAmplitudeColor(1) : brightAmplitude).GetHue();
                    for (int y = bounds.Top + 2; y < Math.Min(gradientRender.Height, bounds.Bottom - 2); y += 2)
                        for (int x = bounds.Left + 2; x < Math.Min(gradientRender.Width, bounds.Right - 2); x++)
                        {
                            var color = gradientRender.GetPixel(x, y); float hueDistance = Math.Abs(color.GetHue() - baseHue); hueDistance = Math.Min(hueDistance, 360 - hueDistance);
                            if (color.GetSaturation() > .25f && hueDistance < 20 && color.GetBrightness() > .12f) colors.Add(color.ToArgb());
                            if (colors.Count >= 8) return colors.Count;
                        }
                    return colors.Count;
                }
                int precisionShadeCount = RenderedShadeCount(0), channelShadeCount = RenderedShadeCount(1);
                Check(precisionShadeCount >= 8 && channelShadeCount >= 8, $"both precision and channel render actual amplitude shading ({precisionShadeCount}/{channelShadeCount} shades)");
                int magnifiedZeroY = gradientCanvas.ToDevice(new Rectangle(gradientCanvas.GetWaveBounds(1).Left, (int)Math.Round(gradientCanvas.GetAmplitudeTicks(1).Single(tick => tick.Percent == 0).Y), 1, 1)).Top;
                gradientCanvas.AutoMagnify = false;
                using var naturalGradient = Render(gradientCanvas);
                var gradientBody = gradientCanvas.ToDevice((Rectangle)typeof(WaveCanvas).GetMethod("WaveBounds", Private)!.Invoke(gradientCanvas, [1])!);
                int naturalZeroY = gradientCanvas.ToDevice(new Rectangle(gradientCanvas.GetWaveBounds(1).Left, (int)Math.Round(gradientCanvas.GetAmplitudeTicks(1).Single(tick => tick.Percent == 0).Y), 1, 1)).Top;
                int comparableShades = 0; bool shadesPreserved = true;
                for (int x = gradientBody.Left + 2; x < gradientBody.Right - 2; x++)
                {
                    var color = gradientRender.GetPixel(x, magnifiedZeroY); float hueDistance = Math.Abs(color.GetHue() - brightAmplitude.GetHue()); hueDistance = Math.Min(hueDistance, 360 - hueDistance);
                    if (color.GetSaturation() <= .25f || hueDistance >= 20 || color.GetBrightness() <= .12f) continue;
                    comparableShades++; shadesPreserved &= color == naturalGradient.GetPixel(x, naturalZeroY);
                }
                Check(comparableShades > 10 && shadesPreserved, "vertical magnification preserves the shading of actual audio levels");

                using var detached = new WaveCanvas { PrecisionMagnifierEnabled = false };
                detached.Slots[0].Track = track;
                detached.CursorFrame = 12000; detached.SetCursorBoundary(true);
                detached.CursorFrame = 36000; detached.SetCursorBoundary(false);
                Check(detached.SelectionStart == 12000 && detached.SelectionEnd == 36000, "cursor shortcuts use the previewed selection positions");
                detached.Size = new Size(1000, 700); detached.SetViewportHeight(700);
                var wave = (Rectangle)typeof(WaveCanvas).GetMethod("WaveBounds", Private)!.Invoke(detached, [1])!;
                var hoverPoint = detached.ToDevice(new Rectangle(wave.Left + wave.Width / 3, wave.Top + 20, 1, 1)).Location;
                typeof(WaveCanvas).GetMethod("OnMouseMove", Private)!.Invoke(detached, [new MouseEventArgs(MouseButtons.None, 0, hoverPoint.X, hoverPoint.Y, 0)]);
                long hoveredFrame = detached.CursorFrame!.Value;
                using (var cursorRender = Render(detached))
                {
                    int y = detached.ToDevice(new Rectangle(wave.Left, wave.Top + 10, 1, 1)).Top;
                    bool whiteGuide = Enumerable.Range(Math.Max(0, hoverPoint.X - 3), 7).Where(x => x < cursorRender.Width).Any(x => cursorRender.GetPixel(x, y).ToArgb() == Color.White.ToArgb());
                    Check(whiteGuide, "white cursor guide is rendered at the hovered waveform position");
                }
                typeof(WaveCanvas).GetMethod("OnMouseLeave", Private)!.Invoke(detached, [EventArgs.Empty]);
                detached.SetCursorBoundary(true);
                Check(detached.CursorFrame == hoveredFrame && detached.SelectionStart == hoveredFrame, "the white guide and toolbar selection retain the last waveform position after pointer leaves");
                var precisionWave = (Rectangle)typeof(WaveCanvas).GetMethod("WaveBounds", Private)!.Invoke(detached, [0])!;
                var precisionPoint = detached.ToDevice(new Rectangle(precisionWave.Left + precisionWave.Width / 3, precisionWave.Top + 20, 1, 1)).Location;
                typeof(WaveCanvas).GetMethod("OnMouseMove", Private)!.Invoke(detached, [new MouseEventArgs(MouseButtons.None, 0, precisionPoint.X, precisionPoint.Y, 0)]);
                long beforeWheelFrame = detached.CursorFrame!.Value;
                typeof(WaveCanvas).GetMethod("OnMouseWheel", Private)!.Invoke(detached, [new MouseEventArgs(MouseButtons.None, 0, precisionPoint.X, precisionPoint.Y, 120)]);
                using (var precisionRender = Render(detached))
                {
                    int y = detached.ToDevice(new Rectangle(precisionWave.Left, precisionWave.Top + 10, 1, 1)).Top;
                    bool guideAtPointer = Enumerable.Range(Math.Max(0, precisionPoint.X - 3), 7).Where(x => x < precisionRender.Width).Any(x => precisionRender.GetPixel(x, y).ToArgb() == Color.White.ToArgb());
                    Check(detached.CursorFrame != beforeWheelFrame && guideAtPointer, "precision wheel zoom keeps its white guide aligned with the pointer");
                }
                detached.Slots[0].Track = new AudioTrack { Path = "Silent.wav", Samples = new float[200], Peaks = new PeakPyramid(new float[200]) };
                detached.AutoMagnify = true; detached.Size = new Size(1000, 700); detached.SetViewportHeight(700);
                using var silentRender = Render(detached);
                Check(silentRender.Width > 0, "silent audio renders safely with auto magnification enabled");
                using var clampCanvas = new WaveCanvas { Size = new Size(1800, 900) };
                var shortSamples = new float[24000 * 2]; var longSamples = new float[96000 * 2];
                clampCanvas.Slots[0].Track = new AudioTrack { Path = "Short offset.wav", Samples = shortSamples, Peaks = new PeakPyramid(shortSamples), Offset = 12000 };
                clampCanvas.Slots.Add(new() { Track = new AudioTrack { Path = "Long.wav", Samples = longSamples, Peaks = new PeakPyramid(longSamples) } });
                clampCanvas.SetViewportHeight(900); clampCanvas.FitHeight(true);
                Check(clampCanvas.ClampCursorToTrack(0, 1) == 12000 && clampCanvas.ClampCursorToTrack(90000, 1) == 36000 && clampCanvas.ClampCursorToTrack(24000, 1) == 24000 && clampCanvas.ClampCursorToTrack(90000, 2) == 90000, "cursor clamping follows each track's offset and duration within a longer multitrack timeline");
                var shortWave = clampCanvas.GetWaveBounds(1);
                var blankPoint = clampCanvas.ToDevice(new Rectangle(shortWave.Right - 20, shortWave.Top + 20, 1, 1)).Location;
                typeof(WaveCanvas).GetMethod("OnMouseMove", Private)!.Invoke(clampCanvas, [new MouseEventArgs(MouseButtons.None, 0, blankPoint.X, blankPoint.Y, 0)]);
                Check(clampCanvas.CursorFrame == 36000, "hovering beyond a shorter sound clamps its guide to that sound's end");
                typeof(WaveCanvas).GetMethod("OnMouseLeave", Private)!.Invoke(clampCanvas, [EventArgs.Empty]);
                using (var retainedGuide = Render(clampCanvas))
                {
                    var guide = clampCanvas.ToDevice(new Rectangle(shortWave.Left + (int)Math.Round(shortWave.Width * .375), shortWave.Top + 10, 1, 1)).Location;
                    Check(clampCanvas.CursorFrame == 36000 && Enumerable.Range(guide.X - 2, 5).Any(x => retainedGuide.GetPixel(x, guide.Y).ToArgb() == Color.White.ToArgb()), "leaving the waveform keeps its clamped white guide visible");
                }

                using var snapCanvas = new WaveCanvas { Size = new Size(1800, 900) };
                snapCanvas.SetViewportHeight(900);
                var snapSamples = new float[48000 * 2];
                var snapTrack = new AudioTrack { Path = "Snap.wav", Samples = snapSamples, Peaks = new PeakPyramid(snapSamples), Rhythm = new RhythmAnalysis(240, [12000, 24000, 36000], []) };
                snapCanvas.Slots[0].Track = snapTrack;
                Check(snapCanvas.SnapToBeat(12050) == 12050, "smart snapping leaves positions unchanged when switched off");
                snapCanvas.SmartSnap = true;
                Check(snapCanvas.SnapToBeat(12050) == 12000, "smart snapping finds a nearby detected beat");
                Check(snapCanvas.SnapToBeat(18000) == 18000, "smart snapping preserves positions far from detected beats");
                Check(snapCanvas.SnapToBeat(12050, bypass: true) == 12050, "explicit snapping bypass preserves a precise manual position");
                snapTrack.Offset = 48000;
                Check(snapCanvas.SnapToBeat(60050) == 60000, "detected beats move with their sound offset");
                var snapWave = (Rectangle)typeof(WaveCanvas).GetMethod("WaveBounds", Private)!.Invoke(snapCanvas, [0])!;
                long precisionTolerance = Math.Max(1, (long)((snapCanvas.Range.Max - snapCanvas.Range.Min) * 12d / snapCanvas.Magnification / snapWave.Width));
                long nearPrecision = 60000 + Math.Max(1, precisionTolerance / 2), finePosition = 60000 + precisionTolerance * 2;
                Check(snapCanvas.SnapToBeat(nearPrecision, precision: true) == 60000 && snapCanvas.SnapToBeat(finePosition, precision: true) == finePosition && snapCanvas.SnapToBeat(finePosition) == 60000, "precision snapping uses the tighter tolerance of its zoomed view");
                snapTrack.Rhythm = null;
                Check(snapCanvas.SnapToBeat(60050) == 60050, "missing rhythm analysis does not move selections");
                snapTrack.Rhythm = new RhythmAnalysis(null, [], []);
                Check(snapCanvas.SnapToBeat(60050) == 60050, "audio without an estimated beat grid does not snap");
                snapTrack.Rhythm = new RhythmAnalysis(240, [12000, 24000, 36000], []);
                snapCanvas.Slots.Add(new() { Track = new AudioTrack { Path = "Other rhythm.wav", Samples = snapSamples, Peaks = new PeakPyramid(snapSamples), Rhythm = new RhythmAnalysis(160, [18000], []) } });
                snapCanvas.FitHeight();
                var otherWave = (Rectangle)typeof(WaveCanvas).GetMethod("WaveBounds", Private)!.Invoke(snapCanvas, [2])!;
                var otherPoint = snapCanvas.ToDevice(new Rectangle(otherWave.Left + otherWave.Width / 2, otherWave.Top + 20, 1, 1)).Location;
                typeof(WaveCanvas).GetMethod("OnMouseMove", Private)!.Invoke(snapCanvas, [new MouseEventArgs(MouseButtons.None, 0, otherPoint.X, otherPoint.Y, 0)]);
                Check(snapCanvas.SnapToBeat(60050) == 60050, "smart snapping follows the hovered channel instead of another sound's beats");

                using var handles = new WaveCanvas { Size = new Size(1800, 900) };
                handles.SetViewportHeight(900);
                var handleSamples = new float[200];
                handles.Slots[0].Track = new AudioTrack { Path = "Handle fixture.wav", Samples = handleSamples, Peaks = new PeakPyramid(handleSamples) };
                handles.SelectionStart = 20; handles.SelectionEnd = 80;
                var startHandle = handles.GetBoundaryHandleBounds(true, 1); var endHandle = handles.GetBoundaryHandleBounds(false, 1);
                Check(!startHandle.IsEmpty && !endHandle.IsEmpty && startHandle.Left < endHandle.Left && handles.GetPaneBounds(1).Contains(startHandle) && handles.GetPaneBounds(1).Contains(endHandle), "selection brackets are independently visible beside their boundaries");
                handles.MoveSelectionBoundary(true, 25); handles.MoveSelectionBoundary(false, 75);
                Check(handles.SelectionStart == 25 && handles.SelectionEnd == 75, "moving bracket handles adjusts the intended selection edge");
                handles.MoveSelectionBoundary(true, 99); handles.MoveSelectionBoundary(false, 0);
                Check(handles.SelectionStart == 74 && handles.SelectionEnd == 75 && handles.Slots[0].Track!.Samples.SequenceEqual(handleSamples), "handles cannot cross and retain at least one audio frame");
                handles.SelectionStart = 50; handles.SelectionEnd = 51;
                Check(!handles.GetBoundaryHandleBounds(true, 1).IntersectsWith(handles.GetBoundaryHandleBounds(false, 1)), "very close bracket handles remain independently reachable");

                using var precisionPointer = new WaveCanvas { Size = new Size(1800, 900) };
                precisionPointer.SetViewportHeight(900);
                var pointerWave = (Rectangle)typeof(WaveCanvas).GetMethod("WaveBounds", Private)!.Invoke(precisionPointer, [1])!;
                var ordinaryPoint = new Point(pointerWave.Left + 100, pointerWave.Top + 20);
                Check(precisionPointer.PrecisionPointer(ordinaryPoint, false) == ordinaryPoint, "ordinary pointer movement preserves its horizontal position");
                var fineRight = precisionPointer.PrecisionPointer(new Point(ordinaryPoint.X + 100, ordinaryPoint.Y + 10), true);
                double preciseRight = precisionPointer.PrecisePointerX;
                var fineFurther = precisionPointer.PrecisionPointer(new Point(ordinaryPoint.X + 200, ordinaryPoint.Y + 20), true);
                double preciseFurther = precisionPointer.PrecisePointerX;
                var fineLeft = precisionPointer.PrecisionPointer(new Point(ordinaryPoint.X + 100, ordinaryPoint.Y + 40), true);
                double expectedFineDelta = 10 / precisionPointer.Magnification;
                Check(Math.Abs(preciseRight - ordinaryPoint.X - expectedFineDelta) < .000001 && Math.Abs(preciseFurther - ordinaryPoint.X - 2 * expectedFineDelta) < .000001 && Math.Abs(precisionPointer.PrecisePointerX - ordinaryPoint.X - expectedFineDelta) < .000001 && fineRight.Y == ordinaryPoint.Y + 10 && fineFurther.Y == ordinaryPoint.Y + 20 && fineLeft.Y == ordinaryPoint.Y + 40, "Shift and zoom retain fractional horizontal movement in both directions while preserving vertical motion");
                var releasedPoint = new Point(ordinaryPoint.X + 220, ordinaryPoint.Y + 50);
                double beforeRelease = precisionPointer.PrecisePointerX;
                var releasedGuide = precisionPointer.PrecisionPointer(releasedPoint, false);
                Check(Math.Abs(precisionPointer.PrecisePointerX - beforeRelease - 120 / Math.Sqrt(precisionPointer.Magnification / 2)) < .000001 && releasedGuide.Y == releasedPoint.Y,
                    "releasing Shift continues lower-wave zoom-scaled movement from the current precise position");

                using var zoomPointer = new WaveCanvas { Size = new Size(1800, 900) };
                zoomPointer.SetViewportHeight(900); zoomPointer.Magnification = 8;
                var fineWave = (Rectangle)typeof(WaveCanvas).GetMethod("WaveBounds", Private)!.Invoke(zoomPointer, [0])!;
                var mainWave = (Rectangle)typeof(WaveCanvas).GetMethod("WaveBounds", Private)!.Invoke(zoomPointer, [1])!;
                var anchor = new Point(fineWave.Left + 100, fineWave.Top + 20);
                foreach (double magnification in new[] { 2d, 8, 2048 })
                    foreach (bool shift in new[] { false, true })
                    {
                        zoomPointer.Magnification = magnification; zoomPointer.PrecisionPointer(anchor, false);
                        var rightPoint = new Point(anchor.X + 160, anchor.Y + 10);
                        var leftPoint = new Point(anchor.X + 40, anchor.Y + 20);
                        bool rightIsDirect = zoomPointer.PrecisionPointer(rightPoint, shift) == rightPoint && zoomPointer.PrecisePointerX == rightPoint.X;
                        Check(rightIsDirect && zoomPointer.PrecisionPointer(leftPoint, shift) == leftPoint && zoomPointer.PrecisePointerX == leftPoint.X, $"top precision pointer maps directly in both directions at {magnification}x with Shift {shift}");
                    }
                zoomPointer.Magnification = 8;
                var mainAnchor = new Point(mainWave.Left + 100, mainWave.Top + 20);
                zoomPointer.PrecisionPointer(mainAnchor, false);
                var mainSpeed = zoomPointer.PrecisionPointer(new Point(mainAnchor.X + 80, mainAnchor.Y + 5), false);
                Check(mainSpeed == new Point(mainAnchor.X + 40, mainAnchor.Y + 5), "main waveform movement uses half speed at eight-times zoom while preserving vertical motion");
                _ = zoomPointer.Handle;
                try
                {
                    typeof(WaveCanvas).GetField("pressedLens", Private)!.SetValue(zoomPointer, false); zoomPointer.Capture = true;
                    zoomPointer.PrecisionPointer(mainAnchor, false);
                    zoomPointer.PrecisionPointer(new Point(mainAnchor.X + 80, mainAnchor.Y), true);
                }
                finally { zoomPointer.Capture = false; }
                var enteredTop = new Point(anchor.X + 200, anchor.Y);
                Check(zoomPointer.PrecisionPointer(enteredTop, true) == enteredTop && zoomPointer.PrecisePointerX == enteredTop.X, "entering top precision after a captured lower fine gesture immediately uses the physical point");
                try
                {
                    typeof(WaveCanvas).GetField("pressedLens", Private)!.SetValue(zoomPointer, true); zoomPointer.Capture = true;
                    var capturedTop = new Point(anchor.X + 240, anchor.Y + 10);
                    var capturedLeft = new Point(anchor.X + 60, anchor.Y + 20);
                    bool capturedRight = zoomPointer.PrecisionPointer(capturedTop, true) == capturedTop;
                    Check(zoomPointer.Capture && capturedRight && zoomPointer.PrecisionPointer(capturedLeft, true) == capturedLeft && zoomPointer.PrecisePointerX == capturedLeft.X, "captured top precision gestures retain direct pointer mapping with Shift");
                }
                finally { zoomPointer.Capture = false; }
                using var fractionalPointer = new WaveCanvas { Size = new Size(1800, 900) };
                fractionalPointer.SetViewportHeight(900); fractionalPointer.Magnification = 8;
                var fractionalMain = fractionalPointer.GetWaveBounds(1);
                var fractionalAnchor = new Point(fractionalMain.Left + 100, fractionalMain.Top + 20);
                fractionalPointer.PrecisionPointer(fractionalAnchor, false);
                long beforeFineFrame = fractionalPointer.PointerAudioFrame(false); bool subpixelAudioChange = false;
                for (int delta = 1; delta <= 10; delta++)
                {
                    var drawn = fractionalPointer.PrecisionPointer(new Point(fractionalAnchor.X + delta, fractionalAnchor.Y), true);
                    subpixelAudioChange |= drawn.X == fractionalAnchor.X && fractionalPointer.PointerAudioFrame(false) != beforeFineFrame;
                }
                Check(subpixelAudioChange && Math.Abs(fractionalPointer.PrecisePointerX - fractionalAnchor.X - .125) < .000001, "fractional Shift movement selects new audio samples before the guide moves a whole screen pixel");

                using var panCanvas = new WaveCanvas { Size = new Size(1800, 900) };
                panCanvas.SetViewportHeight(900); panCanvas.Magnification = 8;
                var panSamples = new float[48000 * 2];
                panCanvas.Slots[0].Track = new AudioTrack { Path = "Pan.wav", Samples = panSamples, Peaks = new PeakPyramid(panSamples) };
                typeof(WaveCanvas).GetField("lensCenter", Private)!.SetValue(panCanvas, 24000L);
                var panWave = (Rectangle)typeof(WaveCanvas).GetMethod("WaveBounds", Private)!.Invoke(panCanvas, [0])!;
                var beforePan = panCanvas.PrecisionViewRange;
                Check(panCanvas.PanPrecisionAtEdge(panWave.Left + 1, .5) && panCanvas.PrecisionViewRange.Min < beforePan.Min, "precision left-edge panning reveals earlier audio");
                var afterLeftPan = panCanvas.PrecisionViewRange;
                Check(panCanvas.PanPrecisionAtEdge(panWave.Right - 2, .5) && panCanvas.PrecisionViewRange.Min > afterLeftPan.Min, "precision right-edge panning reveals later audio");
                var beforeMiddlePan = panCanvas.PrecisionViewRange;
                Check(!panCanvas.PanPrecisionAtEdge(panWave.Left + panWave.Width / 2, .5) && panCanvas.PrecisionViewRange == beforeMiddlePan, "the middle of precision view does not pan its audio");
                for (int i = 0; i < 100; i++) panCanvas.PanPrecisionAtEdge(panWave.Left + 1, .5);
                bool leftClamped = panCanvas.PrecisionViewRange.Min == panCanvas.Range.Min;
                for (int i = 0; i < 100; i++) panCanvas.PanPrecisionAtEdge(panWave.Right - 2, .5);
                Check(leftClamped && panCanvas.PrecisionViewRange.Max == panCanvas.Range.Max, "precision panning clamps at both timeline ends");
                var edgeTimer = (System.Windows.Forms.Timer)typeof(WaveCanvas).GetField("edgeScroll", Private)!.GetValue(panCanvas)!;
                var updateEdge = typeof(WaveCanvas).GetMethod("UpdateEdgeScrolling", Private)!;
                var edgePoint = new Point(panWave.Left + 1, panWave.Top + 20);
                updateEdge.Invoke(panCanvas, [edgePoint]);
                bool edgeTimerStarted = edgeTimer.Enabled;
                bool leftPanCursor = panCanvas.Cursor == Cursors.PanWest;
                typeof(WaveCanvas).GetMethod("OnMouseCaptureChanged", Private)!.Invoke(panCanvas, [EventArgs.Empty]);
                Check(edgeTimerStarted && leftPanCursor && !edgeTimer.Enabled, "left-edge panning shows its direction and capture loss stops its timer");
                typeof(WaveCanvas).GetField("resizeBoundary", Private)!.SetValue(panCanvas, 0);
                updateEdge.Invoke(panCanvas, [edgePoint]);
                Check(!edgeTimer.Enabled, "resizing panes cannot start precision edge panning");
                typeof(WaveCanvas).GetField("resizeBoundary", Private)!.SetValue(panCanvas, -1);
                _ = panCanvas.Handle;
                try
                {
                    typeof(WaveCanvas).GetField("pressedLens", Private)!.SetValue(panCanvas, false);
                    panCanvas.Capture = true;
                    var beforeCapturedEdge = panCanvas.PrecisionViewRange;
                    updateEdge.Invoke(panCanvas, [edgePoint]);
                    Check(panCanvas.Capture && !edgeTimer.Enabled && panCanvas.PrecisionViewRange == beforeCapturedEdge, "a drag originating on a main channel cannot start precision edge panning");
                }
                finally { panCanvas.Capture = false; }
                typeof(WaveCanvas).GetField("lensCenter", Private)!.SetValue(panCanvas, 24000L);
                var slowPanStart = panCanvas.PrecisionViewRange;
                panCanvas.PanPrecisionAtEdge(panWave.Left + 1, .1, .12);
                long slowPanDistance = slowPanStart.Min - panCanvas.PrecisionViewRange.Min;
                typeof(WaveCanvas).GetField("lensCenter", Private)!.SetValue(panCanvas, 24000L);
                var fullPanStart = panCanvas.PrecisionViewRange;
                panCanvas.PanPrecisionAtEdge(panWave.Left + 1, .1);
                long fullPanDistance = fullPanStart.Min - panCanvas.PrecisionViewRange.Min;
                Check(slowPanDistance > 0 && fullPanDistance > slowPanDistance && fullPanDistance <= Math.Ceiling((fullPanStart.Max - fullPanStart.Min) * .45 * .1), "precision edge speed can ramp from a small immediate movement to a moderate bounded speed");
                updateEdge.Invoke(panCanvas, [new Point(panWave.Right - 2, panWave.Top + 20)]);
                Check(edgeTimer.Enabled && panCanvas.Cursor == Cursors.PanEast, "right-edge panning shows its direction cursor");
                typeof(WaveCanvas).GetMethod("OnMouseCaptureChanged", Private)!.Invoke(panCanvas, [EventArgs.Empty]);
                using var emptyPan = new WaveCanvas { Size = new Size(1800, 900) };
                emptyPan.SetViewportHeight(900);
                Check(!emptyPan.PanPrecisionAtEdge(WaveCanvas.LeftGutter + 1, .5), "an empty precision view does not pan nonexistent audio");

                using var nudgeCanvas = new WaveCanvas { Size = new Size(1800, 900) };
                nudgeCanvas.SetViewportHeight(900); nudgeCanvas.Magnification = 8;
                nudgeCanvas.Slots[0].Track = new AudioTrack { Path = "Nudge.wav", Samples = panSamples, Peaks = new PeakPyramid(panSamples), Rhythm = new RhythmAnalysis(120, [24000], []) };
                var nudgeWave = (Rectangle)typeof(WaveCanvas).GetMethod("WaveBounds", Private)!.Invoke(nudgeCanvas, [0])!;
                nudgeCanvas.CursorFrame = 24000;
                bool mainRight = nudgeCanvas.NudgeCursor(1, false) && nudgeCanvas.CursorFrame == 24001;
                Check(mainRight && nudgeCanvas.NudgeCursor(-1, false) && nudgeCanvas.CursorFrame == 24000, "arrow nudges move exactly one audio sample in either direction on a main waveform");
                nudgeCanvas.CursorFrame = nudgeCanvas.Range.Min;
                bool mainLeftClamped = !nudgeCanvas.NudgeCursor(-1, false) && nudgeCanvas.CursorFrame == nudgeCanvas.Range.Min;
                nudgeCanvas.CursorFrame = nudgeCanvas.Range.Max;
                Check(mainLeftClamped && !nudgeCanvas.NudgeCursor(1, false) && nudgeCanvas.CursorFrame == nudgeCanvas.Range.Max, "main waveform nudges clamp at both timeline edges");
                typeof(WaveCanvas).GetField("pointerInPrecision", Private)!.SetValue(nudgeCanvas, true);
                typeof(WaveCanvas).GetField("lensCenter", Private)!.SetValue(nudgeCanvas, 24000L);
                var nudgeView = nudgeCanvas.PrecisionViewRange;
                nudgeCanvas.CursorFrame = 24000;
                bool precisionRight = nudgeCanvas.NudgeCursor(1, false) && nudgeCanvas.CursorFrame == 24001;
                Check(precisionRight && nudgeCanvas.NudgeCursor(-1, false) && nudgeCanvas.CursorFrame == 24000, "precision arrow nudges move exactly one audio sample in either direction at 8x zoom");
                nudgeCanvas.Magnification = 2048; nudgeCanvas.SmartSnap = true; nudgeCanvas.CursorFrame = 24000;
                bool oneSampleRight = nudgeCanvas.NudgeCursor(1, false) && nudgeCanvas.CursorFrame == 24001;
                var nudgedLogicalPoint = (Point)typeof(WaveCanvas).GetField("lastPointer", Private)!.GetValue(nudgeCanvas)!;
                var nudgedDevicePoint = new Point((int)Math.Round(nudgedLogicalPoint.X * nudgeCanvas.DpiScale), (int)Math.Round(nudgedLogicalPoint.Y * nudgeCanvas.DpiScale));
                typeof(WaveCanvas).GetField("nudgedPointer", Private)!.SetValue(nudgeCanvas, (Point?)nudgedDevicePoint);
                typeof(WaveCanvas).GetMethod("OnMouseMove", Private)!.Invoke(nudgeCanvas, [new MouseEventArgs(MouseButtons.None, 0, nudgedDevicePoint.X, nudgedDevicePoint.Y, 0)]);
                Check(nudgeCanvas.CursorFrame == 24001 && (bool)typeof(WaveCanvas).GetField("pointerInPrecision", Private)!.GetValue(nudgeCanvas)! && typeof(WaveCanvas).GetField("nudgedPointer", Private)!.GetValue(nudgeCanvas) == null, "the matching key-positioned pointer event preserves the exact sample and consumes its marker");
                Check(oneSampleRight && nudgeCanvas.NudgeCursor(-1, false) && nudgeCanvas.CursorFrame == 24000, "sub-sample pixels still nudge exactly one sample and bypass smart snapping");
                typeof(WaveCanvas).GetField("lensCenter", Private)!.SetValue(nudgeCanvas, nudgeCanvas.Range.Min);
                nudgeView = nudgeCanvas.PrecisionViewRange; nudgeCanvas.CursorFrame = nudgeView.Min;
                bool precisionLeftClamped = !nudgeCanvas.NudgeCursor(-1, false) && nudgeCanvas.CursorFrame == nudgeView.Min;
                typeof(WaveCanvas).GetField("lensCenter", Private)!.SetValue(nudgeCanvas, nudgeCanvas.Range.Max);
                nudgeView = nudgeCanvas.PrecisionViewRange;
                nudgeCanvas.CursorFrame = nudgeView.Max - 1;
                bool reachesPrecisionRight = nudgeCanvas.NudgeCursor(1, false) && nudgeCanvas.CursorFrame == nudgeView.Max;
                var rightEndpoint = (Point)typeof(WaveCanvas).GetField("lastPointer", Private)!.GetValue(nudgeCanvas)!;
                Check(precisionLeftClamped && reachesPrecisionRight && rightEndpoint.X == nudgeWave.Right - 1 && !nudgeCanvas.NudgeCursor(1, false) && nudgeCanvas.CursorFrame == nudgeView.Max, "precision nudges clamp at both timeline ends and keep the pointer inside its waveform");
                emptyPan.CursorFrame = 1;
                Check(!emptyPan.NudgeCursor(1, false), "an empty channel cannot nudge into nonexistent audio");
                Check(nudgeCanvas.JumpCursor(true, false) && nudgeCanvas.CursorFrame == nudgeCanvas.Range.Max && nudgeCanvas.JumpCursor(false, false) && nudgeCanvas.CursorFrame == nudgeCanvas.Range.Min && !emptyPan.JumpCursor(true, false), "Home and End cursor helpers reach exact timeline boundaries without moving the system pointer");

                using var gridCanvas = new WaveCanvas { Size = new Size(1800, 900) };
                gridCanvas.SetViewportHeight(900);
                bool HasDenseGrid(Bitmap bitmap, int pane, int minimumHorizontalLines)
                {
                    var b = gridCanvas.GetWaveBounds(pane);
                    var ticks = gridCanvas.GetAmplitudeTicks(pane);
                    int divisions = Math.Max(4, b.Width / 40);
                    var gap = ticks.Zip(ticks.Skip(1), (first, second) => (first.Y + second.Y) / 2).First(y => Math.Abs(y - (b.Top + b.Height / 2f)) > 20);
                    int sampleX = gridCanvas.ToDevice(new Rectangle((int)Math.Round(b.Left + b.Width / (divisions * 2f)), b.Top, 1, 1)).X;
                    int sampleY = gridCanvas.ToDevice(new Rectangle(b.Left, (int)Math.Round(gap), 1, 1)).Y;
                    var device = gridCanvas.ToDevice(b);
                    bool GridPixel(Color pixel) => pixel.R >= 32 && pixel.G >= 41 && pixel.B >= 51;
                    int Runs(IEnumerable<Color> pixels)
                    {
                        int count = 0; bool inLine = false;
                        foreach (var pixel in pixels)
                        { bool line = GridPixel(pixel); if (line && !inLine) count++; inLine = line; }
                        return count;
                    }
                    int vertical = Runs(Enumerable.Range(device.Left + 2, device.Width - 4).Select(x => bitmap.GetPixel(x, sampleY)));
                    int horizontal = Runs(Enumerable.Range(device.Top + 1, device.Height - 2).Select(y => bitmap.GetPixel(sampleX, y)));
                    return vertical >= 12 && horizontal >= minimumHorizontalLines;
                }
                using (var gridRender = Render(gridCanvas))
                {
                    Check(HasDenseGrid(gridRender, 0, 10) && HasDenseGrid(gridRender, 1, 10), "precision and channel views render dense horizontal and vertical gridlines");
                    bool LabelInk(int pane)
                    {
                        var b = gridCanvas.GetWaveBounds(pane);
                        var labels = gridCanvas.GetAmplitudeTicks(pane).Where(tick => tick.Label.Length > 0).ToArray();
                        return labels.Length >= 3 && labels.All(tick =>
                        {
                            int y = Math.Clamp((int)Math.Round(tick.Y) - 7, b.Top, b.Bottom - 14);
                            var area = gridCanvas.ToDevice(new Rectangle(b.Right + 7, y, WaveCanvas.AmplitudeGutter - 9, 14));
                            return Enumerable.Range(area.Top, area.Height).Any(py => Enumerable.Range(area.Left, area.Width).Any(px => gridRender.GetPixel(px, py).GetBrightness() > .3f));
                        });
                    }
                    Check(LabelInk(0) && LabelInk(1), "both amplitude gutters render readable percentage labels beside the grid");
                }
                var unscaledTicks = gridCanvas.GetAmplitudeTicks(1);
                Check(unscaledTicks.Any(tick => tick.Label == "0%") && unscaledTicks.Any(tick => tick.Label.StartsWith('+')) && unscaledTicks.Any(tick => tick.Label.StartsWith('-')) && unscaledTicks.All(tick => tick.Major == (tick.Label.Length > 0)), "amplitude labels include zero and signed positive and negative percentages");
                Check(unscaledTicks.All(tick => tick.Percent >= -100 && tick.Percent <= 100) && unscaledTicks.All(tick => Math.Abs(tick.Percent + unscaledTicks.OrderBy(candidate => Math.Abs(candidate.Percent + tick.Percent)).First().Percent) < .00001), "natural amplitude grid stays within full scale and remains symmetric around zero");
                var fullBounds = gridCanvas.GetWaveBounds(1);
                Check(gridCanvas.GetAmplitudeRange(1) == (-1f, 1f) && unscaledTicks.Single(tick => tick.Percent == 100).Y == fullBounds.Top && unscaledTicks.Single(tick => tick.Percent == -100).Y == fullBounds.Bottom - 1, "natural full-scale amplitude endpoints use the entire waveform height");
                var quietSamples = Enumerable.Range(0, 48000).SelectMany(index => new[] { index % 2 == 0 ? .05f : -.05f, 0f }).ToArray();
                var quietOriginal = quietSamples.ToArray();
                var quietTrack = new AudioTrack { Path = "Quiet amplitude.wav", Samples = quietSamples, Peaks = new PeakPyramid(quietSamples) };
                gridCanvas.Slots[0].Track = quietTrack; gridCanvas.AutoMagnify = true;
                var quietTicks = gridCanvas.GetAmplitudeTicks(1); var quietBounds = gridCanvas.GetWaveBounds(1);
                var quietRange = gridCanvas.GetAmplitudeRange(1);
                Check(Math.Abs(quietRange.Min + .05) < .000001 && Math.Abs(quietRange.Max - .05) < .000001 && quietTicks.Max(tick => tick.Percent) < 10 && quietTicks.Single(tick => Math.Abs(tick.Percent - 5) < .00001).Y == quietBounds.Top && quietTicks.Single(tick => Math.Abs(tick.Percent + 5) < .00001).Y == quietBounds.Bottom - 1, "auto-magnified quiet waveforms use their actual peak and trough across the full plot height");
                Check(gridCanvas.GetAmplitudeTicks(0).Select(tick => tick.Percent).SequenceEqual(quietTicks.Select(tick => tick.Percent)), "precision and channel grids share the same actual amplitude scale");
                using (var quietRender = Render(gridCanvas))
                {
                    var peakTick = quietTicks.Single(tick => Math.Abs(tick.Percent - 5) < .00001);
                    var peakPoint = gridCanvas.ToDevice(new Rectangle(quietBounds.Left + quietBounds.Width / 3, (int)Math.Round(peakTick.Y), 1, 1)).Location;
                    Color expectedPeakColor = WaveCanvas.AmplitudeColor(0, .05f);
                    bool waveformAtPeak = Enumerable.Range(peakPoint.Y - 2, 5).Any(y => Enumerable.Range(peakPoint.X, 8).Any(x =>
                    { var pixel = quietRender.GetPixel(x, y); return Math.Abs(pixel.R - expectedPeakColor.R) <= 4 && Math.Abs(pixel.G - expectedPeakColor.G) <= 4 && Math.Abs(pixel.B - expectedPeakColor.B) <= 4; }));
                    Check(waveformAtPeak, "the rendered quiet waveform peak aligns with its actual five-percent gridline");
                }
                quietTrack.Gain = 2;
                var gainRange = gridCanvas.GetAmplitudeRange(1);
                Check(Math.Abs(gainRange.Min - 2 * quietRange.Min) < .000001 && Math.Abs(gainRange.Max - 2 * quietRange.Max) < .000001 && quietTrack.Samples.SequenceEqual(quietOriginal), "amplitude percentages follow the track volume without altering its samples");
                gridCanvas.AutoMagnify = false;
                Check(gridCanvas.GetAmplitudeTicks(1).Select(tick => tick.Percent).SequenceEqual(unscaledTicks.Select(tick => tick.Percent)), "turning auto magnification off restores the full-scale amplitude grid");
                var positiveSamples = Enumerable.Repeat(.1f, 200).ToArray();
                gridCanvas.Slots[0].Track = new AudioTrack { Path = "Positive amplitude.wav", Samples = positiveSamples, Peaks = new PeakPyramid(positiveSamples) }; gridCanvas.AutoMagnify = true;
                var positiveRange = gridCanvas.GetAmplitudeRange(1); var positiveTicks = gridCanvas.GetAmplitudeTicks(1);
                Check(positiveRange.Min == 0 && Math.Abs(positiveRange.Max - .1) < .000001 && positiveTicks.Single(tick => tick.Percent == 0).Y == quietBounds.Bottom - 1 && positiveTicks.Single(tick => Math.Abs(tick.Percent - 10) < .00001).Y == quietBounds.Top, "one-sided audio keeps zero at the plot edge and places its actual maximum at the opposite edge");
                gridCanvas.Slots[0].Track = null;
                gridCanvas.AutoMagnify = false;
                gridCanvas.SetViewportHeight(2 * WaveCanvas.MinPaneHeight + WaveCanvas.SplitterHeight + 16); gridCanvas.FitHeight(true);
                using (var minimumGrid = Render(gridCanvas))
                    Check(gridCanvas.GetPaneBounds(1).Height == WaveCanvas.MinPaneHeight && HasDenseGrid(minimumGrid, 0, 5) && HasDenseGrid(minimumGrid, 1, 5), "minimum-height panes retain dense readable grids");
                Check(gridCanvas.GetAmplitudeTicks(1).All(tick => tick.Y >= gridCanvas.GetWaveBounds(1).Top && tick.Y <= gridCanvas.GetWaveBounds(1).Bottom && (tick.Label.Length == 0 || double.TryParse(tick.Label.TrimEnd('%'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var percent) && Math.Abs(percent - tick.Percent) < .00001)), "amplitude labels remain inside minimum panes and report their tick percentages");

                using (var pitchDialog = new EffectDialog(EffectKind.PitchShift))
                {
                    All(pitchDialog).OfType<CheckBox>().Single(checkbox => checkbox.AccessibleName == "Automate effect parameters").Checked = true;
                    var units = All(pitchDialog).OfType<ComboBox>().Single();
                    var startPitch = All(pitchDialog).OfType<NumericUpDown>().Single(n => n.AccessibleName == "Start pitch");
                    var endPitch = All(pitchDialog).OfType<NumericUpDown>().Single(n => n.AccessibleName == "End pitch");
                    units.SelectedItem = "Percent"; startPitch.Value = 100; endPitch.Value = 200;
                    Check(Math.Abs(pitchDialog.Settings.StartSemitones) < .0001 && Math.Abs(pitchDialog.Settings.EndSemitones - 12) < .0001, "pitch percent controls map 100 percent to unison and 200 percent to an octave");
                    startPitch.Value = 50; endPitch.Value = 400; units.SelectedItem = "Semitones";
                    Check(startPitch.Value == -12 && endPitch.Value == 24 && pitchDialog.Settings.StartSemitones == -12 && pitchDialog.Settings.EndSemitones == 24, "pitch unit conversion preserves the start and end sweep values");
                }
                using (var filterDialog = new EffectDialog(EffectKind.Filter))
                {
                    All(filterDialog).OfType<CheckBox>().Single(checkbox => checkbox.AccessibleName == "Automate effect parameters").Checked = true;
                    var mode = All(filterDialog).OfType<ComboBox>().Single();
                    All(filterDialog).OfType<NumericUpDown>().Single(n => n.AccessibleName == "Start frequency Hz").Value = 250;
                    All(filterDialog).OfType<NumericUpDown>().Single(n => n.AccessibleName == "End frequency Hz").Value = 5000;
                    mode.SelectedItem = "High pass";
                    bool highPass = filterDialog.Settings.HighPass && filterDialog.Settings.StartHz == 250 && filterDialog.Settings.EndHz == 5000;
                    mode.SelectedItem = "Low pass";
                    Check(highPass && !filterDialog.Settings.HighPass && filterDialog.Settings.EndHz == 5000, "filter mode changes retain the separate start and end frequency controls");
                }

                using var editForm = new EditorForm([]);
                _ = editForm.Handle;
                typeof(EditorForm).GetField("outputFactory", Private)!.SetValue(editForm, (Func<IWavePlayer>)(() => new SilentPlayer()));
                var editCanvas = (WaveCanvas)typeof(EditorForm).GetField("canvas", Private)!.GetValue(editForm)!;
                var store = (SelectionStore)typeof(EditorForm).GetField("selections", Private)!.GetValue(editForm)!;
                void EditInvoke(string name, params object[] values) => typeof(EditorForm).GetMethod(name, Private, null, values.Select(v => v.GetType()).ToArray(), null)!.Invoke(editForm, values);
                void EditClick(string name) => InvokeAction(editForm, name);
                var editingSamples = Enumerable.Range(0, 10).SelectMany(i => new[] { i / 10f, -i / 10f }).ToArray(); var originalEditingSamples = editingSamples.ToArray();
                editCanvas.Slots[0].Track = new AudioTrack { Path = "Edit fixture.wav", Samples = editingSamples, Peaks = new PeakPyramid(editingSamples) };
                var secondarySamples = new float[8];
                editCanvas.Slots.Add(new() { Track = new AudioTrack { Path = "Inside cut.wav", Samples = secondarySamples, Peaks = new PeakPyramid(secondarySamples), Offset = 2 } });
                EditInvoke("RebuildTrackControls"); editCanvas.SelectionStart = 2; editCanvas.SelectionEnd = 6;
                AudioTrack[] Targets() => ((IEnumerable<AudioTrack>)typeof(EditorForm).GetMethod("EffectTargets", Private)!.Invoke(editForm, null)!).ToArray();
                var effectChecks = All(editCanvas).OfType<CheckBox>().Where(c => c.AccessibleName?.StartsWith("Include ") == true && c.AccessibleName.EndsWith(" in playback, effects and export")).ToArray();
                Check(effectChecks.Length == 2 && effectChecks.All(c => c.Checked) && Targets().Length == 2, "new channels are ticked and included in the mix and effects by default");
                var firstTarget = effectChecks.Single(c => c.AccessibleName == "Include Edit fixture in playback, effects and export"); firstTarget.Checked = false;
                Check(Targets().Length == 1 && Targets()[0].Name == "Inside cut" && !editCanvas.Tracks.First(t => t.Name == "Edit fixture").Enabled, "unticking a channel excludes that sound from effect targets and its audio state");
                foreach (var target in effectChecks) target.Checked = false;
                var mutedOutput = new float[20]; var mutedMix = new MixProvider(editCanvas.Tracks.Select(t => t.Snapshot()), 0, 10, false); mutedMix.Read(mutedOutput, 0, mutedOutput.Length);
                Check(Targets().Length == 0 && mutedOutput.All(sample => sample == 0), "unticking every channel mutes the mix and does not fall back to targeting all sounds");
                firstTarget.Checked = true;
                Check(Targets().Length == 1 && Targets()[0].Name == "Edit fixture" && Editing.ExportStem(editCanvas.Tracks, 0, 10) == "Edit fixture", "ticked rows alone provide effect targets and export parent names");
                foreach (var target in effectChecks) target.Checked = true;
                EditClick("Save section cue points");
                Check(store.Items.Count == 2 && store.Items.All(item => item.Start == item.End) && store.Items.Select(item => item.Start).Order().SequenceEqual(new long[] { 2, 6 }) && editCanvas.CuePoints.Select(cue => cue.Frame).Order().SequenceEqual(new long[] { 2, 6 }), "saving a section creates visible cue points at both selection boundaries");
                EditClick("Save section cue points");
                Check(store.Items.Count == 2, "saving the same section avoids duplicate cue points at its boundaries");
                EditClick("Undo"); Check(store.Items.Count == 0 && editCanvas.CuePoints.Count == 0, "Undo removes newly saved section cue points");
                EditClick("Redo"); Check(store.Items.Count == 2 && editCanvas.CuePoints.Count == 2, "Redo restores section cue points");
                EditClick("Delete section");
                Check(editCanvas.Tracks.First(t => t.Name == "Edit fixture").Frames == 6 && editCanvas.Tracks.First(t => t.Name == "Inside cut").Frames == 0 && store.Items.All(item => item.Start == 2 && item.End == 2), "Delete removes the shared section from every channel and remaps its cue points");
                using (var afterDelete = Render(editCanvas)) Check(afterDelete.Width > 0, "a completely deleted channel still renders safely");
                EditClick("Undo"); Check(editCanvas.Tracks.First(t => t.Name == "Edit fixture").Frames == 10 && editCanvas.Tracks.First(t => t.Name == "Inside cut").Frames == 4 && editCanvas.SelectionStart == 2 && editCanvas.SelectionEnd == 6 && store.Items.Select(item => item.Start).Order().SequenceEqual(new long[] { 2, 6 }), "Undo Delete restores channel audio, selection and cue points together");
                EditClick("Redo"); Check(editCanvas.Tracks.First(t => t.Name == "Edit fixture").Frames == 6, "Redo reapplies the timeline cut");
                EditClick("Undo"); EditClick("Trim to selection");
                Check(editCanvas.Tracks.First(t => t.Name == "Edit fixture").Frames == 4 && editCanvas.Tracks.First(t => t.Name == "Inside cut").Frames == 4 && editCanvas.SelectionStart == 0 && editCanvas.SelectionEnd == 4 && store.Items.Select(item => item.Start).Order().SequenceEqual(new long[] { 0, 4 }), "Trim keeps the selected region and rebases its audio and cue points");
                EditClick("Undo"); Check(editCanvas.Tracks.First(t => t.Name == "Edit fixture").Frames == 10 && editCanvas.SelectionStart == 2 && editCanvas.SelectionEnd == 6, "Undo Trim restores the full sound and its selection");
                EditClick("Redo"); Check(editCanvas.Tracks.First(t => t.Name == "Edit fixture").Frames == 4 && editingSamples.SequenceEqual(originalEditingSamples), "Redo Trim preserves the reusable original source samples");

                if (renderPath != null)
                {
                    using var fixture = new Bitmap(enlarged.Width, enlarged.Height + iconRender.Height + 16);
                    using var fixtureGraphics = Graphics.FromImage(fixture);
                    fixtureGraphics.Clear(canvas.BackColor);
                    fixtureGraphics.DrawImageUnscaled(iconRender, 16, 8);
                    fixtureGraphics.DrawImageUnscaled(enlarged, 0, iconRender.Height + 16);
                    fixture.Save(renderPath);
                    Console.WriteLine($"Fixture render: {renderPath}; device DPI {form.DeviceDpi}");
                }
                Console.WriteLine($"{checks} UI checks passed.");
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("UI fixture deadline exceeded.");
        if (failure != null) throw new Exception("UI fixture failed", failure);
    }

    sealed class SilentPlayer : IWavePlayer
    {
        public float Volume { get; set; } = 1;
        public PlaybackState PlaybackState { get; private set; }
        public WaveFormat OutputWaveFormat { get; private set; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public void Init(IWaveProvider provider) => OutputWaveFormat = provider.WaveFormat;
        public void Play() => PlaybackState = PlaybackState.Playing;
        public void Pause() => PlaybackState = PlaybackState.Paused;
        public void Stop() { PlaybackState = PlaybackState.Stopped; PlaybackStopped?.Invoke(this, new StoppedEventArgs()); }
        public void Dispose() => PlaybackState = PlaybackState.Stopped;
    }
}
