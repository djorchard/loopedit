using NAudio.Dsp;

namespace OrchardSound;

public enum EffectKind { Maximize, Reverb, Delay, Filter, Reverse, PitchShift, FadeIn, FadeOut, Multiply, Bitcrush, SeamlessRepeat, DeClick, MultibandEq }

public sealed record EffectSettings(EffectKind Kind)
{
    public MultiplySettings? Multiply { get; init; }
    public SeamlessRepeatSettings? SeamlessRepeat { get; init; }
    public MultibandEqSettings? MultibandEq { get; init; }
    public double Sensitivity { get; init; } = 50;
    public double? EndSensitivity { get; init; }
    public double RepairMilliseconds { get; init; } = 2;
    public double? EndRepairMilliseconds { get; init; }
    public bool SelectionOnly { get; init; } = true;
    public bool AutomationEnabled { get; init; } = true;
    public bool CutTail { get; init; } = true;
    public float TargetPeak { get; init; } = .98f;
    public float? EndTargetPeak { get; init; }
    public float Wet { get; init; } = .3f;
    public float? EndWet { get; init; }
    public float Feedback { get; init; } = .35f;
    public float? EndFeedback { get; init; }
    public float RoomSize { get; init; } = .45f;
    public float? EndRoomSize { get; init; }
    public double DelaySeconds { get; init; } = .25;
    public double? EndDelaySeconds { get; init; }
    public bool DelayInBpm { get; init; }
    public double DelayBpm { get; init; } = 120;
    public double? EndDelayBpm { get; init; }
    public double StartHz { get; init; } = 1000;
    public double EndHz { get; init; } = 1000;
    public bool HighPass { get; init; }
    public double StartSemitones { get; init; }
    public double EndSemitones { get; init; }
    public bool PitchInPercent { get; init; }
    public float FadeStrength { get; init; } = 1;
    public float? EndFadeStrength { get; init; }
    public int BitDepth { get; init; } = 8;
    public int? EndBitDepth { get; init; }
    public double CrushSampleRate { get; init; } = 12000;
    public double? EndCrushSampleRate { get; init; }
}

public static class AudioEffects
{
    public const double MaxTailSeconds = 10;
    static double At(double start, double? end, double progress, EffectSettings settings) => start + ((settings.AutomationEnabled ? end ?? start : start) - start) * progress;
    static double Progress(int frame, int frames) => Math.Clamp(frame / (double)Math.Max(1, frames - 1), 0, 1);
    static bool Level(float start, float? end, bool positive = false, double maximum = 1) =>
        float.IsFinite(start) && start >= (positive ? float.Epsilon : 0) && start <= maximum
        && (!end.HasValue || float.IsFinite(end.Value) && end >= (positive ? float.Epsilon : 0) && end <= maximum);
    public static double DelaySecondsAt(EffectSettings settings, double progress) => settings.DelayInBpm
        ? 60 / At(settings.DelayBpm, settings.EndDelayBpm, progress, settings)
        : At(settings.DelaySeconds, settings.EndDelaySeconds, progress, settings);

