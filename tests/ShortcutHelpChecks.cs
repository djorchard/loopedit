using System.Reflection;
using OrchardSound;

public static class ShortcutHelpChecks
{
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                int checks = 0;
                void Check(bool result, string name) { if (!result) throw new Exception("FAILED HELP: " + name); checks++; Console.WriteLine("PASS HELP " + name); }
                static PointF Center(ShortcutHelp.KeyRegion region) => new(region.Bounds.Left + region.Bounds.Width / 2, region.Bounds.Top + region.Bounds.Height / 2);
                static Point ClientPoint(Size client, PointF image)
                {
                    double scale = Math.Min(client.Width / 1920d, client.Height / 1536d);
                    return new((int)Math.Round((client.Width - 1920 * scale) / 2 + image.X * scale), (int)Math.Round((client.Height - 1536 * scale) / 2 + image.Y * scale));
                }
                Check(ShortcutHelp.Bindings.Single(binding => binding.Keys == "Space").Action.Contains("white cursor")
                    && ShortcutHelp.Bindings.Single(binding => binding.Keys == "Ctrl+Space").Action == "Stop playback"
                    && ShortcutHelp.Bindings.Single(binding => binding.Keys == "Shift+Space").Action.Contains("selection start"),
                    "cursor playback and selection-start restart have separate accurate guide bindings");
                var r = ShortcutHelp.MatchingBindings("R");
                Check(r.Count == 2 && r.Any(binding => binding.Keys == "Ctrl+R") && r.Any(binding => binding.Keys.Contains("Shift+R") && binding.Keys.Contains("Ctrl+Alt+R"))
                    && !r.Any(binding => binding.Keys == "Ctrl / Alt"), "R matches exact key tokens rather than letters inside Ctrl or action descriptions");
                Check(ShortcutHelp.MatchingBindings("Ctrl").Any(binding => binding.Keys == "Ctrl+Space")
                    && ShortcutHelp.MatchingBindings("Ctrl").Any(binding => binding.Keys == "Ctrl+click header")
                    && !ShortcutHelp.MatchingBindings("Ctrl").Any(binding => binding.Keys == "F"), "modifier filtering includes chords and header gestures without unrelated shortcuts");
                Check(ShortcutHelp.MatchingBindings("Num+").Single().Action == "Add an empty track"
                    && ShortcutHelp.MatchingBindings("=").Single().Action == "Add an empty track", "main and numpad add-track keys map to the same binding without treating every plus separator as a key");
                var filter = new ShortcutHelp.FilterModel();
                filter.Press(Keys.Control | Keys.R); Check(filter.SelectedKey == "R" && filter.Results.Count == 2, "a Ctrl+R key event filters the physical R key");
                filter.Press(Keys.Control | Keys.R); Check(filter.SelectedKey == null && filter.Results.Count == ShortcutHelp.Bindings.Count, "selecting the same key again restores all shortcuts");
                filter.Press(Keys.ControlKey); bool ctrl = filter.SelectedKey == "Ctrl";
                filter.Press(Keys.ShiftKey); bool shift = filter.SelectedKey == "Shift";
                filter.Press(Keys.Menu); Check(ctrl && shift && filter.SelectedKey == "Alt", "modifier key presses filter Ctrl, Shift and Alt themselves");
                filter.Press(Keys.Control | Keys.Space);
                Check(filter.SelectedKey == "Space" && filter.Results.Count == 3, "Ctrl+Space filters Space and lists its three transport variants");
                filter.Press(Keys.F2); Check(filter.SelectedKey == "F2" && filter.Results.Count == 0, "an unused function key has an explicit empty result state");
                filter.Clear(); Check(filter.SelectedKey == null && filter.Results.Count == ShortcutHelp.Bindings.Count, "All shortcuts clears an active filter");
                Check(ShortcutHelp.NormalizeKey(Keys.F1) == "F1" && ShortcutHelp.NormalizeKey(Keys.Home) == "Home"
                    && ShortcutHelp.NormalizeKey(Keys.End) == "End" && ShortcutHelp.NormalizeKey(Keys.Left) == "Left"
                    && ShortcutHelp.NormalizeKey(Keys.Right) == "Right" && ShortcutHelp.NormalizeKey(Keys.Tab) == "Tab", "function, navigation and Tab keys normalize without editor-specific dispatch");
                bool hits = true;
                foreach (string key in new[] { "R", "F1", "Home", "Left", "Ctrl", "Num+", "LMB", "RMB", "Wheel" })
                {
                    var region = ShortcutHelp.KeyRegions.First(region => region.Key == key);
                    hits &= ShortcutHelp.KeyHitTest(Center(region)) == key;
                }
                Check(hits && ShortcutHelp.KeyHitTest(new PointF(0, 0)) == null, "rendered keyboard and mouse regions share their exact click hit rectangles");
                var rCenter = Center(ShortcutHelp.KeyRegions.Single(region => region.Key == "R"));
                var landscape = new Size(1000, 600); var landscapePoint = ShortcutHelp.ImagePointFromZoom(landscape, new(1920, 1536), ClientPoint(landscape, rCenter));
                Check(landscapePoint.HasValue && ShortcutHelp.KeyHitTest(landscapePoint.Value) == "R"
                    && ShortcutHelp.ImagePointFromZoom(landscape, new(1920, 1536), new(5, 300)) == null,
                    "wide Zoom controls exclude side letterboxing and map image clicks to the correct key");
                var portrait = new Size(600, 1000); var portraitPoint = ShortcutHelp.ImagePointFromZoom(portrait, new(1920, 1536), ClientPoint(portrait, rCenter));
                Check(portraitPoint.HasValue && ShortcutHelp.KeyHitTest(portraitPoint.Value) == "R"
                    && ShortcutHelp.ImagePointFromZoom(portrait, new(1920, 1536), new(300, 10)) == null,
                    "tall Zoom controls exclude top letterboxing without applying DPI twice");
                bool scaledHits = true;
                foreach (int dpi in new[] { 96, 144, 192 })
                {
                    var client = new Size(800 * dpi / 96, 600 * dpi / 96);
                    var point = ShortcutHelp.ImagePointFromZoom(client, new(1920, 1536), ClientPoint(client, rCenter));
                    scaledHits &= point.HasValue && ShortcutHelp.KeyHitTest(point.Value) == "R";
                }
                Check(scaledHits && ShortcutHelp.ImagePointFromZoom(Size.Empty, new(1920, 1536), Point.Empty) == null,
                    "Zoom hit testing remains correct for 96, 144 and 192 DPI size fixtures and empty controls");
                using (var probe = new Bitmap(1920, 1536))
                using (var picture = new PictureBox { Size = new(1001, 601), Image = probe, SizeMode = PictureBoxSizeMode.Zoom })
                {
                    var nativeRectangle = (Rectangle)typeof(PictureBox).GetProperty("ImageRectangle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(picture)!;
                    var first = ShortcutHelp.ImagePointFromZoom(picture.ClientSize, probe.Size, nativeRectangle.Location);
                    var outside = ShortcutHelp.ImagePointFromZoom(picture.ClientSize, probe.Size, new(nativeRectangle.Right, nativeRectangle.Top));
                    Check(first == PointF.Empty && outside == null,
                        "odd-sized hit testing matches the actual PictureBox Zoom rectangle at its exact first and exclusive last pixels");
                }
                using (var all = ShortcutHelp.Render())
                using (var selected = ShortcutHelp.Render("R"))
                using (var empty = ShortcutHelp.Render("F2"))
                {
                    var rRegion = ShortcutHelp.KeyRegions.Single(region => region.Key == "R");
                    int x = (int)(rRegion.Bounds.Left + rRegion.Bounds.Width / 2), y = (int)rRegion.Bounds.Top + 5;
                    Check(selected.GetPixel(x, y).ToArgb() == AppTheme.Accent.ToArgb() && selected.GetPixel(x, y) != all.GetPixel(x, y), "filtered guides visibly highlight the selected physical key");
                    Check(empty.Size == all.Size && empty.GetPixel(1000, 580).ToArgb() == AppTheme.Window.ToArgb()
                        && all.GetPixel(1000, 580).ToArgb() != AppTheme.Window.ToArgb(), "unused-key rendering replaces shortcut rows with its empty state while retaining the full keyboard image");
                }
                const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
                var windowType = typeof(ShortcutHelp).GetNestedType("HelpWindow", BindingFlags.NonPublic)!;
                using var owner = new Form();
                using var window = (Form)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [owner], null)!;
                var model = (ShortcutHelp.FilterModel)windowType.GetField("filter", hidden)!.GetValue(window)!;
                var command = windowType.GetMethod("ProcessCmdKey", hidden)!;
                bool Command(Keys key, long flags = 0)
                { object?[] args = [Message.Create(IntPtr.Zero, 0x100, IntPtr.Zero, (IntPtr)flags), key]; return (bool)command.Invoke(window, args)!; }
                Check(Command(Keys.Control | Keys.Space) && model.SelectedKey == "Space" && !window.Visible && !owner.Visible,
                    "hidden guide command routing consumes Ctrl+Space as filtering without showing a window or running playback");
                Check(Command(Keys.Tab) && model.SelectedKey == "Tab" && Command(Keys.F1) && model.SelectedKey == "F1"
                    && Command(Keys.Home) && model.SelectedKey == "Home" && Command(Keys.Left) && model.SelectedKey == "Left",
                    "guide command routing consumes Tab, F1, Home and arrows instead of forwarding editor actions");
                Check(Command(Keys.Left, 1L << 30) && model.SelectedKey == "Left", "held-key repeats do not flicker a filter between selected and all shortcuts");
                IEnumerable<Control> All(Control parent) => parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(All(child)));
                var clearButton = All(window).OfType<ThemeButton>().Single(button => button.AccessibleName == "Show all shortcuts");
                typeof(Control).GetMethod("OnClick", hidden)!.Invoke(clearButton, [EventArgs.Empty]);
                Check(model.SelectedKey == null && model.Results.Count == ShortcutHelp.Bindings.Count, "the visible All shortcuts action is wired to clear the guide filter");
                Check(Command(Keys.Escape) && window.IsDisposed, "Escape remains a consumed close command for the help tool");
                Console.WriteLine($"{checks} interactive shortcut checks passed. No native input or visible window was used.");
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("Shortcut help fixture deadline exceeded.");
        if (failure != null) throw new Exception("Shortcut help fixture failed", failure);
    }
}
