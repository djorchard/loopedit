namespace OrchardSound;

public sealed partial class WaveCanvas
{
    public bool ZoomToSelection(double contextFraction = .1)
    {
        if (!HasSelection || !Tracks.Any(track => track.Frames > 0)) return false;
        var range = Range; var selection = SelectionBounds;
        long start = Math.Max(range.Min, selection.Start), end = Math.Min(range.Max, selection.End);
        if (end <= start) return false;
        double fraction = double.IsFinite(contextFraction) ? Math.Max(0, contextFraction) : .1;
        double padding = Math.Min((end - start) * fraction, (double)range.Max - range.Min);
        long first = (long)Math.Max(range.Min, Math.Floor(start - padding));
        long last = (long)Math.Min(range.Max, Math.Ceiling(end + padding));
        long span = Math.Max(1, last - first);
        double zoom = Math.Floor((range.Max - range.Min) / (double)span * 10) / 10;
        lensCenter = first + span / 2;
        OverrideFollowCursorForFit();
        ResetNestedPrecisionAnchor();
        Magnification = Math.Clamp(zoom, 1, 2048);
        Invalidate(); return true;
    }
}
