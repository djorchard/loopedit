namespace OrchardSound;

public sealed record RecordingDestination(string Name, ChannelSlot? Slot);

public sealed class RecordingDeviceDialog : Form
{
    readonly ComboBox inputs = new() { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Recording input device" };
    readonly ComboBox destination = new() { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Recording destination" };
    public InputDevice? SelectedInput => inputs.SelectedItem as InputDevice;
    public RecordingDestination? SelectedDestination => destination.SelectedItem as RecordingDestination;

    public RecordingDeviceDialog(IReadOnlyList<InputDevice> devices, RecordingDestination[]? destinations = null)
    {
        ArgumentNullException.ThrowIfNull(devices);
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Choose recording input"; ClientSize = new Size(500, 296);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false; Font = new Font("Segoe UI", 10);
        AppTheme.ApplyWindow(this);
        Controls.Add(new Label { Text = devices.Count == 0 ? "No recording inputs are available." : "Recording input", Location = new Point(24, 20), Size = new Size(452, 26) });
        inputs.Location = new Point(24, 56); inputs.Size = new Size(452, 30); inputs.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        inputs.DisplayMember = nameof(InputDevice.Name); inputs.Items.AddRange(devices.Cast<object>().ToArray());
        inputs.SelectedIndex = devices.Count == 0 ? -1 : 0; inputs.Enabled = devices.Count != 0; Controls.Add(inputs);
        Controls.Add(new Label { Text = "Record into", Location = new Point(24, 108), Size = new Size(452, 26) });
        destination.Location = new Point(24, 144); destination.Size = new Size(452, 30); destination.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        var options = destinations is { Length: > 0 } ? destinations : [new RecordingDestination("New track", null)];
        destination.DisplayMember = nameof(RecordingDestination.Name); destination.Items.AddRange(options.Cast<object>().ToArray());
        int initial = Array.FindIndex(options, option => option.Slot != null && option.Slot.Track == null);
        if (initial < 0) initial = Array.FindIndex(options, option => option.Slot?.Enabled == true);
        if (initial < 0) initial = Array.FindIndex(options, option => option.Slot == null);
        destination.SelectedIndex = Math.Max(0, initial); Controls.Add(destination);
        var record = new ThemeButton("Record", ButtonRole.Primary) { DialogResult = DialogResult.OK, Enabled = devices.Count != 0, AccessibleName = "Start recording" };
        var cancel = new ThemeButton("Cancel") { DialogResult = DialogResult.Cancel, AccessibleName = "Cancel recording" };
        Controls.Add(AppTheme.CreateActionFooter(record, cancel)); AcceptButton = record; CancelButton = cancel;
        void ValidateChoice() => record.Enabled = SelectedInput != null && SelectedDestination != null;
        inputs.SelectedIndexChanged += (_, _) => ValidateChoice(); destination.SelectedIndexChanged += (_, _) => ValidateChoice(); ValidateChoice();
        FormClosing += (_, args) => { if (DialogResult == DialogResult.OK && (SelectedInput == null || SelectedDestination == null)) args.Cancel = true; };
        AppTheme.StyleInputs(this); Scale(new SizeF(DeviceDpi / 96f, DeviceDpi / 96f)); MinimumSize = Size;
    }
}
