using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.RegularExpressions;

namespace OrchardSound;

public sealed record ShortcutBinding(string Keys, string Action);

public static class ShortcutHelp
{
    public static IReadOnlyList<ShortcutBinding> Bindings { get; } = Array.AsReadOnly<ShortcutBinding>([
        new("Ctrl+O", "Add sound files"),
        new("Ctrl+= / Ctrl++ / Ctrl+Num+", "Add an empty track"),
        new("Shift+R / Ctrl+Alt+R", "Choose recording input and destination"),
        new("Space", "Play / restart from the white cursor"),
        new("Shift+Space", "Restart selection start; optional 75/25 refinement"),
        new("Ctrl+Space", "Stop playback"),
        new("LMB / [", "Set selection start"),
        new("RMB / ]", "Set selection end"),
        new("Ctrl+A", "Select the whole timeline"),
        new("Esc", "Clear selection and stop"),
        new("F", "Fade in"),
        new("Shift+F", "Fade out"),
        new("Ctrl+Z", "Undo"),
        new("Ctrl+Y / Ctrl+Shift+Z", "Redo"),
        new("Del", "Delete the selected section"),
        new("Ctrl+D", "Duplicate the section immediately after it"),
        new("Ctrl+T", "Trim to selection"),
        new("Ctrl+I", "Insert silence across the timeline"),
        new("Ctrl+R", "Reverse effect"),
        new("Ctrl+E / Alt+E", "Open the Effects menu"),
        new("Ctrl+B", "Save section boundaries as cue points"),
        new("Ctrl+P", "Save session"),
        new("Ctrl+Shift+O", "Open session"),
        new("Ctrl+0", "Give every pane equal height"),
        new("E / Ctrl+S", "Export in the selected format"),
        new("Ctrl+Shift+S", "Set the export destination folder"),
        new("Ctrl+V", "Paste clipboard audio or sound files"),
        new("Left / Right", "Nudge one audio sample"),
        new("Home / End", "Jump to the timeline beginning / end"),
        new("B", "Toggle beats / BPM"),
        new("K", "Toggle likely kick markers"),
        new("S", "Toggle smart snap to beats and cues"),
        new("Ctrl", "Jump to the mouse and bypass smart snap"),
        new("Alt", "Fit selection with 10% context; hold to bypass snap"),
        new("Shift", "Fine pointer positioning and dragging"),
        new("Wheel", "Change precision zoom"),
        new("Ctrl+click header", "Toggle a track's playback / effects / export"),
        new("F1", "Open this keyboard and pointer guide")
    ]);
    public sealed record KeyRegion(string Key, RectangleF Bounds, bool Pointer = false);
    public static IReadOnlyList<KeyRegion> KeyRegions { get; } = BuildKeyRegions();
    static Form? visibleHelp;
    internal static bool IsGuideWindow(Form window) => window is HelpWindow;
    internal static bool DispatchGuideCommand(Form window, Keys command)
    {
        if (window is not HelpWindow guide) return false;
        guide.DispatchCommand(command); return true;
    }

    public sealed class FilterModel
    {
        public string? SelectedKey { get; private set; }
        public IReadOnlyList<ShortcutBinding> Results => MatchingBindings(SelectedKey);
        public event Action? Changed;
        public void Toggle(string key)
        {
            string canonical = CanonicalKey(key);
            if (canonical.Length == 0) return;
            SelectedKey = string.Equals(SelectedKey, canonical, StringComparison.OrdinalIgnoreCase) ? null : canonical;
            Changed?.Invoke();
        }
        public void Press(Keys keyData) { string key = NormalizeKey(keyData); if (key.Length > 0) Toggle(key); }
        public void Clear() { if (SelectedKey == null) return; SelectedKey = null; Changed?.Invoke(); }
    }

    public static IReadOnlyList<ShortcutBinding> MatchingBindings(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return Bindings;
        string token = CanonicalKey(key);
        return Bindings.Where(binding => BindingTokens(binding).Contains(token)).ToArray();
    }

