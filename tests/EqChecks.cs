using OrchardSound;

public static class EqChecks
{
    public static async Task Run()
    {
        int checks = 0;
        void Check(bool condition, string name)
        { if (!condition) throw new Exception("FAILED EQ: " + name); checks++; Console.WriteLine("PASS EQ " + name); }
        static EffectSettings Settings(MultibandEqSettings? bands = null, bool automation = false)
            => new(EffectKind.MultibandEq) { MultibandEq = bands, AutomationEnabled = automation };
        static float[] Tone(double frequency, int rate = 48000, int frames = 96000, double amplitude = .025)
            => Enumerable.Range(0, frames).SelectMany(frame => new[] { (float)(amplitude * Math.Sin(2 * Math.PI * frequency * frame / rate)), 0f }).ToArray();
        static double Rms(float[] audio, int first, int frames, int channel = 0)
        { double sum = 0; for (int frame = first; frame < first + frames; frame++) { double value = audio[frame * 2 + channel]; sum += value * value; } return Math.Sqrt(sum / frames); }
        static double GainDb(float[] before, float[] after, int first = 48000, int frames = 48000)
            => 20 * Math.Log10(Rms(after, first, frames) / Rms(before, first, frames));
        static bool BitsEqual(float[] first, float[] last) => first.Select(BitConverter.SingleToInt32Bits).SequenceEqual(last.Select(BitConverter.SingleToInt32Bits));

        var defaults = new MultibandEqSettings();
        Check(MultibandEqSettings.CenterFrequencies.SequenceEqual(new double[] { 31.25, 62.5, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 })
            && Enumerable.Range(0, 10).All(index => defaults.BandAt(index) == new EqBandSettings()), "ten octave bands have immutable flat defaults in frequency order");
        var changed = defaults.WithBand(5, new(6, -6));
        Check(defaults.Hz1000.GainDb == 0 && changed.Hz1000 == new EqBandSettings(6, -6)
            && Enumerable.Range(0, 10).Where(index => index != 5).All(index => changed.BandAt(index) == defaults.BandAt(index)), "changing one immutable band retains previous settings and the other nine bands");
        bool readOnly = false;
        try { ((IList<double>)MultibandEqSettings.CenterFrequencies)[0] = 50; } catch (NotSupportedException) { readOnly = true; }
        Check(readOnly && MultibandEqSettings.CenterFrequencies[0] == 31.25, "frequency metadata cannot be mutated by an effect window");
        var signed = new[] { 0f, -0f, .1234567f, -.912345f, float.Epsilon, -float.Epsilon };
        Check(BitsEqual(signed, AudioEffects.Process(signed, 48000, Settings())), "default flat EQ is a bit-exact bypass including signed zero and subnormal values");
        Check(BitsEqual(signed, AudioEffects.Process(signed, 48000, Settings(defaults.WithBand(0, new(0, 0)), true))), "explicit zero start and end automation remains a bit-exact bypass");
        Check(BitsEqual(signed, AudioEffects.Process(signed, 48000, Settings(defaults.WithBand(5, new(0, 24)), false))), "disabled automation ignores a retained endpoint while keeping flat start gains exact");

        for (int band = 0; band < 10; band++)
        {
            double frequency = MultibandEqSettings.CenterFrequencies[band]; var tone = Tone(frequency);
            foreach (double gain in new[] { -12d, 12 })
            {
                var output = AudioEffects.Process(tone, 48000, Settings(defaults.WithBand(band, new(gain))));
                double measured = GainDb(tone, output);
                Check(Math.Abs(measured - gain) < .025 && output.Length == tone.Length,
                    $"{frequency} Hz center measures {gain} dB within 0.025 dB without changing duration");
            }
        }
        var farTone = Tone(100);
        var farOutput = AudioEffects.Process(farTone, 48000, Settings(defaults.WithBand(5, new(12))));
        Check(Math.Abs(GainDb(farTone, farOutput)) < .5, "one band targets its frequency region instead of applying broadband volume");
        var loud = Tone(1000, amplitude: .1);
        var loudOutput = AudioEffects.Process(loud, 48000, Settings(defaults.WithBand(5, new(24))));
        Check(loudOutput.Max() > 1.5f && Math.Abs(GainDb(loud, loudOutput) - 24) < .025,
            "positive band gains retain their requested headroom without hidden normalization or clipping");

        var dual = Tone(125);
        for (int frame = 0; frame < dual.Length / 2; frame++) dual[frame * 2 + 1] = .025f * (float)Math.Sin(2 * Math.PI * 4000 * frame / 48000);
        var dualSettings = defaults.WithBand(2, new(12)).WithBand(7, new(-12));
        var dualOutput = AudioEffects.Process(dual, 48000, Settings(dualSettings));
        double lowGain = GainDb(dual, dualOutput), highGain = 20 * Math.Log10(Rms(dualOutput, 48000, 48000, 1) / Rms(dual, 48000, 48000, 1));
        Check(Math.Abs(lowGain - 12) < .15 && Math.Abs(highGain + 12) < .15, "simultaneous bass boost and treble cut form a frequency EQ with independent stereo state");
        var leftOnly = dual.ToArray(); var rightOnly = dual.ToArray();
        for (int frame = 0; frame < dual.Length / 2; frame++) { leftOnly[frame * 2 + 1] = 0; rightOnly[frame * 2] = 0; }
        var leftOutput = AudioEffects.Process(leftOnly, 48000, Settings(dualSettings)); var rightOutput = AudioEffects.Process(rightOnly, 48000, Settings(dualSettings));
        Check(Enumerable.Range(0, dual.Length / 2).All(frame => dualOutput[frame * 2] == leftOutput[frame * 2] && dualOutput[frame * 2 + 1] == rightOutput[frame * 2 + 1]
            && leftOutput[frame * 2 + 1] == 0 && rightOutput[frame * 2] == 0), "left and right channel histories never leak audio into each other");

        var sweepTone = Tone(1000);
        var sweep = AudioEffects.Process(sweepTone, 48000, Settings(defaults.WithBand(5, new(-12, 12)), true));
        double early = GainDb(sweepTone, sweep, 4800, 4800), middle = GainDb(sweepTone, sweep, 45600, 4800), late = GainDb(sweepTone, sweep, 86400, 4800);
        Check(early < -9 && Math.Abs(middle) < .15 && late > 9 && late - early > 18, "automated band gains progress from the selected start gain to the selected end gain");
        double largestJump = Enumerable.Range(1, sweep.Length / 2 - 1).Max(frame => Math.Abs((double)sweep[frame * 2] - sweep[(frame - 1) * 2]));
        Check(largestJump < .015 && sweep.All(float.IsFinite), "coefficient interpolation keeps an automated clean tone continuous through update boundaries");
        var staticExpected = AudioEffects.Process(sweepTone, 48000, Settings(defaults.WithBand(5, new(6))));
        Check(BitsEqual(staticExpected, AudioEffects.Process(sweepTone, 48000, Settings(defaults.WithBand(5, new(6, -24)), false))), "turning automation off restores the exact static start-band response");
        foreach (int rate in new[] { 1000, 16000, 32000 })
        {
            var lowRateTone = Tone(100, rate, rate);
            var highBands = defaults;
            for (int index = 0; index < 10; index++) if (MultibandEqSettings.CenterFrequencies[index] >= rate / 2d) highBands = highBands.WithBand(index, new(24, -24));
            Check(BitsEqual(lowRateTone, AudioEffects.Process(lowRateTone, rate, Settings(highBands, true))), $"bands at or above Nyquist are safely skipped at {rate} Hz");
        }
        var stressBands = defaults;
        for (int band = 0; band < 10; band++) stressBands = stressBands.WithBand(band, new(band % 2 == 0 ? 24 : -24, band % 2 == 0 ? -24 : 24));
        foreach (int rate in new[] { 1000, 48000, 384000 })
        {
            var impulse = new float[8192]; impulse[0] = .1f; impulse[1] = -.03f;
            var stress = AudioEffects.Process(impulse, rate, Settings(stressBands, true));
            Check(stress.Length == impulse.Length && stress.All(float.IsFinite), $"all ten extreme automated bands remain finite at {rate} Hz");
        }
        Check(AudioEffects.Process([], 48000, Settings()).Length == 0
            && AudioEffects.Process(new float[2000], 48000, Settings(stressBands, true)).All(value => value == 0), "empty audio and silence remain silent under EQ");

        foreach (var bad in new[] { new EqBandSettings(-25), new EqBandSettings(0, 25), new EqBandSettings(double.NaN), new EqBandSettings(0, double.PositiveInfinity) })
        {
            bool rejected = false;
            try { _ = AudioEffects.Process(signed, 48000, Settings(defaults.WithBand(9, bad), false)); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "invalid band gains and retained automation endpoints are rejected even when automation is off");
        }
        bool invalidSamples = false;
        try { _ = AudioEffects.Process([float.NaN, 0], 48000, Settings()); } catch (ArgumentException) { invalidSamples = true; }
        Check(invalidSamples, "invalid source samples are rejected before filtering");
        bool overflow = false;
        try { _ = AudioEffects.Process([float.MaxValue, 0, -float.MaxValue, 0], 48000, Settings(defaults.WithBand(9, new(24)))); } catch (IOException) { overflow = true; }
        Check(overflow, "unrepresentable boosted samples fail clearly instead of returning non-finite audio");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); bool rejected = false;
            try { await Task.Run(() => AudioEffects.Process(sweepTone, 48000, Settings(stressBands, true), cancelled.Token)); } catch (OperationCanceledException) { rejected = true; }
            Check(rejected, "cancelled EQ work exits without changing source samples");
        }
        var original = dual.ToArray();
        var track = new AudioTrack { Path = "EQ fixture.wav", Samples = dual, Peaks = new(dual) };
        var preview = await EffectPreview.PrepareAsync([track], [track], 0, track.Frames, Settings(dualSettings));
        Check(preview.Changes.Length == 1 && preview.PlaybackTracks[0].Frames == track.Frames && preview.SelectionEnd == null
            && track.Patches.Length == 0 && BitsEqual(dual, original), "ordinary EQ preview stays detached, source-immutable and fixed-duration");
        Check(BitsEqual(preview.PlaybackTracks[0].Source.ReadVisibleSamples(0, track.Frames), dualOutput), "preview and direct processing produce the same EQ audio");
        Console.WriteLine($"{checks} multiband EQ checks passed.");
    }
}
