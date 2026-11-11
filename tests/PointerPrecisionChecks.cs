using System.Reflection;
using OrchardSound;

public static class PointerPrecisionChecks
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void Run()
    {
        int checks = 0; Exception? failure = null;
        void Check(bool value, string name)
        { if (!value) throw new Exception("FAILED POINTER PRECISION: " + name); checks++; Console.WriteLine("PASS POINTER PRECISION " + name); }
        var thread = new Thread(() =>
        {
            try
            {
                foreach (double zoom in new[] { 2d, 8, 32, 128, 2048 })
                {
                    using var canvas = new WaveCanvas { Size = new(1800, 900), Magnification = zoom }; canvas.SetViewportHeight(900);
                    var wave = canvas.GetWaveBounds(1); var anchor = new Point(wave.Left + 150, wave.Top + 30);
                    canvas.PrecisionPointer(anchor, false); canvas.PrecisionPointer(new(anchor.X + 80, anchor.Y + 9), false);
                    double gain = 1 / Math.Sqrt(zoom / 2);
                    Check(Math.Abs(canvas.PrecisePointerX - anchor.X - 80 * gain) < .000001, $"lower wave movement slows predictably at {zoom}x without Shift");
                    var back = canvas.PrecisionPointer(new(anchor.X, anchor.Y + 20), false);
                    Check(Math.Abs(canvas.PrecisePointerX - anchor.X) < .000001 && back.Y == anchor.Y + 20, $"lower wave movement remains reversible and vertical movement stays direct at {zoom}x");
                    canvas.PrecisionPointer(new(anchor.X + 80, anchor.Y), true);
                    Check(Math.Abs(canvas.PrecisePointerX - anchor.X - 8 / zoom) < .000001, $"Shift retains its additional fractional precision at {zoom}x");
                    var top = canvas.GetWaveBounds(0); var topPoint = new Point(top.Left + 240, top.Top + 20);
                    Check(canvas.PrecisionPointer(topPoint, true) == topPoint && canvas.PrecisePointerX == topPoint.X, $"top precision view stays directly aligned at {zoom}x");
                }
                var hidden = new WaveCanvas { Size = new(1800, 900) }; hidden.SetViewportHeight(900);
                int hides = 0, shows = 0;
                typeof(WaveCanvas).GetField("hidePointer", Private)!.SetValue(hidden, (Action)(() => hides++));
                typeof(WaveCanvas).GetField("showPointer", Private)!.SetValue(hidden, (Action)(() => shows++));
                void OverWave() => typeof(WaveCanvas).GetField("pointerOverWave", Private)!.SetValue(hidden, true);
                void Visibility(bool shift) => typeof(WaveCanvas).GetMethod("UpdatePointerVisibility", Private)!.Invoke(hidden, [shift]);
                void Event(string name, EventArgs argument) => typeof(WaveCanvas).GetMethod(name, Private)!.Invoke(hidden, [argument]);
                hidden.CursorFrame = 123; OverWave(); Visibility(true); Visibility(true);
                Check(hides == 1 && shows == 0 && hidden.CursorFrame == 123, "repeated Shift updates hide once and retain the white audio guide");
                Event("OnKeyUp", new KeyEventArgs(Keys.ShiftKey)); Event("OnKeyUp", new KeyEventArgs(Keys.ShiftKey));
                Check(hides == 1 && shows == 1, "Shift release balances one native visibility increment");
                OverWave(); Event("OnKeyDown", new KeyEventArgs(Keys.ShiftKey)); Event("OnMouseLeave", EventArgs.Empty);
                Check(hides == 2 && shows == 2, "leaving the waveform restores a pointer hidden during Shift precision");
                OverWave(); Visibility(true); Event("OnLostFocus", EventArgs.Empty);
                Check(hides == 3 && shows == 3, "focus loss restores the hidden pointer");
                OverWave(); Visibility(true); hidden.Dispose(); hidden.Dispose();
                Check(hides == 4 && shows == 4, "disposing a precision canvas restores its pointer exactly once");
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(20))) throw new TimeoutException("Pointer precision fixture deadline exceeded.");
        if (failure != null) throw new Exception("Pointer precision fixture failed.", failure);
        Console.WriteLine($"{checks} pointer precision checks passed. Native pointer visibility was replaced with counters.");
    }
}
