using NAudio.Wave;

namespace OrchardSound;

public sealed partial class EditorForm
{
    long? GetNormalAudibleFrame()
    {
        if (scrubbing || playing == null || playbackBuffer == null || output == null) return null;
        if (output is IWavePosition devicePosition)
        {
            try
            {
                long bytes = devicePosition.GetPosition();
                int blockAlign = devicePosition.OutputWaveFormat.BlockAlign;
                if (bytes >= 0 && blockAlign > 0)
                {
                    // Device bytes count frames actually played. The buffer's
                    // history includes queued loop changes and silent underruns.
                    return playbackBuffer.TryGetAudiblePosition(bytes / blockAlign, out long heard) ? heard : null;
                }
            }
            catch (Exception) { }
        }
        // Some output implementations cannot report a device position. This
        // fallback is callback consumption, so it can lead the audible sound.
        return playbackBuffer.Position;
    }

    void RefineLoopEndOnRestart()
    {
        if (!refineEnd.Checked || busy || !canvas.Tracks.Any(t => t.Frames > 0)) return;
        long? tap = GetNormalAudibleFrame() ?? canvas.LastCursorFrame;
        if (!tap.HasValue) return;
        var range = canvas.Range;
        long start = Math.Max(range.Min, canvas.SelectionStart ?? range.Min);
        if (start >= range.Max) return;
        long end = canvas.SelectionEnd.HasValue
            ? (long)Math.Round(canvas.SelectionEnd.Value * .75 + tap.Value * .25)
            : tap.Value;
        end = Math.Clamp(end, start + 1, range.Max);
        if (canvas.SelectionEnd == end) return;
        Remember();
        canvas.SelectionEnd = end;
        canvas.Invalidate();
        if (effectWindow != null) { InvalidateEffectPreview(); _ = RequestEffectPreview(); }
    }
}
