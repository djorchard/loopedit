namespace OrchardSound;

public static partial class RhythmDetector
{
    // Onset-envelope autocorrelation is also used by librosa.feature.tempo:
    // https://librosa.org/doc/0.11.0/generated/librosa.feature.tempo.html
    static (double Bpm, double Period, double Phase, double Confidence)? EstimatePeriodicity(double[] novelty, long[] onsets, long[] kicks, int hop, int rate, CancellationToken cancel)
    {
        double binsPerSecond = rate / (double)hop;
        if (novelty.Length < binsPerSecond * 1.25 || onsets.Length < 4) return null;
        var compact = CompactOnsets(novelty, onsets, hop, rate);
        if (compact.Length < 4 || compact[^1] - compact[0] < rate * .8) return null;
        var prefix = new double[novelty.Length + 1];
        for (int bin = 0; bin < novelty.Length; bin++) prefix[bin + 1] = prefix[bin] + novelty[bin];
        var whitened = new double[novelty.Length]; int radius = Math.Max(1, (int)Math.Round(binsPerSecond * .5));
        for (int bin = 0; bin < novelty.Length; bin++)
        {
            if ((bin & 8191) == 0) cancel.ThrowIfCancellationRequested();
            int from = Math.Max(0, bin - radius), to = Math.Min(novelty.Length, bin + radius + 1);
            whitened[bin] = novelty[bin] - (prefix[to] - prefix[from]) / (to - from);
        }
        int shortest = Math.Max(2, (int)Math.Floor(binsPerSecond * 60 / 220)), longest = (int)Math.Ceiling(binsPerSecond * 60 / 50);
        int lastLag = Math.Min(longest * 2 + 1, novelty.Length / 2);
        var global = Correlations(whitened, 0, whitened.Length, lastLag, cancel);
        double averageEnergy = whitened.Sum(value => value * value) / whitened.Length;
        if (averageEnergy <= 1e-24) return null;
        int windowLength = Math.Min(whitened.Length, (int)Math.Round(binsPerSecond * 8));
        int windowCount = Math.Clamp((int)Math.Ceiling(whitened.Length / (double)windowLength), 1, 12);
        var windows = new List<double[]>();
        for (int window = 0; window < windowCount; window++)
        {
            int start = windowCount == 1 ? 0 : (int)Math.Round((whitened.Length - windowLength) * window / (double)(windowCount - 1));
            double energy = 0; for (int bin = start; bin < start + windowLength; bin++) energy += whitened[bin] * whitened[bin];
            if (energy < averageEnergy * windowLength * .05) continue;
            windows.Add(Correlations(whitened, start, windowLength, Math.Min(longest + 1, windowLength / 2), cancel));
        }
        if (windows.Count == 0) return null;
        double bestScore = 0, bestPeriod = 0, bestCorrelation = 0, bestSupport = 0;
        for (int lag = shortest; lag <= longest && lag + 1 < global.Length; lag++)
        {
            cancel.ThrowIfCancellationRequested();
            if (global[lag] < .25 || global[lag] < global[lag - 1] || global[lag] <= global[lag + 1]) continue;
            double bend = global[lag - 1] - 2 * global[lag] + global[lag + 1];
            double offset = Math.Abs(bend) < 1e-12 ? 0 : Math.Clamp(.5 * (global[lag - 1] - global[lag + 1]) / bend, -.5, .5);
            double period = lag + offset, bpm = binsPerSecond * 60 / period;
            if (bpm is < 50 or > 220) continue;
            int supported = 0;
            foreach (var local in windows)
            {
                double strongest = 0;
                for (int bin = shortest; bin <= longest && bin < local.Length; bin++) strongest = Math.Max(strongest, local[bin]);
                int from = Math.Max(shortest, (int)Math.Floor(period * .97)), to = Math.Min(local.Length - 1, (int)Math.Ceiling(period * 1.03));
                double aligned = 0; for (int bin = from; bin <= to; bin++) aligned = Math.Max(aligned, local[bin]);
                if (aligned >= .15 && aligned >= strongest * .65) supported++;
            }
            double support = supported / (double)windows.Count;
            if (support < .7) continue;
            double fundamental = Interpolate(global, period), harmonic = Math.Max(0, Interpolate(global, period * 2));
            double prior = Math.Exp(-.5 * Math.Pow(Math.Log2(bpm / 110) / .9, 2));
            double score = (fundamental + .3 * harmonic) * (.85 + .15 * prior) * (.75 + .25 * support);
            if (score > bestScore) { bestScore = score; bestPeriod = period; bestCorrelation = fundamental; bestSupport = support; }
        }
        if (bestPeriod == 0) return null;
        double samplePeriod = bestPeriod * hop, tolerance = Math.Min(rate * .03, samplePeriod * .08);
        long[] anchors = compact; double bestPhase = 0, phaseScore = -1;
        foreach (long anchor in anchors.Take(128))
        {
            double score = 0;
            foreach (long frame in anchors)
            {
                double distance = Distance(frame, anchor, samplePeriod);
                if (distance <= tolerance)
                {
                    int bin = Math.Clamp((int)(frame / hop), 0, novelty.Length - 1);
                    double strength = 0; for (int at = Math.Max(0, bin - 2); at <= Math.Min(novelty.Length - 1, bin + 4); at++) strength = Math.Max(strength, novelty[at]);
                    score += strength * (1 - distance / (tolerance * 2));
                }
            }
            if (score > phaseScore) { phaseScore = score; bestPhase = anchor; }
        }
        var fit = Refine(anchors, samplePeriod, bestPhase, rate * 60d / 220, rate * 60d / 50, rate, 2);
        if (Math.Abs(fit.Period / samplePeriod - 1) <= .015) { samplePeriod = fit.Period; bestPhase = fit.Phase; }
        if (novelty.Length < binsPerSecond * 3)
        {
            var reference = kicks.Length >= 4 ? kicks : anchors;
            int matched = reference.Count(frame => Distance(frame, bestPhase, samplePeriod) <= tolerance);
            if (matched < 4 || matched < reference.Length * .75 || bestCorrelation < .4) return null;
        }
        return (60 * rate / samplePeriod, samplePeriod, bestPhase, Math.Clamp(bestCorrelation * bestSupport, 0, 1));
    }

