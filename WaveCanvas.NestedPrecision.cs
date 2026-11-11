using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace OrchardSound;

public sealed partial class WaveCanvas
{
    public static readonly Color NestedPrecisionWaveformColor = Color.FromArgb(181, 126, 238);
    bool precisionMagnifierEnabled = true, nestedPointerOverPrecision, pointerInNestedPrecision, pressedNestedPrecision, nestedAnchorReady;
    long nestedCenter;
    (long Min, long Max)? anchoredNestedRange;
    WaveRenderEntry? nestedContextCache, nestedViewCache;
    long nestedContextBuilds, nestedViewBuilds;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool PrecisionMagnifierEnabled
    {
        get => precisionMagnifierEnabled;
        set
        {
            if (precisionMagnifierEnabled == value) return;
            precisionMagnifierEnabled = value; ResetNestedPrecisionAnchor(); ResetNestedPrecisionCaches(); Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool NestedPrecisionAvailable => AllowsNestedPrecision(precisionMagnifierEnabled, ToDevice(GetPaneBounds(0)).Height, Magnification);
    static bool AllowsNestedPrecision(bool enabled, int deviceHeight, double zoom) => enabled && deviceHeight > 300 && zoom < 100;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool NestedPrecisionVisible => NestedPrecisionAvailable && (nestedPointerOverPrecision || Capture && pressedNestedPrecision);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public (long Min, long Max) NestedPrecisionRange
    {
        get
        {
            if (pointerInNestedPrecision && anchoredNestedRange.HasValue) return anchoredNestedRange.Value;
            var range = LensRange(); long length = range.Max - range.Min;
            long span = Math.Clamp(length / 10, 1, length);
            long center = nestedAnchorReady ? nestedCenter : range.Min + length / 2;
            long first = Math.Clamp(center - span / 2, range.Min, range.Max - span);
            var result = (first, first + span);
            if (pointerInNestedPrecision) anchoredNestedRange = result;
            return result;
        }
    }
    (long Min, long Max) PrecisionPointerRange => pointerInNestedPrecision && NestedPrecisionAvailable ? NestedPrecisionRange : PrecisionBasePointerRange;

    Rectangle NestedPrecisionBoundsCore()
    {
        var wave = WaveBounds(0); return new(wave.Left, wave.Top, wave.Width, Math.Max(1, wave.Height / 3));
    }
    public Rectangle GetNestedPrecisionBounds() => NestedPrecisionVisible ? NestedPrecisionBoundsCore() : Rectangle.Empty;
    public IReadOnlyList<Rectangle> GetNestedChannelLaneBounds()
        => NestedPrecisionVisible ? WaveLanes(0, NestedPrecisionBoundsCore()).Select(lane => lane.Bounds).ToArray() : [];
    public static Color NestedPrecisionAmplitudeColor(float level) => ShadeWaveformColor(NestedPrecisionWaveformColor, level);
    public RectangleF GetNestedPrecisionContextBounds()
    {
        if (!NestedPrecisionVisible) return RectangleF.Empty;
        var wave = WaveBounds(0); var range = LensRange(); var nested = NestedPrecisionRange;
        float left = X(nested.Min, wave, range.Min, range.Max), right = X(nested.Max, wave, range.Min, range.Max);
        return new(left, wave.Top, Math.Max(0, right - left), wave.Height);
    }
    public Rectangle GetNestedBoundaryHandleBounds(bool start)
        => NestedPrecisionVisible ? BoundaryHandleBounds(start, NestedPrecisionBoundsCore(), NestedPrecisionRange) : Rectangle.Empty;

    void ResetNestedPrecisionAnchor()
    { nestedAnchorReady = false; pointerInNestedPrecision = false; pressedNestedPrecision = false; anchoredNestedRange = null; }
    void ResetNestedMappingRange() => anchoredNestedRange = null;

    void UpdateNestedPrecisionPointer(double logicalX, double logicalY)
    {
        var wave = WaveBounds(0); var point = new PointF((float)logicalX, (float)logicalY);
        nestedPointerOverPrecision = wave.Contains((int)Math.Floor(logicalX), (int)Math.Floor(logicalY));
        if (!NestedPrecisionAvailable) { pointerInNestedPrecision = false; nestedAnchorReady = false; anchoredNestedRange = null; return; }
        if (Capture && pressedNestedPrecision) { pointerInNestedPrecision = true; return; }
        if (!nestedPointerOverPrecision) { pointerInNestedPrecision = false; nestedAnchorReady = false; anchoredNestedRange = null; return; }
        bool inside = NestedPrecisionBoundsCore().Contains((int)Math.Floor(point.X), (int)Math.Floor(point.Y));
        if (!nestedAnchorReady || !inside)
        {
            anchoredNestedRange = null;
            var range = PrecisionBasePointerRange;
            nestedCenter = range.Min + (long)Math.Round(Math.Clamp((logicalX - wave.Left) / wave.Width, 0, 1) * (range.Max - range.Min));
            nestedAnchorReady = true;
        }
        pointerInNestedPrecision = inside;
    }

    (bool Start, int Pane)? NestedBoundaryAt(Point point)
    {
        if (!HasSelection) return null;
        var wave = GetNestedPrecisionBounds(); if (!wave.Contains(point)) return null;
        foreach (bool start in new[] { true, false })
        {
            var handle = GetNestedBoundaryHandleBounds(start); if (handle.IsEmpty) continue;
            handle.Inflate(4, 4); if (handle.Contains(point)) return (start, 0);
        }
        var range = NestedPrecisionRange; var selection = SelectionBounds;
        float left = Math.Abs(point.X - X(selection.Start, wave, range.Min, range.Max));
        float right = Math.Abs(point.X - X(selection.End, wave, range.Min, range.Max));
        return Math.Min(left, right) <= 4 ? (left <= right, 0) : null;
    }

    void ResetNestedPrecisionCaches()
    {
        nestedContextCache?.Image.Dispose(); nestedContextCache = null;
        nestedViewCache?.Image.Dispose(); nestedViewCache = null;
    }

    void DrawNestedPrecisionContext(Graphics graphics, Rectangle wave, long min, long max, AudioTrack? track, int index)
    {
        if (!NestedPrecisionVisible) { ResetNestedPrecisionCaches(); return; }
        var bounds = GetNestedPrecisionContextBounds(); if (bounds.IsEmpty) return;
        var state = graphics.Save(); graphics.SetClip(bounds, CombineMode.Intersect);
        if (PrecisionViewMode == AudioViewMode.Waveform)
        {
            var key = new WaveRenderKey(this, wave, min, max, DisplayTrack(track), index);
            if (nestedContextCache == null || !nestedContextCache.Key.Matches(key))
            {
                var next = CreateWaveLayer(key, wave, layer => DrawWaveBaseColored(layer, wave, min, max, track, index, NestedPrecisionWaveformColor));
                nestedContextCache?.Image.Dispose(); nestedContextCache = next; nestedContextBuilds++;
            }
            DrawWaveLayer(graphics, nestedContextCache);
        }
        else { nestedContextCache?.Image.Dispose(); nestedContextCache = null; }
        using (var fill = new SolidBrush(Color.FromArgb(18, 165, 111, 236))) graphics.FillRectangle(fill, bounds);
        graphics.Restore(state);
        float stroke = 1 / DpiScale;
        using var border = new Pen(Color.FromArgb(165, 192, 144, 250), stroke);
        graphics.DrawRectangle(border, bounds.Left, bounds.Top + stroke / 2, Math.Max(0, bounds.Width - stroke), Math.Max(0, bounds.Height - stroke));
    }

    void DrawNestedPrecision(Graphics graphics)
    {
        if (!NestedPrecisionVisible) { ResetNestedPrecisionCaches(); return; }
        var bounds = NestedPrecisionBoundsCore(); var range = NestedPrecisionRange;
        int index = Math.Clamp(hoveredSlot, 0, Slots.Count - 1); var track = Slots[index].Track;
        var key = new WaveRenderKey(this, bounds, range.Min, range.Max, DisplayTrack(track), index);
        if (nestedViewCache == null || !nestedViewCache.Key.Matches(key))
        {
            var next = CreateWaveLayer(key, bounds, layer => DrawWaveBaseColored(layer, bounds, range.Min, range.Max, track, index, NestedPrecisionWaveformColor));
            nestedViewCache?.Image.Dispose(); nestedViewCache = next; nestedViewBuilds++;
        }
        DrawWaveLayer(graphics, nestedViewCache);
        using (var fill = new SolidBrush(Color.FromArgb(220, panel))) graphics.FillRectangle(fill, bounds.Right - 41, bounds.Top + 3, 37, 17);
        Label(graphics, "10x", gridFont, Color.FromArgb(211, 176, 255), new(bounds.Right - 41, bounds.Top + 3, 37, 17), true);
        DrawWaveOverlaysCore(graphics, bounds, range.Min, range.Max, track, index, true);
        using var edge = new Pen(Color.FromArgb(181, 126, 238), 1 / DpiScale);
        graphics.DrawRectangle(edge, bounds.Left, bounds.Top, Math.Max(0, bounds.Width - 1 / DpiScale), Math.Max(0, bounds.Height - 1 / DpiScale));
    }
}