    public static float[] Process(float[] stereo, int sampleRate, EffectSettings settings, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(stereo); ArgumentNullException.ThrowIfNull(settings);
        cancel.ThrowIfCancellationRequested();
        if (stereo.Length % 2 != 0 || sampleRate < 1000 || sampleRate > 384000) throw new ArgumentException("The effect needs valid stereo audio and a supported sample rate.");
        if (!Enum.IsDefined(settings.Kind)) throw new ArgumentException("Unknown sound effect.");
        if (settings.Kind == EffectKind.Multiply) return MultiplyEffect.ProcessStereo(stereo, sampleRate, settings.Multiply ?? new MultiplySettings(), cancel);
        if (settings.Kind == EffectKind.SeamlessRepeat) throw new NotSupportedException("This effect edits the timeline. Use its effect preview to repeat the selected audio.");
        if (!Level(settings.TargetPeak, settings.EndTargetPeak, true) || !Level(settings.Wet, settings.EndWet) || !Level(settings.Feedback, settings.EndFeedback, maximum: .95)
            || !Level(settings.RoomSize, settings.EndRoomSize) || !Level(settings.FadeStrength, settings.EndFadeStrength))
            throw new ArgumentException("Effect levels are outside their supported range.");
        double sourcePeak = 0;
        for (int i = 0; i < stereo.Length; i++)
        { if ((i & 65535) == 0) cancel.ThrowIfCancellationRequested(); if (!float.IsFinite(stereo[i])) throw new ArgumentException("The selected sound contains invalid samples."); sourcePeak = Math.Max(sourcePeak, Math.Abs(stereo[i])); }
        int frames = stereo.Length / 2;
        int extra = TailFrames(frames, sourcePeak, sampleRate, settings);
        if ((long)(frames + (long)extra) * 2 * sizeof(float) > AudioFiles.MaxTrackBytes) throw new IOException("The processed track exceeds the 256 MiB audio limit.");
        var output = new float[checked((frames + extra) * 2)]; stereo.CopyTo(output, 0);
        if (frames == 0) return output;
        switch (settings.Kind)
        {
            case EffectKind.Maximize:
                if (sourcePeak > 0)
                    for (int frame = 0; frame < frames; frame++)
                    {
                        if ((frame & 32767) == 0) cancel.ThrowIfCancellationRequested();
                        double gain = At(settings.TargetPeak, settings.EndTargetPeak, Progress(frame, frames), settings) / sourcePeak;
                        output[frame * 2] = (float)(stereo[frame * 2] * gain); output[frame * 2 + 1] = (float)(stereo[frame * 2 + 1] * gain);
                    }
                break;
            case EffectKind.Reverse:
                for (int frame = 0; frame < frames; frame++)
                { if ((frame & 32767) == 0) cancel.ThrowIfCancellationRequested(); output[frame * 2] = stereo[(frames - frame - 1) * 2]; output[frame * 2 + 1] = stereo[(frames - frame - 1) * 2 + 1]; }
                break;
            case EffectKind.FadeIn:
            case EffectKind.FadeOut:
                for (int frame = 0; frame < frames; frame++)
                {
                    if ((frame & 32767) == 0) cancel.ThrowIfCancellationRequested();
                    float progress = frames <= 1 ? 1 : frame / (float)(frames - 1);
                    float envelope = settings.Kind == EffectKind.FadeIn ? progress : 1 - progress;
                    float strength = (float)At(settings.FadeStrength, settings.EndFadeStrength, progress, settings);
                    float gain = 1 - strength + strength * envelope;
                    output[frame * 2] = stereo[frame * 2] * gain; output[frame * 2 + 1] = stereo[frame * 2 + 1] * gain;
                }
                break;
            case EffectKind.Delay:
                Delay(stereo, output, sampleRate, settings, cancel);
                break;
            case EffectKind.Reverb:
                Reverb(stereo, output, sampleRate, settings, cancel); break;
            case EffectKind.Filter:
                Filter(stereo, output, sampleRate, settings, cancel); break;
            case EffectKind.PitchShift:
                if (!double.IsFinite(settings.StartSemitones) || !double.IsFinite(settings.EndSemitones) || settings.StartSemitones < -24 || settings.StartSemitones > 24 || settings.EndSemitones < -24 || settings.EndSemitones > 24)
                    throw new ArgumentException("Pitch must stay between -24 and +24 semitones (25% to 400%).");
                if (settings.StartSemitones != 0 || settings.AutomationEnabled && settings.EndSemitones != 0) output = Pitch(stereo, sampleRate, settings, cancel);
                break;
            case EffectKind.Bitcrush:
                Bitcrush(stereo, output, sampleRate, settings, cancel); break;
            case EffectKind.DeClick:
                DeClickEffect.Process(stereo, output, sampleRate, settings, cancel); break;
            case EffectKind.MultibandEq:
                MultibandEqEffect.Process(stereo, output, sampleRate, settings.MultibandEq ?? new MultibandEqSettings(), settings.AutomationEnabled, cancel); break;
        }
        cancel.ThrowIfCancellationRequested();
        if (output.Any(sample => !float.IsFinite(sample))) throw new IOException("The effect produced samples outside the supported range.");
        if (extra > 0)
        {
            if (extra >= sampleRate * MaxTailSeconds)
                for (int i = 0, length = Math.Min(sampleRate / 100, extra); i < length; i++)
                { float level = i / (float)Math.Max(1, length - 1); int at = output.Length - 2 - i * 2; output[at] *= level; output[at + 1] *= level; }
            int retained = output.Length;
            while (retained > stereo.Length && Math.Abs(output[retained - 2]) <= .0000001f && Math.Abs(output[retained - 1]) <= .0000001f) retained -= 2;
            if (retained != output.Length) Array.Resize(ref output, retained);
        }
        return output;
    }