    static long[] CompactOnsets(double[] novelty, long[] onsets, int hop, int rate)
    {
        double binsPerSecond = rate / (double)hop; var compact = new List<long>();
        var smoothed = new double[novelty.Length];
        for (int bin = 0; bin < novelty.Length; bin++)
        {
            int first = Math.Max(0, bin - 2), last = Math.Min(novelty.Length - 1, bin + 2); double sum = 0;
            for (int at = first; at <= last; at++) sum += novelty[at];
            smoothed[bin] = sum / (last - first + 1);
        }
        int near = Math.Max(2, (int)Math.Round(binsPerSecond * .075));
        foreach (long onset in onsets)
        {
            int center = Math.Clamp((int)(onset / hop), 0, novelty.Length - 1), peak = center;
            for (int bin = Math.Max(0, center - near); bin <= Math.Min(novelty.Length - 1, center + near); bin++) if (smoothed[bin] > smoothed[peak]) peak = bin;
            if (smoothed[peak] <= 1e-12) continue;
            int first = peak, last = peak;
            while (first > 0 && smoothed[first - 1] > smoothed[peak] * .5) first--;
            while (last + 1 < novelty.Length && smoothed[last + 1] > smoothed[peak] * .5) last++;
            if (last - first + 1 <= binsPerSecond * .09) compact.Add(onset);
        }
        return compact.ToArray();
    }

    static double Interpolate(double[] values, double index)
    {
        if (index < 0 || index >= values.Length - 1) return 0;
        int first = (int)index; double amount = index - first;
        return values[first] * (1 - amount) + values[first + 1] * amount;
    }
    static double[] Correlations(double[] values, int start, int count, int longest, CancellationToken cancel)
    {
        var correlation = new double[longest + 1];
        double total = 0; for (int bin = start; bin < start + count; bin++) total += values[bin] * values[bin];
        double left = total, right = total;
        for (int lag = 1; lag <= longest; lag++)
        {
            if ((lag & 15) == 0) cancel.ThrowIfCancellationRequested();
            left -= values[start + count - lag] * values[start + count - lag]; right -= values[start + lag - 1] * values[start + lag - 1];
            double product = 0;
            for (int bin = start + lag; bin < start + count; bin++) product += values[bin] * values[bin - lag];
            correlation[lag] = product / Math.Sqrt(Math.Max(1e-30, left * right));
        }
        return correlation;
    }
}

