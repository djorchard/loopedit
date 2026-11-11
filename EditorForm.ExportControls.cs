namespace OrchardSound;

public sealed partial class EditorForm
{
    readonly ContextMenuStrip exportContextMenu = new();
    internal ContextMenuStrip ExportContextMenu => exportContextMenu;

    Control BuildExportControls()
    {
        AppTheme.StyleMenu(exportContextMenu); exportContextMenu.ShowItemToolTips = true;
        exportContextMenu.Renderer = new ExportOptionsRenderer(((ToolStripProfessionalRenderer)exportContextMenu.Renderer).ColorTable);
        string[] extensions = ["WAV", "MP3", "FLAC", "OGG", "M4A"];
        format.Items.AddRange(extensions.Cast<object>().ToArray());
        foreach (string extension in extensions)
        {
            var item = new ToolStripMenuItem(extension) { AccessibleName = "Export format " + extension, AccessibleRole = AccessibleRole.RadioButton, Tag = extension };
            item.Click += (_, _) => format.SelectedItem = extension; exportContextMenu.Items.Add(item);
        }
        void SyncFormats()
        { foreach (var item in exportContextMenu.Items.OfType<ToolStripMenuItem>().Where(item => item.Tag is string)) item.Checked = Equals(item.Tag, format.Text); }
        format.SelectedIndexChanged += (_, _) => SyncFormats(); format.SelectedIndex = 0; SyncFormats();
        exportContextMenu.Items.Add(new ToolStripSeparator());
        exportContextMenu.Items.Add(new ToolStripMenuItem("Set export folder", null, (_, _) => SetExportFolder())
        {
            AccessibleName = "Set export folder", ShortcutKeyDisplayString = "Ctrl+Shift+S",
            ToolTipText = "Choose the folder used by future exports. This action does not export audio."
        });
        var group = new Panel { Size = new(84, 74), Margin = new(2, 3, 2, 3), BackColor = AppTheme.Surface, AccessibleName = "Export controls" };
        var export = MakeButton(EditorIcon.Export, "Export", "E", async () => await Export(false));
        export.Location = Point.Empty; export.Margin = Padding.Empty;
        var options = new ThemeButton("▾") { AccessibleName = "Export options", MinimumSize = Size.Empty, Padding = Padding.Empty, Size = new(24, 74), Location = new(60, 0), Margin = Padding.Empty };
        options.Click += (_, _) => { if (!busy) exportContextMenu.Show(options, new Point(0, options.Height)); };
        tips.SetToolTip(options, "Choose the export format or set the destination folder. Ctrl+Shift+S chooses a folder.");
        group.Controls.Add(export); group.Controls.Add(options); return group;
    }

    sealed class ExportOptionsRenderer(ProfessionalColorTable colors) : ToolStripProfessionalRenderer(colors)
    {
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            if (e.Item.Tag is not string) { base.OnRenderItemCheck(e); return; }
            var state = e.Graphics.Save(); e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(AppTheme.Accent);
            float diameter = Math.Max(4, Math.Min(e.ImageRectangle.Width, e.ImageRectangle.Height) * .45f);
            e.Graphics.FillEllipse(brush, e.ImageRectangle.Left + (e.ImageRectangle.Width - diameter) / 2, e.ImageRectangle.Top + (e.ImageRectangle.Height - diameter) / 2, diameter, diameter);
            e.Graphics.Restore(state);
        }
    }
}
