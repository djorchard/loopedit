using System.Globalization;

namespace OrchardSound;

public sealed record SeamlessRepeatSettings
{
    public int RepeatCount { get; init; } = 10;
    public double CrossfadeMilliseconds { get; init; } = 20;
    public bool SmartSeam { get; init; } = true;
    public bool AdaptiveCrossfade { get; init; } = true;
    public bool LoopEndToStart { get; init; } = true;
}

public sealed record SeamlessRepeatInfo(int OverlapFrames, double Correlation, double Mismatch);

public static class SeamlessRepeatEffect
{
    const int ComparisonPoints = 1024, SearchSteps = 256;

    static void Validate(SeamlessRepeatSettings settings, int rate)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.RepeatCount is < 1 or > 64) throw new ArgumentException("Seamless Repeat needs 1 to 64 total repetitions.");
        if (!double.IsFinite(settings.CrossfadeMilliseconds) || settings.CrossfadeMilliseconds < 0 || settings.CrossfadeMilliseconds > 10000)
            throw new ArgumentException("Crossfade must be between 0 and 10000 milliseconds.");
        if (rate is < 1000 or > 384000) throw new ArgumentException("Seamless Repeat needs a supported sample rate.");
    }

    static int RequestedOverlap(long frames, int rate, SeamlessRepeatSettings settings) =>
        (int)Math.Min(Math.Max(0, (frames - 1) / 2), Math.Round(settings.CrossfadeMilliseconds * rate / 1000, MidpointRounding.AwayFromZero));

    static SeamlessRepeatInfo Measure(long frames, int overlap, Func<long, int, double> sample, int channels, CancellationToken cancel)
    {
        if (overlap == 0) return new(0, 0, 0);
        int points = Math.Min(ComparisonPoints, overlap); double leftEnergy = 0, rightEnergy = 0, dot = 0, mismatch = 0;
        for (int point = 0; point < points; point++)
        {
            if ((point & 127) == 0) cancel.ThrowIfCancellationRequested();
            long at = points <= 1 ? 0 : point * (overlap - 1L) / (points - 1);
            for (int channel = 0; channel < channels; channel++)
            {
                double tail = sample(frames - overlap + at, channel), head = sample(at, channel);
                leftEnergy += tail * tail; rightEnergy += head * head; dot += tail * head;
                double difference = tail - head; mismatch += difference * difference;
            }
        }
        double energy = leftEnergy + rightEnergy;
        double correlation = leftEnergy > 1e-30 && rightEnergy > 1e-30 ? dot / Math.Sqrt(leftEnergy * rightEnergy) : 0;
        return new(overlap, Math.Clamp(correlation, 0, 1), energy > 1e-30 ? mismatch / energy : 0);
    }

    static SeamlessRepeatInfo ChooseSeam(long frames, int rate, SeamlessRepeatSettings settings, Func<long, int, double> sample, int channels, CancellationToken cancel)
    {
        int requested = RequestedOverlap(frames, rate, settings);
        var best = Measure(frames, requested, sample, channels, cancel);
        if (!settings.SmartSeam || requested == 0 || settings.RepeatCount == 1 && !settings.LoopEndToStart) return best;
        int radius = Math.Max(1, (int)Math.Round(rate * .005)), maximum = (int)Math.Min(int.MaxValue, Math.Max(0, (frames - 1) / 2));
        int first = Math.Max(1, requested - radius), last = Math.Min(maximum, requested + radius);
        int step = Math.Max(1, (last - first + SearchSteps - 1) / SearchSteps);
        void Consider(int overlap)
        {
            cancel.ThrowIfCancellationRequested(); var current = Measure(frames, overlap, sample, channels, cancel);
            if (current.Mismatch < best.Mismatch - 1e-12 || Math.Abs(current.Mismatch - best.Mismatch) <= 1e-12
                && Math.Abs(overlap - requested) < Math.Abs(best.OverlapFrames - requested)) best = current;
        }
        for (int overlap = first; overlap <= last; overlap += step) Consider(overlap);
        Consider(last);
        int center = best.OverlapFrames;
        for (int overlap = Math.Max(first, center - step); overlap <= Math.Min(last, center + step); overlap++) Consider(overlap);
        return best;
    }

    public static SeamlessRepeatInfo AnalyzeSeam(float[] stereo, int sampleRate, SeamlessRepeatSettings settings, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(stereo); Validate(settings, sampleRate); cancel.ThrowIfCancellationRequested();
        if (stereo.Length % AudioTrack.Channels != 0) throw new ArgumentException("Provide interleaved stereo samples.");
        for (int i = 0; i < stereo.Length; i++)
        { if ((i & 65535) == 0) cancel.ThrowIfCancellationRequested(); if (!float.IsFinite(stereo[i])) throw new ArgumentException("Seamless Repeat needs finite audio samples."); }
        return ChooseSeam(stereo.LongLength / AudioTrack.Channels, sampleRate, settings, (frame, channel) => stereo[checked((int)(frame * AudioTrack.Channels + channel))], AudioTrack.Channels, cancel);
    }

    public static float[] ProcessStereo(float[] stereo, int sampleRate, SeamlessRepeatSettings settings, CancellationToken cancel = default)
    {
        var seam = AnalyzeSeam(stereo, sampleRate, settings, cancel);
        return Render(stereo, settings, seam.OverlapFrames, cancel);
    }

    static float[] Render(float[] stereo, SeamlessRepeatSettings settings, int overlap, CancellationToken cancel)
    {
        int frames = stereo.Length / AudioTrack.Channels;
        if (settings.LoopEndToStart && overlap > 0) return RenderClosed(stereo, settings, overlap, cancel);
        long length = checked((long)frames * settings.RepeatCount - (settings.RepeatCount - 1L) * overlap);
        if (length * AudioTrack.Channels * sizeof(float) > AudioFiles.MaxTrackBytes) throw new IOException("Seamless Repeat exceeds the 256 MiB track limit.");
        var result = new float[checked((int)(length * AudioTrack.Channels))];
        if (frames == 0) return result;
        stereo.CopyTo(result, 0);
        if (settings.RepeatCount == 1) return result;
        double correlation = settings.AdaptiveCrossfade ? Measure(frames, overlap,
            (frame, channel) => stereo[checked((int)(frame * AudioTrack.Channels + channel))], AudioTrack.Channels, cancel).Correlation : 1;
        int step = frames - overlap;
        for (int copy = 1; copy < settings.RepeatCount; copy++)
        {
            cancel.ThrowIfCancellationRequested(); int destination = checked(copy * step);
            for (int frame = 0; frame < frames; frame++)
            {
                if ((frame & 32767) == 0) cancel.ThrowIfCancellationRequested();
                double incoming = overlap > 0 && frame < overlap ? (frame + 1d) / (overlap + 1d) : 1;
                double outgoing = 1 - incoming;
                double normalization = settings.AdaptiveCrossfade ? Math.Sqrt(outgoing * outgoing + incoming * incoming + 2 * correlation * outgoing * incoming) : 1;
                for (int channel = 0; channel < AudioTrack.Channels; channel++)
                {
                    int at = (destination + frame) * AudioTrack.Channels + channel;
                    float value = frame < overlap
                        ? (float)((result[at] * outgoing + stereo[frame * AudioTrack.Channels + channel] * incoming) / normalization)
                        : stereo[frame * AudioTrack.Channels + channel];
                    if (!float.IsFinite(value)) throw new IOException("The crossfade exceeds the supported audio range.");
                    result[at] = value;
                }
            }
        }
        cancel.ThrowIfCancellationRequested(); return result;
    }

    static float[] RenderClosed(float[] stereo, SeamlessRepeatSettings settings, int overlap, CancellationToken cancel)
    {
        int frames = stereo.Length / AudioTrack.Channels, period = frames - overlap;
        long length = checked((long)period * settings.RepeatCount);
        if (length * AudioTrack.Channels * sizeof(float) > AudioFiles.MaxTrackBytes) throw new IOException("Seamless Repeat exceeds the 256 MiB track limit.");
        var result = new float[checked((int)(length * AudioTrack.Channels))];
        double correlation = settings.AdaptiveCrossfade ? Measure(frames, overlap,
            (frame, channel) => stereo[checked((int)(frame * AudioTrack.Channels + channel))], AudioTrack.Channels, cancel).Correlation : 1;
        for (int frame = 0; frame < period; frame++)
        {
            if ((frame & 32767) == 0) cancel.ThrowIfCancellationRequested();
            double incoming = frame < overlap ? (frame + 1d) / (overlap + 1d) : 1, outgoing = 1 - incoming;
            double normalization = settings.AdaptiveCrossfade ? Math.Sqrt(outgoing * outgoing + incoming * incoming + 2 * correlation * outgoing * incoming) : 1;
            for (int channel = 0; channel < AudioTrack.Channels; channel++)
            {
                float value = frame < overlap
                    ? (float)((stereo[(period + frame) * AudioTrack.Channels + channel] * outgoing + stereo[frame * AudioTrack.Channels + channel] * incoming) / normalization)
                    : stereo[frame * AudioTrack.Channels + channel];
                if (!float.IsFinite(value)) throw new IOException("The crossfade exceeds the supported audio range.");
                result[frame * AudioTrack.Channels + channel] = value;
            }
        }
        for (int copy = 1; copy < settings.RepeatCount; copy++)
        { cancel.ThrowIfCancellationRequested(); Array.Copy(result, 0, result, checked(copy * period * AudioTrack.Channels), period * AudioTrack.Channels); }
        cancel.ThrowIfCancellationRequested(); return result;
    }

    sealed class Reader(TrackState state)
    {
        readonly long frames = state.Frames;
        readonly long[] ends = Ends(state.Segments);
        static long[] Ends(AudioSegment[] segments)
        { long total = 0; return segments.Select(segment => total = checked(total + segment.Length)).ToArray(); }
        public double Sample(long global, int channel)
        {
            long local = global - state.Offset;
            if (local < 0 || local >= frames) return 0;
            int index = Array.BinarySearch(ends, local + 1); if (index < 0) index = ~index;
            long source = state.Segments[index].SourceStart + local - (index == 0 ? 0 : ends[index - 1]);
            double gain = state.Gain; foreach (var fade in state.Fades) gain *= fade.At(local);
            return state.GetSample(source, channel) * gain;
        }
    }

    public static Task<EffectPreviewResult> PrepareAsync(TrackState[] states, IReadOnlySet<AudioTrack> targets,
        long start, long end, SeamlessRepeatSettings settings, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(states); ArgumentNullException.ThrowIfNull(targets); Validate(settings, AudioTrack.Rate);
        cancel.ThrowIfCancellationRequested();
        if (start < -SelectionStore.MaxTimelineFrame || end > SelectionStore.MaxTimelineFrame || end <= start) throw new ArgumentException("Choose a valid range for Seamless Repeat.");
        var frozen = states.ToArray(); var requested = new HashSet<AudioTrack>(targets, ReferenceEqualityComparer.Instance);
        if (frozen.Any(state => state == null || state.Source == null)) throw new ArgumentException("Seamless Repeat tracks cannot be null.");
        if (requested.Any(track => !frozen.Any(state => ReferenceEquals(state.Source, track)))) throw new ArgumentException("Targets must belong to this session.");
        return Task.Run(async () =>
        {
            long duration = end - start;
            var readers = frozen.Where(state => state.Enabled && requested.Contains(state.Source) && state.Offset < end && checked(state.Offset + state.Frames) > start).Select(state => new Reader(state)).ToArray();
            var seam = readers.Length == 0 ? new SeamlessRepeatInfo(RequestedOverlap(duration, AudioTrack.Rate, settings), 0, 0)
                : ChooseSeam(duration, AudioTrack.Rate, settings, (frame, channel) => readers[channel / AudioTrack.Channels].Sample(start + frame, channel % AudioTrack.Channels), readers.Length * AudioTrack.Channels, cancel);
            long step = duration - seam.OverlapFrames, length = settings.LoopEndToStart ? checked(settings.RepeatCount * step) : checked(duration + (settings.RepeatCount - 1L) * step);
            var cues = Enumerable.Range(0, settings.RepeatCount).Select(copy => checked(start + copy * step)).Append(checked(start + length)).ToArray();
            string detail = settings.RepeatCount == 1 && (!settings.LoopEndToStart || seam.OverlapFrames == 0) ? "Seamless Repeat: 1 repetition, no joins."
                : "Seamless Repeat: " + settings.RepeatCount + (settings.RepeatCount == 1 ? " repetition, " : " repetitions, ") + (seam.OverlapFrames * 1000d / AudioTrack.Rate).ToString("0.###", CultureInfo.InvariantCulture)
                    + (settings.LoopEndToStart ? " ms overlap including end-to-start; " : " ms overlap; ")
                    + (length * 1000d / AudioTrack.Rate).ToString("0.###", CultureInfo.InvariantCulture) + " ms result.";
            bool unchanged = settings.RepeatCount == 1 && (!settings.LoopEndToStart || seam.OverlapFrames == 0);
            return await MultiplyEffect.PrepareRepeatedAsync(frozen, requested, start, end, length,
                (audio, token) => Render(audio, settings, seam.OverlapFrames, token), cues, unchanged, true, detail, cancel, allowContraction: settings.LoopEndToStart).ConfigureAwait(false);
        }, cancel);
    }
}
