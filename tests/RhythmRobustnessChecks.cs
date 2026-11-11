using OrchardSound;

public static class RhythmRobustnessChecks
{
    const int Rate = 12000;
    public static async Task Run()
    {
        await Task.Run(() =>
        {
            int checks = 0;
            void Check(bool value, string name)
            { if (!value) throw new Exception("FAILED RHYTHM ROBUSTNESS: " + name); checks++; Console.WriteLine("PASS RHYTHM ROBUSTNESS " + name); }
            var empty = RhythmDetector.Analyze([], Rate); var silence = RhythmDetector.Analyze(new float[Rate * 4], Rate);
            Check(empty.Status == RhythmAnalysisStatus.TooShort && silence.Status == RhythmAnalysisStatus.Silence
                && empty.Bpm == null && silence.Bpm == null, "short/empty audio and silence have different truthful diagnostic states");
            var shortAudio = new float[(int)(Rate * 2.2) * 2];
            foreach (double time in new[] { .1, .6, 1.1, 1.6 }) AddKick(shortAudio, time, .6);
            var shortResult = RhythmDetector.Analyze(shortAudio, Rate);
            Check(shortResult.Bpm.HasValue && Math.Abs(shortResult.Bpm.Value - 120) < 1 && shortResult.UsedFallback,
                "a four-beat short loop obtains a supported120BPM estimate instead of requiring six attacks and three seconds");
            Check(new[] { .1, .6, 1.1, 1.6 }.All(time => shortResult.BeatFrames.Any(frame => Math.Abs(frame / (double)Rate - time) < .025)),
                "short-loop fallback markers stay on real attack starts");
            var tooShort = new float[Rate]; AddKick(tooShort, .1, .6); AddKick(tooShort, .35, .6);
            var tooShortResult = RhythmDetector.Analyze(tooShort, Rate);
            Check(tooShortResult.Bpm == null && tooShortResult.Status == RhythmAnalysisStatus.TooShort, "a genuinely tiny clip does not receive a guessed tempo");
            foreach (double bpm in new[] { 80d, 106, 128 })
            {
                var audio = Dense(bpm); var copy = audio.ToArray(); var result = RhythmDetector.Analyze(audio, Rate);
                Check(result.Bpm.HasValue && Math.Abs(result.Bpm.Value - bpm) < 1.5, $"dense syncopated percussion retains its weighted{bpm}BPM pulse");
                Check(result.Status == RhythmAnalysisStatus.Detected && result.Confidence is > 0 and <= 1 && audio.SequenceEqual(copy),
                    "rhythm evidence remains bounded and source audio stays unchanged");
            }
            var dense = Dense(106); var normal = RhythmDetector.Analyze(dense, Rate);
            for (int index = 0; index < dense.Length; index++) dense[index] *= .001f;
            var quiet = RhythmDetector.Analyze(dense, Rate);
            Check(quiet.Bpm.HasValue && normal.Bpm.HasValue && Math.Abs(quiet.Bpm.Value - normal.Bpm.Value) < .2,
                "dense-pattern tempo detection is independent of ordinary playback volume");
            for (int frame = 0; frame < dense.Length / 2; frame++) dense[frame * 2 + 1] = -dense[frame * 2];
            var opposite = RhythmDetector.Analyze(dense, Rate);
            Check(opposite.Bpm.HasValue && Math.Abs(opposite.Bpm.Value - 106) < 1.5, "opposite-polarity stereo retains the same underlying dense beat pulse");

            var drifting = new float[Rate * 12 * 2];
            for (double time = .2; time < 11.8; time += 60 / (80 + 80 * time / 12)) AddKick(drifting, time, .6);
            var changing = RhythmDetector.Analyze(drifting, Rate);
            Check(changing.Bpm == null && changing.Status == RhythmAnalysisStatus.NoReliableTempo,
                "a gradual doubling of tempo does not become one falsely confident global BPM");
            var ambient = new float[Rate * 12 * 2];
            for (int frame = 0; frame < ambient.Length / 2; frame++)
            {
                double time = frame / (double)Rate;
                float value = (float)(.2 * Math.Min(1, time / .3) * (.55 + .45 * Math.Sin(2 * Math.PI * 1.1 * time)) * Math.Sin(2 * Math.PI * 60 * time));
                ambient[frame * 2] = value; ambient[frame * 2 + 1] = -value;
            }
            var ambientResult = RhythmDetector.Analyze(ambient, Rate);
            Check(ambientResult.Bpm == null && ambientResult.KickFrames.Length == 0, $"slow ambient swells do not masquerade as compact percussive beats (BPM {ambientResult.Bpm}, kicks {ambientResult.KickFrames.Length})");
            var stationary = new float[Rate * 10 * 2]; var random = new Random(8375);
            for (int index = 0; index < stationary.Length; index++) stationary[index] = (float)((random.NextDouble() * 2 - 1) * .02);
            Check(RhythmDetector.Analyze(stationary, Rate).Bpm == null, "stationary noise has no forced periodicity fallback");
            var irregular = new float[Rate * 12 * 2];
            foreach (double time in new[] { .2, .67, 1.81, 2.13, 3.77, 4.19, 5.48, 6.41, 7.9, 8.17, 10.11, 11.4 }) AddKick(irregular, time, .6);
            Check(RhythmDetector.Analyze(irregular, Rate).Bpm == null, "irregular transients remain an explicit no-reliable-tempo result");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); bool rejected = false;
            try { _ = RhythmDetector.Analyze(dense, Rate, cancelled.Token); } catch (OperationCanceledException) { rejected = true; }
            Check(rejected, "fallback-enabled analysis observes cancellation before any work");
            Console.WriteLine($"{checks} rhythm robustness checks passed.");
        });
    }
    static float[] Dense(double bpm)
    {
        var audio = new float[Rate * 12 * 2]; double period = 60 / bpm;
        for (int frame = 0; frame < audio.Length / 2; frame++)
        {
            double time = frame / (double)Rate; float value = (float)(.055 * Math.Sin(2 * Math.PI * 55 * time) + .03 * Math.Sin(2 * Math.PI * 220 * time));
            audio[frame * 2] = value; audio[frame * 2 + 1] = value * .8f;
        }
        for (int beat = 0; .2 + beat * period < 11.7; beat++)
        {
            double time = .2 + beat * period; AddKick(audio, time, .55);
            if (beat % 2 == 0) AddKick(audio, time + period * .29, .13, 92);
            if (beat % 3 == 1) AddKick(audio, time + period * .71, .1, 115);
            for (int subdivision = 0; subdivision < 4; subdivision++) AddBright(audio, time + period * subdivision / 4, .055);
        }
        for (int index = 0; index < audio.Length; index++) audio[index] = (float)(Math.Tanh(audio[index] * 3) * .4);
        return audio;
    }
    static void AddKick(float[] audio, double time, double amplitude, double frequency = 60)
    {
        int first = (int)Math.Round(time * Rate), length = (int)(Rate * .14);
        for (int frame = 0; frame < length && first + frame < audio.Length / 2; frame++)
        {
            double seconds = frame / (double)Rate; float value = (float)(amplitude * Math.Min(1, seconds / .0015) * Math.Exp(-seconds / .025) * Math.Cos(2 * Math.PI * frequency * seconds));
            audio[(first + frame) * 2] += value; audio[(first + frame) * 2 + 1] += value * .8f;
        }
    }
    static void AddBright(float[] audio, double time, double amplitude)
    {
        int first = (int)Math.Round(time * Rate), length = (int)(Rate * .03);
        for (int frame = 0; frame < length && first + frame < audio.Length / 2; frame++)
        {
            double seconds = frame / (double)Rate; float value = (float)(amplitude * Math.Exp(-seconds / .004) * Math.Sin(2 * Math.PI * 3500 * seconds));
            audio[(first + frame) * 2] += value; audio[(first + frame) * 2 + 1] += value;
        }
    }
}
