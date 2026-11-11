using System.Reflection;
using OrchardSound;

public static class ThemeChecks
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                int checks = 0;
                void Check(bool condition, string name)
                { if (!condition) throw new Exception("FAILED THEME: " + name); checks++; Console.WriteLine("PASS THEME " + name); }
                IEnumerable<Control> All(Control parent) => parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(All(child)));
                static double Luminance(Color color)
                {
                    static double Linear(byte value) { double channel = value / 255d; return channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4); }
                    return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
                }
                static double Contrast(Color first, Color second)
                { double a = Luminance(first), b = Luminance(second); return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05); }
                static Rectangle InForm(Control child, Form form)
                { var point = child.Location; for (var parent = child.Parent; parent != null && parent != form; parent = parent.Parent) point.Offset(parent.Location); return new(point, child.Size); }
                static bool Near(double actual, double expected, double tolerance = 2) => Math.Abs(actual - expected) <= tolerance;
                static Bitmap Paint(ThemeButton button)
                {
                    var image = new Bitmap(button.Width, button.Height);
                    using var graphics = Graphics.FromImage(image);
                    graphics.Clear(AppTheme.Surface);
                    typeof(ThemeButton).GetMethod("OnPaint", Private)!.Invoke(button, [new PaintEventArgs(graphics, new Rectangle(Point.Empty, image.Size))]);
                    return image;
                }
                void Layout(Form form)
                {
                    form.PerformLayout();
                    foreach (Control child in All(form).Reverse()) child.PerformLayout();
                    form.PerformLayout();
                }
                bool FooterGeometry(Form form, ThemeButton primary, ThemeButton secondary)
                {
                    Layout(form); float scale = form.DeviceDpi / 96f;
                    var left = InForm(primary, form); var right = InForm(secondary, form);
                    bool valid = left.Size == right.Size && left.Width >= 104 * scale - 2 && left.Height >= 40 * scale - 2
                        && Near(left.Top, right.Top) && Near(right.Left - left.Right, 8 * scale)
                        && Near(form.ClientSize.Width - right.Right, 24 * scale)
                        && Near(form.ClientSize.Height - right.Bottom, 24 * scale)
                        && form.ClientRectangle.Contains(left) && form.ClientRectangle.Contains(right);
                    if (!valid) Console.WriteLine($"Footer geometry for {form.Text}: DPI {form.DeviceDpi}, client {form.ClientSize}, primary {left}, secondary {right}");
                    return valid;
                }
                Check(Contrast(AppTheme.PrimaryText, AppTheme.Accent) >= 4.5, "primary action text has readable contrast on its accent fill");
                Check(Contrast(AppTheme.Text, AppTheme.Surface) >= 4.5, "secondary action text has readable contrast on the dark surface");
                Check(Luminance(AppTheme.Window) < .1 && Luminance(AppTheme.InputBackground) < .15, "window and input palette remain dark");

                foreach (int dpi in new[] { 96, 144, 192 })
                {
                    using var primary = new ThemeButton("Record", ButtonRole.Primary);
                    using var secondary = new ThemeButton("Cancel", ButtonRole.Secondary);
                    float factor = dpi / 96f; primary.Scale(new SizeF(factor, factor)); secondary.Scale(new SizeF(factor, factor));
                    Check(primary.Size == secondary.Size && primary.Width >= 104 * factor - 2 && primary.Height >= 40 * factor - 2
                        && Near(primary.Padding.Left, 16 * factor) && primary.Padding.Left == primary.Padding.Right
                        && Near(primary.Padding.Top, 8 * factor) && primary.Padding.Top == primary.Padding.Bottom,
                        $"button geometry keeps equal cells and padding at {dpi} DPI scale");
                }
                using (var primary = new ThemeButton("Record", ButtonRole.Primary))
                using (var secondary = new ThemeButton("Cancel", ButtonRole.Secondary))
                {
                    primary.Scale(new SizeF(primary.DeviceDpi / 96f, primary.DeviceDpi / 96f));
                    secondary.Scale(new SizeF(secondary.DeviceDpi / 96f, secondary.DeviceDpi / 96f));
                    Check(primary.Role == ButtonRole.Primary && secondary.Role == ButtonRole.Secondary
                        && primary.FlatStyle == FlatStyle.Flat && secondary.FlatStyle == FlatStyle.Flat
                        && !primary.UseVisualStyleBackColor && !secondary.UseVisualStyleBackColor,
                        "primary and secondary buttons use explicit application styling");
                    using var normal = Paint(primary); using var neutral = Paint(secondary);
                    int y = Math.Clamp((int)(6 * primary.DeviceDpi / 96f), 1, normal.Height - 1);
                    Check(normal.GetPixel(normal.Width / 2, y) != neutral.GetPixel(neutral.Width / 2, y), "primary and secondary actions have visibly different fills");
                    Size size = primary.Size;
                    typeof(ThemeButton).GetMethod("OnMouseEnter", Private)!.Invoke(primary, [EventArgs.Empty]);
                    using var hovered = Paint(primary);
                    typeof(ThemeButton).GetMethod("OnMouseLeave", Private)!.Invoke(primary, [EventArgs.Empty]);
                    Check(primary.Size == size && hovered.GetPixel(hovered.Width / 2, y) != normal.GetPixel(normal.Width / 2, y),
                        "hover changes the action fill without zooming or resizing the button");
                    primary.Enabled = false; using var disabled = Paint(primary);
                    Check(disabled.GetPixel(disabled.Width / 2, y) != normal.GetPixel(normal.Width / 2, y), "disabled primary actions are visually distinct");
                    Check(primary.TabStop && secondary.TabStop, "action buttons remain keyboard navigation targets");
                }

                using (var slider = new ThemedSlider { Minimum = -24, Maximum = 24, Value = 0, AccessibleName = "Pitch sweep" })
                using (var native = new TrackBar { Minimum = -24, Maximum = 24, Value = 0 })
                {
                    int changes = 0; slider.ValueChanged += (_, _) => changes++; slider.Value = 12;
                    Check(slider.Size == native.Size && slider.Orientation == native.Orientation && slider.TickStyle == native.TickStyle
                        && slider.SmallChange == native.SmallChange && slider.LargeChange == native.LargeChange
                        && slider.Value == 12 && changes == 1 && slider.TabStop && slider.AccessibleName == "Pitch sweep",
                        "themed sliders retain native geometry, range, change events and accessible names");
                    using var thumb = new Bitmap(144, 44); using var channel = new Bitmap(144, 44); using var disabled = new Bitmap(144, 44);
                    void PaintSlider(Bitmap target, int part)
                    {
                        using var graphics = Graphics.FromImage(target); graphics.Clear(AppTheme.Window);
                        typeof(ThemedSlider).GetMethod("PaintPart", Private)!.Invoke(slider, [graphics, new Rectangle(8, 8, 120, 24), part, 0u]);
                    }
                    PaintSlider(thumb, 2); PaintSlider(channel, 3);
                    Check(Contrast(thumb.GetPixel(60, 20), AppTheme.Window) >= 3 && Contrast(channel.GetPixel(60, 20), AppTheme.Window) >= 3,
                        "slider thumb and channel remain readable against the dark window surface");
                    slider.Enabled = false; PaintSlider(disabled, 2);
                    Check(disabled.GetPixel(60, 20) != thumb.GetPixel(60, 20) && slider.Value == 12,
                        "disabled slider styling stays distinct without altering its value");
                }

                bool iconsVisible = true;
                foreach (EditorIcon icon in Enum.GetValues<EditorIcon>())
                {
                    using var glyph = new Bitmap(28, 28); using var graphics = Graphics.FromImage(glyph); graphics.Clear(AppTheme.Window);
                    IconArtwork.Draw(graphics, icon, new RectangleF(2, 2, 24, 24));
                    int ink = 0; for (int y = 0; y < glyph.Height; y++) for (int x = 0; x < glyph.Width; x++) if (glyph.GetPixel(x, y).ToArgb() != AppTheme.Window.ToArgb()) ink++;
                    iconsVisible &= ink >= 20;
                }
                Check(iconsVisible, "every editing action including Help has visible artwork at its actual 24-pixel glyph size");
                static Bitmap PrintIcon(IconButton button)
                {
                    var image = new Bitmap(button.Width, button.Height);
                    using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.Magenta);
                    button.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                    return image;
                }
                static bool PerimeterMatches(Bitmap image, Color color)
                {
                    int expected = color.ToArgb();
                    for (int x = 0; x < image.Width; x++)
                        if (image.GetPixel(x, 0).ToArgb() != expected || image.GetPixel(x, image.Height - 1).ToArgb() != expected) return false;
                    for (int y = 0; y < image.Height; y++)
                        if (image.GetPixel(0, y).ToArgb() != expected || image.GetPixel(image.Width - 1, y).ToArgb() != expected) return false;
                    return true;
                }
                foreach (Color backdrop in new[] { AppTheme.Window, AppTheme.Surface })
                    foreach (int dpi in new[] { 96, 144, 192 })
                    {
                        using var parent = new Panel { BackColor = backdrop, Size = new Size(500, 400) };
                        using var button = new IconButton(EditorIcon.Play, "Play", "Space", () => { });
                        float factor = dpi / 96f; button.Scale(new SizeF(factor, factor)); parent.Controls.Add(button); Size size = button.Size;
                        bool exact = true, actualCard = true;
                        void PrintedState()
                        {
                            using var printed = PrintIcon(button); exact &= PerimeterMatches(printed, backdrop);
                            actualCard &= printed.GetPixel(printed.Width / 2, Math.Min(printed.Height - 2, (int)(20 * button.DeviceDpi / 96f))).ToArgb() != backdrop.ToArgb();
                        }
                        PrintedState();
                        typeof(IconButton).GetMethod("OnMouseEnter", Private)!.Invoke(button, [EventArgs.Empty]); PrintedState();
                        button.Enabled = false; PrintedState();
                        button.Enabled = true; typeof(IconButton).GetMethod("OnMouseLeave", Private)!.Invoke(button, [EventArgs.Empty]);
                        button.Role = ButtonRole.Primary; PrintedState();
                        button.Role = ButtonRole.Secondary; button.IsPlaying = true; PrintedState();
                        Check(exact && actualCard && button.Size == size,
                            $"native button print paints every perimeter pixel with its parent background across states at {dpi} DPI size scale ({backdrop.R},{backdrop.G},{backdrop.B})");
                    }
                using (var parent = new Panel { BackColor = AppTheme.Window })
                using (var button = new IconButton(EditorIcon.Play, "Play", "Space", () => { }))
                {
                    parent.Controls.Add(button);
                    using var initial = PrintIcon(button); parent.BackColor = AppTheme.Surface; using var changed = PrintIcon(button);
                    Check(PerimeterMatches(initial, AppTheme.Window) && PerimeterMatches(changed, AppTheme.Surface),
                        "native icon printing follows a changed parent color instead of retaining an old or black backdrop");
                    int x = button.Width / 2, y = Math.Min(button.Height - 2, (int)(5 * button.DeviceDpi / 96f));
                    using var stopped = PrintIcon(button); button.IsPlaying = true; using var playing = PrintIcon(button);
                    button.IsPlaying = false; using var stoppedAgain = PrintIcon(button);
                    Check(playing.GetPixel(x, y).ToArgb() == AppTheme.Accent.ToArgb()
                        && stopped.GetPixel(x, y).ToArgb() == stoppedAgain.GetPixel(x, y).ToArgb()
                        && stopped.GetPixel(x, y).ToArgb() != playing.GetPixel(x, y).ToArgb(),
                        "Play uses the mint primary background only while playing and returns to its neutral stopped state");
                    button.IsPlaying = true; button.Enabled = false; using var disabledPlay = PrintIcon(button);
                    Check(disabledPlay.GetPixel(x, y).ToArgb() == AppTheme.Surface.ToArgb() && PerimeterMatches(disabledPlay, parent.BackColor),
                        "disabled Play keeps disabled styling and the parent's opaque perimeter even when its playing flag is set");
                }
                using (var guide = ShortcutHelp.Render())
                    Check(guide.Size == new Size(1920, 1536) && ShortcutHelp.Bindings.Any(binding => binding.Keys == "Home / End")
                        && ShortcutHelp.Bindings.Any(binding => binding.Keys == "F1")
                        && ShortcutHelp.Bindings.Any(binding => binding.Keys == "Ctrl+D")
                        && ShortcutHelp.Bindings.Single(binding => binding.Keys == "Ctrl+Shift+S").Action.Contains("Set the export"),
                        "the deterministic keyboard image includes timeline jumps, F1 and the separate export-folder setting");

                using (var recording = new RecordingDeviceDialog([new InputDevice(7, "Synthetic recording input")]))
                {
                    var record = All(recording).OfType<ThemeButton>().Single(b => b.AccessibleName == "Start recording");
                    var cancel = All(recording).OfType<ThemeButton>().Single(b => b.Text == "Cancel");
                    Check(FooterGeometry(recording, record, cancel), "recording footer has equal actions, an even gap and matching outside margins");
                    Check(record.Role == ButtonRole.Primary && cancel.Role == ButtonRole.Secondary, "Record is the primary recording action and Cancel is secondary");
                    Check(ReferenceEquals(recording.AcceptButton, record) && ReferenceEquals(recording.CancelButton, cancel)
                        && record.DialogResult == DialogResult.OK && cancel.DialogResult == DialogResult.Cancel,
                        "recording picker preserves Enter and Escape dialog behavior");
                    var input = All(recording).OfType<ComboBox>().Single(combo => combo.AccessibleName == "Recording input device");
                    Check(input.BackColor == AppTheme.InputBackground && input.ForeColor == AppTheme.Text, "recording input matches the dark input palette");
                }
                using (var empty = new RecordingDeviceDialog([]))
                    Check(!All(empty).OfType<ThemeButton>().Single(b => b.AccessibleName == "Start recording").Enabled, "theme does not enable recording when no input is available");

                bool equalFooters = true, roles = true, darkInputs = true, modeless = true, events = true;
                foreach (EffectKind kind in Enum.GetValues<EffectKind>())
                {
                    using var effect = new EffectDialog(kind);
                    var apply = All(effect).OfType<ThemeButton>().Single(b => b.AccessibleName == "Apply effect");
                    var cancel = All(effect).OfType<ThemeButton>().Single(b => b.AccessibleName == "Cancel effect");
                    equalFooters &= FooterGeometry(effect, apply, cancel);
                    roles &= apply.Role == ButtonRole.Primary && cancel.Role == ButtonRole.Secondary;
                    darkInputs &= All(effect).Where(c => c is ComboBox or NumericUpDown).All(c => c.BackColor == AppTheme.InputBackground && c.ForeColor == AppTheme.Text);
                    modeless &= effect.FormBorderStyle == FormBorderStyle.SizableToolWindow && !effect.Modal && !effect.ShowInTaskbar
                        && apply.DialogResult == DialogResult.None && cancel.DialogResult == DialogResult.None;
                    int applies = 0, cancels = 0; effect.ApplyRequested += () => applies++; effect.CancelRequested += () => cancels++;
                    typeof(Control).GetMethod("OnClick", Private)!.Invoke(apply, [EventArgs.Empty]);
                    typeof(Control).GetMethod("OnClick", Private)!.Invoke(cancel, [EventArgs.Empty]);
                    events &= applies == 1 && cancels == 1 && !effect.IsDisposed;
                }
                Check(equalFooters, "every effect tool shares equal action dimensions and footer spacing");
                Check(roles, "every effect tool uses Apply primary and Cancel secondary");
                Check(darkInputs, "effect numeric and mode fields use the same dark input palette");
                Check(modeless, "themed effect windows retain movable modeless tool behavior");
                Check(events, "themed effect buttons preserve explicit Apply and Cancel events");
                using (var editor = new EditorForm([]))
                {
                    Check(editor.BackColor == AppTheme.Window, "main editor uses the shared dark window palette");
                    var tooltip = (ToolTip)typeof(EditorForm).GetField("tips", Private)!.GetValue(editor)!;
                    Check(tooltip.OwnerDraw && tooltip.BackColor == AppTheme.Elevated && tooltip.ForeColor == AppTheme.Text,
                        "tooltips use the application's dark surfaces and text colors");
                }
                Console.WriteLine($"{checks} theme checks passed. Native title-bar appearance is a separate visual check.");
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("Theme fixture deadline exceeded.");
        if (failure != null) throw new Exception("Theme fixture failed", failure);
    }
}