    static void Bitcrush(float[] source, float[] result, int rate, EffectSettings settings, CancellationToken cancel)
    {
        if (settings.BitDepth is < 1 or > 24 || settings.EndBitDepth is < 1 or > 24
            || !double.IsFinite(settings.CrushSampleRate) || settings.CrushSampleRate < 100 || settings.CrushSampleRate > 48000
            || settings.EndCrushSampleRate.HasValue && (!double.IsFinite(settings.EndCrushSampleRate.Value) || settings.EndCrushSampleRate < 100 || settings.EndCrushSampleRate > 48000))
            throw new ArgumentException("Bitcrush requires 1 to 24 bits and a sample rate from 100 to 48000 Hz.");
        if (settings.Wet == 0 && At(settings.Wet, settings.EndWet, 1, settings) == 0) return;
        double phase = 1; float heldLeft = 0, heldRight = 0; int frames = source.Length / AudioTrack.Channels;
        for (int frame = 0; frame < frames; frame++)
        {
            if ((frame & 32767) == 0) cancel.ThrowIfCancellationRequested();
            double progress = Progress(frame, frames);
            if (frame == 0 || phase >= 1 - 1e-12)
            { heldLeft = source[frame * 2]; heldRight = source[frame * 2 + 1]; phase = Math.Max(0, phase - 1); }
            phase += Math.Min(rate, At(settings.CrushSampleRate, settings.EndCrushSampleRate, progress, settings)) / rate;
            int bits = (int)Math.Round(At(settings.BitDepth, settings.EndBitDepth, progress, settings), MidpointRounding.AwayFromZero);
            double scale = 1 << (bits - 1); float wet = (float)At(settings.Wet, settings.EndWet, progress, settings);
            float Quantize(float value) => (float)(Math.Clamp(Math.Round(value * scale, MidpointRounding.AwayFromZero), -scale, scale - 1) / scale);
            result[frame * 2] = source[frame * 2] * (1 - wet) + Quantize(heldLeft) * wet;
            result[frame * 2 + 1] = source[frame * 2 + 1] * (1 - wet) + Quantize(heldRight) * wet;
        }
    }

    static int TailFrames(int frames, double peak, int rate, EffectSettings settings)
    {
        if (settings.Kind == EffectKind.Delay)
        {
            double from = DelaySecondsAt(settings, 0), to = DelaySecondsAt(settings, 1);
            if (!double.IsFinite(from) || !double.IsFinite(to) || from < .001 || to < .001 || from > 10 || to > 10)
                throw new ArgumentException("Delay must be between 1 and 10000 ms, or 6 and 60000 BPM.");
        }
        if (settings.CutTail || frames == 0 || peak == 0 || settings.Kind is not (EffectKind.Delay or EffectKind.Reverb)
            || Math.Max(settings.Wet, At(settings.Wet, settings.EndWet, 1, settings)) == 0) return 0;
        double seconds;
        if (settings.Kind == EffectKind.Delay)
        {
            double feedback = Math.Max(settings.Feedback, At(settings.Feedback, settings.EndFeedback, 1, settings));
            double repeats = feedback <= 0 ? 1 : Math.Ceiling(Math.Log(.0000001 / Math.Max(1, peak)) / Math.Log(feedback)) + 1;
            seconds = Math.Max(DelaySecondsAt(settings, 0), DelaySecondsAt(settings, 1)) * repeats + .02;
        }
        else
        {
            double room = Math.Max(settings.RoomSize, At(settings.RoomSize, settings.EndRoomSize, 1, settings));
            double longest = .0444 * (.7 + room * .6), feedback = .42 + room * .5;
            seconds = longest * (Math.Ceiling(Math.Log(.0000001 / Math.Max(1, peak)) / Math.Log(feedback)) + 1) + .02;
        }
        return checked((int)Math.Ceiling(Math.Min(MaxTailSeconds, seconds) * rate));
    }

    static void Delay(float[] source, float[] result, int rate, EffectSettings settings, CancellationToken cancel)
    {
        if (settings.Wet == 0 && At(settings.Wet, settings.EndWet, 1, settings) == 0) return;
        int ringFrames = checked((int)Math.Ceiling(Math.Max(DelaySecondsAt(settings, 0), DelaySecondsAt(settings, 1)) * rate) + 2);
        var ring = new float[ringFrames * 2];
        int sourceFrames = source.Length / 2, at = 0;
        for (int frame = 0; frame < result.Length / 2; frame++, at = (at + 1) % ringFrames)
        {
            if ((frame & 32767) == 0) cancel.ThrowIfCancellationRequested();
            double progress = Progress(frame, sourceFrames), delay = Math.Max(1, DelaySecondsAt(settings, progress) * rate);
            double tap = (at - delay + ringFrames) % ringFrames; int first = (int)tap, second = (first + 1) % ringFrames; float blend = (float)(tap - first);
            float wet = (float)At(settings.Wet, settings.EndWet, progress, settings), feedback = (float)At(settings.Feedback, settings.EndFeedback, progress, settings);
            for (int ch = 0; ch < 2; ch++)
            {
                float delayed = ring[first * 2 + ch] * (1 - blend) + ring[second * 2 + ch] * blend;
                float dry = frame < sourceFrames ? source[frame * 2 + ch] : 0;
                ring[at * 2 + ch] = dry + delayed * feedback; result[frame * 2 + ch] = dry * (1 - wet) + delayed * wet;
            }
        }
    }

