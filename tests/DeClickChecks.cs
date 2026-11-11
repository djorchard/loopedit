using OrchardSound;

public static class DeClickChecks
{
    public static async Task Run()
    {
        int checks = 0;
        void Check(bool value, string name)
        { if (!value) throw new Exception("FAILED DECLICK: " + name); checks++; Console.WriteLine("PASS DECLICK " + name); }
        var settings = new EffectSettings(EffectKind.DeClick) { AutomationEnabled = false };
        static float[] Stereo(IEnumerable<float> values) => values.SelectMany(value => new[] { value, -value * .4f }).ToArray();
        static double Jump(float[] samples, int channel) => Enumerable.Range(1, samples.Length / 2 - 1).Max(frame => Math.Abs((double)samples[frame * 2 + channel] - samples[(frame - 1) * 2 + channel]));
        var impulse = new float[4800 * 2]; impulse[2400 * 2] = .8f; impulse[2400 * 2 + 1] = -.4f;
        var repaired = AudioEffects.Process(impulse, 48000, settings);
        Check(repaired.Length == impulse.Length && repaired.All(value => value == 0), "isolated stereo clicks interpolate to their surrounding silence without changing length");
        Check(impulse[4800] == .8f && impulse[4801] == -.4f, "repair leaves the source buffer immutable");
        var crack = Stereo(Enumerable.Range(0, 4800).Select(frame => frame is >= 2394 and <= 2404 ? .7f : .1f));
        repaired = AudioEffects.Process(crack, 48000, settings);
        Check(repaired.Select((value, index) => Math.Abs(value - (index % 2 == 0 ? .1f : -.04f)) < .00001f).All(equal => equal), "short multi-sample crack is bridged between undamaged surrounding levels");
        var join = Stereo(Enumerable.Range(0, 4800).Select(frame => frame < 2400 ? -.35f : .45f));
        repaired = AudioEffects.Process(join, 48000, settings);
        Check(Jump(repaired, 0) < Jump(join, 0) / 20 && repaired.Take(2200 * 2).SequenceEqual(join.Take(2200 * 2))
            && repaired.Skip(2600 * 2).SequenceEqual(join.Skip(2600 * 2)), "an abrupt edit join becomes a local ramp while distant audio remains bit-exact");
        Check(repaired[0] == join[0] && repaired[^1] == join[^1], "repair retains the selected first and last audio endpoints");
        var narrow = AudioEffects.Process(join, 48000, settings with { RepairMilliseconds = .2 });
        Check(Jump(narrow, 0) > Jump(repaired, 0) * 3 && Jump(narrow, 0) < Jump(join, 0), "repair width controls the smoothing span rather than filtering the entire selection");
        foreach (double frequency in new[] { 40d, 440, 8000, 18000 })
        {
            var tone = Stereo(Enumerable.Range(0, 4800).Select(frame => .4f * (float)Math.Sin(2 * Math.PI * frequency * frame / 48000)));
            Check(AudioEffects.Process(tone, 48000, settings with { Sensitivity = 100 }).SequenceEqual(tone), $"a clean sustained {frequency} Hz tone remains bit-exact at maximum sensitivity");
        }
        var leftOnly = impulse.ToArray();
        for (int frame = 0; frame < leftOnly.Length / 2; frame++) leftOnly[frame * 2 + 1] = .3f * (float)Math.Sin(frame * .2);
        repaired = AudioEffects.Process(leftOnly, 48000, settings);
        Check(Enumerable.Range(0, repaired.Length / 2).All(frame => repaired[frame * 2 + 1] == leftOnly[frame * 2 + 1]) && repaired[4800] == 0,
            "one damaged channel is repaired without modifying the other channel or swapping stereo frames");
        var quiet = new float[4800 * 2]; quiet[240 * 2] = .01f; quiet[4560 * 2] = .01f;
        var low = AudioEffects.Process(quiet, 48000, settings with { Sensitivity = 1 });
        var high = AudioEffects.Process(quiet, 48000, settings with { Sensitivity = 100 });
        Check(low.SequenceEqual(quiet) && high.All(value => value == 0), "higher sensitivity detects quieter isolated cracks");
        var automated = AudioEffects.Process(quiet, 48000, settings with { AutomationEnabled = true, Sensitivity = 1, EndSensitivity = 100 });
        Check(automated[480] == .01f && automated[9120] == 0, "sensitivity automation changes detection over the selected range");
        var twoJoins = Stereo(Enumerable.Range(0, 9600).Select(frame => frame < 2400 ? -.3f : frame < 7200 ? .4f : -.3f));
        automated = AudioEffects.Process(twoJoins, 48000, settings with { AutomationEnabled = true, RepairMilliseconds = .2, EndRepairMilliseconds = 8 });
        int Changed(int center) => Enumerable.Range(center - 400, 800).Count(frame => automated[frame * 2] != twoJoins[frame * 2]);
        Check(Changed(7200) > Changed(2400) * 2, "repair width automation widens later local repairs");
        Check(AudioEffects.Process(new float[400], 48000, settings).All(value => value == 0)
            && AudioEffects.Process([], 48000, settings).Length == 0, "silence and empty selections remain finite and silent");
        Check(AudioEffects.Process([.2f, -.2f], 48000, settings).SequenceEqual(new[] { .2f, -.2f }), "tiny selections remain unchanged when there is no surrounding repair context");
        foreach (var bad in new[] { settings with { Sensitivity = 0 }, settings with { EndSensitivity = double.NaN }, settings with { RepairMilliseconds = 21 }, settings with { EndRepairMilliseconds = 0 } })
        {
            bool rejected = false; try { _ = AudioEffects.Process(impulse, 48000, bad); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "invalid de-click settings are rejected before repair");
        }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); bool rejected = false;
            try { await Task.Run(() => AudioEffects.Process(impulse, 48000, settings, cancelled.Token)); } catch (OperationCanceledException) { rejected = true; }
            Check(rejected && impulse[4800] == .8f, "cancelled jobs leave source audio untouched");
        }
        var track = new AudioTrack { Path = "DeClick fixture.wav", Samples = join, Peaks = new(join) };
        var preview = await EffectPreview.PrepareAsync([track], [track], 0, track.Frames, settings);
        Check(preview.Changes.Length == 1 && preview.PlaybackTracks[0].Frames == track.Frames && track.Patches.Length == 0
            && Jump(preview.PlaybackTracks[0].Source.ReadVisibleSamples(0, track.Frames), 0) < Jump(join, 0) / 20,
            "de-click live preview uses detached repaired audio and preserves timeline duration");
        Console.WriteLine($"{checks} de-click checks passed.");
    }
}
