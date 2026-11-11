namespace OrchardSound;

public sealed partial class EditorForm
{
    const string NoteAuditionControlName = "HearHoveredNote";
    bool precisionHearHoveredNote;

    CheckBox CreateNoteAuditionCheckBox(string paneName, bool enabled, Action<bool> setEnabled)
    {
        const string description = "Hear the hovered note's frequency band while playing or scrubbing. Move out of the spectrogram to hear the full mix.";
        var checkBox = new CheckBox
        {
            Name = NoteAuditionControlName,
            Text = "Hear hovered note",
            AutoSize = true,
            Checked = enabled,
            Visible = false,
            AccessibleName = "Hear hovered note in " + paneName,
            AccessibleDescription = description
        };
        checkBox.CheckedChanged += (_, _) =>
        {
            setEnabled(checkBox.Checked);
            RefreshNoteAudition();
        };
        tips.SetToolTip(checkBox, description);
        return checkBox;
    }

    internal bool IsNoteAuditionEnabled(int pane)
    {
        if (pane == 0)
            return canvas.PrecisionViewMode == AudioViewMode.Spectrogram && precisionHearHoveredNote;
        if (pane < 1 || pane > canvas.Slots.Count)
            return false;
        var slot = canvas.Slots[pane - 1];
        return slot.ViewMode == AudioViewMode.Spectrogram && slot.HearHoveredNote;
    }

    internal static void LayoutSidebarPanel(Panel panel, bool precision, float scale)
    {
        int gap = Math.Max(1, (int)Math.Round(6 * scale));
        var view = panel.Controls.OfType<ComboBox>().FirstOrDefault();
        if (view == null) return;
        var amount = panel.Controls.OfType<NumericUpDown>().FirstOrDefault();
        var slider = panel.Controls.OfType<TrackBar>().FirstOrDefault();
        var audition = panel.Controls.OfType<CheckBox>().SingleOrDefault(box => box.Name == NoteAuditionControlName);
        view.Height = view.PreferredHeight;
        view.IntegralHeight = false;
        view.DropDownHeight = Math.Max(view.PreferredHeight, view.ItemHeight * view.Items.Count + gap);
        view.DropDownWidth = Math.Max(view.Width, TextRenderer.MeasureText("Spectrogram", view.Font).Width + SystemInformation.VerticalScrollBarWidth + gap);
        if (amount != null) amount.Height = amount.PreferredHeight;
        if (slider != null) { slider.AutoSize = false; slider.Left = 0; slider.Width = panel.ClientSize.Width; }

        int PlaceAuditionAfter(int bottom)
        {
            if (audition == null) return bottom;
            bool show = view.SelectedIndex == (int)AudioViewMode.Spectrogram;
            audition.Visible = show;
            if (!show) return bottom;
            audition.Location = new Point(0, bottom + gap);
            audition.Size = audition.GetPreferredSize(Size.Empty);
            return audition.Bottom;
        }

        static void CenterLabel(Label label, Control input) => label.Top = input.Top + Math.Max(0, (input.Height - label.Height) / 2);
        if (precision)
        {
            if (amount == null || slider == null) return;
            var title = panel.Controls.OfType<Label>().Single(label => label.Text == "PRECISION");
            amount.Top = title.Bottom + gap;
            foreach (var label in panel.Controls.OfType<Label>().Where(label => label != title)) CenterLabel(label, amount);
            slider.Top = Math.Max(amount.Bottom, panel.Controls.OfType<Label>().Where(label => label != title).Select(label => label.Bottom).DefaultIfEmpty(0).Max()) + gap;
            view.Left = 0; view.Width = panel.ClientSize.Width; view.Top = slider.Bottom + gap;
            panel.Height = PlaceAuditionAfter(view.Bottom) + gap;
            return;
        }

        var open = panel.Controls.OfType<IconButton>().Single(button => button.Icon == EditorIcon.Open);
        var remove = panel.Controls.OfType<IconButton>().Single(button => button.Icon == EditorIcon.Remove);
        remove.Top = 0;
        var split = panel.Controls.OfType<CheckBox>().Single(control => control.AccessibleName?.StartsWith("Split channels in Track ") == true);
        int titleBottom = panel.Controls.Cast<Control>().Where(control => control is CheckBox && control != split && control != audition || control is Label { Tag: "KeepColor" } || control is IconButton { Icon: EditorIcon.Remove }).Max(control => control.Bottom);
        int rowTop = titleBottom + gap, rowHeight = Math.Max(open.Height, view.Height);
        open.Top = rowTop + (rowHeight - open.Height) / 2;
        view.Top = rowTop + (rowHeight - view.Height) / 2; view.Left = open.Right + gap; view.Width = Math.Max(1, panel.ClientSize.Width - view.Left);
        split.Left = 0; split.Top = PlaceAuditionAfter(rowTop + rowHeight) + gap;
        if (amount == null || slider == null) { panel.Height = split.Bottom + gap; return; }
        amount.Top = split.Bottom + gap;
        foreach (var label in panel.Controls.OfType<Label>().Where(label => label.Text is "Volume" or "%")) CenterLabel(label, amount);
        slider.Top = Math.Max(amount.Bottom, panel.Controls.OfType<Label>().Where(label => label.Text is "Volume" or "%").Select(label => label.Bottom).DefaultIfEmpty(0).Max()) + gap;
        var tempo = panel.Controls.OfType<Label>().Single(label => label.AccessibleName == "Estimated BPM");
        tempo.Top = slider.Bottom + gap;
        panel.Height = tempo.Bottom + gap;
    }
}
