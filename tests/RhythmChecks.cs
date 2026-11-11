using OrchardSound;

public static class RhythmChecks
{
    const int Rate = 48000;

    public static void Run()
    {
        int checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAILED RHYTHM: " + name);
            checks++; Console.WriteLine("PASS RHYTHM " + name);
        }
        static bool Near(double? actual, double expected) => actual.HasValue && Math.Abs(actual.Value - expected) <= 3;
        static bool Ordered(long[] frames) => frames.SequenceEqual(frames.Order()) && frames.Distinct().Count() == frames.Length;

        var silent = RhythmDetector.Analyze(new float[Rate * 8 * 2]);
        Check(silent.Bpm == null && silent.BeatFrames.Length == 0 && silent.KickFrames.Length == 0, "silence has no tempo or markers");
        var empty = RhythmDetector.Analyze([]);
        Check(empty.Bpm == null && empty.BeatFrames.Length == 0 && empty.KickFrames.Length == 0, "empty audio has no tempo or markers");

        foreach (int bpm in new[] { 90, 120, 150 })
        {
            double[] times = RegularTimes(bpm);
            var result = RhythmDetector.Analyze(Pulses(times));
            Check(Near(result.Bpm, bpm), $"steady {bpm} BPM kick rhythm has the expected tempo (actual {result.Bpm?.ToString("0.00") ?? "none"})");
            Check(times.All(time => result.BeatFrames.Any(frame => Math.Abs(frame / (double)Rate - time) <= .020)), $"{bpm} BPM beat positions preserve attack starts within 20 ms");
            Check(result.KickFrames.Length >= times.Length - 1, $"{bpm} BPM low-frequency decays produce kick candidates");
            Check(Ordered(result.BeatFrames) && Ordered(result.KickFrames), $"{bpm} BPM markers are sorted and unique");
        }

        double[] referenceTimes = RegularTimes(120);
        var antiPhase = RhythmDetector.Analyze(Pulses(referenceTimes, antiPhase: true));
        Check(Near(antiPhase.Bpm, 120) && antiPhase.BeatFrames.Length >= referenceTimes.Length - 1, "antiphase stereo preserves onset energy and tempo");
        Check(antiPhase.BeatFrames.Any(frame => Math.Abs(frame / (double)Rate - referenceTimes[0]) <= .020), "leading silence is retained in local attack positions");

        var oneShot = RhythmDetector.Analyze(Pulses([1.0]));
        Check(oneShot.Bpm == null && oneShot.KickFrames.Length <= 1, "one isolated attack does not invent a tempo or repeated kicks");
        var bright = RhythmDetector.Analyze(Pulses(referenceTimes, bright: true));
        Check(Near(bright.Bpm, 120) && bright.BeatFrames.Length >= referenceTimes.Length - 1, "bright clicks can establish tempo and beat positions");
        Check(bright.KickFrames.Length == 0, "bright clicks are not labelled as low-frequency kick candidates");

        float[] sustained = new float[Rate * 10 * 2];
        for (int i = 0; i < sustained.Length / 2; i++)
        {
            double time = i / (double)Rate;
            float sample = (float)(.25 * Math.Min(1, time / .1) * Math.Sin(2 * Math.PI * 60 * time));
            sustained[i * 2] = sample; sustained[i * 2 + 1] = -sample;
        }
        var bass = RhythmDetector.Analyze(sustained);
        Check(bass.Bpm == null && bass.KickFrames.Length == 0, "sustained bass without repeated attacks is not treated as a kick rhythm");

        var irregular = RhythmDetector.Analyze(Pulses([.35, .91, 1.83, 2.17, 3.52, 4.01, 5.47, 6.3, 7.79, 8.16, 10.2]));
        Check(irregular.Bpm == null, "irregular unrelated attacks do not receive a confident fixed tempo");

        double[] longTimes = Enumerable.Range(0, 150).Select(i => .35 + i * 60d / 128).TakeWhile(t => t < 64.5).ToArray();
        var longRhythm = RhythmDetector.Analyze(Pulses(longTimes, duration: 65));
        Check(longRhythm.Bpm.HasValue && Math.Abs(longRhythm.Bpm.Value - 128) <= .05, "65-second 128 BPM rhythm preserves its fractional-millisecond period");
        Check(longTimes.All(time => longRhythm.BeatFrames.Any(frame => Math.Abs(frame / (double)Rate - time) <= .020)), "long 128 BPM beat grid does not accumulate attack timing drift");
        double[] jitterTimes = longTimes.Select((time, i) => time + .006 * Math.Sin(i * 1.73) + .003 * Math.Cos(i * .57)).ToArray();
        var jitterRhythm = RhythmDetector.Analyze(Pulses(jitterTimes, duration: 65));
        Check(jitterRhythm.Bpm.HasValue && Math.Abs(jitterRhythm.Bpm.Value - 128) <= .2, "long 128 BPM rhythm tolerates small deterministic human timing jitter");
        Check(jitterTimes.All(time => jitterRhythm.BeatFrames.Any(frame => Math.Abs(frame / (double)Rate - time) <= .020)), "jittered beat markers remain on actual attacks across the full minute");

