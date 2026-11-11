using System.Drawing.Drawing2D;

namespace OrchardSound;

public sealed class WordmarkControl : Control
{
    const int DesignWidth = 200, DesignHeight = 74;

    public WordmarkControl()
    {
        Size = new(DesignWidth, DesignHeight); Margin = new(7, 3, 8, 3); AccessibleName = Branding.Name; AccessibleRole = AccessibleRole.StaticText;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    { using var background = new SolidBrush(Parent?.BackColor ?? AppTheme.Surface); e.Graphics.FillRectangle(background, ClientRectangle); }

    protected override void OnPaint(PaintEventArgs e)
    {
        OnPaintBackground(e); var graphics = e.Graphics; var state = graphics.Save();
        graphics.ScaleTransform(Width / (float)DesignWidth, Height / (float)DesignHeight); graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var mint = new SolidBrush(AppTheme.Accent); using var copper = new SolidBrush(IconArtwork.Copper);
        using var dark = new SolidBrush(Parent?.BackColor ?? AppTheme.Surface);
        using var tape = new Pen(IconArtwork.Copper, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var path = new GraphicsPath();
        path.AddBezier(35, 36, 22, 32, 22, 18, 35, 18); path.AddLine(35, 18, 62, 18);
        path.AddBezier(62, 18, 77, 18, 77, 32, 62, 36); path.AddBezier(62, 36, 77, 40, 77, 55, 62, 55);
        path.AddLine(62, 55, 35, 55); path.AddBezier(35, 55, 22, 55, 22, 41, 35, 36); graphics.DrawPath(tape, path);
        foreach (var point in new[] { new PointF(25, 22), new PointF(73, 22), new PointF(25, 51), new PointF(73, 51) })
        { graphics.FillEllipse(copper, point.X - 2, point.Y - 2, 4, 4); graphics.FillEllipse(dark, point.X - .7f, point.Y - .7f, 1.4f, 1.4f); }
        foreach (float center in new[] { 35f, 62f })
        {
            graphics.FillEllipse(mint, center - 12, 24, 24, 24);
            foreach (double angle in new[] { -Math.PI / 2, Math.PI / 6, Math.PI * 5 / 6 })
            {
                float x = center + (float)Math.Cos(angle) * 6, y = 36 + (float)Math.Sin(angle) * 6;
                graphics.FillEllipse(dark, x - 3, y - 3, 6, 6);
            }
            graphics.FillEllipse(copper, center - 2.3f, 33.7f, 4.6f, 4.6f);
        }
        using var family = new FontFamily("Segoe UI"); using var letters = new GraphicsPath(); using var textFormat = StringFormat.GenericTypographic;
        letters.AddString(Branding.Name[..1], family, (int)FontStyle.Bold, 30, new PointF(2, 17), textFormat);
        letters.AddString(Branding.Name[3..], family, (int)FontStyle.Bold, 24, new PointF(77, 22), textFormat);
        graphics.FillPath(mint, letters); graphics.Restore(state);
    }
}
