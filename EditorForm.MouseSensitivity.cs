namespace OrchardSound;

public sealed partial class EditorForm
{
    MouseSpeedOverride? mouseSpeed;
    System.Windows.Forms.Timer? mouseSpeedTimer;
    MouseSensitivitySettings mouseSettings = MouseSensitivityPreferences.Read();
    EventHandler? mouseExitHandler;
    Func<IMouseSpeedBackend> mouseSpeedBackendFactory = static () => new WindowsMouseSpeedBackend();
    Func<bool>? mouseSpeedHoverOverride = null;

    void InitializeMouseSensitivity()
    {
        ViewContextMenu.Items.Add("Mouse sensitivity...", null, (_, _) => ConfigureMouseSensitivity());
        Shown += (_, _) => StartMouseSensitivity();
        FormClosing += (_, _) => { mouseSpeedTimer?.Stop(); mouseSpeed?.Dispose(); };
        Disposed += (_, _) =>
        {
            mouseSpeedTimer?.Dispose(); mouseSpeedTimer = null;
            var controller = mouseSpeed; controller?.Dispose();
            if (mouseExitHandler != null && controller?.Applied != true)
            { AppDomain.CurrentDomain.ProcessExit -= mouseExitHandler; mouseExitHandler = null; }
            mouseSpeed = null;
        };
    }
    void StartMouseSensitivity()
    {
        if (appClosing || resourcesDisposed || mouseSpeed != null) return;
        var controller = new MouseSpeedOverride(mouseSpeedBackendFactory()); mouseSpeed = controller;
        mouseSpeedTimer = new() { Interval = 30 };
        mouseSpeedTimer.Tick += (_, _) => UpdateMouseSensitivity(); mouseSpeedTimer.Start();
        // Capture the controller itself so ProcessExit can retry even after the
        // form releases its field following a failed close restoration.
        mouseExitHandler = (_, _) => controller.Dispose(); AppDomain.CurrentDomain.ProcessExit += mouseExitHandler;
    }
    void UpdateMouseSensitivity()
    {
        if (appClosing || mouseSpeed == null) return;
        bool hover = mouseSpeedHoverOverride?.Invoke() ?? hoverHotkeys?.PointerOverApplication ?? false;
        if (!mouseSpeed.Update(hover, mouseSettings.Enabled, mouseSettings.Speed))
            status.Text = "Pointer speed could not be changed: " + mouseSpeed.LastError;
    }
    void ConfigureMouseSensitivity()
    {
        using var dialog = new MouseSensitivityDialog(mouseSettings.Enabled, mouseSettings.Speed);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            mouseSettings = new(dialog.OverrideEnabled, dialog.Speed);
            try { MouseSensitivityPreferences.Save(mouseSettings); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { status.Text = "Pointer speed changed for this session. Could not save setting: " + error.Message; }
            UpdateMouseSensitivity();
        }
        if (!IsDisposed && !appClosing) { Activate(); canvas.Focus(); }
    }
}