        var quiet = Pulses(referenceTimes);
        for (int i = 0; i < quiet.Length; i++) quiet[i] *= .003f;
        var quietRhythm = RhythmDetector.Analyze(quiet);
        Check(Near(quietRhythm.Bpm, 120) && quietRhythm.KickFrames.Length >= referenceTimes.Length - 1, "quiet kick recordings retain tempo and low-band attacks without an absolute loudness gate");
        var mixed = Pulses(referenceTimes);
        for (int frame = 0; frame < mixed.Length / 2; frame++)
        {
            float bassNote = (float)(.08 * Math.Min(1, frame / (Rate * .1)) * Math.Sin(2 * Math.PI * 55 * frame / Rate));
            mixed[frame * 2] = mixed[frame * 2] * .4f + bassNote; mixed[frame * 2 + 1] = mixed[frame * 2 + 1] * .4f - bassNote;
        }
        AddBrightBursts(mixed, referenceTimes.Select(time => time + .25).ToArray(), 1800, .04, .5f);
        AddBrightBursts(mixed, referenceTimes.SelectMany(time => new[] { time, time + .125, time + .25, time + .375 }).ToArray(), 8500, .006, .35f);
        var mixedRhythm = RhythmDetector.Analyze(mixed);
        Check(Near(mixedRhythm.Bpm, 120), "independent bass transients retain the kick pulse beneath sustained bass and faster bright percussion");
        Check(referenceTimes.Count(time => mixedRhythm.KickFrames.Any(frame => Math.Abs(frame / (double)Rate - time) <= .030)) >= referenceTimes.Length - 2,
            "kick markers follow low-frequency attacks while other percussion is present");
        Check(mixedRhythm.KickFrames.Length <= referenceTimes.Length + 2, "mixed percussion does not create multiple kick markers for each hit");
        var snare = new float[Rate * 12 * 2]; AddBrightBursts(snare, referenceTimes, 1800, .04, .5f);
        var snareRhythm = RhythmDetector.Analyze(snare);
        Check(snareRhythm.KickFrames.Length == 0, "synthetic bright snare-like bursts do not become kick markers");
        var hats = new float[Rate * 12 * 2]; AddBrightBursts(hats, referenceTimes, 8500, .006, .5f);
        Check(RhythmDetector.Analyze(hats).KickFrames.Length == 0, "short high-frequency hi-hat-like bursts are excluded from kick markers");
        var noise = new float[Rate * 8 * 2]; var random = new Random(1947);
        for (int i = 0; i < noise.Length; i++) noise[i] = (float)((random.NextDouble() * 2 - 1) * .0008);
        var noiseRhythm = RhythmDetector.Analyze(noise);
        Check(noiseRhythm.Bpm == null && noiseRhythm.KickFrames.Length == 0, "quiet stationary noise does not receive a beat grid or kick markers");
        double[] incompleteTimes = referenceTimes.Where((_, index) => index % 7 != 3).ToArray();
        Check(Near(RhythmDetector.Analyze(Pulses(incompleteTimes)).Bpm, 120), "occasional missing kicks do not force a half-time tempo alias");
        double[] changingTimes = referenceTimes.Where(time => time < 6).Concat(Enumerable.Range(0, 15).Select(index => 6.35 + index * .4).Where(time => time < 11.5)).ToArray();
        Check(RhythmDetector.Analyze(Pulses(changingTimes)).Bpm == null, "two sustained different tempos do not invent one confident global tempo");

        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        bool cancelledPromptly = false;
        try { RhythmDetector.Analyze(Pulses(referenceTimes), cancel: cancelled.Token); }
        catch (OperationCanceledException) { cancelledPromptly = true; }
        Check(cancelledPromptly, "analysis observes cancellation before processing audio");
        Console.WriteLine($"{checks} rhythm checks passed.");
    }

    static double[] RegularTimes(int bpm) => Enumerable.Range(0, 40).Select(i => .35 + i * 60d / bpm).TakeWhile(t => t < 11.5).ToArray();

    static void AddBrightBursts(float[] samples, double[] times, double hz, double decay, float amplitude)
    {
        foreach (double start in times)
        {
            int first = (int)Math.Round(start * Rate), length = (int)(Rate * decay * 5);
            for (int i = 0; i < length && first + i < samples.Length / 2; i++)
            {
                double time = i / (double)Rate;
                float value = (float)(amplitude * Math.Min(1, time / .0005) * Math.Exp(-time / decay) * (Math.Sin(2 * Math.PI * hz * time) + .3 * Math.Sin(2 * Math.PI * hz * 1.731 * time)));
                samples[(first + i) * 2] += value; samples[(first + i) * 2 + 1] += value;
            }
        }
    }

    static float[] Pulses(double[] times, bool bright = false, bool antiPhase = false, double duration = 12)
    {
        float[] samples = new float[(int)(Rate * duration) * 2];
        foreach (double start in times)
        {
            int frame = (int)Math.Round(start * Rate);
            int length = (int)(Rate * (bright ? .008 : .15));
            for (int i = 0; i < length && frame + i < samples.Length / 2; i++)
            {
                double time = i / (double)Rate;
                double attack = Math.Min(1, time / (bright ? .0005 : .002));
                double envelope = attack * Math.Exp(-time / (bright ? .001 : .035));
                float value = (float)(.65 * envelope * Math.Cos(2 * Math.PI * (bright ? 3500 : 60) * time));
                samples[(frame + i) * 2] += value;
                samples[(frame + i) * 2 + 1] += antiPhase ? -value : value;
            }
        }
        return samples;
    }
}
