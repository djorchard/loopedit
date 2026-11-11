using System.Drawing.Drawing2D;

namespace OrchardSound;

public static class IconArtwork
{
    public const string StyleName = "Ink & Mint";
    public static readonly Color Copper = Color.FromArgb(229, 163, 105);

    internal static GraphicsPath Rounded(RectangleF box, float radius)
    {
        float diameter = Math.Min(radius * 2, Math.Min(box.Width, box.Height));
        var path = new GraphicsPath();
        path.AddArc(box.Left, box.Top, diameter, diameter, 180, 90);
        path.AddArc(box.Right - diameter, box.Top, diameter, diameter, 270, 90);
        path.AddArc(box.Right - diameter, box.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(box.Left, box.Bottom - diameter, diameter, diameter, 90, 90); path.CloseFigure(); return path;
    }

    public static void Draw(Graphics graphics, EditorIcon icon, RectangleF bounds, bool active = false, bool enabled = true, bool primary = false)
    {
        var state = graphics.Save(); graphics.TranslateTransform(bounds.Left, bounds.Top); graphics.ScaleTransform(bounds.Width / 24, bounds.Height / 24);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Color main = !enabled ? AppTheme.DisabledText : primary ? AppTheme.PrimaryText : AppTheme.Accent;
        Color accent = !enabled ? AppTheme.DisabledText : primary ? AppTheme.PrimaryText : Copper;
        using var pen = new Pen(main, 2) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var detail = new Pen(accent, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var ink = new SolidBrush(main); using var copper = new SolidBrush(accent); using var shade = new SolidBrush(Color.FromArgb(30, main));
        void Card(float x, float y, float width, float height) { using var path = Rounded(new(x, y, width, height), 2); graphics.FillPath(shade, path); graphics.DrawPath(pen, path); }
        void Wave(float y) => graphics.DrawLines(detail, new PointF[] { new(6, y), new(9, y), new(11, y - 3), new(13, y + 3), new(15, y), new(18, y) });
        switch (icon)
        {
            case EditorIcon.Silence:
                graphics.DrawLine(pen, 2, 12, 8, 12); graphics.DrawLine(pen, 16, 12, 22, 12);
                graphics.DrawLine(detail, 8, 5, 8, 19); graphics.DrawLine(detail, 16, 5, 16, 19);
                detail.DashStyle = DashStyle.Dot; graphics.DrawLine(detail, 9, 12, 15, 12); break;
            case EditorIcon.View:
                Card(3, 4, 18, 16); graphics.DrawLine(detail, 3, 10, 21, 10); graphics.DrawLine(detail, 10, 10, 10, 20); break;
            case EditorIcon.Scrub:
                graphics.DrawLine(pen, 12, 3, 12, 21); graphics.DrawLines(detail, new PointF[] { new(3, 12), new(6, 8), new(8, 16), new(10, 10), new(14, 14), new(16, 7), new(18, 16), new(21, 12) });
                graphics.DrawLine(pen, 2, 5, 6, 5); graphics.DrawLine(pen, 2, 5, 4, 3); graphics.DrawLine(pen, 2, 5, 4, 7); break;
            case EditorIcon.RefineLoop:
                graphics.DrawArc(pen, 4, 5, 16, 14, 35, 285); graphics.DrawLines(pen, new PointF[] { new(17, 3), new(21, 6), new(17, 9) });
                graphics.DrawLine(detail, 13, 9, 13, 21); graphics.FillEllipse(copper, 10, 13, 6, 6); break;
            case EditorIcon.Open:
                graphics.FillPolygon(shade, [new(2, 7), new(9, 7), new(12, 10), new(22, 10), new(19, 20), new(2, 20)]);
                graphics.DrawLines(pen, [new(2, 19), new(2, 6), new(9, 6), new(12, 9), new(21, 9)]);
                graphics.DrawLines(pen, [new(2, 20), new(6, 12), new(22, 12), new(19, 20), new(2, 20)]); break;
            case EditorIcon.AddTrack:
                Card(3, 3, 18, 6); graphics.DrawLine(detail, 6, 6, 11, 6); graphics.DrawLine(pen, 3, 17, 10, 17);
                graphics.DrawLine(pen, 17, 12, 17, 22); graphics.DrawLine(pen, 12, 17, 22, 17); break;
            case EditorIcon.Play:
                graphics.FillPolygon(ink, [new(6, 3), new(21, 12), new(6, 21)]); break;
            case EditorIcon.PlayLoop:
                graphics.DrawArc(pen, 2, 3, 20, 18, 30, 290); graphics.DrawLines(pen, new PointF[] { new(18, 1), new(22, 5), new(18, 8) });
                graphics.FillPolygon(copper, new PointF[] { new(9, 7), new(17, 12), new(9, 17) }); break;
            case EditorIcon.Start:
            case EditorIcon.End:
                bool start = icon == EditorIcon.Start; float edge = start ? 5 : 19;
                graphics.DrawLine(start ? pen : detail, edge, 3, edge, 21);
                graphics.DrawLine(pen, start ? 11 : 3, 12, start ? 21 : 13, 12);
                graphics.DrawLines(start ? detail : pen, start ? new PointF[] { new(15, 7), new(10, 12), new(15, 17) } : new PointF[] { new(9, 7), new(14, 12), new(9, 17) }); break;
            case EditorIcon.SelectAll:
                graphics.DrawLines(pen, [new(3, 9), new(3, 4), new(8, 4)]); graphics.DrawLines(pen, [new(16, 4), new(21, 4), new(21, 9)]);
                graphics.DrawLines(pen, [new(3, 15), new(3, 20), new(8, 20)]); graphics.DrawLines(pen, [new(16, 20), new(21, 20), new(21, 15)]); Wave(12); break;
            case EditorIcon.ClearSelection:
                Card(3, 4, 18, 16); graphics.DrawLine(detail, 8, 8, 16, 16); graphics.DrawLine(detail, 16, 8, 8, 16); break;
            case EditorIcon.FadeIn:
            case EditorIcon.FadeOut:
                bool fadeIn = icon == EditorIcon.FadeIn; graphics.FillPolygon(shade, [new(3, 20), new(21, 20), new(fadeIn ? 21 : 3, 4)]);
                graphics.DrawLine(detail, 3, 21, 21, 21); graphics.DrawBezier(pen, 3, fadeIn ? 19 : 4, 9, fadeIn ? 18 : 6, 15, fadeIn ? 6 : 18, 21, fadeIn ? 4 : 19); break;
            case EditorIcon.Undo:
                graphics.DrawArc(pen, 5, 5, 15, 15, 205, 275); graphics.DrawLines(detail, [new(3, 4), new(3, 11), new(10, 11)]); break;
            case EditorIcon.Redo:
                graphics.DrawArc(pen, 4, 5, 15, 15, 60, 275); graphics.DrawLines(detail, [new(21, 4), new(21, 11), new(14, 11)]); break;
            case EditorIcon.EqualHeights:
                Card(4, 3, 17, 3); Card(4, 10, 17, 3); Card(4, 17, 17, 3); graphics.DrawLine(detail, 2, 3, 2, 20); break;
            case EditorIcon.Export:
            case EditorIcon.ExportFolder:
                if (icon == EditorIcon.ExportFolder) graphics.DrawLines(detail, [new(2, 17), new(2, 9), new(7, 9), new(9, 12), new(22, 12)]);
                graphics.DrawLine(pen, 12, 3, 12, 16); graphics.DrawLines(pen, [new(7, 8), new(12, 3), new(17, 8)]);
                graphics.DrawLines(detail, [new(3, 16), new(3, 21), new(21, 21), new(21, 16)]); break;
            case EditorIcon.Remove:
                graphics.DrawEllipse(pen, 3, 3, 18, 18); graphics.DrawLine(detail, 8, 8, 16, 16); graphics.DrawLine(detail, 16, 8, 8, 16); break;
            case EditorIcon.Delete:
                Card(6, 8, 12, 13); graphics.DrawLine(pen, 4, 5, 20, 5); graphics.DrawLine(detail, 9, 2, 15, 2);
                graphics.DrawLine(detail, 10, 11, 10, 17); graphics.DrawLine(detail, 14, 11, 14, 17); break;
            case EditorIcon.Trim:
                graphics.DrawLines(pen, [new(3, 7), new(17, 7), new(17, 21)]); graphics.DrawLines(pen, [new(7, 3), new(7, 17), new(21, 17)]);
                graphics.DrawLine(detail, 11, 11, 13, 13); break;
            case EditorIcon.Bookmark:
                graphics.FillPolygon(shade, [new(5, 3), new(19, 3), new(19, 21), new(12, 16), new(5, 21)]);
                graphics.DrawPolygon(pen, [new(5, 3), new(19, 3), new(19, 21), new(12, 16), new(5, 21)]); graphics.DrawLine(detail, 9, 7, 15, 7); break;
            case EditorIcon.OpenSession:
                Card(3, 4, 18, 17); graphics.DrawLine(detail, 7, 9, 16, 9); graphics.DrawLines(pen, [new(11, 13), new(16, 17), new(11, 21)]); break;
            case EditorIcon.SaveSession:
                Card(3, 3, 18, 18); graphics.DrawRectangle(detail, 7, 3, 9, 6); graphics.DrawLines(pen, [new(7, 21), new(7, 15), new(17, 15), new(17, 21)]); break;
            case EditorIcon.Record:
                graphics.DrawEllipse(pen, 3, 3, 18, 18);
                if (active) { using var stop = Rounded(new(7, 7, 10, 10), 2); graphics.FillPath(copper, stop); }
                else graphics.FillEllipse(copper, 7, 7, 10, 10); break;
            case EditorIcon.Effects:
                graphics.DrawLine(pen, 5, 3, 5, 21); graphics.DrawLine(pen, 12, 3, 12, 21); graphics.DrawLine(pen, 19, 3, 19, 21);
                using (var knobs = new SolidBrush(AppTheme.Surface)) { graphics.FillEllipse(knobs, 2, 6, 6, 6); graphics.FillEllipse(knobs, 9, 13, 6, 6); graphics.FillEllipse(knobs, 16, 5, 6, 6); }
                graphics.DrawEllipse(detail, 2, 6, 6, 6); graphics.DrawEllipse(detail, 9, 13, 6, 6); graphics.DrawEllipse(detail, 16, 5, 6, 6); break;
            case EditorIcon.Paste:
                Card(5, 5, 15, 17); using (var clip = Rounded(new(9, 2, 7, 5), 2)) { graphics.FillPath(copper, clip); } Wave(15); break;
            case EditorIcon.Duplicate:
                Card(3, 3, 13, 13); using (var clear = new SolidBrush(AppTheme.Surface)) graphics.FillRectangle(clear, 8, 8, 13, 13);
                Card(8, 8, 13, 13); graphics.DrawLine(detail, 12, 14, 17, 14); graphics.DrawLine(detail, 14.5f, 11.5f, 14.5f, 16.5f); break;
            case EditorIcon.Cue:
                graphics.DrawLine(pen, 6, 3, 6, 22); graphics.FillPolygon(shade, [new(7, 3), new(21, 8), new(7, 14)]);
                graphics.DrawPolygon(detail, [new(7, 3), new(21, 8), new(7, 14)]); break;
            case EditorIcon.Help:
                graphics.DrawEllipse(pen, 2, 2, 20, 20); graphics.DrawArc(detail, 8, 6, 8, 7, 185, 260);
                graphics.DrawLine(detail, 12, 12, 12, 14); graphics.FillEllipse(ink, 11, 17, 2, 2); break;
        }
        graphics.Restore(state);
    }
}
