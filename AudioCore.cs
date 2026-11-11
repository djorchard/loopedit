using NAudio.Wave;
using System.Runtime.CompilerServices;

namespace OrchardSound;

public record Fade(long Start, long End, bool FadeIn, float? StartLevel = null, float? EndLevel = null)
{
    public float At(long frame)
    {
        if (frame < Start || frame >= End) return 1;
        var progress = End - Start <= 1 ? 1 : (float)(frame - Start) / (End - Start - 1);
        float from = StartLevel ?? (FadeIn ? 0 : 1), to = EndLevel ?? (FadeIn ? 1 : 0);
        return from + (to - from) * progress;
    }
}

public sealed record AudioSegment(long SourceStart, long Length);

public sealed record AudioPatch(long SourceStart, float[] Samples)
{
    public float[] Samples { get; } = Validate(Samples);
    [System.Text.Json.Serialization.JsonIgnore] public long Frames => Samples.LongLength / AudioTrack.Channels;
    static readonly ConditionalWeakTable<AudioPatch, PeakPyramid> peakCache = new();
    [System.Text.Json.Serialization.JsonIgnore] public PeakPyramid Peaks => peakCache.GetValue(this, patch => new(patch.Samples));
    static float[] Validate(float[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Length % AudioTrack.Channels != 0 || samples.Any(sample => !float.IsFinite(sample)))
            throw new ArgumentException("Audio patches require finite interleaved stereo samples.", nameof(samples));
        return samples;
    }
}

