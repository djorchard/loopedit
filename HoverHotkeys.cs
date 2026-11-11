using System.Runtime.InteropServices;

namespace OrchardSound;

public readonly record struct HoverKeyDecision(bool Consume, Keys? Command = null, bool FitSelection = false);

public sealed class HoverKeyRouter
{
    readonly HashSet<Keys> consumed = [];
    object? altTarget;
    Point altPoint;
    bool altUsed, altBlocked;

    public static bool IsEditorShortcut(Keys key) => key switch
    {
        Keys.F1 or Keys.Space or (Keys.Shift | Keys.Space) or (Keys.Control | Keys.Space)
            or Keys.Left or Keys.Right or Keys.Home or Keys.End or Keys.Delete or Keys.Escape
            or Keys.E or Keys.F or Keys.B or Keys.K or Keys.S or Keys.OemOpenBrackets or Keys.OemCloseBrackets
            or (Keys.Shift | Keys.F) or (Keys.Shift | Keys.R) or (Keys.Alt | Keys.E)
            or (Keys.Control | Keys.Alt | Keys.R) or (Keys.Control | Keys.V) or (Keys.Control | Keys.O)
            or (Keys.Control | Keys.Oemplus) or (Keys.Control | Keys.Shift | Keys.Oemplus) or (Keys.Control | Keys.Add)
            or (Keys.Control | Keys.Z) or (Keys.Control | Keys.Y) or (Keys.Control | Keys.Shift | Keys.Z)
            or (Keys.Control | Keys.A) or (Keys.Control | Keys.D) or (Keys.Control | Keys.D0)
            or (Keys.Control | Keys.R) or (Keys.Control | Keys.T) or (Keys.Control | Keys.B)
            or (Keys.Control | Keys.P) or (Keys.Control | Keys.Shift | Keys.O) or (Keys.Control | Keys.E)
            or (Keys.Control | Keys.S) or (Keys.Control | Keys.Shift | Keys.S) or (Keys.Control | Keys.I) => true,
        _ => false
    };

    public HoverKeyDecision Process(Keys code, bool down, Keys modifiers, object? target, bool alreadyActive, Point pointer, bool guide = false)
    {
        if (guide && alreadyActive) return down ? default : new(consumed.Remove(code));
        if (guide && target != null && !alreadyActive)
        {
            if (code is Keys.Menu or Keys.LMenu or Keys.RMenu or Keys.ControlKey or Keys.LControlKey or Keys.RControlKey or Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey)
                return down ? new(false, code | modifiers) : default;
            if (!down) return new(consumed.Remove(code));
            if (!consumed.Add(code)) return new(true);
            return new(true, code | modifiers);
        }
        bool alt = code is Keys.Menu or Keys.LMenu or Keys.RMenu;
        if (alt)
        {
            if (down)
            {
                if (altTarget == null && target != null)
                { altTarget = target; altPoint = pointer; altUsed = (modifiers & (Keys.Control | Keys.Shift)) != 0; altBlocked = alreadyActive; }
                return default;
            }
            bool fit = target != null && ReferenceEquals(target, altTarget) && !altUsed
                && Math.Abs(pointer.X - altPoint.X) <= 3 && Math.Abs(pointer.Y - altPoint.Y) <= 3;
            bool block = altBlocked && fit;
            altTarget = null; altUsed = altBlocked = false;
            return new(block, FitSelection: fit);
        }
        if (down && altTarget != null) altUsed = true;
        if (!down) return new(consumed.Remove(code));
        if (consumed.Contains(code)) return code is Keys.Space or Keys.Left or Keys.Right
            ? new(true, code | modifiers) : new(true);
        if (target == null || alreadyActive) return default;
        if (code is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey or Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey)
            return default;
        Keys command = code | modifiers;
        if (!guide && !IsEditorShortcut(command)) return default;
        consumed.Add(code); return new(true, command);
    }

    public void Reset() { consumed.Clear(); altTarget = null; altUsed = altBlocked = false; }
}

