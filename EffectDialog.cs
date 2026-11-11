using System.ComponentModel;

namespace OrchardSound;

public sealed class EffectDialog : Form
{
    static readonly string[] EqBandNames = ["31 Hz gain dB", "63 Hz gain dB", "125 Hz gain dB", "250 Hz gain dB", "500 Hz gain dB", "1 kHz gain dB", "2 kHz gain dB", "4 kHz gain dB", "8 kHz gain dB", "16 kHz gain dB"];
    readonly EffectKind kind;
    readonly Dictionary<string, NumericUpDown> values = [];
    readonly Dictionary<string, Action> syncSliders = [];
    readonly Dictionary<string, TrackBar> sliders = [];
    readonly Dictionary<string, Label> parameterLabels = [];
    readonly List<(string Start, string End)> pairs = [];
    readonly HashSet<string> editedEnds = [];
    sealed record LayoutRow(int Height, Control[] Controls, int[] Offsets, bool EndOnly = false);
    readonly List<LayoutRow> rows = [];
    readonly Dictionary<string, LayoutRow> parameterRows = [];
    readonly Dictionary<LayoutRow, LayoutRow> columnPairs = [];
    readonly HashSet<LayoutRow> columnEnds = [];
    readonly Panel body = new() { Dock = DockStyle.Fill, AutoScroll = true };
    readonly Panel content = new() { Location = Point.Empty, Width = 390 };
    readonly ComboBox mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, Anchor = AnchorStyles.Top | AnchorStyles.Right };
    readonly CheckBox preview = new() { Text = "Live preview", Checked = true, AutoSize = true, AccessibleName = "Live preview" };
    readonly CheckBox selectionOnly = new() { Text = "Apply to selection only", AutoSize = true, AccessibleName = "Apply to selection only" };
    readonly CheckBox automate = new() { Text = "Automate start and end", AutoSize = true, AccessibleName = "Automate effect parameters" };
    readonly CheckBox cutTail = new() { Text = "Cut tail at selection end", Checked = true, AutoSize = true, AccessibleName = "Cut tail at selection end" };
    readonly CheckBox multiplyPitch = new() { Text = "Pitch (semitones)", AutoSize = true, AccessibleName = "Enable pitch variation" };
    readonly CheckBox multiplyFilter = new() { Text = "Filter", AutoSize = true, AccessibleName = "Enable filter variation" };
    readonly CheckBox multiplyReverb = new() { Text = "Reverb", AutoSize = true, AccessibleName = "Enable reverb variation" };
    readonly CheckBox loopEndToStart = new() { Text = "Loop end back to start", Checked = true, AutoSize = true, AccessibleName = "Loop end back to start" };
    readonly CheckBox smartSeam = new() { Text = "Smart seam", Checked = true, AutoSize = true, AccessibleName = "Smart seam" };
    readonly CheckBox adaptiveCrossfade = new() { Text = "Adaptive crossfade", Checked = true, AutoSize = true, AccessibleName = "Adaptive crossfade" };
    readonly Dictionary<Control, CheckBox> multiplyGroups = [];
    int multiplySeed = Random.Shared.Next();
    bool percent, changingUnits, cancelSignalled, delayInBpm, automationInitialized, initialized, layingOut, wideColumns, changingColumnWidth;
    double columnWidthLogical = 390;
    int lastClientWidth;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool Applied { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool IsClosing { get; private set; }
    public bool PreviewEnabled => preview.Checked;
    public event Action? SettingsChanged;
    public event Action? ApplyRequested;
    public event Action? CancelRequested;
    public event Action? StopPlaybackRequested;
    public event Action? TogglePlaybackRequested
    { add => StopPlaybackRequested += value; remove => StopPlaybackRequested -= value; }
    public event Action? RestartPlaybackRequested;
    public event Action? CursorPlaybackRequested;

    public EffectSettings Settings
    {
        get
        {
            double Value(string name, double fallback) => values.TryGetValue(name, out var input) ? (double)input.Value : fallback;
            double start = Value("Start pitch", 0), end = Value("End pitch", 0);
            if (percent) { start = 12 * Math.Log2(start / 100); end = 12 * Math.Log2(end / 100); }
            MultibandEqSettings? equalizer = null;
            if (kind == EffectKind.MultibandEq)
            {
                equalizer = new();
                for (int i = 0; i < EqBandNames.Length; i++)
                {
                    string name = EqBandNames[i];
                    double? bandEnd = automate.Checked ? Value("End " + name, Value(name, 0)) : null;
                    equalizer = equalizer.WithBand(i, new EqBandSettings(Value(name, 0), bandEnd));
                }
            }
            return new(kind)
            {
                MultibandEq = equalizer,
                TargetPeak = (float)Value("Target peak %", 98) / 100,
                SelectionOnly = kind != EffectKind.Maximize || selectionOnly.Checked,
                AutomationEnabled = kind is not EffectKind.Reverse and not EffectKind.Multiply and not EffectKind.SeamlessRepeat && automate.Checked,
                EndTargetPeak = automate.Checked ? (float)Value("End target peak %", Value("Target peak %", 98)) / 100 : null,
                FadeStrength = (float)Value("Fade strength %", 100) / 100,
                EndFadeStrength = automate.Checked ? (float)Value("End fade strength %", Value("Fade strength %", 100)) / 100 : null,
                Wet = (float)Value("Mix %", 30) / 100,
                EndWet = automate.Checked ? (float)Value("End mix %", Value("Mix %", 30)) / 100 : null,
                Feedback = (float)Value("Feedback %", 35) / 100,
                EndFeedback = automate.Checked ? (float)Value("End feedback %", Value("Feedback %", 35)) / 100 : null,
                RoomSize = (float)Value("Room size %", 45) / 100,
                EndRoomSize = automate.Checked ? (float)Value("End room size %", Value("Room size %", 45)) / 100 : null,
                DelayInBpm = delayInBpm, DelayBpm = delayInBpm ? Value("Delay ms", 120) : 120,
                EndDelayBpm = automate.Checked && delayInBpm ? Value("End delay ms", Value("Delay ms", 120)) : null,
                DelaySeconds = delayInBpm ? .25 : Value("Delay ms", 250) / 1000,
                EndDelaySeconds = automate.Checked && !delayInBpm ? Value("End delay ms", Value("Delay ms", 250)) / 1000 : null,
                CutTail = cutTail.Checked,
                BitDepth = (int)Value("Bit depth", 8), EndBitDepth = automate.Checked ? (int)Value("End bit depth", Value("Bit depth", 8)) : null,
                CrushSampleRate = Value("Sample rate Hz", 12000), EndCrushSampleRate = automate.Checked ? Value("End sample rate Hz", Value("Sample rate Hz", 12000)) : null,
                Sensitivity = Value("Sensitivity %", 50), EndSensitivity = automate.Checked ? Value("End sensitivity %", Value("Sensitivity %", 50)) : null,
                RepairMilliseconds = Value("Repair width ms", 2), EndRepairMilliseconds = automate.Checked ? Value("End repair width ms", Value("Repair width ms", 2)) : null,
                StartHz = Value("Start frequency Hz", 1000), EndHz = Value("End frequency Hz", 1000),
                HighPass = mode.SelectedItem?.ToString() == "High pass", StartSemitones = start, EndSemitones = end, PitchInPercent = percent,
                Multiply = kind == EffectKind.Multiply ? new MultiplySettings
                {
                    CopyCount = (int)Value("Total copies", 4), VariationPercent = Value("Random variation %", 25), Seed = multiplySeed,
                    EnablePitch = multiplyPitch.Checked, StartSemitones = Value("Start pitch", 0), EndSemitones = Value("End pitch", 0),
                    EnableFilter = multiplyFilter.Checked, HighPass = mode.SelectedItem?.ToString() == "High pass", StartHz = Value("Start frequency Hz", 1000), EndHz = Value("End frequency Hz", 1000),
                    EnableReverb = multiplyReverb.Checked, StartRoomSize = (float)Value("Start room size %", 45) / 100, EndRoomSize = (float)Value("End room size %", 45) / 100,
                    StartWet = (float)Value("Start mix %", 30) / 100, EndWet = (float)Value("End mix %", 30) / 100
                } : null,
                SeamlessRepeat = kind == EffectKind.SeamlessRepeat ? new SeamlessRepeatSettings
                {
                    RepeatCount = (int)Value("Total repetitions", 10), CrossfadeMilliseconds = Value("Crossfade ms", 20),
                    LoopEndToStart = loopEndToStart.Checked, SmartSeam = smartSeam.Checked, AdaptiveCrossfade = adaptiveCrossfade.Checked
                } : null
            };
        }
    }

    public EffectDialog(EffectKind kind)
    {
        this.kind = kind;
        Text = kind switch { EffectKind.PitchShift => "Pitch shift", EffectKind.FadeIn => "Fade in", EffectKind.FadeOut => "Fade out", EffectKind.SeamlessRepeat => "Seamless Repeat", EffectKind.DeClick => "De-click", EffectKind.MultibandEq => "Multiband EQ", _ => kind.ToString() };
        AutoScaleDimensions = new(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        Font = new("Segoe UI", 10); FormBorderStyle = FormBorderStyle.SizableToolWindow; StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false; MaximizeBox = false; MinimizeBox = false;
        AppTheme.ApplyWindow(this);
        ClientSize = new(390, 300);
        body.Controls.Add(content); Controls.Add(body);
        LayoutRow AddRow(int height, Control[] controls, int[] offsets, bool endOnly = false)
        {
            var row = new LayoutRow(height, controls, offsets, endOnly);
            content.Controls.AddRange(controls); rows.Add(row); return row;
        }
        NumericUpDown Add(string label, decimal value, decimal min, decimal max, int decimals = 0, bool endOnly = false)
        {
            var caption = new Label { Text = label, Location = new(24, 4), Size = new(180, 26), Anchor = AnchorStyles.Top | AnchorStyles.Left };
            var number = new LiveNumericUpDown { Minimum = min, Maximum = max, Value = value, DecimalPlaces = decimals, Width = 142, Location = new(224, 0), AccessibleName = label, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            var slider = new ThemedSlider { Minimum = 0, TickStyle = TickStyle.None, AutoSize = false, Location = new(24, 32), Size = new(342, 30), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, BackColor = AppTheme.Window, AccessibleName = label + " slider" };
            decimal units = (decimal)Math.Pow(10, decimals); bool syncing = false;
            void SyncSlider()
            {
                if (syncing) return;
                syncing = true;
                try
                {
                    slider.Maximum = Math.Max(1, decimal.ToInt32((number.Maximum - number.Minimum) * units));
                    slider.LargeChange = Math.Max(1, slider.Maximum / 20);
                    slider.Value = Math.Clamp(decimal.ToInt32(decimal.Round((number.Value - number.Minimum) * units)), slider.Minimum, slider.Maximum);
                }
                finally { syncing = false; }
            }
            number.ValueChanged += (_, _) => { if (syncing) return; if (endOnly && !changingUnits) editedEnds.Add(label); SyncSlider(); NotifySettingsChanged(); };
            slider.ValueChanged += (_, _) =>
            {
                if (syncing) return;
                syncing = true;
                try { number.Value = Math.Clamp(number.Minimum + slider.Value / units, number.Minimum, number.Maximum); }
                finally { syncing = false; }
                if (endOnly && !changingUnits) editedEnds.Add(label);
                NotifySettingsChanged();
            };
            parameterRows[label] = AddRow(72, [caption, number, slider], [4, 0, 32], endOnly);
            values[label] = number; parameterLabels[label] = caption; sliders[label] = slider; syncSliders[label] = SyncSlider; SyncSlider(); return number;
        }
        void PairColumns(string start, string end)
        {
            var startRow = parameterRows[start]; var endRow = parameterRows[end];
            columnPairs.Add(startRow, endRow); columnEnds.Add(endRow);
        }
        void AddPair(string start, string end, decimal value, decimal min, decimal max, int decimals = 0)
        {
            Add(start, value, min, max, decimals); Add(end, value, min, max, decimals, true);
            pairs.Add((start, end)); PairColumns(start, end);
        }
        void AddMode(string label, string first, string second)
        {
            var caption = new Label { Text = label, Location = new(24, 4), Size = new(180, 26) };
            mode.Location = new(224, 0); mode.Width = 142; mode.AccessibleName = label; mode.Items.AddRange(new object[] { first, second }); mode.SelectedIndex = 0;
            AddRow(43, [caption, mode], [4, 0]);
        }
        if (kind is not EffectKind.Reverse and not EffectKind.Multiply and not EffectKind.SeamlessRepeat) AddRow(36, [automate], [3]);
        if (kind == EffectKind.Maximize)
        {
            AddPair("Target peak %", "End target peak %", 98, 1, 100);
            selectionOnly.CheckedChanged += (_, _) => NotifySettingsChanged(); AddRow(36, [selectionOnly], [3]);
        }
        if (kind is EffectKind.FadeIn or EffectKind.FadeOut) AddPair("Fade strength %", "End fade strength %", 100, 0, 100);
        if (kind == EffectKind.Reverb) { AddPair("Room size %", "End room size %", 45, 1, 100); AddPair("Mix %", "End mix %", 30, 0, 100); }
        if (kind == EffectKind.Delay)
        {
            AddMode("Delay timing", "Milliseconds", "BPM");
            AddPair("Delay ms", "End delay ms", 250, 1, 5000, 2);
            AddPair("Feedback %", "End feedback %", 35, 0, 95); AddPair("Mix %", "End mix %", 30, 0, 100);
            mode.SelectedIndexChanged += (_, _) => ChangeDelayUnits();
        }
        if (kind == EffectKind.Bitcrush)
        {
            AddPair("Bit depth", "End bit depth", 8, 1, 24);
            AddPair("Sample rate Hz", "End sample rate Hz", 12000, 100, 48000);
            AddPair("Mix %", "End mix %", 100, 0, 100);
        }
        if (kind == EffectKind.DeClick)
        {
            AddPair("Sensitivity %", "End sensitivity %", 50, 1, 100);
            AddPair("Repair width ms", "End repair width ms", 2, .1m, 20, 1);
        }
        if (kind == EffectKind.MultibandEq)
        {
            var reset = new ThemeButton("Reset to flat") { AccessibleName = "Reset EQ to flat", AccessibleDescription = "Reset all start and end band gains to 0 dB.", Size = new(150, 36) };
            reset.Click += (_, _) =>
            {
                var inputs = EqBandNames.SelectMany(name => new[] { values[name], values["End " + name] }).ToArray();
                if (!inputs.Any(input => input.Value != 0)) return;
                changingUnits = true;
                try { foreach (var input in inputs) input.Value = 0; }
                finally { changingUnits = false; }
                NotifySettingsChanged();
            };
            AddRow(44, [reset], [0]);
            for (int i = 0; i < EqBandNames.Length; i++)
            {
                string name = EqBandNames[i]; AddPair(name, "End " + name, 0, -24, 24, 1);
                string description = $"Center frequency: {MultibandEqSettings.CenterFrequencies[i]:0.##} Hz. Gain in dB.";
                values[name].Increment = .1m; values["End " + name].Increment = .1m;
                values[name].AccessibleDescription = description; values["End " + name].AccessibleDescription = description;
            }
        }
        if (kind == EffectKind.SeamlessRepeat)
        {
            Add("Total repetitions", 10, 1, 64);
            Add("Crossfade ms", 20, 0, 2000, 1);
            loopEndToStart.CheckedChanged += (_, _) => NotifySettingsChanged();
            smartSeam.CheckedChanged += (_, _) => NotifySettingsChanged();
            adaptiveCrossfade.CheckedChanged += (_, _) => NotifySettingsChanged();
            AddRow(36, [loopEndToStart], [3]);
            AddRow(36, [smartSeam], [3]); AddRow(36, [adaptiveCrossfade], [3]);
            AddRow(120, [new Label
            {
                Text = "Includes the original copy. Overlaps shorten the result; looping back adds one closing overlap.\nSmart seam refines the match. Adaptive crossfade helps keep the join's volume steady.",
                Location = new(24, 0), Size = new(342, 112), ForeColor = AppTheme.Muted, Tag = "Muted",
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, AccessibleName = "Seamless repeat explanation"
            }], [0]);
        }
        if (kind == EffectKind.Multiply)
        {
            Add("Total copies", 4, 1, 64);
            var variation = Add("Random variation %", 25, 1, 100);
            var variationHelp = new Label { Location = new(24, 0), Size = new(342, 58), ForeColor = AppTheme.Muted, Tag = "Muted", Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, AccessibleName = "Multiply variation explanation" };
            void UpdateVariationHelp() => variationHelp.Text = $"All copies play together. Each copy multiplies parameters by {1 - (double)variation.Value / 100:0.##} to {1 + (double)variation.Value / 100:0.##}. Values stay within each effect's limits.";
            variation.ValueChanged += (_, _) => UpdateVariationHelp(); UpdateVariationHelp(); AddRow(64, [variationHelp], [0]);
            var reroll = new ThemeButton("Reroll variation") { AccessibleName = "Reroll multiply variation", Size = new(150, 36) };
            reroll.Click += (_, _) => { int next; do next = Random.Shared.Next(); while (next == multiplySeed); multiplySeed = next; NotifySettingsChanged(); };
            AddRow(44, [reroll], [0]);
            void ApplyMultiplyGate(Control control, CheckBox gate)
            {
                if (control is Label)
                {
                    control.Enabled = true; control.Tag = gate.Checked ? null : "Muted";
                    control.ForeColor = gate.Checked ? AppTheme.Text : AppTheme.Muted;
                }
                else control.Enabled = gate.Checked;
            }
            void GateLastRow(CheckBox gate)
            { foreach (var control in rows[^1].Controls) { multiplyGroups[control] = gate; ApplyMultiplyGate(control, gate); } }
            void AddMultiplyField(CheckBox gate, string name, decimal value, decimal min, decimal max, int decimals = 0)
            { Add(name, value, min, max, decimals); GateLastRow(gate); }
            void AddMultiplyPair(CheckBox gate, string start, string end, decimal value, decimal min, decimal max, int decimals = 0)
            {
                AddMultiplyField(gate, start, value, min, max, decimals); AddMultiplyField(gate, end, value, min, max, decimals);
                PairColumns(start, end);
            }
            void AddMultiplyGroup(CheckBox gate)
            {
                AddRow(36, [gate], [3]);
                gate.CheckedChanged += (_, _) =>
                { foreach (var pair in multiplyGroups.Where(pair => ReferenceEquals(pair.Value, gate))) ApplyMultiplyGate(pair.Key, gate); NotifySettingsChanged(); };
            }
            AddMultiplyGroup(multiplyPitch);
            AddMultiplyPair(multiplyPitch, "Start pitch", "End pitch", 0, -24, 24, 2);
            AddMultiplyGroup(multiplyFilter);
            AddMode("Filter type", "Low pass", "High pass"); GateLastRow(multiplyFilter); mode.SelectedIndexChanged += (_, _) => NotifySettingsChanged();
            AddMultiplyPair(multiplyFilter, "Start frequency Hz", "End frequency Hz", 1000, 20, 20000);
            AddMultiplyGroup(multiplyReverb);
            AddMultiplyPair(multiplyReverb, "Start room size %", "End room size %", 45, 1, 100);
            AddMultiplyPair(multiplyReverb, "Start mix %", "End mix %", 30, 0, 100);
        }
        if (kind == EffectKind.Filter)
        {
            AddMode("Filter", "Low pass", "High pass");
            AddPair("Start frequency Hz", "End frequency Hz", 1000, 20, 20000);
            mode.SelectedIndexChanged += (_, _) => NotifySettingsChanged();
        }
        if (kind == EffectKind.PitchShift)
        {
            AddMode("Units", "Semitones", "Percent");
            AddPair("Start pitch", "End pitch", 0, -24, 24, 2);
            var start = values["Start pitch"]; var end = values["End pitch"];
            mode.SelectedIndexChanged += (_, _) =>
            {
                if (mode.SelectedIndex < 0) return;
                changingUnits = true;
                try
                {
                    double a = (double)start.Value, b = (double)end.Value;
                    if (percent) { a = 12 * Math.Log2(a / 100); b = 12 * Math.Log2(b / 100); }
                    percent = mode.SelectedIndex == 1;
                    foreach (var number in new[] { start, end }) { number.Minimum = -24; number.Maximum = 400; }
                    start.Value = (decimal)Math.Clamp(percent ? 100 * Math.Pow(2, a / 12) : a, percent ? 25 : -24, percent ? 400 : 24);
                    end.Value = (decimal)Math.Clamp(percent ? 100 * Math.Pow(2, b / 12) : b, percent ? 25 : -24, percent ? 400 : 24);
                    foreach (var number in new[] { start, end }) { number.Minimum = percent ? 25 : -24; number.Maximum = percent ? 400 : 24; }
                }
                finally { changingUnits = false; foreach (var sync in syncSliders.Values) sync(); }
                NotifySettingsChanged();
            };
        }
        if (kind is EffectKind.Delay or EffectKind.Reverb)
        { cutTail.CheckedChanged += (_, _) => NotifySettingsChanged(); AddRow(36, [cutTail], [3]); }
        if (kind == EffectKind.Reverse)
            AddRow(48, [new Label { Text = "Reverse the selected audio without swapping its stereo channels.", Location = new(24, 0), Size = new(342, 42), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right }], [0]);
        preview.CheckedChanged += (_, _) => NotifySettingsChanged(); AddRow(36, [preview], [3]);
        AddRow(72, [new Label { Text = "Space: cursor; Shift+Space: restart selection; Ctrl+Space: stop\nKeep this window open while adjusting the waveform.", Location = new(24, 0), Size = new(342, 68), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, AccessibleName = "Effect playback shortcuts" }], [0]);
        automate.CheckedChanged += (_, _) =>
        {
            if (automate.Checked && !automationInitialized)
            {
                changingUnits = true;
                try { foreach (var pair in pairs) if (!editedEnds.Contains(pair.End)) values[pair.End].Value = values[pair.Start].Value; automationInitialized = true; }
                finally { changingUnits = false; }
            }
            SetColumnMode(automate.Checked); LayoutRows(); NotifySettingsChanged();
        };
        var apply = new ThemeButton("Apply", ButtonRole.Primary) { AccessibleName = "Apply effect" };
        var cancel = new ThemeButton("Cancel") { AccessibleName = "Cancel effect" };
        apply.Click += (_, _) => ApplyRequested?.Invoke(); cancel.Click += (_, _) => RequestCancel();
        var footer = AppTheme.CreateActionFooter(apply, cancel); Controls.Add(footer); AcceptButton = apply;
        float scale = DeviceDpi / 96f;
        int maximumHeight = Math.Min(680, Math.Max(260, (int)((Screen.PrimaryScreen?.WorkingArea.Height ?? 900) / scale) - 120));
        wideColumns = kind == EffectKind.Multiply;
        ClientSize = new(wideColumns ? 780 : 390, Math.Min(40 + rows.Where(row => !row.EndOnly && !(wideColumns && columnEnds.Contains(row))).Sum(row => row.Height) + footer.Height, maximumHeight));
        AppTheme.StyleInputs(this); Scale(new SizeF(scale, scale)); MinimumSize = new(Width, Math.Min(Height, (int)(260 * scale)));
        columnWidthLogical = ClientSize.Width / scale / (wideColumns ? 2 : 1); lastClientWidth = ClientSize.Width;
        ClientSizeChanged += (_, _) =>
        {
            int width = ClientSize.Width; if (width == lastClientWidth) return; lastClientWidth = width;
            if (initialized && !changingColumnWidth) columnWidthLogical = width / (DeviceDpi / 96d) / (wideColumns ? 2 : 1);
        };
        body.Resize += (_, _) => LayoutRows(); body.Layout += (_, _) => LayoutRows();
        initialized = true; LayoutRows();
        FormClosing += (_, _) => { IsClosing = true; if (!Applied) RequestCancel(); };
    }

    void ChangeDelayUnits()
    {
        bool nextBpm = mode.SelectedItem?.ToString() == "BPM";
        if (nextBpm == delayInBpm) return;
        changingUnits = true;
        try
        {
            foreach (string key in new[] { "Delay ms", "End delay ms" })
            {
                var number = values[key];
                double seconds = delayInBpm ? 60 / (double)number.Value : (double)number.Value / 1000;
                decimal min = nextBpm ? 12 : 1, max = nextBpm ? 60000 : 5000;
                number.Minimum = 0; number.Maximum = 60000;
                number.Value = Math.Clamp((decimal)(nextBpm ? 60 / seconds : seconds * 1000), min, max);
                number.Minimum = min; number.Maximum = max;
                string label = (key.StartsWith("End ") ? "End delay " : "Delay ") + (nextBpm ? "BPM" : "ms");
                number.AccessibleName = label; parameterLabels[key].Text = label; sliders[key].AccessibleName = label + " slider";
            }
            delayInBpm = nextBpm;
        }
        finally { changingUnits = false; foreach (var sync in syncSliders.Values) sync(); }
        LayoutRows(); NotifySettingsChanged();
    }

    protected override void SetClientSizeCore(int x, int y)
    {
        if (initialized) x = Math.Max(x, (int)Math.Ceiling((wideColumns ? 780 : 390) * DeviceDpi / 96d));
        base.SetClientSizeCore(x, y);
    }

    void SetColumnMode(bool enabled)
    {
        bool next = kind == EffectKind.Multiply || enabled;
        if (next == wideColumns) return;
        changingColumnWidth = true;
        try
        {
            wideColumns = next;
            double scale = DeviceDpi / 96d;
            int frameWidth = Width - ClientSize.Width;
            int minimumClientWidth = (int)Math.Ceiling((next ? 780 : 390) * scale);
            int desiredWidth = (int)Math.Round(Math.Clamp(columnWidthLogical * scale * (next ? 2 : 1), minimumClientWidth, int.MaxValue - frameWidth));
            MinimumSize = new(minimumClientWidth + frameWidth, MinimumSize.Height);
            ClientSize = new(desiredWidth, ClientSize.Height);
            lastClientWidth = ClientSize.Width;
            if (Visible)
            {
                Rectangle area = Screen.FromControl(this).WorkingArea;
                Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width));
            }
        }
        finally { changingColumnWidth = false; }
    }

    void LayoutRows()
    {
        if (!initialized || layingOut) return;
        layingOut = true;
        try
        {
            float scale = DeviceDpi / 96f;
            int margin = (int)Math.Round(24 * scale), gap = (int)Math.Round(16 * scale);
            void SetWidths()
            {
                content.Width = Math.Max((int)(300 * scale) * (wideColumns ? 2 : 1), body.ClientSize.Width);
                foreach (var row in rows)
                {
                    int left = 0, right = content.Width;
                    if (wideColumns)
                    {
                        if (columnPairs.ContainsKey(row)) right = content.Width / 2;
                        else if (columnEnds.Contains(row)) left = content.Width / 2;
                    }
                    var field = row.Controls.FirstOrDefault(control => control is NumericUpDown or ComboBox);
                    if (field != null) field.Left = right - margin - field.Width;
                    foreach (var control in row.Controls)
                    {
                        if (control is NumericUpDown or ComboBox) continue;
                        control.Left = left + margin;
                        if (control is TrackBar) control.Width = Math.Max(1, right - left - 2 * margin);
                        else if (control is Label) control.Width = Math.Max(1, field == null ? right - left - 2 * margin : field.Left - left - margin - gap);
                    }
                }
            }
            SetWidths();
            int y = margin;
            void PlaceRow(LayoutRow row, bool visible)
            {
                for (int i = 0; i < row.Controls.Length; i++)
                { row.Controls[i].Visible = visible; if (visible) row.Controls[i].Top = y + (int)Math.Round(row.Offsets[i] * scale); }
            }
            foreach (var row in rows)
            {
                if (wideColumns && columnEnds.Contains(row)) continue;
                bool visible = !row.EndOnly || automate.Checked;
                PlaceRow(row, visible);
                if (wideColumns && columnPairs.TryGetValue(row, out var endRow))
                {
                    PlaceRow(endRow, visible);
                    if (visible) y += (int)Math.Round(Math.Max(row.Height, endRow.Height) * scale);
                }
                else if (visible) y += (int)Math.Round(row.Height * scale);
            }
            foreach (var pair in pairs)
            {
                string label = values[pair.Start].AccessibleName ?? pair.Start;
                parameterLabels[pair.Start].Text = automate.Checked && !label.StartsWith("Start ", StringComparison.Ordinal) ? "Start " + char.ToLowerInvariant(label[0]) + label[1..] : label;
            }
            content.Height = y + (int)Math.Round(16 * scale);
            body.AutoScrollMinSize = new(0, content.Height); body.PerformLayout(); SetWidths();
        }
        finally { layingOut = false; }
    }

    void NotifySettingsChanged() { if (!changingUnits) SettingsChanged?.Invoke(); }
    void RequestCancel() { if (cancelSignalled || Applied) return; cancelSignalled = true; CancelRequested?.Invoke(); }
    public bool DispatchHoverCommand(Keys keyData)
    {
        if (IsDisposed || IsClosing) return false;
        switch (keyData)
        {
            case Keys.F1: ShortcutHelp.Show(this); return true;
            case Keys.Escape: RequestCancel(); return true;
            case Keys.Space: CursorPlaybackRequested?.Invoke(); return true;
            case Keys.Shift | Keys.Space: RestartPlaybackRequested?.Invoke(); return true;
            case Keys.Control | Keys.Space: StopPlaybackRequested?.Invoke(); return true;
            default: return false;
        }
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (DispatchHoverCommand(keyData)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
