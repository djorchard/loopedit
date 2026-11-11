using System.Drawing.Drawing2D;

namespace OrchardSound;

public sealed partial class WaveCanvas
{
    public static readonly Color PrecisionWaveformColor = Color.FromArgb(86, 161, 238);
    WaveRenderEntry? zoomContextCache;
    int zoomContextSlot = -1;
    long zoomContextBuilds;

    static Color ShadeWaveformColor(Color color, float level)
    {
        float amount = MathF.Sqrt(Math.Clamp(float.IsFinite(level) ? level : 0, 0, 1));
        int Shade(int component) => (int)MathF.Round(component * .43f + amount * (component + (255 - component) * .5f - component * .43f));
        return Color.FromArgb(Shade(color.R), Shade(color.G), Shade(color.B));
    }

    static Color WaveformBaseColor(int pane, int trackIndex)
        => pane == 0 ? PrecisionWaveformColor : Colors[Math.Abs(trackIndex % Colors.Length)];

    public static Color PrecisionAmplitudeColor(float level) => ShadeWaveformColor(PrecisionWaveformColor, level);

    RectangleF ZoomContextBounds(Rectangle wave, long min, long max)
    {
        var lens = LensRange();
        float left = Math.Clamp(X(lens.Min, wave, min, max), wave.Left, wave.Right);
        float right = Math.Clamp(X(lens.Max, wave, min, max), wave.Left, wave.Right);
        return right > left ? new(left, wave.Top, right - left, wave.Height) : RectangleF.Empty;
    }

    public RectangleF GetZoomContextBounds(int pane)
    {
        if (pane <= 0 || pane > Slots.Count || pane - 1 != Math.Clamp(hoveredSlot, 0, Slots.Count - 1)
            || DisplayTrack(Slots[pane - 1].Track) is not AudioTrack track || track.Frames <= 0) return RectangleF.Empty;
        var range = Range;
        return ZoomContextBounds(WaveBounds(pane), range.Min, range.Max);
    }

    void ResetZoomContextCache()
    {
        zoomContextCache?.Image.Dispose(); zoomContextCache = null; zoomContextSlot = -1;
    }

    void PruneZoomContextCache()
    {
        if (zoomContextCache != null && (zoomContextSlot < 0 || zoomContextSlot >= Slots.Count
            || zoomContextSlot != hoveredSlot || Slots[zoomContextSlot].Track == null)) ResetZoomContextCache();
    }

    void DrawZoomContext(Graphics graphics, Rectangle wave, long min, long max, AudioTrack? track, int index)
    {
        if (wave.Top == WaveBounds(0).Top || index != Math.Clamp(hoveredSlot, 0, Slots.Count - 1)
            || DisplayTrack(track) is not AudioTrack displayed || displayed.Frames <= 0) return;
        RectangleF bounds = ZoomContextBounds(wave, min, max);
        if (bounds.IsEmpty) return;
        var state = graphics.Save();
        graphics.SetClip(bounds, CombineMode.Intersect);
        if (GetPaneViewMode(index + 1) == AudioViewMode.Waveform)
        {
            var key = new WaveRenderKey(this, wave, min, max, displayed, index);
            if (zoomContextCache == null || zoomContextSlot != index || !zoomContextCache.Key.Matches(key))
            {
                var next = CreateWaveLayer(key, wave, layer => DrawWaveBaseColored(layer, wave, min, max, track, index, PrecisionWaveformColor));
                ResetZoomContextCache(); zoomContextCache = next; zoomContextSlot = index; zoomContextBuilds++;
            }
            DrawWaveLayer(graphics, zoomContextCache);
        }
        else ResetZoomContextCache();
        using (var fill = new SolidBrush(Color.FromArgb(19, 64, 137, 226))) graphics.FillRectangle(fill, bounds);
        graphics.Restore(state);
        float stroke = 1 / DpiScale;
        using var border = new Pen(Color.FromArgb(155, 101, 174, 245), stroke);
        graphics.DrawRectangle(border, bounds.Left, bounds.Top + stroke / 2, Math.Max(0, bounds.Width - stroke), Math.Max(0, bounds.Height - stroke));
    }
}