    static HashSet<string> BindingTokens(ShortcutBinding binding)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string alias in binding.Keys.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string text = alias;
            if (text.Contains("Num+", StringComparison.OrdinalIgnoreCase)) { tokens.Add("Num+"); text = text.Replace("Num+", "", StringComparison.OrdinalIgnoreCase); }
            if (text.Contains("++", StringComparison.Ordinal)) { tokens.Add("="); tokens.Add("Shift"); }
            foreach (Match match in Regex.Matches(text, "[A-Za-z0-9]+|\\[|\\]|=")) tokens.Add(CanonicalKey(match.Value));
        }
        return tokens;
    }

    static string CanonicalKey(string key)
    {
        string value = key.Trim();
        value = value.ToUpperInvariant() switch { "CONTROL" or "CONTROLKEY" => "Ctrl", "SHIFTKEY" => "Shift", "ESCAPE" => "Esc", "RETURN" => "Enter", "ADD" => "Num+", _ => value };
        return KeyRegions.FirstOrDefault(region => region.Key.Equals(value, StringComparison.OrdinalIgnoreCase))?.Key ?? value;
    }

    public static string NormalizeKey(Keys keyData)
    {
        int code = (int)(keyData & Keys.KeyCode);
        if (code >= (int)Keys.A && code <= (int)Keys.Z || code >= (int)Keys.F1 && code <= (int)Keys.F24) return ((Keys)code).ToString();
        if (code >= (int)Keys.D0 && code <= (int)Keys.D9) return (code - (int)Keys.D0).ToString();
        if (code >= (int)Keys.NumPad0 && code <= (int)Keys.NumPad9) return (code - (int)Keys.NumPad0).ToString();
        return (Keys)code switch
        {
            Keys.ControlKey or Keys.LControlKey or Keys.RControlKey => "Ctrl",
            Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey => "Shift",
            Keys.Menu or Keys.LMenu or Keys.RMenu => "Alt",
            Keys.Space => "Space", Keys.Escape => "Esc", Keys.Delete => "Del", Keys.Back => "Backspace",
            Keys.Tab => "Tab", Keys.Enter => "Enter", Keys.Capital => "Caps", Keys.LWin or Keys.RWin => "Win", Keys.Apps => "Menu",
            Keys.Left => "Left", Keys.Right => "Right", Keys.Up => "Up", Keys.Down => "Down", Keys.Home => "Home", Keys.End => "End",
            Keys.Oemplus => "=", Keys.Add => "Num+", Keys.OemMinus or Keys.Subtract => "-", Keys.OemOpenBrackets => "[", Keys.OemCloseBrackets => "]",
            Keys.OemSemicolon => ";", Keys.OemQuotes => "'", Keys.Oemcomma => ",", Keys.OemPeriod => ".", Keys.OemQuestion or Keys.Divide => "/",
            Keys.Oemtilde => "`", Keys.OemPipe or Keys.OemBackslash => "\\",
            Keys.None when (keyData & Keys.Control) != 0 => "Ctrl",
            Keys.None when (keyData & Keys.Shift) != 0 => "Shift",
            Keys.None when (keyData & Keys.Alt) != 0 => "Alt",
            _ => code == 0 ? "" : ((Keys)code).ToString()
        };
    }

    public static string? KeyHitTest(PointF imagePoint) => KeyRegions.FirstOrDefault(region => region.Bounds.Contains(imagePoint))?.Key;

    public static PointF? ImagePointFromZoom(Size clientSize, Size imageSize, Point clientPoint)
    {
        if (clientSize.Width <= 0 || clientSize.Height <= 0 || imageSize.Width <= 0 || imageSize.Height <= 0) return null;
        // Match PictureBox.Zoom's integer destination rectangle, including its
        // odd-size rounding. Mouse coordinates are already device pixels.
        float scale = Math.Min(clientSize.Width / (float)imageSize.Width, clientSize.Height / (float)imageSize.Height);
        int width = (int)(imageSize.Width * scale), height = (int)(imageSize.Height * scale);
        var destination = new Rectangle((clientSize.Width - width) / 2, (clientSize.Height - height) / 2, width, height);
        if (width <= 0 || height <= 0 || !destination.Contains(clientPoint)) return null;
        return new PointF((clientPoint.X - destination.Left) * imageSize.Width / (float)width,
            (clientPoint.Y - destination.Top) * imageSize.Height / (float)height);
    }

    static IReadOnlyList<KeyRegion> BuildKeyRegions()
    {
        var regions = new List<KeyRegion>();
        void Key(string name, float x, float y, float width, float height = 48) => regions.Add(new(name, new(x, y, width, height)));
        float fx = 44; Key("Esc", fx, 122, 76, 42); fx += 100;
        for (int i = 1; i <= 12; i++) { Key("F" + i, fx, 122, 72, 42); fx += 80; }
        void Row(float y, params (string Name, float Units)[] keys)
        { float x = 44; foreach (var key in keys) { float width = key.Units * 81 - 7; Key(key.Name, x, y, width); x += width + 7; } }
        Row(188, ("`", 1), ("1", 1), ("2", 1), ("3", 1), ("4", 1), ("5", 1), ("6", 1), ("7", 1), ("8", 1), ("9", 1), ("0", 1), ("-", 1), ("=", 1), ("Backspace", 2));
        Row(248, ("Tab", 1.5f), ("Q", 1), ("W", 1), ("E", 1), ("R", 1), ("T", 1), ("Y", 1), ("U", 1), ("I", 1), ("O", 1), ("P", 1), ("[", 1), ("]", 1), ("\\", 1.5f));
        Row(308, ("Caps", 1.75f), ("A", 1), ("S", 1), ("D", 1), ("F", 1), ("G", 1), ("H", 1), ("J", 1), ("K", 1), ("L", 1), (";", 1), ("'", 1), ("Enter", 2.25f));
        Row(368, ("Shift", 2.25f), ("Z", 1), ("X", 1), ("C", 1), ("V", 1), ("B", 1), ("N", 1), ("M", 1), (",", 1), (".", 1), ("/", 1), ("Shift", 2.75f));
        Row(428, ("Ctrl", 1.5f), ("Win", 1), ("Alt", 1.25f), ("Space", 6.25f), ("Alt", 1.25f), ("Win", 1), ("Menu", 1), ("Ctrl", 1.5f));
        Key("Home", 1330, 188, 110); Key("End", 1448, 188, 110); Key("Del", 1566, 188, 110); Key("Num+", 1694, 188, 130);
        Key("Up", 1448, 248, 110); Key("Left", 1330, 308, 110); Key("Down", 1448, 308, 110); Key("Right", 1566, 308, 110);
        regions.Add(new("LMB", new(1340, 380, 210, 42), true)); regions.Add(new("RMB", new(1550, 380, 260, 42), true));
        regions.Add(new("Wheel", new(1340, 423, 180, 42), true));
        return regions.AsReadOnly();
    }

    public static void Show(Form owner)
    {
        if (visibleHelp is { IsDisposed: false }) { visibleHelp.Activate(); return; }
        var help = new HelpWindow(owner); visibleHelp = help;
        help.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(visibleHelp, help)) visibleHelp = null;
            Form main = owner; while (main.Owner != null) main = main.Owner;
            if (!main.IsDisposed && main.IsHandleCreated && main.Visible) { main.Activate(); Descendants(main).OfType<WaveCanvas>().FirstOrDefault()?.Focus(); }
            help.Dispose();
        };
        help.Show(owner);
    }

    sealed class HelpWindow : Form
    {
        readonly FilterModel filter = new();
        readonly PictureBox picture = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = AppTheme.Window, TabStop = false };
        readonly Label heading = new() { AutoSize = true, ForeColor = AppTheme.Text, Margin = new(14, 10, 8, 0) };
        Bitmap? currentImage;

        public HelpWindow(Form owner)
        {
            Text = Branding.Name + " keyboard and pointer guide"; Icon = Branding.LoadIcon(); Size = new(1440, 1080); MinimumSize = new(800, 600);
            StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.SizableToolWindow; ShowInTaskbar = false; KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None; Font = new Font("Segoe UI", 10); AppTheme.ApplyWindow(this);
            var area = Screen.FromControl(owner).WorkingArea; Size = new(Math.Min(Width, area.Width - 48), Math.Min(Height, area.Height - 48));
            float scale = DeviceDpi / 96f;
            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = (int)(64 * scale), Padding = new((int)(14 * scale), (int)(10 * scale), (int)(14 * scale), (int)(8 * scale)), BackColor = AppTheme.Surface, WrapContents = false };
            var clear = new ThemeButton("All shortcuts") { AccessibleName = "Show all shortcuts" }; clear.Click += (_, _) => filter.Clear();
            clear.Scale(new SizeF(scale, scale)); heading.Margin = new((int)(14 * scale), (int)(10 * scale), (int)(8 * scale), 0);
            toolbar.Controls.Add(clear); toolbar.Controls.Add(heading); Controls.Add(picture); Controls.Add(toolbar);
            picture.MouseClick += (_, args) =>
            {
                if (args.Button != MouseButtons.Left || picture.Image == null) return;
                var point = ImagePointFromZoom(picture.ClientSize, picture.Image.Size, args.Location);
                if (point.HasValue && KeyHitTest(point.Value) is string key) filter.Toggle(key);
            };
            picture.MouseMove += (_, args) =>
            {
                var point = picture.Image == null ? null : ImagePointFromZoom(picture.ClientSize, picture.Image.Size, args.Location);
                picture.Cursor = point.HasValue && KeyHitTest(point.Value) != null ? Cursors.Hand : Cursors.Default;
            };
            filter.Changed += RefreshImage; RefreshImage();
        }

        void RefreshImage()
        {
            heading.Text = filter.SelectedKey == null ? "Click a key or press it to filter." : $"Key: {filter.SelectedKey}   ({filter.Results.Count} shortcuts)";
            var next = Render(filter.SelectedKey); var previous = currentImage; currentImage = next; picture.Image = next; previous?.Dispose();
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if ((keyData & Keys.KeyCode) == Keys.Escape) { Close(); return true; }
            if ((message.Msg == 0x100 || message.Msg == 0x104) && (message.LParam.ToInt64() & (1L << 30)) != 0) return true;
            filter.Press(keyData); return true;
        }
        internal void DispatchCommand(Keys keyData)
        { if ((keyData & Keys.KeyCode) == Keys.Escape) Close(); else filter.Press(keyData); }
        protected override bool ProcessDialogKey(Keys keyData)
        { if ((keyData & Keys.KeyCode) == Keys.Escape) Close(); else filter.Press(keyData); return true; }
        protected override void OnKeyPress(KeyPressEventArgs e) => e.Handled = true;
        protected override void Dispose(bool disposing)
        { if (disposing) { picture.Image = null; currentImage?.Dispose(); currentImage = null; } base.Dispose(disposing); }
    }

    static IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>()
        .SelectMany(child => new[] { child }.Concat(Descendants(child)));

    public static void SaveImage(string path) { using var image = Render(); image.Save(path, ImageFormat.Png); }
    public static Bitmap Render() => Render(null);
    public static Bitmap Render(string? filterKey)
    {
        string? selected = string.IsNullOrWhiteSpace(filterKey) ? null : CanonicalKey(filterKey);
        var visible = MatchingBindings(selected);
        var image = new Bitmap(1920, 1536, PixelFormat.Format32bppPArgb); image.SetResolution(96, 96);
        using var graphics = Graphics.FromImage(image); graphics.Clear(AppTheme.Window); graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var title = new Font("Segoe UI Semibold", 38, FontStyle.Regular, GraphicsUnit.Pixel);
        using var subtitle = new Font("Segoe UI", 23, FontStyle.Regular, GraphicsUnit.Pixel);
        using var keyFont = new Font("Segoe UI Semibold", 19, FontStyle.Regular, GraphicsUnit.Pixel);
        using var legendFont = new Font("Segoe UI", 18, FontStyle.Regular, GraphicsUnit.Pixel);
        using var tiny = new Font("Segoe UI", 15, FontStyle.Regular, GraphicsUnit.Pixel);
        using var text = new SolidBrush(AppTheme.Text); using var muted = new SolidBrush(AppTheme.Muted);
        using var mint = new SolidBrush(AppTheme.Accent); using var copper = new SolidBrush(IconArtwork.Copper); using var dark = new SolidBrush(AppTheme.PrimaryText);
        string brandHeading = Branding.Name.ToUpperInvariant();
        graphics.DrawString(brandHeading, title, mint, 44, 24);
        graphics.DrawString("Keyboard & pointer guide", subtitle, text, Math.Max(360, 82 + graphics.MeasureString(brandHeading, title).Width), 38);
        IconArtwork.Draw(graphics, EditorIcon.Help, new(1778, 30, 42, 42)); graphics.DrawString("F1", keyFont, copper, 1840, 41);
        var active = Bindings.SelectMany(BindingTokens).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in KeyRegions.Where(key => !key.Pointer))
        {
            bool chosen = string.Equals(selected, key.Key, StringComparison.OrdinalIgnoreCase), used = active.Contains(key.Key);
            using var path = IconArtwork.Rounded(key.Bounds, 7); using var fill = new SolidBrush(chosen ? AppTheme.Accent : used ? AppTheme.Elevated : AppTheme.Surface);
            using var border = new Pen(chosen ? IconArtwork.Copper : used ? Color.FromArgb(160, AppTheme.Accent) : AppTheme.Border, chosen ? 2.5f : 1.2f);
            graphics.FillPath(fill, path); graphics.DrawPath(border, path);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString(key.Key, key.Key.Length > 5 ? tiny : keyFont, chosen ? dark : used ? text : muted, key.Bounds, format);
        }
        using (var mouseBox = IconArtwork.Rounded(new(1330, 376, 494, 100), 10))
        using (var fill = new SolidBrush(AppTheme.Surface)) graphics.FillPath(fill, mouseBox);
        foreach (var region in KeyRegions.Where(region => region.Pointer && region.Key == selected))
        { using var highlight = new Pen(IconArtwork.Copper, 2); using var path = IconArtwork.Rounded(region.Bounds, 5); graphics.DrawPath(highlight, path); }
        graphics.DrawString("LMB: start     RMB: end", keyFont, mint, 1350, 390); graphics.DrawString("Wheel: zoom   Ctrl+click header: include", tiny, muted, 1350, 430);
        graphics.DrawString(selected == null ? "THE SHORTCUTS" : "SHORTCUTS USING " + selected.ToUpperInvariant(), subtitle, copper, 44, 518);
        if (visible.Count == 0)
        {
            graphics.DrawString("No shortcuts use " + selected + ".", subtitle, text, 58, 590);
            graphics.DrawString("Choose another key, or use All shortcuts to clear the filter.", legendFont, muted, 58, 634);
        }
        int rows = Math.Max(1, (visible.Count + 1) / 2);
        for (int i = 0; i < visible.Count; i++)
        {
            int column = i / rows, row = i % rows; float x = 44 + column * 938, y = 572 + row * 44;
            using var rowPath = IconArtwork.Rounded(new(x, y, 892, 39), 7); using var rowFill = new SolidBrush(row % 2 == 0 ? AppTheme.Surface : Color.FromArgb(24, 34, 47)); graphics.FillPath(rowFill, rowPath);
            graphics.DrawString(visible[i].Keys, visible[i].Keys.Length > 19 ? tiny : legendFont, mint, x + 13, y + 9);
            graphics.DrawString(visible[i].Action, legendFont, text, x + 256, y + 9);
        }
        graphics.DrawString("In the F1 window, click a key or press it to filter. Select the same key again, or All shortcuts, to clear.", legendFont, muted, 44, 1440);
        graphics.DrawString("Guide keystrokes filter this list without running editor actions. Escape closes the guide.", legendFont, muted, 44, 1474);
        return image;
    }
}
