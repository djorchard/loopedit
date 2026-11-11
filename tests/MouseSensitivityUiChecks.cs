using System.Reflection;
using OrchardSound;

public static class MouseSensitivityUiChecks
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void Run()
    {
        int checks = 0;
        void Check(bool value, string name)
        { if (!value) throw new Exception("FAILED MOUSE SENSITIVITY UI: " + name); checks++; Console.WriteLine("PASS MOUSE SENSITIVITY UI " + name); }
        string folder = Path.Combine(Path.GetTempPath(), "loopsmith-mouse-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder); string path = Path.Combine(folder, "mouse.json");
            Check(MouseSensitivityPreferences.Read(path) == new MouseSensitivitySettings(false, 10), "missing preferences default to disabled and Windows midpoint speed");
            MouseSensitivityPreferences.Save(new(true, 4), path);
            Check(MouseSensitivityPreferences.Read(path) == new MouseSensitivitySettings(true, 4), "enabled state and chosen speed persist in the explicit temporary settings file");
            MouseSensitivityPreferences.Save(new(false, 18), path);
            Check(MouseSensitivityPreferences.Read(path) == new MouseSensitivitySettings(false, 18) && Directory.GetFiles(folder).Length == 1,
                "replacing preferences retains the desired value without leftover temporary files");
            File.WriteAllText(path, "{broken JSON");
            Check(MouseSensitivityPreferences.Read(path) == new MouseSensitivitySettings(), "damaged preferences safely restore disabled defaults");
            File.WriteAllText(path, "{\"Enabled\":true,\"Speed\":21}");
            Check(MouseSensitivityPreferences.Read(path) == new MouseSensitivitySettings(), "out-of-range stored speed is not enabled");
            File.WriteAllText(path, new string('x', 4097));
            Check(MouseSensitivityPreferences.Read(path) == new MouseSensitivitySettings(), "oversized preferences are ignored without parsing arbitrary content");
            bool rejected = false; try { MouseSensitivityPreferences.Save(new(true, 0), path); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected && Directory.GetFiles(folder).Length == 1, "invalid preferences are rejected before any temporary file is created");

            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    IEnumerable<Control> All(Control parent) => parent.Controls.Cast<Control>().SelectMany(control => new[] { control }.Concat(All(control)));
                    using (var dialog = new MouseSensitivityDialog(false, 10))
                    {
                        var gate = All(dialog).OfType<CheckBox>().Single();
                        var amount = All(dialog).OfType<LiveNumericUpDown>().Single(); var slider = All(dialog).OfType<ThemedSlider>().Single();
                        var apply = All(dialog).OfType<ThemeButton>().Single(button => button.Text == "Apply"); var cancel = All(dialog).OfType<ThemeButton>().Single(button => button.Text == "Cancel");
                        Check(!dialog.OverrideEnabled && dialog.Speed == 10 && !amount.Enabled && !slider.Enabled, "hidden sensitivity dialog starts with its custom speed disabled");
                        Check(amount.Minimum == 1 && amount.Maximum == 20 && slider.Minimum == 1 && slider.Maximum == 20, "numeric and slider inputs share the Windows speed bounds");
                        gate.Checked = true; amount.Value = 6;
                        Check(dialog.OverrideEnabled && dialog.Speed == 6 && slider.Value == 6 && amount.Enabled && slider.Enabled, "enabling and typing a speed synchronizes its slider");
                        slider.Value = 17;
                        Check(dialog.Speed == 17 && amount.Value == 17, "moving the sensitivity slider synchronizes its numeric field");
                        gate.Checked = false;
                        Check(!amount.Enabled && !slider.Enabled && dialog.Speed == 17, "disabling the override retains the chosen speed for later use");
                        Check(apply.Role == ButtonRole.Primary && cancel.Role == ButtonRole.Secondary && ReferenceEquals(dialog.AcceptButton, apply)
                            && ReferenceEquals(dialog.CancelButton, cancel) && apply.DialogResult == DialogResult.OK && cancel.DialogResult == DialogResult.Cancel,
                            "themed Apply and Cancel preserve primary/secondary styling and Enter/Escape behavior");
                        Check(amount.BackColor == AppTheme.InputBackground && amount.ForeColor == AppTheme.Text,
                            "sensitivity controls use the application's dark input palette");
                    }
                    using (var clamped = new MouseSensitivityDialog(true, 99)) Check(clamped.Speed == 20 && clamped.OverrideEnabled, "the dialog clamps an incoming value to its supported range");
                    object? Field(EditorForm form, string name) => typeof(EditorForm).GetField(name, Private)!.GetValue(form);
                    void Set(EditorForm form, string name, object? value) => typeof(EditorForm).GetField(name, Private)!.SetValue(form, value);
                    void Call(EditorForm form, string name) => typeof(EditorForm).GetMethod(name, Private)!.Invoke(form, null);
                    var backend = new FakeBackend();
                    using (var form = new EditorForm([]))
                    {
                        _ = form.Handle;
                        Check(Field(form, "mouseSpeed") == null && Field(form, "mouseSpeedTimer") == null && Field(form, "mouseExitHandler") == null,
                            "constructing or creating a hidden editor handle never starts mouse-speed services");
                        Set(form, "mouseSpeedBackendFactory", (Func<IMouseSpeedBackend>)(() => backend));
                        Set(form, "mouseSpeedHoverOverride", (Func<bool>)(() => true)); Set(form, "mouseSettings", new MouseSensitivitySettings(false, 5));
                        Call(form, "StartMouseSensitivity"); var timer = (System.Windows.Forms.Timer)Field(form, "mouseSpeedTimer")!;
                        Call(form, "UpdateMouseSensitivity");
                        Check(timer.Interval == 30 && backend.Reads == 0 && backend.Writes == 0, "an explicitly injected service polls promptly while disabled settings make no backend calls");
                        Set(form, "mouseSettings", new MouseSensitivitySettings(true, 5)); Call(form, "UpdateMouseSensitivity");
                        Check(backend.Speed == 5 && backend.Writes == 1, "enabled hover uses the injected backend instead of a native Windows setting");
                        Set(form, "mouseSpeedHoverOverride", (Func<bool>)(() => false)); Call(form, "UpdateMouseSensitivity");
                        Check(backend.Speed == 10, "leaving through the injected hover state immediately restores the original speed");
                        Set(form, "mouseSpeedHoverOverride", (Func<bool>)(() => true)); Call(form, "UpdateMouseSensitivity");
                        typeof(Form).GetMethod("OnFormClosing", Private)!.Invoke(form, [new FormClosingEventArgs(CloseReason.UserClosing, false)]);
                        Check(!timer.Enabled && backend.Speed == 10, "normal form closing stops polling before disposing the mouse-speed controller");
                        form.Dispose();
                        Check(Field(form, "mouseExitHandler") == null && Field(form, "mouseSpeedTimer") == null, "successful close detaches its ProcessExit handler and disposes the timer");
                    }
                    var failing = new FakeBackend(); var failedForm = new EditorForm([]);
                    try
                    {
                        Set(failedForm, "mouseSpeedBackendFactory", (Func<IMouseSpeedBackend>)(() => failing));
                        Set(failedForm, "mouseSpeedHoverOverride", (Func<bool>)(() => true)); Set(failedForm, "mouseSettings", new MouseSensitivitySettings(true, 4));
                        Call(failedForm, "StartMouseSensitivity"); Call(failedForm, "UpdateMouseSensitivity"); failing.FailWrite = true;
                        failedForm.Dispose(); var handler = (EventHandler?)Field(failedForm, "mouseExitHandler");
                        Check(handler != null && Field(failedForm, "mouseSpeed") == null && failing.Speed == 4,
                            "a failed close restore retains an exit handler owning the controller after the form releases its field");
                        failing.FailWrite = false; handler!(null, EventArgs.Empty);
                        Check(failing.Speed == 10, "the retained ProcessExit callback can restore after a transient normal-close failure");
                        AppDomain.CurrentDomain.ProcessExit -= handler; Set(failedForm, "mouseExitHandler", null);
                    }
                    finally { failing.FailWrite = false; failedForm.Dispose(); }
                }
                catch (Exception error) { failure = error; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("Mouse sensitivity hidden UI fixture deadline exceeded.");
            if (failure != null) throw new Exception("Mouse sensitivity hidden UI fixture failed.", failure);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        Console.WriteLine($"{checks} mouse sensitivity UI checks passed. Settings used a temporary file and all mouse operations used a fake backend.");
    }
    sealed class FakeBackend : IMouseSpeedBackend
    {
        public int Speed = 10, Reads, Writes; public bool FailWrite;
        public int Read() { Reads++; return Speed; }
        public void Write(int value) { if (FailWrite) throw new IOException("Synthetic restore failure"); Speed = value; Writes++; }
    }
}
