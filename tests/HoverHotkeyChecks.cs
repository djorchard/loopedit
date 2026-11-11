using OrchardSound;

public static class HoverHotkeyChecks
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool condition, string name)
        { if (!condition) throw new Exception("FAILED HOVER KEYS: " + name); checks++; Console.WriteLine("PASS HOVER KEYS " + name); }
        var router = new HoverKeyRouter(); var target = new object(); var other = new object(); var point = new Point(20, 30);
        HoverKeyDecision Key(Keys code, bool down = true, Keys modifiers = Keys.None, object? over = null, bool active = false, bool guide = false)
            => router.Process(code, down, modifiers, over ?? target, active, point, guide);
        Check(Key(Keys.Space) == new HoverKeyDecision(true, Keys.Space) && Key(Keys.Space) == new HoverKeyDecision(true, Keys.Space), "inactive hover routes Space and its retrigger repeats");
        Check(Key(Keys.Space, false).Consume && !Key(Keys.Space, false).Consume, "the swallowed shortcut releases exactly one corresponding keyup");
        Check(Key(Keys.Space, modifiers: Keys.Shift).Command == (Keys.Shift | Keys.Space), "Shift Space retains its selection transport modifier"); Key(Keys.Space, false);
        Check(Key(Keys.Space, modifiers: Keys.Control).Command == (Keys.Control | Keys.Space), "Ctrl Space retains the explicit Stop command"); Key(Keys.Space, false);
        Check(Key(Keys.D, modifiers: Keys.Control).Command == (Keys.Control | Keys.D) && Key(Keys.D, modifiers: Keys.Control).Command == null,
            "destructive commands dispatch once while a key is held"); Key(Keys.D, false);
        Check(!Key(Keys.Q).Consume && !Key(Keys.Q, false).Consume && !Key(Keys.ControlKey).Consume,
            "unassigned letters and modifier keys remain available to the foreground application");
        Check(!Key(Keys.Space, active: true).Consume && !router.Process(Keys.Space, true, Keys.None, null, false, point).Consume,
            "normal active-window processing and other hovered applications are left alone");
        Check(!Key(Keys.Menu).Consume && Key(Keys.Menu, false).FitSelection, "a plain Alt tap fits the selection without swallowing a system modifier");
        Key(Keys.Menu); Key(Keys.E, modifiers: Keys.Alt); Key(Keys.E, false);
        Check(!Key(Keys.Menu, false).FitSelection, "Alt E opens Effects without also fitting the selection");
        Key(Keys.Menu);
        Check(!Key(Keys.Tab, modifiers: Keys.Alt).Consume && !Key(Keys.Menu, false).Consume,
            "system Alt Tab remains available while the pointer is over the inactive editor");
        Key(Keys.Menu);
        Check(!router.Process(Keys.Menu, false, Keys.None, target, false, new(30, 30)).FitSelection,
            "using held Alt while moving the pointer only bypasses snapping");
        Key(Keys.Menu);
        Check(!Key(Keys.Menu, false, over: other).FitSelection, "an Alt tap cannot act on a different hovered window");
        Check(Key(Keys.ControlKey, guide: true).Command == Keys.ControlKey, "the inactive guide can filter by a modifier key"); Key(Keys.ControlKey, false, guide: true);
        Check(Key(Keys.Q, guide: true).Command == Keys.Q && Key(Keys.Q, guide: true).Command == null,
            "the guide accepts every key for filtering and suppresses hold repeats"); Key(Keys.Q, false, guide: true);
        Check(Key(Keys.Menu, guide: true).Command == Keys.Menu && !Key(Keys.Menu, false, guide: true).FitSelection,
            "Alt filters the guide instead of invoking the editor's fit command");
        Check(!Key(Keys.Menu, active: true, guide: true).Consume && !Key(Keys.Menu, false, active: true, guide: true).FitSelection,
            "the active guide retains normal key filtering without fitting the main canvas");
        Check(HoverKeyRouter.IsEditorShortcut(Keys.Control | Keys.I) && HoverKeyRouter.IsEditorShortcut(Keys.Shift | Keys.R)
            && !HoverKeyRouter.IsEditorShortcut(Keys.Control | Keys.Q), "new silence and record bindings are allowlisted without intercepting arbitrary shortcuts");
        router.Reset(); Check(!Key(Keys.D, false).Consume, "reset discards stale consumed-key state");
        Console.WriteLine($"{checks} hover shortcut routing checks passed. No global keyboard hook or native input was used.");
    }
}