internal sealed class HoverHotkeys(EditorForm owner, Action<Form, Keys> command, Action<Form> fit, Action<Keys, bool> modifier) : IDisposable
{
    readonly HoverKeyRouter router = new();
    IntPtr hook;
    bool disposed;
    delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] struct KeyboardData { public uint Key, ScanCode, Flags, Time; public UIntPtr ExtraInfo; }

    public void Start()
    {
        if (disposed || hook != IntPtr.Zero) return;
        handler = Handle;
        hook = SetWindowsHookEx(13, handler, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    HookProc? handler;
    internal bool PointerOverApplication
    {
        get
        {
            if (!owner.Visible || owner.IsDisposed) return false;
            GetCursorPos(out Point point);
            GetWindowThreadProcessId(WindowFromPoint(point), out uint process);
            return process == (uint)Environment.ProcessId;
        }
    }
    IntPtr Handle(int code, IntPtr message, IntPtr data)
    {
        if (code < 0 || disposed) return CallNextHookEx(hook, code, message, data);
        try
        {
            int id = message.ToInt32(); bool down = id is 0x100 or 0x104, up = id is 0x101 or 0x105;
            if (!down && !up) return CallNextHookEx(hook, code, message, data);
            var key = (Keys)Marshal.PtrToStructure<KeyboardData>(data).Key;
            if (GetAsyncKeyState(0x5B) < 0 || GetAsyncKeyState(0x5C) < 0)
            { router.Reset(); return CallNextHookEx(hook, code, message, data); }
            GetCursorPos(out Point point);
            var target = TargetAt(point);
            IntPtr foreground = GetAncestor(GetForegroundWindow(), 2);
            // The focused guide owns its keyboard even when the pointer rests
            // over another editor tool. Other windows route by hover target.
            if (Control.FromHandle(foreground) is Form foregroundForm && ShortcutHelp.IsGuideWindow(foregroundForm))
            { router.Reset(); return CallNextHookEx(hook, code, message, data); }
            bool active = target != null && target.Handle == foreground;
            Keys modifiers = (GetAsyncKeyState(0x10) < 0 ? Keys.Shift : 0) | (GetAsyncKeyState(0x11) < 0 ? Keys.Control : 0) | (GetAsyncKeyState(0x12) < 0 ? Keys.Alt : 0);
            if (target is EffectDialog && (key | modifiers) is not (Keys.F1 or Keys.Space or (Keys.Shift | Keys.Space) or (Keys.Control | Keys.Space) or Keys.Escape))
                target = null;
            var decision = router.Process(key, down, modifiers, target, active, point, target != null && ShortcutHelp.IsGuideWindow(target));
            if (target != null && !active && key is (Keys.ControlKey or Keys.LControlKey or Keys.RControlKey or Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey))
                owner.BeginInvoke(() => { if (!owner.IsDisposed) modifier(key, down); });
            if (target != null && (decision.Command.HasValue || decision.FitSelection))
                owner.BeginInvoke(() =>
                {
                    if (owner.IsDisposed || target.IsDisposed || !target.Visible) return;
                    if (decision.FitSelection) fit(target);
                    else if (decision.Command is Keys value) command(target, value);
                });
            if (decision.Consume) return (IntPtr)1;
        }
        catch (Exception) { router.Reset(); }
        return CallNextHookEx(hook, code, message, data);
    }
    Form? TargetAt(Point point)
    {
        if (!owner.Visible || owner.IsDisposed) return null;
        IntPtr window = WindowFromPoint(point);
        GetWindowThreadProcessId(window, out uint process);
        if (process != (uint)Environment.ProcessId) return null;
        Form? form = Control.FromChildHandle(window)?.FindForm() ?? Form.FromHandle(GetAncestor(window, 2)) as Form;
        return form != null && form.Visible && (form == owner || (form.Owner == owner || form.Owner?.Owner == owner)
            && (form is EffectDialog || ShortcutHelp.IsGuideWindow(form))) ? form : null;
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; router.Reset();
        if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        handler = null;
    }
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
}