public sealed partial class AudioTrack
{
    public const int Rate = 48000;
    public const int Channels = 2;
    public const long MaxSourceFrames = AudioFiles.MaxTrackBytes / (Channels * sizeof(float));
    public required string Path { get; init; }
    public required float[] Samples { get; init; }
    public required PeakPyramid Peaks { get; init; }
    public long Offset { get; set; }
    public float Gain { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public List<Fade> Fades { get; set; } = [];
    public RhythmAnalysis? Rhythm { get; set; }
    AudioSegment[]? segments;
    AudioPatch[] patches = [];
    public AudioPatch[] Patches
    {
        get => patches.ToArray();
        set => patches = NormalizePatches(value ?? [], MaxSourceFrames);
    }
    public AudioSegment[]? Segments
    {
        get => segments?.ToArray();
        set
        {
            if (value != null)
                foreach (var segment in value)
                    if (segment == null || segment.SourceStart < 0 || segment.Length < 0 || segment.SourceStart > SourceFrames || segment.Length > SourceFrames - segment.SourceStart)
                        throw new ArgumentException("Audio segments must remain within the available source audio.", nameof(value));
            segments = value?.Where(segment => segment.Length > 0).ToArray();
        }
    }
    public long SourceFrames => Math.Max(Samples.LongLength / Channels, patches.Length == 0 ? 0 : patches[^1].SourceStart + patches[^1].Frames);
    public long Frames => segments == null ? SourceFrames : segments.Sum(segment => segment.Length);
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public AudioSegment[] GetSegments() => segments?.ToArray() ?? (SourceFrames > 0 ? [new(0, SourceFrames)] : []);
    public TrackState Snapshot() => new(this, Offset, Gain, Fades.ToArray(), GetSegments(), patches, Enabled);
    public float GetSample(long sourceFrame, int channel)
    {
        if (sourceFrame < 0 || sourceFrame >= SourceFrames || channel < 0 || channel >= Channels) throw new ArgumentOutOfRangeException(nameof(sourceFrame));
        return PatchedSample(Samples, patches, sourceFrame, channel);
    }
    internal static float PatchedSample(float[] source, AudioPatch[] patches, long frame, int channel)
    {
        int low = 0, high = patches.Length - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2; var patch = patches[middle];
            if (frame < patch.SourceStart) high = middle - 1;
            else if (frame - patch.SourceStart >= patch.Frames) low = middle + 1;
            else return patch.Samples[checked((int)((frame - patch.SourceStart) * Channels + channel))];
        }
        return frame >= 0 && frame < source.LongLength / Channels ? source[checked((int)(frame * Channels + channel))] : 0;
    }
    public float[] ReadVisibleSamples(long visibleStart, long visibleEnd)
    {
        if (visibleStart < 0 || visibleEnd < visibleStart || visibleEnd > Frames) throw new ArgumentOutOfRangeException(nameof(visibleStart));
        var output = new float[checked((int)((visibleEnd - visibleStart) * Channels))];
        long position = 0;
        foreach (var segment in GetSegments())
        {
            long from = Math.Max(visibleStart, position), to = Math.Min(visibleEnd, position + segment.Length);
            for (long frame = from; frame < to; frame++)
            {
                long original = segment.SourceStart + frame - position; int dest = checked((int)((frame - visibleStart) * Channels));
                output[dest] = GetSample(original, 0); output[dest + 1] = GetSample(original, 1);
            }
            position += segment.Length; if (position >= visibleEnd) break;
        }
        return output;
    }
    public void WriteVisibleSamples(long visibleStart, float[] renderedStereo)
    {
        ArgumentNullException.ThrowIfNull(renderedStereo);
        if (renderedStereo.Length % Channels != 0) throw new ArgumentException("Provide interleaved stereo samples.", nameof(renderedStereo));
        long count = renderedStereo.LongLength / Channels;
        if (visibleStart < 0 || visibleStart > Frames) throw new ArgumentOutOfRangeException(nameof(visibleStart));
        long oldFrames = Frames, visibleEnd = visibleStart + count, position = 0;
        long appendFrames = Math.Max(0, visibleEnd - oldFrames);
        if (appendFrames > MaxSourceFrames - SourceFrames) throw new IOException("The processed track exceeds the 256 MiB audio limit.");
        var oldSegments = GetSegments(); var overlays = patches.ToList();
        foreach (var segment in oldSegments)
        {
            long from = Math.Max(visibleStart, position), to = Math.Min(visibleEnd, position + segment.Length);
            if (to > from)
            {
                var samples = new float[checked((int)((to - from) * Channels))];
                Array.Copy(renderedStereo, checked((int)((from - visibleStart) * Channels)), samples, 0, samples.Length);
                overlays.Add(new(segment.SourceStart + from - position, samples));
            }
            position += segment.Length; if (position >= visibleEnd) break;
        }
        Patches = overlays.ToArray();
        if (appendFrames > 0)
        {
            var extra = new float[checked((int)(appendFrames * Channels))];
            Array.Copy(renderedStereo, checked((int)((oldFrames - visibleStart) * Channels)), extra, 0, extra.Length);
            var appended = AppendSourceSamples(extra); Segments = [.. oldSegments, appended];
        }
    }
    public AudioSegment AppendSourceSamples(float[] stereo)
    {
        ArgumentNullException.ThrowIfNull(stereo);
        if (stereo.Length % Channels != 0) throw new ArgumentException("Provide interleaved stereo samples.", nameof(stereo));
        long from = SourceFrames, frames = stereo.LongLength / Channels;
        if (frames > MaxSourceFrames - from) throw new IOException("The processed track exceeds the 256 MiB audio limit.");
        if (frames == 0) return new(from, 0);
        var retained = GetSegments();
        Patches = [.. patches, new AudioPatch(from, stereo)];
        segments = retained;
        return new(from, frames);
    }
    static AudioPatch[] NormalizePatches(IEnumerable<AudioPatch> input, long sourceFrames)
    {
        var normalized = new List<AudioPatch>();
        foreach (var patch in input)
        {
            if (patch == null || patch.SourceStart < 0 || patch.SourceStart > sourceFrames || patch.Frames > sourceFrames - patch.SourceStart)
                throw new ArgumentException("Audio patches exceed the supported source audio limit.", nameof(input));
            if (patch.Frames == 0) continue;
            long from = patch.SourceStart, to = patch.SourceStart + patch.Frames; var next = new List<AudioPatch>();
            foreach (var old in normalized)
            {
                long oldEnd = old.SourceStart + old.Frames;
                if (oldEnd <= from || old.SourceStart >= to) { next.Add(old); continue; }
                if (old.SourceStart < from) next.Add(SlicePatch(old, old.SourceStart, from));
                if (oldEnd > to) next.Add(SlicePatch(old, to, oldEnd));
            }
            next.Add(patch); normalized = next;
        }
        return normalized.OrderBy(patch => patch.SourceStart).ToArray();
    }
    static AudioPatch SlicePatch(AudioPatch patch, long from, long to)
    {
        var samples = new float[checked((int)((to - from) * Channels))];
        Array.Copy(patch.Samples, checked((int)((from - patch.SourceStart) * Channels)), samples, 0, samples.Length);
        return new(from, samples);
    }
    public long SourceFrameAt(long visibleFrame)
    {
        if (visibleFrame < 0) return -1;
        if (segments == null) return visibleFrame < SourceFrames ? visibleFrame : -1;
        foreach (var segment in segments)
        { if (visibleFrame < segment.Length) return segment.SourceStart + visibleFrame; visibleFrame -= segment.Length; }
        return -1;
    }
    public (float Min, float Max) QueryPeaks(long visibleStart, long visibleEnd)
        => QueryVisiblePeaks(visibleStart, visibleEnd, -1);
    public (float Min, float Max) QueryPeaks(long visibleStart, long visibleEnd, int channel)
    {
        if (channel < 0 || channel >= Channels) throw new ArgumentOutOfRangeException(nameof(channel));
        return QueryVisiblePeaks(visibleStart, visibleEnd, channel);
    }
    (float Min, float Max) QueryVisiblePeaks(long visibleStart, long visibleEnd, int channel)
    {
        if (segments == null) return QuerySourcePeaks(visibleStart, visibleEnd, channel);
        float min = 0, max = 0; long position = 0;
        foreach (var segment in segments)
        {
            long from = Math.Max(visibleStart, position), to = Math.Min(visibleEnd, position + segment.Length);
            if (to > from)
            {
                var peak = QuerySourcePeaks(segment.SourceStart + from - position, segment.SourceStart + to - position, channel);
                min = Math.Min(min, peak.Min); max = Math.Max(max, peak.Max);
            }
            position += segment.Length; if (position >= visibleEnd) break;
        }
        return (min, max);
    }
    (float Min, float Max) QuerySourcePeaks(long start, long end, int channel)
    {
        start = Math.Clamp(start, 0, SourceFrames); end = Math.Clamp(end, 0, SourceFrames);
        if (end <= start) return (0, 0);
        var sourcePeaks = channel < 0 ? Peaks : GetChannelPeaks(Samples, channel);
        if (patches.Length == 0) return sourcePeaks.Query(start, end);
        float min = 0, max = 0; long position = start;
        void Include((float Min, float Max) peak) { min = Math.Min(min, peak.Min); max = Math.Max(max, peak.Max); }
        foreach (var patch in patches)
        {
            long from = Math.Max(start, patch.SourceStart), to = Math.Min(end, patch.SourceStart + patch.Frames);
            if (to <= from) continue;
            if (from > position) Include(sourcePeaks.Query(position, from));
            var patchPeaks = channel < 0 ? patch.Peaks : GetChannelPeaks(patch.Samples, channel);
            Include(patchPeaks.Query(from - patch.SourceStart, to - patch.SourceStart)); position = to;
            if (position >= end) break;
        }
        if (position < end) Include(sourcePeaks.Query(position, end));
        return (min, max);
    }
    public long[] VisibleRhythmFrames(bool kicks)
    {
        if (Rhythm == null) return [];
        var markers = kicks ? Rhythm.KickFrames : Rhythm.BeatFrames;
        if (segments == null) return markers.Where(frame => frame >= 0 && frame < SourceFrames).Order().Distinct().ToArray();
        var visible = new List<long>(); long position = 0;
        foreach (var segment in segments)
        {
            foreach (long frame in markers)
                if (frame >= segment.SourceStart && frame - segment.SourceStart < segment.Length) visible.Add(position + frame - segment.SourceStart);
            position += segment.Length;
        }
        return visible.Order().Distinct().ToArray();
    }
}

