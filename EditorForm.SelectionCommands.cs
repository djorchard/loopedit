namespace OrchardSound;

public sealed partial class EditorForm
{
    readonly ContextMenuStrip selectionContextMenu = new();
    internal ContextMenuStrip SelectionContextMenu => selectionContextMenu;

    void InitializeSelectionMenu()
    {
        AppTheme.StyleMenu(selectionContextMenu);
        selectionContextMenu.ShowItemToolTips = true;
        int size = Math.Max(16, (int)Math.Round(20 * DeviceDpi / 96f)); selectionContextMenu.ImageScalingSize = new(size, size);
        AddSelectionMenuCommand(EditorIcon.Start, "Selection start", "LMB / [", () => canvas.SetCursorBoundary(true));
        AddSelectionMenuCommand(EditorIcon.End, "Selection end", "RMB / ]", () => canvas.SetCursorBoundary(false));
        AddSelectionMenuCommand(EditorIcon.SelectAll, "Select all", "Ctrl+A", SelectAll);
        selectionContextMenu.Items.Add(new ToolStripSeparator());
        AddSelectionMenuCommand(EditorIcon.ClearSelection, "Clear selection", "Esc", ClearLoop);
        AddSelectionMenuCommand(EditorIcon.Delete, "Delete section", "Del", DeleteSection);
        AddSelectionMenuCommand(EditorIcon.Duplicate, "Duplicate selection", "Ctrl+D", DuplicateSelection);
        AddSelectionMenuCommand(EditorIcon.Trim, "Trim to selection", "Ctrl+T", TrimSelection);
        selectionContextMenu.Items.Add(new ToolStripSeparator());
        AddSelectionMenuCommand(EditorIcon.Cue, "Save section cue points", "Ctrl+B", SaveSelection);
    }

    internal ToolStripMenuItem AddSelectionMenuCommand(EditorIcon icon, string name, string shortcut, Action action)
    {
        int size = selectionContextMenu.ImageScalingSize.Width;
        var image = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(image)) { graphics.Clear(Color.Transparent); IconArtwork.Draw(graphics, icon, new RectangleF(0, 0, size, size)); }
        var item = new ToolStripMenuItem(name, image, (_, _) => action())
        {
            AccessibleName = name, ShortcutKeyDisplayString = shortcut,
            ToolTipText = IconDescriptions.For(icon) + (shortcut.Length == 0 ? "" : "\nShortcut: " + shortcut)
        };
        selectionContextMenu.Items.Add(item); return item;
    }

    void ShowSelectionMenu()
    {
        if (busy) return;
        var button = Controls.OfType<FlowLayoutPanel>().SelectMany(panel => panel.Controls.OfType<IconButton>()).FirstOrDefault(control => control.AccessibleName == "Selection");
        if (button == null) return;
        selectionContextMenu.Show(button, new Point(0, button.Height));
    }

    internal void FitSelectionView()
    {
        if (busy || !canvas.HasSelection) return;
        canvas.ZoomToSelection(.1); canvas.Invalidate(); status.Text = "Selection fitted with surrounding context.";
    }

    void DisposeSelectionMenu()
    {
        foreach (ToolStripItem item in selectionContextMenu.Items) item.Image?.Dispose();
        selectionContextMenu.Dispose();
    }
}