    static void Reverb(float[] source, float[] result, int rate, EffectSettings settings, CancellationToken cancel)
    {
        if (settings.Wet == 0 && At(settings.Wet, settings.EndWet, 1, settings) == 0) return;
        double[] times = [.0297, .0371, .0411, .0437];
        var delays = new float[8][]; var positions = new int[8];
        double maxRoom = Math.Max(settings.RoomSize, At(settings.RoomSize, settings.EndRoomSize, 1, settings));
        for (int i = 0; i < delays.Length; i++) delays[i] = new float[Math.Max(3, (int)Math.Ceiling((times[i / 2] + (i % 2) * .0007) * (.7 + maxRoom * .6) * rate) + 2)];
        int sourceFrames = source.Length / 2;
        for (int frame = 0; frame < result.Length / 2; frame++)
        {
            if ((frame & 32767) == 0) cancel.ThrowIfCancellationRequested();
            double progress = Progress(frame, sourceFrames), room = At(settings.RoomSize, settings.EndRoomSize, progress, settings);
            float feedback = .42f + (float)room * .5f, wet = (float)At(settings.Wet, settings.EndWet, progress, settings);
            for (int ch = 0; ch < 2; ch++)
            {
                float sum = 0, dry = frame < sourceFrames ? source[frame * 2 + ch] : 0;
                for (int comb = 0; comb < 4; comb++)
                {
                    int index = comb * 2 + ch; var buffer = delays[index]; int at = positions[index];
                    double delay = Math.Max(1, (times[comb] + ch * .0007) * (.7 + room * .6) * rate);
                    double tap = (at - delay + buffer.Length) % buffer.Length; int first = (int)tap, second = (first + 1) % buffer.Length;
                    float blend = (float)(tap - first), delayed = buffer[first] * (1 - blend) + buffer[second] * blend;
                    buffer[at] = dry + delayed * feedback; positions[index] = (at + 1) % buffer.Length; sum += delayed;
                }
                result[frame * 2 + ch] = dry * (1 - wet) + sum * wet / 4;
            }
        }
    }

    static void Filter(float[] source, float[] result, int rate, EffectSettings settings, CancellationToken cancel)
    {
        double upper = rate / 2d - 100;
        double endHz = settings.AutomationEnabled ? settings.EndHz : settings.StartHz;
        if (!double.IsFinite(settings.StartHz) || !double.IsFinite(endHz) || settings.StartHz < 20 || endHz < 20 || settings.StartHz > upper || endHz > upper)
            throw new ArgumentException($"Filter frequencies must be between 20 Hz and {upper:0} Hz.");
        var filters = new[] { BiQuadFilter.LowPassFilter(rate, (float)settings.StartHz, .7071f), BiQuadFilter.LowPassFilter(rate, (float)settings.StartHz, .7071f) };
        int frames = source.Length / 2;
        for (int frame = 0; frame < frames; frame++)
        {
            if ((frame & 32767) == 0) cancel.ThrowIfCancellationRequested();
            if ((frame & 31) == 0)
            {
                float hz = (float)At(settings.StartHz, endHz, Progress(frame, frames), settings);
                foreach (var filter in filters)
                    if (settings.HighPass) filter.SetHighPassFilter(rate, hz, .7071f); else filter.SetLowPassFilter(rate, hz, .7071f);
            }
            result[frame * 2] = filters[0].Transform(source[frame * 2]); result[frame * 2 + 1] = filters[1].Transform(source[frame * 2 + 1]);
        }
    }

    static float[] Pitch(float[] source, int rate, EffectSettings settings, CancellationToken cancel)
    {
        const int fft = 2048, overlap = 8, step = fft / overlap, latency = fft - step;
        int frames = source.Length / 2; var result = new float[source.Length];
        for (int ch = 0; ch < 2; ch++)
        {
            var shifter = new SmbPitchShifter(); var block = new float[step];
            for (int from = 0; from < frames + fft * 2; from += step)
            {
                cancel.ThrowIfCancellationRequested(); Array.Clear(block);
                for (int i = 0; i < step && from + i < frames; i++) block[i] = source[(from + i) * 2 + ch];
                double progress = Math.Clamp(from / (double)Math.Max(1, frames - 1), 0, 1);
                double end = settings.AutomationEnabled ? settings.EndSemitones : settings.StartSemitones;
                double semitones = At(settings.StartSemitones, end, progress, settings);
                double ratio = settings.PitchInPercent ? At(Math.Pow(2, settings.StartSemitones / 12), Math.Pow(2, end / 12), progress, settings) : Math.Pow(2, semitones / 12);
                shifter.PitchShift((float)ratio, step, fft, overlap, rate, block);
                for (int i = 0; i < step; i++)
                { int at = from + i - latency; if (at >= 0 && at < frames) result[at * 2 + ch] = block[i]; }
            }
        }
        return result;
    }
}