public record TrackState(AudioTrack Source, long Offset, float Gain, Fade[] Fades, AudioSegment[]? Segments = null, AudioPatch[]? Patches = null, bool Enabled = true)
{
    public Fade[] Fades { get; init; } = Fades.ToArray();
    public AudioSegment[] Segments { get; init; } = (Segments ?? Source.GetSegments()).ToArray();
    public AudioPatch[] Patches { get; init; } = (Patches ?? Source.Patches).ToArray();
    public long Frames => Segments.Sum(segment => segment.Length);
    public float Envelope(long localFrame)
    {
        var value = Gain;
        foreach (var fade in Fades) value *= fade.At(localFrame);
        return value;
    }
    public float VisualEnvelope(long visibleFrame) => Envelope(visibleFrame);
    public float GetSample(long sourceFrame, int channel) => AudioTrack.PatchedSample(Source.Samples, Patches, sourceFrame, channel);
    public void ApplyToSource()
    { Source.Offset = Offset; Source.Gain = Gain; Source.Enabled = Enabled; Source.Fades = Fades.ToList(); Source.Patches = Patches; Source.Segments = Segments; }
}

public sealed class PeakPyramid
{
    readonly List<(float Min, float Max)[]> levels = [];
    readonly float[] samples;
    readonly int channel;
    public PeakPyramid(float[] samples) : this(samples, -1) { }
    public PeakPyramid(float[] samples, int channel)
    {
        if (channel < -1 || channel >= AudioTrack.Channels) throw new ArgumentOutOfRangeException(nameof(channel));
        this.samples = samples; this.channel = channel;
        int frames = samples.Length / 2;
        var first = new (float Min, float Max)[(frames + 127) / 128];
        for (int b = 0; b < first.Length; b++)
        {
            float min = 0, max = 0;
            for (int frame = b * 128; frame < Math.Min(frames, (b + 1) * 128); frame++)
            {
                int i = frame * 2;
                if (channel < 0) { min = Math.Min(min, Math.Min(samples[i], samples[i + 1])); max = Math.Max(max, Math.Max(samples[i], samples[i + 1])); }
                else { min = Math.Min(min, samples[i + channel]); max = Math.Max(max, samples[i + channel]); }
            }
            first[b] = (min, max);
        }
        levels.Add(first);
        while (levels[^1].Length > 1)
        {
            var prev = levels[^1];
            var next = new (float, float)[(prev.Length + 1) / 2];
            for (int i = 0; i < next.Length; i++)
            {
                var a = prev[i * 2]; var b = prev[Math.Min(prev.Length - 1, i * 2 + 1)];
                next[i] = (Math.Min(a.Min, b.Min), Math.Max(a.Max, b.Max));
            }
            levels.Add(next);
        }
    }
    public (float Min, float Max) Query(long start, long end)
    {
        start = Math.Clamp(start, 0, samples.LongLength / 2);
        end = Math.Clamp(end, 0, samples.LongLength / 2);
        if (end <= start) return (0, 0);
        float min = 0, max = 0;
        // Exact range decomposition preserves short transients at every zoom level.
        while (start < end)
        {
            if (start % 128 != 0 || end - start < 128)
            {
                int p = checked((int)(start++ * 2));
                if (channel < 0) { min = Math.Min(min, Math.Min(samples[p], samples[p + 1])); max = Math.Max(max, Math.Max(samples[p], samples[p + 1])); }
                else { min = Math.Min(min, samples[p + channel]); max = Math.Max(max, samples[p + channel]); }
                continue;
            }
            int level = 0; long size = 128;
            while (level + 1 < levels.Count && start % (size * 2) == 0 && end - start >= size * 2)
            { size *= 2; level++; }
            var peak = levels[level][(int)(start / size)];
            min = Math.Min(min, peak.Min); max = Math.Max(max, peak.Max); start += size;
        }
        return (min, max);
    }
}

