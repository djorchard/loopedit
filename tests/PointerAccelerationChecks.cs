using System.Diagnostics;
using System.Reflection;
using OrchardSound;

public static class PointerAccelerationChecks
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void Run()
    {
        int checks = 0;
        void Check(bool value, string name)
        { if (!value) throw new Exception("FAILED POINTER ACCELERATION: " + name); checks++; Console.WriteLine("PASS POINTER ACCELERATION " + name); }
        var acceleration = new PointerAcceleration();
        double slow = 0;
        for (int index = 0; index < 20; index++) slow = acceleration.Update(.125, 1, .016);
        Check(slow == .125 && acceleration.SmoothedSpeed < PointerAcceleration.SlowSpeed, "slow motions retain the zoom-scaled precision gain");
        double firstFast = acceleration.Update(.125, 32, .016), lastFast = firstFast;
        for (int index = 0; index < 8; index++) lastFast = acceleration.Update(.125, 32, .016);
        Check(firstFast > .125 && firstFast <= 1 && lastFast == 1, "fast sweeps smoothly approach normal speed within a bounded gain");
        double reversed = acceleration.Update(.125, -32, .016);
        Check(reversed == 1, "acceleration depends on horizontal speed rather than movement direction");
        Check(acceleration.Update(.125, 160, .5) == .125 && acceleration.SmoothedSpeed == 0, "an idle gap resets speed instead of causing a catch-up jump");
        Check(acceleration.Update(.125, 0, .016) == .125, "stationary or vertical-only input never creates horizontal acceleration");
        acceleration.Update(.125, 1000, .016); acceleration.Reset();
        Check(acceleration.Update(.125, 1, .016) == .125, "resetting after a cursor warp removes its previous velocity");
        Check(acceleration.Update(.125, double.NaN, .016) == .125 && acceleration.Update(.125, 10, double.NaN) == .125
            && acceleration.Update(.125, 10, 0) == .125, "invalid or duplicate clock readings fall back to precision movement");
        Check(acceleration.Update(1, 10000, .0001) == 1, "extreme event speeds remain bounded at normal pointer gain");

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var canvas = new WaveCanvas { Size = new(1800, 1000), Magnification = 128 }; canvas.SetViewportHeight(1000);
                long now = Stopwatch.Frequency;
                typeof(WaveCanvas).GetField("pointerClock", Private)!.SetValue(canvas, (Func<long>)(() => now));
                Point Move(Point raw, double seconds = .016, bool shift = false, bool control = false)
                {
                    now += (long)Math.Round(seconds * Stopwatch.Frequency);
                    return (Point)typeof(WaveCanvas).GetMethod("MapPointer", Private)!.Invoke(canvas, [(double)raw.X, (double)raw.Y, shift, control, true])!;
                }
                var lower = canvas.GetWaveBounds(1); var anchor = new Point(lower.Left + 300, lower.Top + 30);
                Move(anchor); double expected = anchor.X;
                for (int index = 1; index <= 12; index++) { Move(new(anchor.X + index, anchor.Y)); expected += .125; }
                Check(Math.Abs(canvas.PrecisePointerX - expected) < .000001, "timed lower input uses the same zoom gain for a slow physical movement");
                double beforeFast = canvas.PrecisePointerX; int rawX = anchor.X + 12;
                for (int index = 0; index < 8; index++) { rawX += 32; Move(new(rawX, anchor.Y)); }
                Check(canvas.PrecisePointerX - beforeFast > 220 && canvas.PrecisePointerX < rawX,
                    "accelerated lower input speeds up without snapping the lagging guide to the absolute pointer");
                double beforeIdle = canvas.PrecisePointerX; rawX += 80; Move(new(rawX, anchor.Y), .5);
                Check(Math.Abs(canvas.PrecisePointerX - beforeIdle - 10) < .000001, "the first motion after an idle pause uses base precision even after a fast sweep");
                double beforeShift = canvas.PrecisePointerX; rawX += 80; Move(new(rawX, anchor.Y), .016, true);
                Check(Math.Abs(canvas.PrecisePointerX - beforeShift - 8 / 128d) < .000001, "Shift precision stays fixed and ignores sweep acceleration");
                var absolute = Move(new(rawX, anchor.Y), .016, true, true);
                Check(absolute.X == rawX && canvas.PrecisePointerX == rawX, "Ctrl moves the lower guide to the absolute physical point even with Shift held");
                double beforeReleased = canvas.PrecisePointerX; rawX += 8; Move(new(rawX, anchor.Y));
                Check(Math.Abs(canvas.PrecisePointerX - beforeReleased - 1) < .000001, "releasing Ctrl resumes relative zoom precision without retaining old acceleration");
                var top = canvas.GetWaveBounds(0); var topPoint = new Point(top.Left + 200, top.Top + 30);
                Check(Move(topPoint, .001, true, true) == topPoint, "top precision stays direct under speed, Shift and Ctrl");
                canvas.PrecisionPointer(anchor, false); canvas.PrecisionPointer(new(anchor.X + 80, anchor.Y), true);
                Check(canvas.PrecisionPointer(new(anchor.X + 160, anchor.Y), true, true).X == anchor.X + 160,
                    "the untimed deterministic mapping exposes the same Ctrl absolute override");
                typeof(WaveCanvas).GetField("pointerOverWave", Private)!.SetValue(canvas, true);
                typeof(WaveCanvas).GetField("pointerInPrecision", Private)!.SetValue(canvas, false);
                canvas.PrecisionPointer(new(anchor.X + 200, anchor.Y), true);
                typeof(WaveCanvas).GetMethod("OnKeyDown", Private)!.Invoke(canvas, [new KeyEventArgs(Keys.ControlKey)]);
                Check(canvas.PrecisePointerX == anchor.X + 200 && canvas.CursorFrame.HasValue,
                    "pressing Ctrl immediately catches the guide up to its last physical point without requiring a new mouse move");
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(20))) throw new TimeoutException("Pointer acceleration fixture deadline exceeded.");
        if (failure != null) throw new Exception("Pointer acceleration fixture failed.", failure);
        Console.WriteLine($"{checks} pointer acceleration checks passed. No native pointer input or Windows settings were changed.");
    }
}
