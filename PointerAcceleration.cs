namespace OrchardSound;

/// <summary>Maps timed horizontal motion to a bounded local precision gain.</summary>
public sealed class PointerAcceleration
{
    public const double SlowSpeed = 120, FastSpeed = 900, SmoothingSeconds = .035, IdleSeconds = .150;
    double speed;
    public double SmoothedSpeed => speed;
    public void Reset() => speed = 0;
    public double Update(double baseGain, double deltaLogicalPixels, double elapsedSeconds)
    {
        baseGain = double.IsFinite(baseGain) ? Math.Clamp(baseGain, 0, 1) : 1;
        if (!double.IsFinite(deltaLogicalPixels) || !double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0 || elapsedSeconds > IdleSeconds)
        { Reset(); return baseGain; }
        double duration = Math.Clamp(elapsedSeconds, 1d / 240, .05);
        double measured = Math.Min(6000, Math.Abs(deltaLogicalPixels) / duration);
        speed += (measured - speed) * (1 - Math.Exp(-duration / SmoothingSeconds));
        double amount = Math.Clamp((speed - SlowSpeed) / (FastSpeed - SlowSpeed), 0, 1);
        amount = amount * amount * (3 - 2 * amount);
        return baseGain + (1 - baseGain) * amount;
    }
}
