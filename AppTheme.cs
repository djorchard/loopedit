using System.Runtime.InteropServices;

namespace OrchardSound;

public static class AppTheme
{
    public static Color Window => Color.FromArgb(21, 29, 40);
    public static Color Surface => Color.FromArgb(28, 38, 51);
    public static Color Elevated => Color.FromArgb(37, 50, 67);
    public static Color InputBackground => Color.FromArgb(32, 44, 59);
    public static Color Border => Color.FromArgb(62, 80, 101);
    public static Color Text => Color.FromArgb(211, 223, 235);
    public static Color Muted => Color.FromArgb(144, 164, 185);
    public static Color Accent => Color.FromArgb(81, 207, 177);
    public static Color AccentHover => Color.FromArgb(111, 224, 197);
    public static Color PrimaryText => Color.FromArgb(14, 41, 37);
    public static Color Danger => Color.FromArgb(230, 132, 146);
    public static Color DisabledText => Color.FromArgb(109, 129, 150);
    public static readonly Font BodyFont = new("Segoe UI", 10);

    public static void ApplyWindow(Form window)
    {
        window.BackColor = Window; window.ForeColor = Text;
        window.HandleCreated += (_, _) => ApplyCaption(window.Handle);
        if (window.IsHandleCreated) ApplyCaption(window.Handle);
    }
    static void ApplyCaption(IntPtr handle)
    {
        try
        {
            int enabled = 1;
            if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) < 0) DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
            int caption = ColorRef(Surface), text = ColorRef(Text), border = ColorRef(Border);
            DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
            DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
            DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }
    static int ColorRef(Color color) => color.R | color.G << 8 | color.B << 16;
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    public static void StyleInputs(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            if (control is ComboBox combo) { combo.FlatStyle = FlatStyle.Flat; combo.BackColor = InputBackground; combo.ForeColor = Text; }
            else if (control is NumericUpDown number) { number.BackColor = InputBackground; number.ForeColor = Text; number.BorderStyle = BorderStyle.FixedSingle; LiveNumeric.Configure(number); }
            else if (control is TextBoxBase input) { input.BackColor = InputBackground; input.ForeColor = Text; input.BorderStyle = input.Parent is UpDownBase ? BorderStyle.None : BorderStyle.FixedSingle; }
            else if (control is Label label && label.Tag?.ToString() != "KeepColor") label.ForeColor = label.Tag?.ToString() == "Muted" ? Muted : Text;
            else if (control is CheckBox check) check.ForeColor = Text;
            StyleInputs(control);
        }
    }
    public static Panel CreateActionFooter(params ThemeButton[] buttons)
    {
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 88, BackColor = Surface, AccessibleName = "Dialog actions" };
        footer.Controls.AddRange(buttons);
        void LayoutButtons()
        {
            float scale = footer.DeviceDpi / 96f; int gap = (int)(8 * scale), margin = (int)(24 * scale), height = (int)(40 * scale);
            int width = Math.Max((int)(104 * scale), buttons.Select(b => TextRenderer.MeasureText(b.Text, b.Font).Width + b.Padding.Horizontal).DefaultIfEmpty(0).Max());
            int left = footer.ClientSize.Width - margin - width * buttons.Length - gap * Math.Max(0, buttons.Length - 1);
            int top = Math.Max(0, footer.ClientSize.Height - margin - height);
            for (int i = 0; i < buttons.Length; i++) buttons[i].Bounds = new(left + i * (width + gap), top, width, height);
        }
        footer.Layout += (_, _) => LayoutButtons(); footer.SizeChanged += (_, _) => LayoutButtons(); LayoutButtons(); return footer;
    }
    public static void ConfigureToolTip(ToolTip tips)
    {
        tips.OwnerDraw = true; tips.ShowAlways = true; tips.BackColor = Elevated; tips.ForeColor = Text;
        tips.UseAnimation = false; tips.UseFading = false; tips.InitialDelay = 300; tips.ReshowDelay = 100;
        tips.Popup += (_, args) =>
        {
            string text = args.AssociatedControl == null ? "" : tips.GetToolTip(args.AssociatedControl) ?? "";
            int padding = (int)(12 * (args.AssociatedControl?.DeviceDpi ?? 96) / 96f);
            int maxWidth = (int)(460 * (args.AssociatedControl?.DeviceDpi ?? 96) / 96f);
            Size measured = TextRenderer.MeasureText(text, BodyFont, new Size(maxWidth, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            args.ToolTipSize = new(measured.Width + padding * 2, measured.Height + padding * 2);
        };
        tips.Draw += (_, args) =>
        {
            using var fill = new SolidBrush(Elevated); using var border = new Pen(Border);
            args.Graphics.FillRectangle(fill, args.Bounds); args.Graphics.DrawRectangle(border, 0, 0, args.Bounds.Width - 1, args.Bounds.Height - 1);
            int padding = (int)(12 * (args.AssociatedControl?.DeviceDpi ?? 96) / 96f);
            var bounds = args.Bounds; bounds.Inflate(-padding, -padding);
            TextRenderer.DrawText(args.Graphics, args.ToolTipText, BodyFont, bounds, Text, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        };
    }
    public static void StyleMenu(ContextMenuStrip menu)
    { menu.BackColor = Surface; menu.ForeColor = Text; menu.Renderer = new ToolStripProfessionalRenderer(new MenuColors()); }
    sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Border;
        public override Color MenuItemSelected => Elevated;
        public override Color MenuItemSelectedGradientBegin => Elevated;
        public override Color MenuItemSelectedGradientEnd => Elevated;
    }
}
