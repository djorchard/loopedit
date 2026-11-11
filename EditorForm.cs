using NAudio.Wave;

namespace OrchardSound;

public sealed partial class EditorForm : Form
{
    sealed record EditState((ChannelSlot Slot, AudioTrack? Track)[] Slots, TrackState[] Tracks, long? Start, long? End, SavedSelection[] Selections);
    readonly WaveCanvas canvas = new();
    readonly Panel scroll = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = AppTheme.Window };
    readonly Label status = new() { Dock = DockStyle.Bottom, Height = 32, Padding = new(14, 0, 14, 0), TextAlign = ContentAlignment.MiddleLeft, ForeColor = AppTheme.Muted, Tag = "Muted", AutoEllipsis = true };
    readonly IconButton play;
    readonly IconButton loopPlay;
    readonly List<Control> trackControls = [];
    readonly Dictionary<ChannelSlot, IconButton> emptyOpenButtons = [];
    readonly Dictionary<ChannelSlot, IconButton> emptyPasteButtons = [];
    readonly Panel zoomControls = new() { Width = WaveCanvas.LeftGutter - 24, Height = 130 };
    readonly ThemeButton addTrackOverlay = new("+ Add track") { Visible = false, MinimumSize = Size.Empty, Padding = new(10, 2, 10, 2), AccessibleName = "Add new track at bottom" };
    readonly Stack<EditState> undo = new();
    readonly Stack<EditState> redo = new();
    readonly SelectionStore selections = new();
    readonly HashSet<ChannelSlot> selectedSlots = [];
    readonly ComboBox selectionMenu = new() { Width = 170, MaxLength = 120, DropDownStyle = ComboBoxStyle.DropDown, Margin = new(8, 21, 8, 0), AccessibleName = "Saved selections" };
    readonly ContextMenuStrip effectMenu = new();
    readonly IconButton recordButton;
    RecordingCapture? recording;
    ChannelSlot? recordingSlot;
    bool updatingSelections;
    readonly CancellationTokenSource lifetime = new();
    readonly ToolTip tips = new() { AutoPopDelay = 15000, InitialDelay = 350, ReshowDelay = 150 };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 40 };
    readonly ComboBox format = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 76, Margin = new(12, 21, 6, 0), AccessibleName = "Export format" };
    readonly CheckBox magnify = new() { Text = "Fit waveform height", AutoSize = true, Checked = true, Margin = new(12, 25, 12, 0), AccessibleName = "Fit waveform height" };
    readonly CheckBox beats = new() { Text = "Beats / BPM [B]", AutoSize = true, Margin = new(8, 25, 8, 0), AccessibleName = "Show beats and estimated BPM" };
    readonly CheckBox kicks = new() { Text = "Kick hits [K]", AutoSize = true, Margin = new(8, 25, 8, 0), AccessibleName = "Show likely kick markers" };
    readonly CheckBox snap = new() { Text = "Smart snap [S]", AutoSize = true, Margin = new(8, 25, 8, 0), AccessibleName = "Smart snap to detected beats" };
    readonly CheckBox scrub = new() { Text = "Scrub", AutoSize = true, Margin = new(8, 25, 8, 0), AccessibleName = "Audition audio at the cursor while stopped" };
    readonly CheckBox refineEnd = new() { Text = "Refine loop end on restart", AutoSize = true, Margin = new(8, 25, 8, 0), AccessibleName = "Refine loop end on restart" };
    CancellationTokenSource? rhythmCancellation;
    IWavePlayer? output;
    Func<IWavePlayer> outputFactory = () => new WaveOutEvent { DesiredLatency = 120, NumberOfBuffers = 3 };
    MixProvider? playing;
    BufferedPlayback? playbackBuffer;
    bool cursorPlayback;
    bool busy, rebuilding, sizing, changingPlayback, resourcesDisposed;
    string? lastExport;
    string? exportFolder = ExportPreferences.ReadFolder();
    public string SelectedExportExtension => "." + format.Text.ToLowerInvariant();

    public EditorForm(string[] files)
    {
        AutoScaleDimensions = new(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        Text = Branding.Name; Icon = Branding.LoadIcon(); Width = 1360; Height = 900; MinimumSize = new(1024, 640);
        Font = new("Segoe UI", 10); AppTheme.ApplyWindow(this); AppTheme.ConfigureToolTip(tips); AppTheme.StyleMenu(effectMenu);
        StartPosition = FormStartPosition.CenterScreen; WindowState = FormWindowState.Maximized;
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Padding = new(10, 5, 10, 5), BackColor = AppTheme.Surface };
        toolbar.Controls.Add(new WordmarkControl());
        void Add(EditorIcon icon, string name, string key, Action action) => toolbar.Controls.Add(MakeButton(icon, name, key, action));
        Add(EditorIcon.AddTrack, "Add track", "Ctrl+=", AddEmptyTrack);
        recordButton = MakeButton(EditorIcon.Record, "Record", "Shift+R", async () => await ToggleRecording()); toolbar.Controls.Add(recordButton);
        play = MakeButton(EditorIcon.Play, "Play from cursor", "Space", StartCursorPlayback);
        loopPlay = MakeButton(EditorIcon.PlayLoop, "Play loop", "Shift+Space", RestartLoop);
        toolbar.Controls.Add(play); toolbar.Controls.Add(loopPlay);
        InitializeSelectionMenu();
        var selectionActions = MakeButton(EditorIcon.SelectAll, "Selection", "[ / ]", ShowSelectionMenu);
        selectionActions.AccessibleDescription = "Selection commands\nSet boundaries, select all, clear, delete, duplicate, trim or save cue points.\nShortcuts: [ and ]";
        tips.SetToolTip(selectionActions, selectionActions.AccessibleDescription); toolbar.Controls.Add(selectionActions);
        Add(EditorIcon.FadeIn, "Fade in", "F", () => OpenEffectTool(EffectKind.FadeIn));
        Add(EditorIcon.FadeOut, "Fade out", "Shift+F", () => OpenEffectTool(EffectKind.FadeOut));
        Add(EditorIcon.Undo, "Undo", "Ctrl+Z", Undo);
        Add(EditorIcon.Redo, "Redo", "Ctrl+Y", Redo);
        var fx = MakeButton(EditorIcon.Effects, "Effects", "Ctrl+E", ShowEffects); toolbar.Controls.Add(fx);
        InitializeViewMenu();
        var viewOptions = MakeButton(EditorIcon.View, "View", "", ShowViewMenu); viewOptions.Caption = "View"; toolbar.Controls.Add(viewOptions);
        toolbar.Controls.Add(MakeOptionToggle(EditorIcon.Scrub, "Scrub", scrub, "Scrub"));
        toolbar.Controls.Add(MakeOptionToggle(EditorIcon.RefineLoop, "Refine selection end", refineEnd, "Refine end"));
        foreach (var entry in new[] { ("Maximize", EffectKind.Maximize), ("Multiband EQ...", EffectKind.MultibandEq), ("Reverb...", EffectKind.Reverb), ("Delay...", EffectKind.Delay), ("Filter...", EffectKind.Filter), ("Reverse", EffectKind.Reverse), ("Pitch shift...", EffectKind.PitchShift), ("Multiply...", EffectKind.Multiply), ("Seamless Repeat...", EffectKind.SeamlessRepeat), ("Bitcrush...", EffectKind.Bitcrush), ("De-click...", EffectKind.DeClick) })
        { var kind = entry.Item2; effectMenu.Items.Add(entry.Item1, null, async (_, _) => await ApplyEffect(kind)); }
        effectMenu.Items.Add("Fade in", null, (_, _) => OpenEffectTool(EffectKind.FadeIn)); effectMenu.Items.Add("Fade out", null, (_, _) => OpenEffectTool(EffectKind.FadeOut));
        Add(EditorIcon.SaveSession, "Save session and selections", "Ctrl+P", async () => await SaveSession());
        Add(EditorIcon.OpenSession, "Open session and selections", "Ctrl+Shift+O", async () => await OpenSession());
        selectionMenu.SelectedIndexChanged += (_, _) => { if (!updatingSelections && selectionMenu.SelectedItem is SavedSelection item) RestoreSavedSelection(item.Name); };
        selectionMenu.DisplayMember = nameof(SavedSelection.Name);
        tips.SetToolTip(selectionMenu, "Type a name, then Save selection. Save session keeps all named selections and edits on disk.");
        var savedMenu = new ContextMenuStrip(); AppTheme.StyleMenu(savedMenu); savedMenu.Items.Add("Delete named selection", null, (_, _) => { if (!busy && selectionMenu.SelectedItem is SavedSelection item) { Remember(); selections.Remove(item.Name); RefreshSelectionMenu(); } }); selectionMenu.ContextMenuStrip = savedMenu;
        Add(EditorIcon.EqualHeights, "Equal heights", "Ctrl+0", () => canvas.FitHeight());
        toolbar.Controls.Add(BuildExportControls()); toolbar.Controls.Add(snap);
        Add(EditorIcon.Help, "Keyboard shortcuts", "F1", ShowShortcutHelp);
        tips.SetToolTip(scrub, "While stopped, audition the cursor position. Move left to hear backwards.");
        tips.SetToolTip(refineEnd, "Shift+Space blends the heard position into the loop end: 75% existing end, 25% tap.");
        scrub.CheckedChanged += (_, _) => { if (!scrub.Checked) DisableScrub(); };
        tips.SetToolTip(magnify, "Enlarge quiet waveforms vertically. Playback and export volume are unchanged.");
        magnify.CheckedChanged += (_, _) => { canvas.AutoMagnify = magnify.Checked; canvas.Invalidate(); };
        tips.SetToolTip(beats, "Optional estimated BPM and beat markers. Very short or irregular sounds may have no tempo estimate.");
        tips.SetToolTip(kicks, "Optional likely kick onsets. Bass notes and low impacts can also trigger these markers.");
        tips.SetToolTip(snap, "Snap to nearby cue points and detected beats. Hold Ctrl to bypass.");
        beats.CheckedChanged += async (_, _) => await RefreshRhythmAsync();
        kicks.CheckedChanged += async (_, _) => await RefreshRhythmAsync();
        snap.CheckedChanged += async (_, _) => await RefreshRhythmAsync();
        scroll.Controls.Add(canvas); canvas.Location = Point.Empty;
        canvas.Controls.Add(addTrackOverlay); addTrackOverlay.Click += (_, _) => AddEmptyTrack();
        canvas.MouseMove += (_, e) => UpdateAddTrackOverlay(e.Location);
        addTrackOverlay.MouseLeave += (_, _) => LayoutAddTrackOverlay();
        canvas.MouseLeave += (_, _) => LayoutAddTrackOverlay();
        toolbar.Scale(new SizeF(DeviceDpi / 96f, DeviceDpi / 96f));
        Controls.Add(scroll); Controls.Add(status); Controls.Add(toolbar);
        scroll.Resize += (_, _) => SizeCanvas();
        canvas.PaneLayoutChanged += LayoutTrackControls;
        canvas.SelectionChanged += SelectionUpdated;
        canvas.SelectionChanging += SelectionChanging;
        canvas.BeforeMove += () => { Remember(); StopPlayback(); };
        canvas.BoundaryDragStarted += Remember;
        canvas.DragFinished += SelectionUpdated;
        canvas.OpenSlotRequested += async i => await OpenFiles(canvas.Slots[i]);
        canvas.SpectralHoverChanged += RefreshNoteAudition;
        canvas.PaneLayoutChanged += RefreshNoteAudition;
        canvas.ZoomChanged += RefreshNoteAudition;
        canvas.EnabledChanged += (_, _) => RefreshNoteAudition();
        canvas.CursorMoved += frame => { if (scrub.Checked) UpdateScrub(frame); };
        canvas.MouseLeave += (_, _) => DisableScrub();
        timer.Tick += (_, _) =>
        {
            if (playing == null) return;
            RefreshPlaybackButtons();
            canvas.Playhead = playbackBuffer?.Position ?? playing.Position; canvas.Invalidate();
            if ((playbackBuffer?.Peak ?? playing.Peak) > 1) status.Text = "Audio is clipping. Lower the track volumes for a clean export.";
        };
        AttachDrops(this);
        Shown += async (_, _) =>
        {
            SizeCanvas();
            try { AudioFiles.FindFfmpeg(); }
            catch (Exception error) { status.Text = error.Message; }
            if (files.Length > 0) await LoadFiles(files);
        };
        FormClosing += (_, _) => { appClosing = true; CloseEffectTool(false); lifetime.Cancel(); rhythmCancellation?.Cancel(); recording?.Dispose(); StopPlayback(); };
        void SizeStatus() { status.Height = Math.Max((int)(32 * DeviceDpi / 96f), status.Font.Height + (int)(12 * DeviceDpi / 96f)); status.Padding = new((int)(14 * DeviceDpi / 96f), 0, (int)(14 * DeviceDpi / 96f), 0); }
        SizeStatus(); DpiChanged += (_, _) => { SizeStatus(); RebuildTrackControls(); SizeCanvas(); };
        AppTheme.StyleInputs(this); BuildZoomControls(); RebuildTrackControls(); InitializeAdditionalCommands();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !resourcesDisposed)
        {
            appClosing = true; CloseEffectTool(false); resourcesDisposed = true; lifetime.Cancel();
            rhythmCancellation?.Cancel(); rhythmCancellation?.Dispose(); recording?.Dispose();
            effectMenu.Dispose(); DisposeSelectionMenu(); viewContextMenu.Dispose(); exportContextMenu.Dispose();
            StopPlayback(); DisposeScrubResources(); timer.Dispose(); lifetime.Dispose(); tips.Dispose();
            format.Dispose(); magnify.Dispose(); precisionMagnifier.Dispose(); followCursor.Dispose();
            beats.Dispose(); kicks.Dispose(); scrub.Dispose(); refineEnd.Dispose(); selectionMenu.Dispose();
        }
        base.Dispose(disposing);
    }
    IconButton MakeButton(EditorIcon icon, string name, string key, Action action)
    {
        var button = new IconButton(icon, name, key, action) { Role = icon == EditorIcon.Export ? ButtonRole.Primary : ButtonRole.Secondary };
        string description = name + "\n" + IconDescriptions.For(icon) + (key.Length > 0 ? "\nShortcut: " + key.Replace('\n', ' ') : "");
        button.AccessibleDescription = description; tips.SetToolTip(button, description); return button;
    }
    void SizeCanvas()
    {
        if (sizing) return; sizing = true;
        try
        {
            canvas.SetViewportHeight(scroll.ClientSize.Height);
            canvas.Width = Math.Max((int)(480 * canvas.DpiScale), scroll.ClientSize.Width - (scroll.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0));
            LayoutTrackControls();
        }
        finally { sizing = false; }
    }
    void BuildZoomControls()
    {
        zoomControls.BackColor = canvas.BackColor;
        zoomControls.Controls.Add(new Label { Text = "PRECISION", ForeColor = Color.FromArgb(199, 211, 224), Location = new(0, 0), Size = new(160, 24) });
        zoomControls.Controls.Add(new Label { Text = "Zoom", AutoSize = true, Location = new(0, 36), ForeColor = Color.Silver });
        var amount = new LiveNumericUpDown { Minimum = 1, Maximum = 2048, DecimalPlaces = 1, Value = (decimal)canvas.Magnification, Location = new(66, 31), Size = new(74, 25), AccessibleName = "Precision zoom amount" };
        zoomControls.Controls.Add(new Label { Text = "x", AutoSize = true, Location = new(144, 35), ForeColor = Color.Silver });
        var slider = new ThemedSlider { AutoSize = false, Minimum = 0, Maximum = 1100, TickStyle = TickStyle.None, Location = new(0, 65), Size = new(164, 35), AccessibleName = "Precision zoom slider" };
        bool changing = false;
        void Sync()
        {
            if (changing) return; changing = true;
            amount.Value = (decimal)canvas.Magnification;
            slider.Value = Math.Clamp((int)Math.Round(Math.Log2(canvas.Magnification) * 100), 0, 1100); changing = false;
        }
        amount.ValueChanged += (_, _) => { if (!changing) canvas.Magnification = (double)amount.Value; };
        slider.ValueChanged += (_, _) => { if (!changing) canvas.Magnification = Math.Pow(2, slider.Value / 100d); };
        canvas.ZoomChanged += Sync; zoomControls.Controls.Add(amount); zoomControls.Controls.Add(slider); Sync();
        var view = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new(0, 103), Width = 160, AccessibleName = "Precision display mode" };
        view.Items.AddRange(Enum.GetNames<AudioViewMode>()); view.SelectedIndex = (int)canvas.PrecisionViewMode;
        view.SelectedIndexChanged += (_, _) =>
        {
            canvas.PrecisionViewMode = (AudioViewMode)view.SelectedIndex;
            LayoutTrackControls();
            RefreshNoteAudition();
            canvas.Invalidate();
        };
        zoomControls.Controls.Add(view); tips.SetToolTip(view, "Choose a waveform or a spectrogram with musical note grid lines for the precision view.");
        zoomControls.Controls.Add(CreateNoteAuditionCheckBox("Precision", precisionHearHoveredNote, enabled => precisionHearHoveredNote = enabled));
        tips.SetToolTip(slider, "Precision zoom. Mouse-wheel zoom stays available.");
        AppTheme.StyleInputs(zoomControls); zoomControls.Scale(new SizeF(DeviceDpi / 96f, DeviceDpi / 96f)); LayoutSidebarPanel(zoomControls, true, canvas.DpiScale); canvas.Controls.Add(zoomControls); AttachDrops(zoomControls);
    }
    void AttachDrops(Control control)
    {
        control.AllowDrop = true;
        control.DragEnter += (_, e) => e.Effect = !busy && e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        control.DragDrop += async (_, e) =>
        {
            if (busy || e.Data?.GetData(DataFormats.FileDrop) is not string[] paths) return;
            int index = canvas.SlotAt(canvas.PointToClient(new Point(e.X, e.Y)));
            await LoadFiles(paths, index >= 0 ? canvas.Slots[index] : null);
        };
        foreach (Control child in control.Controls) AttachDrops(child);
    }
    async Task OpenFiles(ChannelSlot? target = null, bool replaceTarget = false)
    {
        if (busy) return;
        using var dialog = new OpenFileDialog { Title = replaceTarget ? "Open audio in this track" : "Add sounds", Multiselect = !replaceTarget, Filter = "Audio files|*.wav;*.mp3;*.flac;*.ogg;*.opus;*.m4a;*.aac;*.aif;*.aiff;*.wma;*.mp4|All files|*.*" };
        if (dialog.ShowDialog(this) == DialogResult.OK) await LoadFiles(dialog.FileNames, target, replaceTarget);
    }
    async Task LoadFiles(string[] files, ChannelSlot? target = null, bool replaceTarget = false)
    {
        if (busy) return; busy = true; canvas.Enabled = false; StopPlayback(); var failures = new List<string>();
        try
        {
            foreach (string path in files)
            {
                status.Text = "Loading " + System.IO.Path.GetFileName(path) + "...";
                try
                {
                    var track = await Task.Run(() => AudioFiles.LoadAsync(path, lifetime.Token), lifetime.Token);
                    if (canvas.Tracks.Where(t => !replaceTarget || !ReferenceEquals(t, target?.Track)).Sum(t => t.Samples.LongLength * 4 + t.Patches.Sum(p => p.Samples.LongLength * 4)) + track.Samples.LongLength * 4 > AudioFiles.MaxSessionBytes) throw new IOException("The session is full. Remove a track before adding more audio.");
                    ChannelSlot? slot = target != null && (target.Track == null || replaceTarget) ? target : null;
                    slot ??= target == null ? canvas.Slots.FirstOrDefault(s => s.Track == null) : null;
                    if (slot == null) { slot = new(); canvas.Slots.Add(slot); }
                    slot.Track = track; ImportTrackCues(track); target = null;
                }
                catch (OperationCanceledException) { if (lifetime.IsCancellationRequested) return; failures.Add(System.IO.Path.GetFileName(path) + ": loading timed out."); }
                catch (Exception error) { failures.Add(System.IO.Path.GetFileName(path) + ": " + error.Message); }
            }
            undo.Clear(); redo.Clear(); canvas.FitHeight(); RebuildTrackControls(); SizeCanvas(); SelectionUpdated(false); status.Text = "";
            _ = RefreshRhythmAsync();
            if (failures.Count > 0) MessageBox.Show(this, string.Join("\n\n", failures), "Some sounds could not be loaded", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { busy = false; if (!IsDisposed) { canvas.Enabled = true; if (effectWindow != null) _ = RequestEffectPreview(); } }
    }
    void AddEmptyTrack()
    { if (busy) return; canvas.Slots.Add(new()); canvas.FitHeight(); RebuildTrackControls(); SizeCanvas(); }
    void RebuildTrackControls()
    {
        InvalidateEffectPreview();
        selectedSlots.RemoveWhere(s => !canvas.Slots.Contains(s));
        rebuilding = true;
        foreach (var c in trackControls) { canvas.Controls.Remove(c); c.Dispose(); } trackControls.Clear();
        foreach (var button in emptyOpenButtons.Values) { canvas.Controls.Remove(button); button.Dispose(); } emptyOpenButtons.Clear();
        foreach (var button in emptyPasteButtons.Values) { canvas.Controls.Remove(button); button.Dispose(); } emptyPasteButtons.Clear();
        for (int i = 0; i < canvas.Slots.Count; i++)
        {
            var slot = canvas.Slots[i]; var track = slot.Track;
            var panel = new Panel { Width = WaveCanvas.LeftGutter - 24, Height = 130, BackColor = canvas.BackColor, Tag = slot };
            var target = new CheckBox { Checked = slot.Enabled, Location = new(0, 3), Size = new(20, 20), AccessibleName = "Include " + (track?.Name ?? "Track " + (i + 1)) + " in playback, effects and export" };
            var name = new Label { Text = track?.Name ?? "Track " + (i + 1), ForeColor = track == null || !slot.Enabled ? AppTheme.Muted : WaveCanvas.Colors[i % WaveCanvas.Colors.Length], Location = new(22, 0), Size = new(102, 24), AutoEllipsis = true, Tag = "KeepColor" };
            tips.SetToolTip(target, "Use this track in playback, effects and export. Untick to mute and exclude it. Ctrl-click the row header to toggle.");
            target.CheckedChanged += (_, _) =>
            {
                if (rebuilding || busy) return; Remember(); slot.Enabled = target.Checked;
                if (target.Checked) selectedSlots.Add(slot); else selectedSlots.Remove(slot);
                name.ForeColor = target.Checked && track != null ? WaveCanvas.Colors[canvas.Slots.IndexOf(slot) % WaveCanvas.Colors.Length] : AppTheme.Muted;
                canvas.Invalidate(); RefreshAudio();
            };
            void HeaderClick(object? sender, EventArgs args) { if ((ModifierKeys & Keys.Control) != 0) target.Checked = !target.Checked; }
            panel.Click += HeaderClick; name.Click += HeaderClick; panel.Controls.Add(target); panel.Controls.Add(name);
            var headerOpen = MakeButton(EditorIcon.Open, "Open or replace audio in Track " + (i + 1), "", async () => await OpenFiles(slot, true));
            headerOpen.Size = new(26, 26); headerOpen.Location = new(0, 25); panel.Controls.Add(headerOpen);
            var view = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new(34, 26), Width = 130, AccessibleName = "Track " + (i + 1) + " display mode" };
            view.Items.AddRange(Enum.GetNames<AudioViewMode>()); view.SelectedIndex = (int)slot.ViewMode;
            view.SelectedIndexChanged += (_, _) =>
            {
                slot.ViewMode = (AudioViewMode)view.SelectedIndex;
                canvas.FitHeight(false);
                RefreshNoteAudition();
            };
            panel.Controls.Add(view); tips.SetToolTip(view, "Choose a waveform or a spectrogram for this channel. Spectrogram height represents musical pitch.");
            panel.Controls.Add(CreateNoteAuditionCheckBox("Track " + (i + 1), slot.HearHoveredNote, enabled => slot.HearHoveredNote = enabled));
            var split = new CheckBox { Text = "Split channels", AutoSize = true, Checked = slot.SplitChannels, AccessibleName = "Split channels in Track " + (i + 1) };
            split.CheckedChanged += (_, _) => { if (rebuilding || busy) return; slot.SplitChannels = split.Checked; RefreshNoteAudition(); canvas.Invalidate(); };
            tips.SetToolTip(split, "Show the left and right channels separately within this track. Volume, timing and edits remain shared."); panel.Controls.Add(split);
            var remove = MakeButton(EditorIcon.Remove, "Remove track", "", () => RemoveSlot(slot)); remove.Size = new(26, 26); remove.Location = new(132, -2); panel.Controls.Add(remove);
            if (track != null)
            {
                panel.Controls.Add(new Label { Text = "Volume", AutoSize = true, Location = new(0, 53), ForeColor = AppTheme.Text });
                var volume = new LiveNumericUpDown { Minimum = 0, Maximum = 1000, Value = (decimal)Math.Round(track.Gain * 100), Location = new(66, 48), Size = new(74, 25), AccessibleName = track.Name + " volume percent" };
                panel.Controls.Add(new Label { Text = "%", AutoSize = true, Location = new(144, 52), ForeColor = AppTheme.Text });
                var slider = new ThemedSlider { AutoSize = false, Minimum = 0, Maximum = 1000, Value = (int)volume.Value, TickStyle = TickStyle.None, SmallChange = 1, LargeChange = 25, Location = new(0, 80), Size = new(164, 28), AccessibleName = track.Name + " volume slider" };
                bool changing = false;
                volume.ValueChanged += (_, _) => { if (rebuilding || changing || busy) return; changing = true; Remember(); track.Gain = (float)volume.Value / 100; slider.Value = (int)volume.Value; changing = false; RefreshAudio(); };
                slider.ValueChanged += (_, _) => { if (rebuilding || changing || busy) return; changing = true; Remember(); track.Gain = slider.Value / 100f; volume.Value = slider.Value; changing = false; RefreshAudio(); };
                panel.Controls.Add(volume); panel.Controls.Add(slider);
                var tempo = new Label { Text = "BPM: off (B)", AccessibleName = "Estimated BPM", Location = new(0, 110), Size = new(158, 20), ForeColor = AppTheme.Muted, Tag = "Muted", AutoEllipsis = true };
                tips.SetToolTip(tempo, "Estimated tempo. Half-time and double-time values can both be plausible."); panel.Controls.Add(tempo);
            }
            AppTheme.StyleInputs(panel); panel.Scale(new SizeF(DeviceDpi / 96f, DeviceDpi / 96f)); LayoutSidebarPanel(panel, false, canvas.DpiScale);
            canvas.Controls.Add(panel); trackControls.Add(panel); AttachDrops(panel);
            if (track == null)
            {
                var open = MakeButton(EditorIcon.Open, "Open audio in Track " + (i + 1), "", async () => await OpenFiles(slot));
                open.Caption = "Open"; open.Size = new((int)(76 * canvas.DpiScale), (int)(44 * canvas.DpiScale)); canvas.Controls.Add(open); emptyOpenButtons[slot] = open; AttachDrops(open);
                var paste = MakeButton(EditorIcon.Paste, "Paste audio in Track " + (i + 1), "", async () => await PasteAudio(slot));
                paste.Caption = "Paste"; paste.Size = open.Size; canvas.Controls.Add(paste); emptyPasteButtons[slot] = paste; AttachDrops(paste);
            }
        }
        rebuilding = false; canvas.FitHeight(false); LayoutTrackControls(); UpdateRhythmLabels(); RefreshNoteAudition(); if (effectWindow != null) _ = RequestEffectPreview();
    }
    void LayoutTrackControls()
    {
        var precision = canvas.ToDevice(canvas.GetPaneBounds(0));
        LayoutSidebarPanel(zoomControls, true, canvas.DpiScale);
        zoomControls.Location = new((int)(18 * canvas.DpiScale), precision.Top + (int)(12 * canvas.DpiScale));
        for (int i = 0; i < trackControls.Count && i < canvas.Slots.Count; i++)
        { var b = canvas.ToDevice(canvas.GetPaneBounds(i + 1)); LayoutSidebarPanel((Panel)trackControls[i], false, canvas.DpiScale); trackControls[i].Location = new((int)(18 * canvas.DpiScale), b.Top + (int)(12 * canvas.DpiScale)); }
        foreach (var pair in emptyOpenButtons)
        {
            int index = canvas.Slots.IndexOf(pair.Key); if (index < 0) continue;
            var b = canvas.ToDevice(canvas.GetWaveBounds(index + 1));
            pair.Value.Location = new(b.Left + (b.Width - pair.Value.Width) / 2, b.Top + b.Height / 2 + (int)(21 * canvas.DpiScale));
            pair.Value.BringToFront();
            if (emptyPasteButtons.TryGetValue(pair.Key, out var paste))
            { paste.Location = new(pair.Value.Left, b.Top + b.Height / 2 - (int)(65 * canvas.DpiScale)); paste.BringToFront(); }
        }
        LayoutAddTrackOverlay();
    }
    void UpdateAddTrackOverlay(Point point)
    {
        LayoutAddTrackOverlay();
    }
    void RemoveSlot(ChannelSlot slot)
    {
        if (busy) return; StopPlayback();
        if (canvas.Slots.Count == 1) slot.Track = null; else canvas.Slots.Remove(slot);
        if (!canvas.Tracks.Any()) { canvas.SelectionStart = null; canvas.SelectionEnd = null; }
        undo.Clear(); redo.Clear(); canvas.FitHeight(); RebuildTrackControls(); SizeCanvas(); SelectionUpdated(false);
        _ = RefreshRhythmAsync();
    }
    void Remember()
    {
        if (busy) return;
        redo.Clear();
        if (undo.Count >= 100) { var recent = undo.Take(99).Reverse().ToArray(); undo.Clear(); foreach (var state in recent) undo.Push(state); }
        undo.Push(CaptureEditState());
    }
    void PruneAudioHistory()
    {
        long Bytes()
        {
            var buffers = new HashSet<float[]>();
            foreach (var track in canvas.Tracks) { buffers.Add(track.Samples); foreach (var patch in track.Patches) buffers.Add(patch.Samples); }
            foreach (var state in undo.Concat(redo)) foreach (var track in state.Tracks) { buffers.Add(track.Source.Samples); foreach (var patch in track.Patches) buffers.Add(patch.Samples); }
            return buffers.Sum(b => b.LongLength * sizeof(float));
        }
        void DropOldest(Stack<EditState> stack) { var retained = stack.Take(stack.Count - 1).Reverse().ToArray(); stack.Clear(); foreach (var state in retained) stack.Push(state); }
        while (Bytes() > AudioFiles.MaxSessionBytes * 2 && (undo.Count > 1 || redo.Count > 0))
        { if (undo.Count > 1) DropOldest(undo); else DropOldest(redo); }
    }
    EditState CaptureEditState() => new(canvas.Slots.Select(s => (s, s.Track)).ToArray(), canvas.Tracks.Select(t => t.Snapshot()).ToArray(), canvas.SelectionStart, canvas.SelectionEnd, selections.Capture());
    void RestoreEditState(EditState state)
    {
        rhythmCancellation?.Cancel();
        canvas.Slots.Clear(); foreach (var entry in state.Slots) { entry.Slot.Track = entry.Track; canvas.Slots.Add(entry.Slot); }
        foreach (var track in state.Tracks) { track.ApplyToSource(); track.Source.Rhythm = null; }
        canvas.SelectionStart = state.Start; canvas.SelectionEnd = state.End; selections.Restore(state.Selections); selectedSlots.RemoveWhere(s => !canvas.Slots.Contains(s));
        canvas.FitHeight(false); RefreshSelectionMenu(); _ = RefreshRhythmAsync();
    }
    IEnumerable<AudioTrack> EffectTargets() => canvas.Slots.Where(s => s.Enabled && s.Track != null).Select(s => s.Track!);
    void RefreshSelectionMenu()
    {
        SyncCuePoints();
        updatingSelections = true; string text = selectionMenu.Text;
        selectionMenu.Items.Clear(); selectionMenu.Items.AddRange(selections.Items.Cast<object>().ToArray()); selectionMenu.SelectedIndex = -1; selectionMenu.Text = text;
        updatingSelections = false;
    }
    void SaveSelection()
    {
        if (busy || !RequireSelection()) return;
        try
        {
            var bounds = canvas.SelectionBounds;
            int needed = new[] { bounds.Start, bounds.End }.Count(frame => !selections.Items.Any(s => s.Start == frame || s.End == frame));
            if (selections.Items.Count + needed > SelectionStore.MaxSelections) throw new InvalidOperationException("This session has reached its cue point limit.");
            if (needed > 0) { Remember(); AddCuePoint(bounds.Start); AddCuePoint(bounds.End); RefreshSelectionMenu(); }
            status.Text = "Section cue points saved.";
        }
        catch (ArgumentException error) { status.Text = error.Message; }
        catch (InvalidOperationException error) { status.Text = error.Message; }
    }
    void RestoreSavedSelection(string name)
    {
        if (busy) return; var item = selections.Items.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (item == null) return;
        if (item.End <= item.Start) { status.Text = "That selection was removed by timeline edits."; return; }
        canvas.SelectionStart = item.Start; canvas.SelectionEnd = item.End; SelectionUpdated();
    }
    void DeleteSection()
    {
        if (busy || !RequireSelection()) return; var bounds = canvas.SelectionBounds; Remember(); StopPlayback(); rhythmCancellation?.Cancel();
        TimelineEditing.Delete(canvas.Tracks, bounds.Start, bounds.End); selections.ApplyDelete(bounds.Start, bounds.End);
        canvas.SelectionStart = null; canvas.SelectionEnd = null; canvas.CursorFrame = Math.Clamp(bounds.Start, canvas.Range.Min, canvas.Range.Max);
        RebuildTrackControls(); RefreshSelectionMenu(); canvas.Invalidate(); status.Text = "Section deleted. Ctrl+Z to undo."; _ = RefreshRhythmAsync();
    }
    void TrimSelection()
    {
        if (busy || !RequireSelection()) return; var bounds = canvas.SelectionBounds; Remember(); StopPlayback(); rhythmCancellation?.Cancel();
        TimelineEditing.Trim(canvas.Tracks, bounds.Start, bounds.End); selections.ApplyTrim(bounds.Start, bounds.End);
        canvas.SelectionStart = 0; canvas.SelectionEnd = bounds.End - bounds.Start;
        RebuildTrackControls(); RefreshSelectionMenu(); canvas.Invalidate(); status.Text = "Trimmed to selection. Ctrl+Z to undo."; _ = RefreshRhythmAsync();
    }
    async Task SaveSession()
    {
        if (busy || !canvas.Tracks.Any()) return;
        using var dialog = new SaveFileDialog { Filter = $"{Branding.Name} session|*.orchardsound", DefaultExt = "orchardsound", Title = "Save session and named selections" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        busy = true; canvas.Enabled = false; StopPlayback();
        try { await SessionFiles.SaveAsync(dialog.FileName, canvas.Tracks.ToArray(), selections, lifetime.Token); status.Text = "Session and selections saved."; }
        catch (Exception error) { if (!IsDisposed) status.Text = "Could not save session: " + error.Message; }
        finally { busy = false; if (!IsDisposed) { canvas.Enabled = true; if (effectWindow != null) _ = RequestEffectPreview(); } }
    }
    async Task OpenSession()
    {
        if (busy) return;
        using var dialog = new OpenFileDialog { Filter = $"{Branding.Name} session|*.orchardsound", Title = "Open session and named selections" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        busy = true; canvas.Enabled = false; StopPlayback();
        try
        {
            var session = await SessionFiles.LoadAsync(dialog.FileName, lifetime.Token);
            canvas.Slots.Clear(); foreach (var track in session.Tracks) canvas.Slots.Add(new() { Track = track });
            if (canvas.Slots.Count == 0) canvas.Slots.Add(new()); selections.Restore(session.Selections); selectedSlots.Clear(); undo.Clear(); redo.Clear();
            canvas.SelectionStart = null; canvas.SelectionEnd = null; canvas.FitHeight(); RebuildTrackControls(); RefreshSelectionMenu(); SizeCanvas(); _ = RefreshRhythmAsync();
            status.Text = "Session and selections loaded.";
        }
        catch (Exception error) { if (!IsDisposed) status.Text = "Could not open session: " + error.Message; }
        finally { busy = false; if (!IsDisposed) { canvas.Enabled = true; if (effectWindow != null) _ = RequestEffectPreview(); } }
    }
    void ShowEffects() => effectMenu.Show(this, PointToClient(Cursor.Position));
    Task ApplyEffect(EffectKind kind) { OpenEffectTool(kind); return Task.CompletedTask; }
    async Task ToggleRecording()
    {
        try { await ToggleRecordingCore(); }
        catch (Exception error) { if (!IsDisposed) status.Text = "Could not record: " + error.Message; }
    }
    async Task ToggleRecordingCore()
    {
        if (recording != null) { try { await recording.StopAsync(); } catch (Exception error) { status.Text = error.Message; } return; }
        if (busy) return;
        CloseEffectTool(false);
        var inputs = RecordingCapture.ListInputs(); if (inputs.Length == 0) { status.Text = "No recording input devices were found."; return; }
        var destinations = canvas.Slots.Select((slot, i) => new RecordingDestination("Track " + (i + 1) + (slot.Track == null ? " (empty)" : ": " + slot.Track.Name), slot)).Append(new RecordingDestination("New track", null)).ToArray();
        using var device = new RecordingDeviceDialog(inputs, destinations); if (device.ShowDialog(this) != DialogResult.OK) return;
        string folder = canvas.Tracks.Any() ? System.IO.Path.GetDirectoryName(canvas.Tracks.First().Path)! : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), Branding.Name + " Recordings");
        Directory.CreateDirectory(folder); int number = 1; string path; do { path = System.IO.Path.Combine(folder, $"Recording {number++}.wav"); } while (File.Exists(path));
        recordingSlot = device.SelectedDestination?.Slot;
        StopPlayback(); var capture = new RecordingCapture(); recording = capture; busy = true; canvas.Enabled = false;
        capture.Finished += async (_, result) =>
        {
            recording = null; recordButton.IsPlaying = false; recordButton.Invalidate();
            try
            {
                if (IsDisposed) return;
                if (result.Error != null) { status.Text = "Recording failed: " + result.Error.Message; return; }
                var track = await AudioFiles.LoadAsync(result.Path, lifetime.Token);
                var target = recordingSlot != null && canvas.Slots.Contains(recordingSlot) ? recordingSlot : new ChannelSlot();
                if (canvas.Tracks.Where(t => !ReferenceEquals(t, target.Track)).Sum(t => t.Samples.LongLength * 4 + t.Patches.Sum(p => p.Samples.LongLength * 4)) + track.Samples.LongLength * 4 > AudioFiles.MaxSessionBytes) throw new IOException("The session is full. The recording is saved, but could not be loaded.");
                busy = false; Remember(); busy = true;
                if (!canvas.Slots.Contains(target)) canvas.Slots.Add(target);
                target.Track = track; RebuildTrackControls(); SizeCanvas(); PruneAudioHistory(); _ = RefreshRhythmAsync(); status.Text = "Recorded " + System.IO.Path.GetFileName(result.Path);
            }
            catch (Exception error) { if (!IsDisposed) status.Text = "Could not load recording: " + error.Message; }
            finally { capture.Dispose(); busy = false; if (!IsDisposed) canvas.Enabled = true; }
        };
        try { capture.Start(device.SelectedInput!.Id, path); recordButton.IsPlaying = true; recordButton.Invalidate(); status.Text = "Recording. Click Record again to stop."; }
        catch (Exception error) { recording = null; busy = false; canvas.Enabled = true; capture.Dispose(); status.Text = "Could not record: " + error.Message; }
    }
    void Undo()
    {
        if (busy || undo.Count == 0) return;
        redo.Push(CaptureEditState()); RestoreEditState(undo.Pop());
        RebuildTrackControls(); RefreshAudio(); status.Text = "";
    }
    void Redo()
    {
        if (busy || redo.Count == 0) return;
        undo.Push(CaptureEditState()); RestoreEditState(redo.Pop());
        RebuildTrackControls(); RefreshAudio(); status.Text = "";
    }
    void ApplyFade(bool fadeIn)
    {
        if (busy || !RequireSelection()) return;
        var bounds = canvas.SelectionBounds;
        Remember(); Editing.ApplyFade(EffectTargets(), bounds.Start, bounds.End, fadeIn); RefreshAudio(); status.Text = "";
    }
    bool RequireSelection()
    {
        if (canvas.Tracks.Any() && canvas.HasSelection) return true;
        status.Text = "Choose selection start with left click and selection end with right click."; return false;
    }
    void SelectAll()
    { if (busy || !canvas.Tracks.Any()) return; canvas.SelectionStart = canvas.Range.Min; canvas.SelectionEnd = canvas.Range.Max; SelectionUpdated(); }
    void ClearLoop()
    { if (busy) return; effectAutoPlay = false; StopPlayback(); canvas.SelectionStart = null; canvas.SelectionEnd = null; InvalidateEffectPreview(); if (effectWindow != null) _ = RequestEffectPreview(); canvas.Invalidate(); }
    void SelectionUpdated() => SelectionUpdated(true);
    void SelectionUpdated(bool autoPlay)
    {
        if (effectWindow != null)
        {
            InvalidateEffectPreview();
            if (output != null && !scrubbing) ReplacePlayback(playbackBuffer?.Position);
            _ = RequestEffectPreview(); canvas.Invalidate(); return;
        }
        long? previousPosition = playbackBuffer?.Position ?? playing?.Position;
        canvas.Invalidate();
        if (autoPlay && canvas.HasSelection && !busy) ReplacePlayback(previousPosition ?? canvas.SelectionBounds.Start); else StopPlayback();
    }
    void SelectionChanging()
    {
        canvas.Invalidate();
        if (busy || appClosing || scrubbing) return;
        if (effectWindow != null) InvalidateEffectPreview();
        if (output != null) ReplacePlayback(playbackBuffer?.Position ?? playing?.Position);
        if (effectWindow != null) _ = RequestEffectPreview();
    }
    void RefreshAudio()
    {
        canvas.Invalidate();
        if (!canvas.Tracks.Any(t => t.Enabled && t.Frames > 0)) { StopPlayback(); InvalidateEffectPreview(); return; }
        if (effectWindow != null) { InvalidateEffectPreview(); if (output != null) ReplacePlayback(playbackBuffer?.Position); _ = RequestEffectPreview(); return; }
        if (output != null) ReplacePlayback(playbackBuffer?.Position ?? playing?.Position);
    }
    void ReplacePlayback(long? initialFrame = null)
    {
        if (busy || appClosing) return;
        if (scrubbing) { DisableScrub(); return; }
        if (!canvas.Tracks.Any(t => t.Enabled && t.Frames > 0)) { StopPlayback(); return; }
        if (output == null || playbackBuffer == null) { StartPlayback(initialFrame); return; }
        var bounds = GetPlaybackBounds(); if (bounds.End <= bounds.Start) { StopPlayback(); return; }
        var replacement = CreatePlaybackMixCore(initialFrame ?? playbackBuffer.Position, cursorPlayback, true);
        bool retarget = playing == null || playing.Looping != replacement.Looping
            || playing.LoopStartFrame != replacement.LoopStartFrame || playing.LoopEndFrame != replacement.LoopEndFrame;
        playing = replacement;
        if (retarget) playbackBuffer.Retarget(replacement, crossfade: false); else playbackBuffer.Replace(replacement, crossfade: true);
        timer.Start(); RefreshPlaybackButtons();
    }
    public (long Start, long End) GetPlaybackBounds()
    {
        var bounds = canvas.SelectionBounds;
        if (!EffectPreviewActive || effectPreview == null) return bounds;
        long end = effectPreview.SelectionEnd ?? bounds.End;
        return (bounds.Start, Math.Max(end, effectPreview.AuditionEnd ?? end));
    }
    void TogglePlayback() => StartCursorPlayback();
    void RestartLoop() { if (busy) return; RefineLoopEndOnRestart(); effectAutoPlay = true; StopPlayback(); StartPlayback(GetPlaybackBounds().Start); }
    internal void StopEditorPlayback() { effectAutoPlay = false; StopPlayback(); }
    void StartPlayback(long? initialFrame = null) => StartPlaybackCore(initialFrame, false);
    void StartCursorPlayback() { if (busy) return; effectAutoPlay = true; StartPlaybackCore(canvas.LastCursorFrame, true); }
    MixProvider CreatePlaybackMix(long? initialFrame, bool fromCursor)
        => CreatePlaybackMixCore(initialFrame, fromCursor, false);
    MixProvider CreatePlaybackMixCore(long? initialFrame, bool fromCursor, bool live)
    {
        var audition = EffectPreviewActive ? effectPreview?.PlaybackTracks : null;
        var tracks = audition ?? canvas.Tracks.Select(t => t.Snapshot()).ToArray();
        var bounds = GetPlaybackBounds();
        if (!fromCursor && !live)
        {
            bool selectedLoop = canvas.HasSelection || effectWindow != null;
            if (selectedLoop && initialFrame is long position && position >= bounds.End)
                initialFrame = bounds.Start;
            return new MixProvider(tracks, bounds.Start, bounds.End, selectedLoop, initialFrame);
        }
        var range = audition == null ? canvas.Range : (Min: Math.Min(0, tracks.Select(track => track.Offset).DefaultIfEmpty(0).Min()),
            Max: tracks.Select(track => checked(track.Offset + track.Frames)).DefaultIfEmpty(1).Max());
        long minimum = Math.Min(range.Min, bounds.Start), maximum = Math.Max(range.Max, bounds.End);
        foreach (var track in tracks) { minimum = Math.Min(minimum, track.Offset); maximum = Math.Max(maximum, checked(track.Offset + track.Frames)); }
        long cursor = Math.Clamp(initialFrame ?? minimum, minimum, maximum);
        bool retainedCursorLoop = live && fromCursor && playing?.Looping == true;
        bool loop = fromCursor ? canvas.HasSelection && (cursor < bounds.End || retainedCursorLoop) : canvas.HasSelection || effectWindow != null;
        if (loop && (!fromCursor || retainedCursorLoop) && cursor >= bounds.End) cursor = bounds.Start;
        return new MixProvider(tracks, minimum, maximum, loop, cursor, bounds.Start, bounds.End);
    }
    void StartPlaybackCore(long? initialFrame, bool fromCursor)
    {
        if (!canvas.Tracks.Any(t => t.Enabled && t.Frames > 0)) { StopPlayback(); status.Text = "Tick a track to include it in playback."; return; }
        if (busy || changingPlayback) return;
        var bounds = GetPlaybackBounds();
        if (bounds.End <= bounds.Start) { status.Text = "Selection end must be after selection start."; return; }
        changingPlayback = true;
        try
        {
            // Every caller replaces the previous player through this single ownership point.
            StopPlayback();
            playing = CreatePlaybackMix(initialFrame, fromCursor); cursorPlayback = fromCursor;
            playbackBuffer = new BufferedPlayback(playing, 180);
            var device = outputFactory(); output = device;
            device.PlaybackStopped += (_, e) =>
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(() => { if (output != device) return; StopPlayback(); if (e.Exception != null) status.Text = "Playback failed: " + e.Exception.Message; });
            };
            output.Init(WithNoteAudition(playbackBuffer)); output.Play(); timer.Start(); RefreshPlaybackButtons();
        }
        catch (Exception error) { StopPlayback(); status.Text = "Cannot play audio: " + error.Message; }
        finally { changingPlayback = false; }
    }
    void StopPlayback()
    {
        ResetScrubState(); cursorPlayback = false; noteAudition = null;
        timer.Stop(); var old = output; var oldBuffer = playbackBuffer; output = null; playbackBuffer = null; playing = null; canvas.Playhead = null;
        try
        {
            if (old != null)
            {
                try { old.Stop(); }
                catch (Exception error) { status.Text = "Audio device stopped responding: " + error.Message; }
                finally
                {
                    try { old.Dispose(); }
                    catch (Exception error) { status.Text = "Audio device could not close: " + error.Message; }
                }
            }
        }
        finally { oldBuffer?.Dispose(); }
        RefreshPlaybackButtons(); canvas.Invalidate();
    }
    async Task Export(bool chooseFolder)
    {
        if (chooseFolder) { SetExportFolder(); return; }
        if (busy || !RequireSelection()) return;
        if (!canvas.Tracks.Any(t => t.Enabled && t.Frames > 0)) { status.Text = "Tick a track to include it in the export."; return; }
        string folder = exportFolder ?? System.IO.Path.GetDirectoryName(canvas.Tracks.First().Path)!;
        string extension = SelectedExportExtension;
        var bounds = canvas.SelectionBounds; long start = bounds.Start, end = bounds.End;
        var tracks = canvas.Tracks.Select(t => t.Snapshot()).ToArray(); string stem = Editing.ExportStem(canvas.Tracks, start, end);
        bool resume = output != null; StopPlayback(); busy = true; canvas.Enabled = false; status.Text = "Exporting...";
        try
        {
            var result = await AudioFiles.ExportAsync(tracks, start, end, folder, stem, extension, lifetime.Token, canvas.CuePoints); lastExport = result.Path;
            status.Text = "Saved " + System.IO.Path.GetFileName(result.Path) + (result.Clipped > 0 ? ". Audio clipped; lower volume if needed." : "");
        }
        catch (OperationCanceledException) { if (!IsDisposed) status.Text = "Export cancelled or timed out."; }
        catch (Exception error) { if (!IsDisposed) MessageBox.Show(this, error.Message, "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { busy = false; if (!IsDisposed) { canvas.Enabled = true; if (resume) StartPlayback(); } }
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        => DispatchEditorCommand(keyData) || base.ProcessCmdKey(ref msg, keyData);
    internal bool DispatchEditorCommand(Keys keyData)
    {
        if (keyData == Keys.F1) { ShowShortcutHelp(); return true; }
        if (keyData == (Keys.Control | Keys.Alt | Keys.R)) { _ = ToggleRecording(); return true; }
        if (busy) return false;
        if (selectionMenu.ContainsFocus && (keyData & (Keys.Control | Keys.Alt)) == 0) return false;
        bool editing = selectionMenu.ContainsFocus || zoomControls.Controls.OfType<NumericUpDown>().Any(n => n.ContainsFocus) || trackControls.Any(c => c.Controls.OfType<NumericUpDown>().Any(n => n.ContainsFocus));
        switch (keyData)
        {
            case Keys.Left when !HasArrowEditorFocus(this) && (canvas.Focused || canvas.CursorFrame.HasValue): canvas.NudgeCursor(-1); return true;
            case Keys.Right when !HasArrowEditorFocus(this) && (canvas.Focused || canvas.CursorFrame.HasValue): canvas.NudgeCursor(1); return true;
            case Keys.Home when !HasArrowEditorFocus(this): canvas.JumpCursor(false); return true;
            case Keys.End when !HasArrowEditorFocus(this): canvas.JumpCursor(true); return true;
            case Keys.Control | Keys.V when !editing && canvas.Focused: _ = PasteAudio(canvas.Slots.ElementAtOrDefault(canvas.SlotAt(canvas.PointToClient(System.Windows.Forms.Cursor.Position)))); return true;
            case Keys.Control | Keys.O: _ = OpenFiles(); return true;
            case Keys.Control | Keys.Oemplus: case Keys.Control | Keys.Shift | Keys.Oemplus: case Keys.Control | Keys.Add: AddEmptyTrack(); return true;
            case Keys.Control | Keys.Z: Undo(); return true;
            case Keys.Control | Keys.Y: case Keys.Control | Keys.Shift | Keys.Z: Redo(); return true;
            case Keys.Control | Keys.A when !editing: SelectAll(); return true;
            case Keys.Control | Keys.D: DuplicateSelection(); return true;
            case Keys.Control | Keys.D0: canvas.FitHeight(); return true;
            case Keys.Delete when !editing: DeleteSection(); return true;
            case Keys.Control | Keys.R: OpenEffectTool(EffectKind.Reverse); return true;
            case Keys.Control | Keys.T: TrimSelection(); return true;
            case Keys.Control | Keys.B: SaveSelection(); return true;
            case Keys.Control | Keys.P: _ = SaveSession(); return true;
            case Keys.Control | Keys.Shift | Keys.O: _ = OpenSession(); return true;
            case Keys.Control | Keys.E: case Keys.Alt | Keys.E: ShowEffects(); return true;
            case Keys.E: case Keys.Control | Keys.S: _ = Export(false); return true;
            case Keys.Control | Keys.Shift | Keys.S: SetExportFolder(); return true;
            case Keys.Control | Keys.Space: StopEditorPlayback(); return true;
            case Keys.Shift | Keys.Space: RestartLoop(); return true;
            case Keys.Space: StartCursorPlayback(); return true;
            case Keys.Shift | Keys.R when !editing: _ = ToggleRecording(); return true;
            case Keys.F when !editing: OpenEffectTool(EffectKind.FadeIn); return true;
            case Keys.B when !editing: beats.Checked = !beats.Checked; return true;
            case Keys.K when !editing: kicks.Checked = !kicks.Checked; return true;
            case Keys.S when !editing: snap.Checked = !snap.Checked; return true;
            case Keys.Shift | Keys.F when !editing: OpenEffectTool(EffectKind.FadeOut); return true;
            case Keys.OemOpenBrackets: canvas.SetCursorBoundary(true); return true;
            case Keys.OemCloseBrackets: canvas.SetCursorBoundary(false); return true;
            case Keys.Escape: ClearLoop(); canvas.Focus(); return true;
        }
        return DispatchAdditionalCommand(keyData);
    }
    static bool HasArrowEditorFocus(Control parent)
        => parent.Controls.Cast<Control>().Any(c => c.ContainsFocus && (c is TrackBar or ComboBox or NumericUpDown or TextBoxBase || HasArrowEditorFocus(c)));
    void ShowShortcutHelp() => ShortcutHelp.Show(this);
    void SetExportFolder()
    {
        if (busy) return;
        using var picker = new FolderBrowserDialog { Description = "Set export folder", InitialDirectory = exportFolder ?? canvas.Tracks.Select(t => System.IO.Path.GetDirectoryName(t.Path)).FirstOrDefault() ?? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), UseDescriptionForTitle = true };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        exportFolder = picker.SelectedPath;
        try { ExportPreferences.SaveFolder(exportFolder); status.Text = "Export folder: " + exportFolder; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { status.Text = "Export folder set for this session. Could not save preference: " + error.Message; }
        canvas.Focus();
    }
}
