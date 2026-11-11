using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace OrchardSound;

public enum EditorIcon { Open, AddTrack, Play, Start, End, SelectAll, ClearSelection, FadeIn, FadeOut, Undo, Redo, EqualHeights, Export, ExportFolder, Remove, Delete, Trim, Bookmark, OpenSession, SaveSession, Record, Effects, Paste, Duplicate, Cue, Help, View, Scrub, RefineLoop, PlayLoop, Silence }

public sealed class IconButton : Button
{
    public EditorIcon Icon { get; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public string Shortcut { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public string Caption { get; set; } = "";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool IsPlaying { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool IsToggled { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public ButtonRole Role { get; set; } = ButtonRole.Secondary;
    bool hover;
    public IconButton(EditorIcon icon, string name, string shortcut, Action action)
    {
        Icon = icon; Shortcut = shortcut; Text = name; AccessibleName = name; AccessibleDescription = shortcut;
        Size = new(58, 74); Margin = new(2, 3, 2, 3); FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false;
        Click += (_, _) => action(); SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
    }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        Control? surface = Parent;
        while (surface != null && surface.BackColor.A != 255) surface = surface.Parent;
        using var background = new SolidBrush(surface?.BackColor ?? AppTheme.Surface);
        e.Graphics.FillRectangle(background, ClientRectangle);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        // Rounded cards leave their corners outside the path. Paint the entire
        // client first, including native WM_PRINT and opaque double buffering.
        OnPaintBackground(e);
        var g = e.Graphics; var saved = g.Save(); float scale = DeviceDpi / 96f; g.ScaleTransform(scale, scale);
        int w = (int)(Width / scale), h = (int)(Height / scale);
        bool primary = Role == ButtonRole.Primary || IsToggled || (Icon is EditorIcon.Play or EditorIcon.PlayLoop) && IsPlaying;
        Color top = !Enabled ? AppTheme.Surface : primary ? hover ? AppTheme.AccentHover : AppTheme.Accent : hover ? AppTheme.Elevated : Color.FromArgb(34, 47, 63);
        Color bottom = !Enabled ? AppTheme.Surface : primary ? AppTheme.Accent : AppTheme.Surface;
        var box = new RectangleF(1.5f, 1.5f, Math.Max(1, w - 3), Math.Max(1, h - 3));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = IconArtwork.Rounded(box, 7))
        using (var fill = new LinearGradientBrush(box, top, bottom, 90))
        using (var outline = new Pen(!Enabled ? AppTheme.Border : primary ? Color.FromArgb(160, AppTheme.AccentHover) : hover || Focused ? Color.FromArgb(150, AppTheme.Accent) : AppTheme.Border))
        { g.FillPath(fill, path); g.DrawPath(outline, path); }
        if (Focused && ShowFocusCues)
        {
            using var focus = IconArtwork.Rounded(new RectangleF(4, 4, Math.Max(1, w - 8), Math.Max(1, h - 8)), 5);
            using var pen = new Pen(primary ? AppTheme.PrimaryText : AppTheme.Accent) { DashStyle = DashStyle.Dot }; g.DrawPath(pen, focus);
        }
        IconArtwork.Draw(g, Icon, new RectangleF((w - 24) / 2f, Shortcut.Length > 0 ? 8 : Caption.Length > 0 ? 3 : (h - 24) / 2f, 24, 24), IsPlaying, Enabled, primary);
        if (Caption.Length > 0)
        {
            using var font = new Font("Segoe UI Semibold", 10, FontStyle.Regular, GraphicsUnit.Pixel);
            using var color = new SolidBrush(!Enabled ? AppTheme.DisabledText : primary ? AppTheme.PrimaryText : AppTheme.Text);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(Caption, font, color, new Rectangle(0, 28, w, Math.Max(1, h - 28)), format);
        }
        if (Shortcut.Length > 0)
        {
            using var font = new Font("Segoe UI", 9, FontStyle.Regular, GraphicsUnit.Pixel);
            using var color = new SolidBrush(!Enabled ? AppTheme.DisabledText : primary ? AppTheme.PrimaryText : AppTheme.Muted);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(Shortcut, font, color, new Rectangle(0, 44, w, Math.Max(1, h - 46)), format);
        }
        g.Restore(saved);
    }
}
