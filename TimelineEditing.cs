namespace OrchardSound;

public static class TimelineEditing
{
    public static long MapAfterDelete(long frame, long start, long end)
    {
        ValidateRange(start, end);
        return frame < start ? frame : frame < end ? start : frame - (end - start);
    }
    public static long MapAfterTrim(long frame, long start, long end)
    { ValidateRange(start, end); return Math.Clamp(frame, start, end) - start; }

    public static void Delete(IEnumerable<AudioTrack> tracks, long start, long end)
    {
        ValidateRange(start, end);
        foreach (var track in tracks)
        {
            long offset = track.Offset, frames = track.Frames;
            if (offset + frames <= start) continue;
            if (offset >= end) { track.Offset = MapAfterDelete(offset, start, end); continue; }
            long firstEnd = Math.Clamp(start - offset, 0, frames), secondStart = Math.Clamp(end - offset, 0, frames);
            Keep(track, [(0, firstEnd), (secondStart, frames)]);
            track.Offset = MapAfterDelete(offset, start, end);
        }
    }
    public static void Trim(IEnumerable<AudioTrack> tracks, long start, long end)
    {
        ValidateRange(start, end);
        foreach (var track in tracks)
        {
            long offset = track.Offset, frames = track.Frames;
            long from = Math.Clamp(start - offset, 0, frames), to = Math.Clamp(end - offset, 0, frames);
            Keep(track, [(from, to)]);
            track.Offset = to > from ? Math.Max(offset, start) - start : 0;
        }
    }
    static void ValidateRange(long start, long end)
    {
        if (end <= start) throw new ArgumentException("Choose a time range with a positive duration.");
        if (start < 0 && end > long.MaxValue + start) throw new ArgumentException("Time range is too long.");
    }
    static void Keep(AudioTrack track, (long Start, long End)[] ranges)
    {
        var segments = track.GetSegments(); var fades = track.Fades.ToArray();
        var retained = new List<AudioSegment>(); var retainedFades = new List<Fade>(); long destination = 0;
        foreach (var range in ranges)
        {
            if (range.End <= range.Start) continue;
            long position = 0;
            foreach (var segment in segments)
            {
                long from = Math.Max(range.Start, position), to = Math.Min(range.End, position + segment.Length);
                if (to > from) Append(retained, new(segment.SourceStart + from - position, to - from));
                position += segment.Length; if (position >= range.End) break;
            }
            foreach (var fade in fades)
            {
                long from = Math.Max(range.Start, fade.Start), to = Math.Min(range.End, fade.End);
                if (to > from)
                    retainedFades.Add(new(destination + from - range.Start, destination + to - range.Start, fade.FadeIn, fade.At(from), fade.At(to - 1)));
            }
            destination += range.End - range.Start;
        }
        track.Segments = retained.ToArray(); track.Fades = retainedFades;
    }
    static void Append(List<AudioSegment> segments, AudioSegment next)
    {
        if (segments.Count > 0 && segments[^1].SourceStart + segments[^1].Length == next.SourceStart)
            segments[^1] = segments[^1] with { Length = segments[^1].Length + next.Length };
        else segments.Add(next);
    }
}