public sealed class MixProvider : ISampleProvider
{
    readonly TrackState[] tracks;
    readonly long start, end, loopStart, loopEnd;
    readonly bool loop;
    long cursor;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(AudioTrack.Rate, 2);
    public long Position => Interlocked.Read(ref cursor);
    public long StartFrame => start;
    public long EndFrame => end;
    public long LoopStartFrame => loopStart;
    public long LoopEndFrame => loopEnd;
    public bool Looping => loop;
    public float Peak { get; private set; }
    public long ClippedSamples { get; private set; }
    public MixProvider(IEnumerable<TrackState> tracks, long start, long end, bool loop, long? initialFrame = null, long? loopStart = null, long? loopEnd = null)
    {
        if (end <= start) throw new ArgumentException("Choose a selection with a positive duration.");
        this.tracks = tracks.Where(t => t.Enabled).ToArray(); this.start = start; this.end = end; this.loop = loop;
        this.loopStart = loopStart ?? start; this.loopEnd = loopEnd ?? end;
        if (this.loopStart < start || this.loopEnd > end || this.loopEnd <= this.loopStart) throw new ArgumentException("The loop must fit inside the playback timeline.");
        cursor = initialFrame >= start && initialFrame <= end ? initialFrame.Value : start;
    }
    public void Seek(long frame) => Interlocked.Exchange(ref cursor, frame >= start && frame <= end ? frame : start);
    public int Read(float[] buffer, int offset, int count) => ReadCore(buffer, offset, count, null);
    internal int ReadWithPositions(float[] buffer, int offset, int count, long[] framePositions) => ReadCore(buffer, offset, count, framePositions);
    int ReadCore(float[] buffer, int offset, int count, long[]? framePositions)
    {
        count -= count % 2;
        int written = 0; float peak = 0;
        while (written < count)
        {
            if (loop && cursor >= loopEnd) cursor = loopStart;
            if (cursor >= end) break;
            int frames = (int)Math.Min((count - written) / 2, (loop ? loopEnd : end) - cursor);
            for (int i = offset + written; i < offset + written + frames * 2; i++) buffer[i] = 0;
            foreach (var t in tracks)
            {
                long visiblePosition = 0;
                foreach (var segment in t.Segments)
                {
                    long segmentGlobal = t.Offset + visiblePosition;
                    long from = Math.Max(cursor, segmentGlobal), to = Math.Min(cursor + frames, segmentGlobal + segment.Length);
                    for (long global = from; global < to; global++)
                    {
                        long local = global - t.Offset;
                        long sourceFrame = segment.SourceStart + global - segmentGlobal;
                        int dst = offset + written + (int)((global - cursor) * 2);
                        float gain = t.Envelope(local);
                        buffer[dst] += t.GetSample(sourceFrame, 0) * gain;
                        buffer[dst + 1] += t.GetSample(sourceFrame, 1) * gain;
                    }
                    visiblePosition += segment.Length;
                }
            }
            for (int i = offset + written; i < offset + written + frames * 2; i++)
            {
                peak = Math.Max(peak, Math.Abs(buffer[i]));
                if (Math.Abs(buffer[i]) > 1) ClippedSamples++;
                buffer[i] = Math.Clamp(buffer[i], -1, 1);
            }
            if (framePositions != null)
                for (int frame = 0; frame < frames; frame++) framePositions[written / 2 + frame] = cursor + frame + 1;
            cursor += frames; written += frames * 2;
        }
        Peak = peak; return written;
    }
}

