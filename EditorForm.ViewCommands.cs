namespace OrchardSound;

public sealed partial class EditorForm
{
    readonly ContextMenuStrip viewContextMenu = new();
    readonly CheckBox precisionMagnifier = new() { Checked = true };
    readonly CheckBox followCursor = new();
    internal ContextMenuStrip ViewContextMenu => viewContextMenu;

    void InitializeViewMenu()
    {
        AppTheme.StyleMenu(viewContextMenu); viewContextMenu.ShowItemToolTips = true;
        void Add(string caption, string name, string shortcut, CheckBox state, string description)
        {
            var item = new ToolStripMenuItem(caption) { AccessibleName = name, CheckOnClick = true, Checked = state.Checked, ShortcutKeyDisplayString = shortcut, ToolTipText = description };
            item.CheckedChanged += (_, _) => { if (state.Checked != item.Checked) state.Checked = item.Checked; };
            state.CheckedChanged += (_, _) => { if (item.Checked != state.Checked) item.Checked = state.Checked; };
            viewContextMenu.Items.Add(item);
        }
        Add("Fit waveform height", "Fit waveform height", "", magnify, "Enlarge quiet waveforms vertically. Playback and export volume stay the same.");
        Add("Precision magnifier", "Precision magnifier", "", precisionMagnifier, "Show a purple close-up when the precision view has enough room. Available below 100x zoom.");
        precisionMagnifier.CheckedChanged += (_, _) => { canvas.PrecisionMagnifierEnabled = precisionMagnifier.Checked; RefreshNoteAudition(); canvas.Invalidate(); };
        Add("Follow cursor", "Follow cursor", "", followCursor, "Keep precision centered on the white cursor. Alt temporarily fits the selection.");
        followCursor.CheckedChanged += (_, _) => { canvas.FollowCursor = followCursor.Checked; canvas.Invalidate(); };
        viewContextMenu.Items.Add(new ToolStripSeparator());
        Add("Beats / BPM", "Show beats and estimated BPM", "B", beats, "Show estimated BPM and beat markers. Very short or irregular sounds may have no tempo estimate.");
        Add("Kick hits", "Show likely kick markers", "K", kicks, "Show likely kick onsets. Bass notes and low impacts can also produce markers.");
    }

    IconButton MakeOptionToggle(EditorIcon icon, string name, CheckBox state, string caption)
    {
        var button = MakeButton(icon, name, "", () => { if (!busy) state.Checked = !state.Checked; });
        button.Caption = caption; button.IsToggled = state.Checked;
        state.CheckedChanged += (_, _) => { button.IsToggled = state.Checked; button.Invalidate(); };
        return button;
    }

    void ShowViewMenu()
    {
        if (busy) return;
        var button = Controls.OfType<FlowLayoutPanel>().SelectMany(panel => panel.Controls.OfType<IconButton>()).FirstOrDefault(control => control.AccessibleName == "View");
        if (button != null) viewContextMenu.Show(button, new Point(0, button.Height));
    }
}
