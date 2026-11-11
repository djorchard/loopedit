using OrchardSound;

public static class BitcrushChecks
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool condition, string name)
        { if (!condition) throw new Exception("FAILED BITCRUSH: " + name); checks++; Console.WriteLine("PASS BITCRUSH " + name); }
        static bool Near(float[] first, float[] second, float tolerance = .000001f) => first.Length == second.Length && first.Zip(second).All(pair => Math.Abs(pair.First - pair.Second) <= tolerance);
        float[] Stereo(params float[] values) => values.SelectMany(value => new[] { value, -value }).ToArray();
        var source = Stereo(-1, -.75f, -.25f, 0, .25f, .75f, 1);
        var settings = new EffectSettings(EffectKind.Bitcrush) { BitDepth = 2, CrushSampleRate = 48000, Wet = 1 };
        var quantized = AudioEffects.Process(source, 48000, settings);
        float[] expected = [-1, .5f, -1, .5f, -.5f, .5f, 0, 0, .5f, -.5f, .5f, -1, .5f, -1];
        Check(quantized.SequenceEqual(expected), "signed PCM quantization preserves zero and rounds stereo samples away from midpoint zero");
        Check(source.SequenceEqual(Stereo(-1, -.75f, -.25f, 0, .25f, .75f, 1)), "bitcrushing leaves input samples unchanged");
        Check(AudioEffects.Process(source, 48000, settings with { Wet = 0 }).SequenceEqual(source), "zero wet mix bypasses quantization and sample holding exactly");

        var ramp = Stereo(.1f, .2f, .3f, .4f, .5f, .6f, .7f, .8f);
        var held = AudioEffects.Process(ramp, 48000, settings with { BitDepth = 24, CrushSampleRate = 12000 });
        Check(Near(held, Stereo(.1f, .1f, .1f, .1f, .5f, .5f, .5f, .5f)), "12000 Hz sample holding updates both stereo channels together every four input frames");
        Check(Near(AudioEffects.Process(ramp, 1000, settings with { BitDepth = 24, CrushSampleRate = 48000 }), ramp), "effective crush rate clamps to the input rate without skipping frames");
        var mix = AudioEffects.Process(Stereo(.26f), 48000, settings with { Wet = .25f });
        Check(Near(mix, Stereo(.32f)), "wet mix combines quantized held audio with the current dry stereo frame");
        var constant = Stereo(.26f, .26f, .26f, .26f, .26f);
        var wetSweep = AudioEffects.Process(constant, 48000, settings with { Wet = 0, EndWet = 1 });
        Check(Math.Abs(wetSweep[0] - .26f) < .000001 && Math.Abs(wetSweep[4] - .38f) < .000001 && wetSweep[8] == .5f, "bitcrush wet automation reaches its linear start middle and end levels");
        var depthSweep = AudioEffects.Process(Stereo(.3f, .3f, .3f), 48000, settings with { BitDepth = 1, EndBitDepth = 3 });
        Check(depthSweep.Where((_, index) => index % 2 == 0).SequenceEqual(new[] { 0f, .5f, .25f }), "animated bit depth requantizes held raw samples at each frame's integer bit depth");
        var longRamp = Stereo(.1f, .2f, .3f, .4f, .5f, .6f, .7f, .8f, .9f);
        var rateSweep = AudioEffects.Process(longRamp, 48000, settings with { BitDepth = 24, CrushSampleRate = 12000, EndCrushSampleRate = 48000 });
        Check(Math.Abs(rateSweep[0] - .1f) < .000001 && Math.Abs(rateSweep[2] - .1f) < .000001 && Math.Abs(rateSweep[^2] - .9f) < .000001,
            "sample-rate automation changes the shared hold phase from sparse to every-frame updates");
        var disabled = settings with { AutomationEnabled = false, EndBitDepth = 20, EndCrushSampleRate = 100, EndWet = 0 };
        Check(AudioEffects.Process(ramp, 48000, disabled).SequenceEqual(AudioEffects.Process(ramp, 48000, settings)), "disabled automation holds all bitcrush settings at their start values");
        var zero = AudioEffects.Process(new float[100], 48000, settings with { BitDepth = 1, CrushSampleRate = 100 });
        Check(zero.All(value => value == 0), "one-bit sample holding does not turn silence into DC audio");
        var clipped = AudioEffects.Process(Stereo(-2, 2), 48000, settings);
        Check(clipped.All(value => value >= -1 && value <= .5f), "low-bit quantization clamps to the signed PCM representable range");
        bool rejected = false;
        try { AudioEffects.Process(ramp, 48000, settings with { BitDepth = 0 }); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "unsupported bit depth is rejected");
        rejected = false;
        try { AudioEffects.Process(ramp, 48000, settings with { EndCrushSampleRate = double.NaN }); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "invalid automated sample rates are rejected before returning audio");
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel(); rejected = false;
            try { AudioEffects.Process(ramp, 48000, settings, cancel.Token); } catch (OperationCanceledException) { rejected = true; }
            Check(rejected && ramp.SequenceEqual(Stereo(.1f, .2f, .3f, .4f, .5f, .6f, .7f, .8f)), "cancelled bitcrush processing preserves the input");
        }
        var track = new AudioTrack { Path = "Bitcrush fixture.wav", Samples = ramp, Peaks = new PeakPyramid(ramp) };
        var preview = EffectPreview.PrepareAsync([track], [track], 1, 7, settings).GetAwaiter().GetResult();
        Check(track.Patches.Length == 0 && preview.PlaybackTracks[0].Frames == track.Frames && preview.Changes.Single().Samples.Length == 12,
            "bitcrush preview remains non-destructive and keeps its selected stereo duration");
        var chain = EffectChain.PrepareAsync([track], [track], 0, track.Frames, [settings, new(EffectKind.Reverse)]).GetAwaiter().GetResult();
        var chained = Editing.RenderSelection(chain.PlaybackTracks[0].Source, 0, track.Frames);
        Check(Near(chained, AudioEffects.Process(AudioEffects.Process(ramp, 48000, settings), 48000, new(EffectKind.Reverse))) && track.Patches.Length == 0,
            "bitcrush composes with other preview effects in their opening order");
        Console.WriteLine($"{checks} bitcrush checks passed.");
    }
}
