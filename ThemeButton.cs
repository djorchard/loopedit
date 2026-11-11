using System.Drawing.Drawing2D;

namespace OrchardSound;

public enum ButtonRole { Primary, Secondary, Destructive }

public sealed class ThemeButton : Button
{
    public ButtonRole Role { get; }
    bool hovered, pressed;
    public ThemeButton(string text, ButtonRole role = ButtonRole.Secondary)
    {
        Text = text; Role = role; Size = MinimumSize = new(104, 40); Padding = new(16, 8, 16, 8); Margin = new(4, 0, 4, 0);
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false;
        BackColor = role == ButtonRole.Primary ? AppTheme.Accent : AppTheme.Elevated;
        ForeColor = role == ButtonRole.Primary ? AppTheme.PrimaryText : AppTheme.Text;
        Font = new Font("Segoe UI Semibold", 10); Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; pressed = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); pressed = e.Button == MouseButtons.Left; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        var rectangle = new Rectangle(0, 0, Width - 1, Height - 1); int radius = Math.Max(4, (int)(6 * DeviceDpi / 96f));
        using var shape = new GraphicsPath();
        shape.AddArc(rectangle.Left, rectangle.Top, radius * 2, radius * 2, 180, 90);
        shape.AddArc(rectangle.Right - radius * 2, rectangle.Top, radius * 2, radius * 2, 270, 90);
        shape.AddArc(rectangle.Right - radius * 2, rectangle.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
        shape.AddArc(rectangle.Left, rectangle.Bottom - radius * 2, radius * 2, radius * 2, 90, 90); shape.CloseFigure();
        Color fill = !Enabled ? AppTheme.Surface : Role == ButtonRole.Primary ? hovered && !pressed ? AppTheme.AccentHover : AppTheme.Accent : hovered ? Color.FromArgb(47, 65, 85) : AppTheme.Elevated;
        using var brush = new SolidBrush(fill); using var border = new Pen(Role == ButtonRole.Destructive ? AppTheme.Danger : Role == ButtonRole.Primary ? fill : AppTheme.Border);
        g.FillPath(brush, shape); g.DrawPath(border, shape);
        Color text = !Enabled ? AppTheme.DisabledText : Role == ButtonRole.Primary ? AppTheme.PrimaryText : AppTheme.Text;
        var bounds = ClientRectangle; bounds = new(bounds.Left + Padding.Left, bounds.Top + Padding.Top, Math.Max(1, bounds.Width - Padding.Horizontal), Math.Max(1, bounds.Height - Padding.Vertical));
        TextRenderer.DrawText(g, Text, Font, bounds, text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        if (Focused && ShowFocusCues) { var focus = ClientRectangle; focus.Inflate(-5, -5); ControlPaint.DrawFocusRectangle(g, focus, Role == ButtonRole.Primary ? AppTheme.PrimaryText : AppTheme.Accent, fill); }
    }
}