public static class Editing
{
    public static float[] RenderSelection(AudioTrack track, long visibleStart, long visibleEnd)
    {
        ArgumentNullException.ThrowIfNull(track);
        var samples = track.ReadVisibleSamples(visibleStart, visibleEnd);
        var fades = track.Fades.ToArray();
        for (long frame = visibleStart; frame < visibleEnd; frame++)
        {
            float level = 1;
            foreach (var fade in fades) level *= fade.At(frame);
            int offset = checked((int)((frame - visibleStart) * AudioTrack.Channels));
            samples[offset] *= level; samples[offset + 1] *= level;
        }
        return samples;
    }
    public static void RemoveFadesInRange(AudioTrack track, long visibleStart, long visibleEnd)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (visibleStart < 0 || visibleEnd < visibleStart || visibleEnd > track.Frames) throw new ArgumentOutOfRangeException(nameof(visibleStart));
        if (visibleEnd == visibleStart) return;
        var retained = new List<Fade>();
        foreach (var fade in track.Fades)
        {
            if (fade.End <= visibleStart || fade.Start >= visibleEnd) { retained.Add(fade); continue; }
            void Keep(long from, long to)
            { if (to > from) retained.Add(new(from, to, fade.FadeIn, fade.At(from), fade.At(to - 1))); }
            Keep(fade.Start, Math.Min(fade.End, visibleStart));
            Keep(Math.Max(fade.Start, visibleEnd), fade.End);
        }
        track.Fades = retained;
    }
    public static void ApplyFade(IEnumerable<AudioTrack> tracks, long start, long end, bool fadeIn)
    {
        if (end <= start) throw new ArgumentException("Select a time range first.");
        foreach (var t in tracks)
            if (end > t.Offset && start < t.Offset + t.Frames)
                t.Fades.Add(new Fade(start - t.Offset, end - t.Offset, fadeIn));
    }
    public static string ExportStem(IEnumerable<AudioTrack> tracks, long start, long end)
    {
        var names = tracks.Where(t => t.Enabled && t.Gain > 0 && t.Offset < end && t.Offset + t.Frames > start)
            .Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase);
        string stem = string.Join(" + ", names);
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) stem = stem.Replace(c, '_');
        stem = stem.Trim().TrimEnd('.');
        return stem.Length == 0 ? "Clip" : stem[..Math.Min(stem.Length, 110)];
    }
}
