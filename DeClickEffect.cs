namespace OrchardSound;

/// <summary>Repairs isolated abrupt changes, retaining audio outside each bounded repair.</summary>
public static class DeClickEffect
{
    public static void Process(float[] source, float[] result, int rate, EffectSettings settings, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(result); ArgumentNullException.ThrowIfNull(settings);
        cancel.ThrowIfCancellationRequested();
        if (source.Length != result.Length || source.Length % 2 != 0 || ReferenceEquals(source, result) || rate is < 1000 or > 384000)
            throw new ArgumentException("De-click requires separate equal-length stereo buffers and a supported sample rate.");
        static bool Range(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
        if (!Range(settings.Sensitivity, 1, 100) || settings.EndSensitivity.HasValue && !Range(settings.EndSensitivity.Value, 1, 100)
            || !Range(settings.RepairMilliseconds, .1, 20) || settings.EndRepairMilliseconds.HasValue && !Range(settings.EndRepairMilliseconds.Value, .1, 20))
            throw new ArgumentException("De-click requires 1 to 100% sensitivity and a repair width from 0.1 to 20 ms.");
        if ((long)source.Length * sizeof(float) > AudioFiles.MaxTrackBytes) throw new IOException("The processed track exceeds the 256 MiB audio limit.");
        for (int index = 0; index < source.Length; index++)
        { if ((index & 65535) == 0) cancel.ThrowIfCancellationRequested(); if (!float.IsFinite(source[index])) throw new ArgumentException("The selected sound contains invalid samples."); result[index] = source[index]; }
        int frames = source.Length / 2; if (frames < 4) return;
        double At(double start, double? end, int frame) => start + ((settings.AutomationEnabled ? end ?? start : start) - start) * frame / Math.Max(1d, frames - 1);
        int maximumWidth = Math.Max(1, (int)Math.Ceiling(Math.Max(settings.RepairMilliseconds,
            settings.AutomationEnabled ? settings.EndRepairMilliseconds ?? settings.RepairMilliseconds : settings.RepairMilliseconds) * rate / 1000));
        int guard = Math.Max(1, maximumWidth / 2), context = Math.Max(guard + 2, Math.Max(rate / 250, maximumWidth * 2));
        for (int channel = 0; channel < 2; channel++)
        {
            double Difference(int frame) => (double)source[frame * 2 + channel] - source[(frame - 1) * 2 + channel];
            double Square(int frame) { if (frame < 1 || frame >= frames) return 0; double delta = Difference(frame); return delta * delta; }
            double energy = 0;
            for (int neighbour = 1 + guard; neighbour <= 1 + context && neighbour < frames; neighbour++) energy += Square(neighbour);
            int repairLeft = -1, repairRight = -1;
            void Repair()
            {
                if (repairLeft < 0) return;
                double first = source[repairLeft * 2 + channel], last = source[repairRight * 2 + channel];
                int width = repairRight - repairLeft;
                for (int frame = repairLeft + 1; frame < repairRight; frame++)
                { double blend = (frame - repairLeft) / (double)width; result[frame * 2 + channel] = (float)(first * (1 - blend) + last * blend); }
            }
            for (int frame = 1; frame < frames; frame++)
            {
                if ((frame & 8191) == 0) cancel.ThrowIfCancellationRequested();
                int neighbours = Math.Max(0, frame - guard - Math.Max(1, frame - context))
                    + Math.Max(0, Math.Min(frames - 1, frame + context) - (frame + guard) + 1);
                double sensitivity = (At(settings.Sensitivity, settings.EndSensitivity, frame) - 1) / 99;
                double threshold = .02 * Math.Pow(.025, sensitivity) + (12 - 9 * sensitivity) * Math.Sqrt(Math.Max(0, energy) / Math.Max(1, neighbours));
                if (Math.Abs(Difference(frame)) > threshold)
                {
                    int width = Math.Max(1, (int)Math.Round(At(settings.RepairMilliseconds, settings.EndRepairMilliseconds, frame) * rate / 1000));
                    int left = Math.Max(0, frame - width / 2 - 1), right = Math.Min(frames - 1, frame + (width + 1) / 2);
                    if (repairLeft >= 0 && (left > repairRight || right - repairLeft > maximumWidth * 2 + 2)) { Repair(); repairLeft = -1; }
                    if (repairLeft < 0) { repairLeft = left; repairRight = right; }
                    else repairRight = Math.Max(repairRight, right);
                }
                // Two sliding side windows exclude the candidate and its short
                // neighbourhood, so a crack does not raise its own noise floor.
                energy += Square(frame - guard) - Square(frame - context)
                    + Square(frame + context + 1) - Square(frame + guard);
            }
            Repair();
        }
        cancel.ThrowIfCancellationRequested();
    }
}
