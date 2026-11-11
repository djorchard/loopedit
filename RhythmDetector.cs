using NAudio.Dsp;

namespace OrchardSound;

public enum RhythmAnalysisStatus { Detected, Silence, TooShort, NoReliableTempo }
public sealed record RhythmAnalysis(double? Bpm, long[] BeatFrames, long[] KickFrames)
{
    public RhythmAnalysisStatus Status { get; init; } = Bpm.HasValue ? RhythmAnalysisStatus.Detected : RhythmAnalysisStatus.NoReliableTempo;
    // An evidence score, not a calibrated probability of correctness.
    public double Confidence { get; init; } = Bpm.HasValue ? 1 : 0;
    public bool UsedFallback { get; init; }
}

/// <summary>
/// Offline onset analysis and periodicity fitting. Independent low-band spectral
/// flux finds kick candidates under other transients. Candidates are not instrument
/// classification. See https://librosa.org/doc/0.11.0/generated/librosa.onset.onset_strength.html
/// and https://librosa.org/doc/0.11.0/generated/librosa.beat.beat_track.html .
/// </summary>
public static partial class RhythmDetector
{
    public static RhythmAnalysis Analyze(float[] stereoSamples, int sampleRate = 48000, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(stereoSamples);
        if (sampleRate < 1000 || sampleRate > 384000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        cancel.ThrowIfCancellationRequested();
        int frames = stereoSamples.Length / 2;
        if (frames == 0) return new(null, [], []) { Status = RhythmAnalysisStatus.TooShort };
        int hop = Math.Max(1, (int)Math.Round(sampleRate * .005));
        int count = (frames + hop - 1) / hop;
        var full = new double[count]; var low = new double[count];
        int stride = Math.Max(1, sampleRate / 12000);
        var bassLeft = new float[(frames + stride - 1) / stride]; var bassRight = new float[bassLeft.Length];
        double alphaLow = 1 - Math.Exp(-2 * Math.PI * 180 / sampleRate);
        double alphaDc = 1 - Math.Exp(-2 * Math.PI * 30 / sampleRate);
        double lowL = 0, lowR = 0, dcL = 0, dcR = 0;
        for (int bin = 0; bin < count; bin++)
        {
            if ((bin & 127) == 0) cancel.ThrowIfCancellationRequested();
            int from = bin * hop, to = Math.Min(frames, from + hop);
            double energy = 0, lowEnergy = 0;
            for (int frame = from; frame < to; frame++)
            {
                double l = stereoSamples[frame * 2], r = stereoSamples[frame * 2 + 1];
                if (!double.IsFinite(l)) l = 0; if (!double.IsFinite(r)) r = 0;
                lowL += alphaLow * (l - lowL); lowR += alphaLow * (r - lowR);
                dcL += alphaDc * (l - dcL); dcR += alphaDc * (r - dcR);
                double bandL = lowL - dcL, bandR = lowR - dcR;
                if (frame % stride == 0) { bassLeft[frame / stride] = (float)bandL; bassRight[frame / stride] = (float)bandR; }
                // Sum channel energies so opposite-polarity stereo does not cancel.
                energy += l * l + r * r; lowEnergy += bandL * bandL + bandR * bandR;
            }
            full[bin] = energy / Math.Max(1, (to - from) * 2);
            low[bin] = lowEnergy / Math.Max(1, (to - from) * 2);
        }
        full = SmoothRms(full); low = SmoothRms(low);
        double maxRms = full.Max();
        if (maxRms < 1e-10) return new(null, [], []) { Status = RhythmAnalysisStatus.Silence };
        var novelty = new double[count]; var prefix = new double[count + 1];
        double maxNovelty = 0;
        for (int i = 0; i < count; i++)
        {
            novelty[i] = Math.Max(0, full[i] - (i >= 2 ? full[i - 2] : 0));
            maxNovelty = Math.Max(maxNovelty, novelty[i]); prefix[i + 1] = prefix[i] + full[i];
        }
        var attacks = new List<long>();
        long last = -sampleRate;
        for (int i = 0; i < count; i++)
        {
            if ((i & 127) == 0) cancel.ThrowIfCancellationRequested();
            if (novelty[i] < maxNovelty * .025 || novelty[i] <= 1e-12) continue;
            bool peak = true;
            for (int j = Math.Max(0, i - 2); j <= Math.Min(count - 1, i + 2); j++)
                if (j < i ? novelty[j] >= novelty[i] : j > i && novelty[j] > novelty[i]) { peak = false; break; }
            if (!peak) continue;
            int historyEnd = Math.Max(0, i - 6), historyStart = Math.Max(0, historyEnd - 60);
            double baseline = historyEnd > historyStart ? (prefix[historyEnd] - prefix[historyStart]) / (historyEnd - historyStart) : 0;
            if (full[i] < baseline * 1.3 + maxRms * .003) continue;
            int back = i;
            while (back > Math.Max(0, i - 10) && full[back - 1] > full[i] * .12) back--;
            int start = Math.Max(0, (back - 3) * hop), end = Math.Min(frames, (i + 2) * hop);
            double samplePeak = 0;
            for (int f = start; f < end; f++) samplePeak = Math.Max(samplePeak, Math.Max(Math.Abs(stereoSamples[f * 2]), Math.Abs(stereoSamples[f * 2 + 1])));
            double trigger = samplePeak * .04;
            long onset = start;
            for (int f = start; f < end; f++)
                if (Math.Max(Math.Abs(stereoSamples[f * 2]), Math.Abs(stereoSamples[f * 2 + 1])) >= trigger) { onset = f; break; }
            if (onset - last < sampleRate * .08) continue;
            attacks.Add(onset); last = onset;
        }
        var flux = BassFlux(bassLeft, bassRight, sampleRate / (double)stride, hop / (double)stride, count, cancel);
        long[] kicks = DetectKicks(flux, low, full, attacks, hop, sampleRate, cancel);
        var combined = attacks.ToList();
        foreach (long kick in kicks) if (!combined.Any(attack => Math.Abs(attack - kick) <= sampleRate * .025)) combined.Add(kick);
        long[] onsets = combined.Order().ToArray();
        var tempo = kicks.Length >= 6 ? EstimateTempo(kicks, sampleRate, cancel) : null;
        var tempoOnsets = CompactOnsets(novelty, onsets, hop, sampleRate);
        var broader = tempo == null || onsets.Length > kicks.Length + 2 ? EstimateTempo(tempoOnsets, sampleRate, cancel) : null;
        // Prefer a stable ordinary beat pulse over an otherwise plausible sparse
        // kick half-time interpretation. Both sources still need a coherent grid.
        if (tempo == null || broader.HasValue && tempo.Value.Bpm < 85 && broader.Value.Bpm >= 85 && broader.Value.Bpm <= 180 && Math.Abs(broader.Value.Bpm / tempo.Value.Bpm - 2) < .08) tempo = broader;
        bool fallback = false;
        if (tempo == null) { tempo = EstimatePeriodicity(novelty, onsets, kicks, hop, sampleRate, cancel); fallback = tempo.HasValue; }
        if (tempo == null) return new(null, [], kicks) { Status = frames < sampleRate * 1.25 ? RhythmAnalysisStatus.TooShort : RhythmAnalysisStatus.NoReliableTempo };
        var (bpm, period, phase, confidence) = tempo.Value;
        double tolerance = Math.Min(sampleRate * .03, period * .08);
        var beats = onsets.Where(frame => Distance(frame, phase, period) <= tolerance).ToArray();
        return new(Math.Round(bpm, 1), beats, kicks) { Confidence = confidence, UsedFallback = fallback };
    }

    static double[] BassFlux(float[] left, float[] right, double rate, double hop, int count, CancellationToken cancel)
    {
        int size = 64, exponent = 6;
        while (size < rate * .04) { size *= 2; exponent++; }
        var l = new Complex[size]; var r = new Complex[size]; var window = new double[size];
        for (int i = 0; i < size; i++) window[i] = .5 - .5 * Math.Cos(2 * Math.PI * i / (size - 1));
        var history = new[] { new double[size / 2 + 1], new double[size / 2 + 1], new double[size / 2 + 1] };
        var flux = new double[count];
        int first = Math.Max(1, (int)Math.Ceiling(25 * size / rate)), last = Math.Min(size / 2 - 1, (int)Math.Ceiling(230 * size / rate));
        for (int bin = 0; bin < count; bin++)
        {
            if ((bin & 127) == 0) cancel.ThrowIfCancellationRequested();
            int start = (int)Math.Round(bin * hop) - size / 2;
            for (int i = 0; i < size; i++)
            {
                int at = start + i; l[i].X = at >= 0 && at < left.Length ? (float)(left[at] * window[i]) : 0; l[i].Y = 0;
                r[i].X = at >= 0 && at < right.Length ? (float)(right[at] * window[i]) : 0; r[i].Y = 0;
            }
            FastFourierTransform.FFT(true, exponent, l); FastFourierTransform.FFT(true, exponent, r);
            var current = history[bin % 3]; var previous = history[(bin + 1) % 3];
            for (int frequency = Math.Max(0, first - 1); frequency <= last + 1; frequency++)
            {
                double energy = l[frequency].X * l[frequency].X + l[frequency].Y * l[frequency].Y + r[frequency].X * r[frequency].X + r[frequency].Y * r[frequency].Y;
                current[frequency] = Math.Log(1 + Math.Sqrt(energy) * 100);
            }
            for (int frequency = first; frequency <= last; frequency++)
            {
                double reference = Math.Max(previous[frequency], Math.Max(previous[frequency - 1], previous[frequency + 1]));
                flux[bin] += Math.Max(0, current[frequency] - reference);
            }
        }
        return flux;
    }

    static long[] DetectKicks(double[] flux, double[] low, double[] full, List<long> attacks, int hop, int rate, CancellationToken cancel)
    {
        var kicks = new List<long>(); double maximum = flux.Max(), maxLow = low.Max();
        if (maximum < 1e-10 || maxLow < 1e-10) return [];
        var history = new double[64];
        for (int i = 0; i < flux.Length; i++)
        {
            if ((i & 127) == 0) cancel.ThrowIfCancellationRequested();
            if (flux[i] < maximum * .012) continue;
            bool peak = true;
            for (int j = Math.Max(0, i - 2); j <= Math.Min(flux.Length - 1, i + 2); j++)
                if (j < i ? flux[j] >= flux[i] : j > i && flux[j] > flux[i]) { peak = false; break; }
            if (!peak) continue;
            int historyEnd = Math.Max(0, i - 6), historyStart = Math.Max(0, historyEnd - history.Length), n = historyEnd - historyStart;
            Array.Copy(flux, historyStart, history, 0, n); Array.Sort(history, 0, n);
            double median = n == 0 ? 0 : history[n / 2];
            for (int j = 0; j < n; j++) history[j] = Math.Abs(history[j] - median);
            Array.Sort(history, 0, n); double deviation = n == 0 ? 0 : history[n / 2];
            if (flux[i] < median * 2 + deviation * 3 + maximum * .003) continue;
            double baseline = 0;
            for (int j = historyStart; j < historyEnd; j++) baseline += low[j];
            baseline /= Math.Max(1, n);
            int peakAt = i;
            for (int j = i; j <= Math.Min(low.Length - 1, i + 16); j++) if (low[j] > low[peakAt]) peakAt = j;
            double excess = low[peakAt] - baseline;
            if (excess < maxLow * .012 || excess < baseline * .08) continue;
            double bassEnergy = 0, fullEnergy = 0;
            for (int j = Math.Max(0, i - 4); j <= Math.Min(low.Length - 1, i + 22); j++) { bassEnergy += low[j] * low[j]; fullEnergy += full[j] * full[j]; }
            if (bassEnergy < fullEnergy * .035) continue;
            int tailStart = Math.Min(low.Length - 1, peakAt + 24), tailEnd = Math.Min(low.Length - 1, peakAt + 40);
            double tail = 0;
            for (int j = tailStart; j <= tailEnd; j++) tail += low[j];
            tail /= tailEnd - tailStart + 1;
            if (Math.Max(0, tail - baseline) > excess * .72) continue;
            int onsetBin = Math.Max(0, i - 8);
            double threshold = baseline + excess * .08;
            while (onsetBin < peakAt && low[onsetBin] < threshold) onsetBin++;
            long onset = Math.Max(0, (onsetBin - 1) * hop);
            long nearest = attacks.OrderBy(frame => Math.Abs(frame - onset)).FirstOrDefault(-1);
            if (nearest >= 0 && Math.Abs(nearest - onset) <= rate * .025) onset = nearest;
            if (kicks.Count == 0 || onset - kicks[^1] >= rate * .08) kicks.Add(onset);
        }
        return kicks.ToArray();
    }
    static double[] SmoothRms(double[] energy)
    {
        var output = new double[energy.Length]; double sum = 0;
        for (int i = 0; i < energy.Length; i++)
        { sum += energy[i]; if (i >= 4) sum -= energy[i - 4]; output[i] = Math.Sqrt(Math.Max(0, sum / 4)); }
        return output;
    }
    static double Distance(double frame, double phase, double period)
    { double cycles = (frame - phase) / period; return Math.Abs(cycles - Math.Round(cycles)) * period; }
    static (double Bpm, double Period, double Phase, double Confidence)? EstimateTempo(long[] attacks, int rate, CancellationToken cancel)
    {
        if (attacks.Length < 6 || attacks[^1] - attacks[0] < rate * 3) return null;
        double shortest = rate * 60d / 220, longest = rate * 60d / 50;
        var candidates = new HashSet<long>();
        // Adjacent events and nearby pairs provide period hypotheses, including
        // subdivisions. Coverage penalizes half/double-period interpretations.
        for (int i = 0; i < Math.Min(attacks.Length, 256); i++)
            for (int j = i + 1; j < Math.Min(attacks.Length, i + 5); j++)
                for (int division = 1; division <= 4; division++)
                {
                    double period = (attacks[j] - attacks[i]) / (double)division;
                    // Keep sub-sample hypotheses. Millisecond rounding accumulates
                    // substantial grid drift on long clips at tempos such as 128 BPM.
                    if (period >= shortest && period <= longest) candidates.Add((long)Math.Round(period * 4));
                }
        var early = attacks.Take(64).ToArray();
        var validated = new HashSet<(long Period, long Phase)>();
        double bestScore = 0, bestPeriod = 0, bestPhase = 0;
        foreach (long candidate in candidates)
        {
            cancel.ThrowIfCancellationRequested();
            foreach (long anchor in attacks.Take(8))
            {
                // First fit the local period rather than extrapolating a noisy
                // adjacent attack interval over the entire recording. Full-track
                // refinement then averages timing jitter over a much wider span.
                var fit = Refine(early, candidate / 4d, anchor, shortest, longest, rate, 4);
                fit = Refine(attacks, fit.Period, fit.Phase, shortest, longest, rate, 2);
                double period = fit.Period, phase = fit.Phase;
                double tolerance = Math.Min(rate * .03, period * .08);
                double firstGrid = phase + Math.Round((attacks[0] - phase) / period) * period;
                var key = ((long)Math.Round(period * 8), (long)Math.Round(firstGrid / (rate * .001)));
                if (!validated.Add(key)) continue;
                int matched = 0; double errors = 0; var slots = new HashSet<long>();
                foreach (long frame in attacks)
                {
                    double distance = Distance(frame, phase, period);
                    if (distance <= tolerance) { matched++; errors += distance / tolerance; slots.Add((long)Math.Round((frame - phase) / period)); }
                }
                double coverage = matched / (double)attacks.Length;
                double expected = Math.Round((attacks[^1] - attacks[0]) / period) + 1;
                double occupancy = Math.Min(1, slots.Count / expected);
                if (coverage < .8 || occupancy < .65) continue;
                double score = coverage * .55 + occupancy * .45 - errors / Math.Max(1, matched) * .1;
                if (score > bestScore) { bestScore = score; bestPeriod = period; bestPhase = phase; }
            }
        }
        return bestScore >= .82 ? (60d * rate / bestPeriod, bestPeriod, bestPhase, bestScore) : null;
    }
    static (double Period, double Phase) Refine(long[] attacks, double period, double phase, double shortest, double longest, int rate, int iterations)
    {
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            double tolerance = Math.Min(rate * .06, period * .13);
            double origin = attacks[0], sumX = 0, sumY = 0, sumXX = 0, sumXY = 0; int count = 0;
            foreach (long frame in attacks)
            {
                double slot = Math.Round((frame - phase) / period);
                if (Math.Abs(frame - (phase + slot * period)) > tolerance) continue;
                double y = frame - origin;
                count++; sumX += slot; sumY += y; sumXX += slot * slot; sumXY += slot * y;
            }
            double denominator = count * sumXX - sumX * sumX;
            if (count < 6 || denominator <= 0) break;
            double nextPeriod = (count * sumXY - sumX * sumY) / denominator;
            if (nextPeriod < shortest || nextPeriod > longest || Math.Abs(nextPeriod / period - 1) > .06) break;
            phase = origin + (sumY - nextPeriod * sumX) / count;
            period = nextPeriod;
        }
        return (period, phase);
    }
}
