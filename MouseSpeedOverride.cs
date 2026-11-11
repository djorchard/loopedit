using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OrchardSound;

public interface IMouseSpeedBackend
{
    int Read();
    void Write(int speed);
}

public sealed class WindowsMouseSpeedBackend : IMouseSpeedBackend
{
    const uint GetMouseSpeed = 0x70, SetMouseSpeed = 0x71;
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ReadSpeed(uint action, uint parameter, out int value, uint flags);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool WriteSpeed(uint action, uint parameter, IntPtr value, uint flags);
    public int Read()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows mouse speed is only available on Windows.");
        if (!ReadSpeed(GetMouseSpeed, 0, out int value, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not read the mouse speed.");
        return value;
    }
    public void Write(int speed)
    {
        if (speed is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(speed));
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows mouse speed is only available on Windows.");
        // Flags0 applies only to the current Windows session and does not save
        // the chosen value to the user's registry profile.
        if (!WriteSpeed(SetMouseSpeed, 0, (IntPtr)speed, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not set the mouse speed.");
    }
}

/// <summary>Temporarily owns mouse speed while hovered, without replacing external changes.</summary>
public sealed class MouseSpeedOverride(IMouseSpeedBackend backend) : IDisposable
{
    readonly IMouseSpeedBackend backend = backend ?? throw new ArgumentNullException(nameof(backend));
    readonly object gate = new();
    bool disposed, relinquished;
    int? original, lastApplied;
    public bool Applied => original.HasValue && lastApplied.HasValue;
    public int? OriginalSpeed => original;
    public int? LastAppliedSpeed => lastApplied;
    public string? LastError { get; private set; }

    public bool Update(bool hover, bool enabled, int desired)
    { lock (gate) return UpdateCore(hover, enabled, desired); }
    bool UpdateCore(bool hover, bool enabled, int desired)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (desired is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(desired));
        LastError = null;
        if (!hover || !enabled) { relinquished = false; return Restore(); }
        if (relinquished) return true;
        try
        {
            int current = Read();
            if (original.HasValue && current != lastApplied)
            { original = lastApplied = null; relinquished = true; return true; }
            if (!original.HasValue) { original = current; lastApplied = current; }
            if (current != desired) { backend.Write(desired); lastApplied = desired; }
            return true;
        }
        catch (Exception error) { LastError = error.Message; return false; }
    }
    int Read()
    {
        int current = backend.Read();
        if (current is < 1 or > 20) throw new InvalidOperationException("Windows returned an invalid mouse speed.");
        return current;
    }
    bool Restore()
    {
        if (!original.HasValue) return true;
        try
        {
            int current = Read();
            if (current == lastApplied && current != original) backend.Write(original.Value);
            original = lastApplied = null; return true;
        }
        catch (Exception error) { LastError = error.Message; return false; }
    }
    public void Dispose()
    {
        lock (gate)
        {
            // A later ProcessExit call can retry a failed normal-close restore.
            if (disposed && !original.HasValue) return;
            disposed = true; LastError = null; _ = Restore();
        }
    }
}
