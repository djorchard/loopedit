using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace OrchardSound;

public sealed class ThemedSlider : TrackBar
{
    const int ReflectedNotify = 0x204e, CustomDrawCode = -12;
    const uint PrePaint = 1, ItemPrePaint = 0x10001, DisabledState = 4, HotState = 0x40;
    const int ThumbPart = 2, ChannelPart = 3;
    static readonly Color ChannelColor = Color.FromArgb(95, 124, 153);

    public ThemedSlider() => BackColor = AppTheme.Window;

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == ReflectedNotify && message.LParam != IntPtr.Zero)
        {
            var draw = Marshal.PtrToStructure<CustomDraw>(message.LParam);
            if (draw.Header.Code == CustomDrawCode)
            {
                if (draw.Stage == PrePaint) { message.Result = (IntPtr)0x20; return; }
                int part = unchecked((int)draw.Item.ToUInt64());
                if (draw.Stage == ItemPrePaint && part is ThumbPart or ChannelPart)
                {
                    using var graphics = Graphics.FromHdc(draw.Hdc);
                    PaintPart(graphics, Rectangle.FromLTRB(draw.Bounds.Left, draw.Bounds.Top, draw.Bounds.Right, draw.Bounds.Bottom), part, draw.State);
                    message.Result = (IntPtr)4; return;
                }
            }
        }
        base.WndProc(ref message);
    }

    void PaintPart(Graphics graphics, Rectangle bounds, int part, uint state)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        bool disabled = !Enabled || (state & DisabledState) != 0;
        if (part == ChannelPart)
        {
            using var channel = new SolidBrush(disabled ? AppTheme.Border : ChannelColor);
            graphics.FillRectangle(channel, bounds); return;
        }
        var saved = graphics.Save(); graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rectangle = new RectangleF(bounds.X + .5f, bounds.Y + .5f, Math.Max(1, bounds.Width - 1), Math.Max(1, bounds.Height - 1));
        float diameter = Math.Min(6 * DeviceDpi / 96f, Math.Min(rectangle.Width, rectangle.Height));
        using var path = new GraphicsPath();
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90); path.CloseFigure();
        using var fill = new SolidBrush(disabled ? AppTheme.DisabledText : (state & HotState) != 0 ? AppTheme.AccentHover : AppTheme.Accent);
        using var border = new Pen(disabled ? AppTheme.Border : AppTheme.PrimaryText);
        graphics.FillPath(fill, path); graphics.DrawPath(border, path);
        if (Focused && ShowFocusCues)
        { var focus = bounds; focus.Inflate(-3, -3); if (focus.Width > 0 && focus.Height > 0) ControlPaint.DrawFocusRectangle(graphics, focus, AppTheme.PrimaryText, AppTheme.Accent); }
        graphics.Restore(saved);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Header { public IntPtr Window; public UIntPtr Id; public int Code; }
    [StructLayout(LayoutKind.Sequential)]
    struct NativeRectangle { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct CustomDraw
    {
        public Header Header;
        public uint Stage;
        public IntPtr Hdc;
        public NativeRectangle Bounds;
        public UIntPtr Item;
        public uint State;
        public IntPtr ItemParameter;
    }
}
