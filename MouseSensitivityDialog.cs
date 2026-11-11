namespace OrchardSound;

public sealed class MouseSensitivityDialog : Form
{
    readonly CheckBox enabled;
    readonly LiveNumericUpDown speed;
    public bool OverrideEnabled => enabled.Checked;
    public int Speed => (int)speed.Value;

    public MouseSensitivityDialog(bool useOverride, int currentSpeed)
    {
        Text = "Mouse sensitivity"; Font = new("Segoe UI", 10); FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent; MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
        AutoScaleDimensions = new(96, 96); AutoScaleMode = AutoScaleMode.Dpi; AppTheme.ApplyWindow(this);
        ClientSize = new(430, 300);
        var body = new Panel { Dock = DockStyle.Fill }; Controls.Add(body);
        enabled = new() { Text = $"Use custom pointer speed over {Branding.Name}", AutoSize = true, Checked = useOverride, Location = new(24, 22), AccessibleName = "Use custom pointer speed" };
        body.Controls.Add(enabled);
        body.Controls.Add(new Label { Text = "Pointer speed", AutoSize = true, Location = new(24, 70) });
        speed = new() { Minimum = 1, Maximum = 20, Value = Math.Clamp(currentSpeed, 1, 20), Location = new(260, 65), Width = 144, AccessibleName = "Pointer speed amount" };
        var slider = new ThemedSlider { AutoSize = false, Minimum = 1, Maximum = 20, Value = (int)speed.Value, TickStyle = TickStyle.None, Location = new(24, 105), Size = new(380, 30), AccessibleName = "Pointer speed slider" };
        speed.ValueChanged += (_, _) => { slider.Value = (int)speed.Value; };
        slider.ValueChanged += (_, _) => { speed.Value = slider.Value; };
        void Toggle() { speed.Enabled = slider.Enabled = enabled.Checked; }
        enabled.CheckedChanged += (_, _) => Toggle(); Toggle();
        body.Controls.Add(speed); body.Controls.Add(slider);
        body.Controls.Add(new Label { Text = $"Temporarily changes Windows pointer speed while the pointer is over {Branding.Name}. Restores your previous speed when you leave or close the app.", Location = new(24, 151), Size = new(380, 65), ForeColor = AppTheme.Muted, Tag = "Muted" });
        var apply = new ThemeButton("Apply", ButtonRole.Primary) { DialogResult = DialogResult.OK };
        var cancel = new ThemeButton("Cancel") { DialogResult = DialogResult.Cancel };
        Controls.Add(AppTheme.CreateActionFooter(apply, cancel)); AcceptButton = apply; CancelButton = cancel;
        AppTheme.StyleInputs(this); Scale(new SizeF(DeviceDpi / 96f, DeviceDpi / 96f));
    }
}
