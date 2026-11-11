namespace OrchardSound;

public sealed class InsertSilenceDialog : Form
{
    readonly LiveNumericUpDown duration;
    readonly ComboBox location;
    public long DurationFrames => Math.Max(1, (long)decimal.Round(duration.Value * AudioTrack.Rate, 0, MidpointRounding.AwayFromZero));
    public int PositionChoice => location.SelectedIndex;

    public InsertSilenceDialog(bool hasSelection)
    {
        Text = "Insert silence"; Font = new("Segoe UI", 10); FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent; MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
        AutoScaleDimensions = new(96, 96); AutoScaleMode = AutoScaleMode.Dpi; AppTheme.ApplyWindow(this);
        ClientSize = new(410, 285);
        var body = new Panel { Dock = DockStyle.Fill, Padding = new(24) }; Controls.Add(body);
        body.Controls.Add(new Label { Text = "Duration (seconds)", AutoSize = true, Location = new(24, 25) });
        duration = new() { Minimum = .001m, Maximum = 600, DecimalPlaces = 3, Value = 1, Increment = .1m, Location = new(230, 20), Width = 150, AccessibleName = "Silence duration seconds" };
        var slider = new ThemedSlider { AutoSize = false, Minimum = 1, Maximum = 600000, Value = 1000, TickStyle = TickStyle.None, Location = new(24, 58), Size = new(356, 28), AccessibleName = "Silence duration slider" };
        bool changing = false;
        duration.ValueChanged += (_, _) => { if (changing) return; changing = true; slider.Value = Math.Clamp((int)(duration.Value * 1000), 1, slider.Maximum); changing = false; };
        slider.ValueChanged += (_, _) => { if (changing) return; changing = true; duration.Value = slider.Value / 1000m; changing = false; };
        body.Controls.Add(duration); body.Controls.Add(slider);
        body.Controls.Add(new Label { Text = "Insert at", AutoSize = true, Location = new(24, 103) });
        location = new() { DropDownStyle = ComboBoxStyle.DropDownList, Location = new(170, 98), Width = 210, AccessibleName = "Silence insertion position" };
        location.Items.Add("White cursor");
        if (hasSelection) location.Items.AddRange(new object[] { "Selection start", "Selection end" });
        location.SelectedIndex = 0; body.Controls.Add(location);
        body.Controls.Add(new Label { Text = "Adds time across all tracks. Later audio and cue points move together.", Location = new(24, 143), Size = new(356, 45), ForeColor = AppTheme.Muted, Tag = "Muted" });
        var insert = new ThemeButton("Insert", ButtonRole.Primary) { DialogResult = DialogResult.OK, AccessibleName = "Insert silence" };
        var cancel = new ThemeButton("Cancel") { DialogResult = DialogResult.Cancel };
        Controls.Add(AppTheme.CreateActionFooter(insert, cancel)); AcceptButton = insert; CancelButton = cancel;
        AppTheme.StyleInputs(this); Scale(new SizeF(DeviceDpi / 96f, DeviceDpi / 96f));
    }
}
