using System.ComponentModel;
using System.Globalization;

namespace OrchardSound;

public sealed class ChannelSlot
{
    public AudioViewMode ViewMode { get; set; }
    public bool SplitChannels { get; set; }
    internal bool HearHoveredNote { get; set; }
    AudioTrack? track;
    bool? placeholderEnabled;
    public AudioTrack? Track
    {
        get => track;
        set { bool? enabled = track?.Enabled ?? placeholderEnabled; track = value; if (track != null && enabled.HasValue) track.Enabled = enabled.Value; else if (track == null) placeholderEnabled = enabled; }
    }
    public bool Enabled { get => track?.Enabled ?? placeholderEnabled ?? true; set { placeholderEnabled = value; if (track != null) track.Enabled = value; } }
}

public sealed partial class WaveCanvas : Control
{
    public const int LeftGutter = 188, AmplitudeGutter = 56, SplitterHeight = 8, MinPaneHeight = 208;
    internal const int SpectrogramTrackMinPaneHeight = 232;
    public List<ChannelSlot> Slots { get; } = [new()];
    public IEnumerable<AudioTrack> Tracks => Slots.Where(s => s.Track != null).Select(s => s.Track!);
    public Dictionary<AudioTrack, AudioTrack> PreviewSources { get; } = [];
    public List<CuePoint> CuePoints { get; } = [];
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public long? SelectionStart { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public long? SelectionEnd { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public long? Playhead { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public long? CursorFrame { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool AutoMagnify { get; set; } = true;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool ShowBeats { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool ShowKicks { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool SmartSnap { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Magnification
    {
        get => magnification;
        set
        {
            double next = Math.Round(double.IsFinite(value) ? Math.Clamp(value, 1, 2048) : 12, 1);
            if (Math.Abs(next - magnification) < .001) return;
            magnification = next; ResetFollowCursorPointerRange(); ResetNestedMappingRange(); ZoomChanged?.Invoke(); Invalidate();
        }
    }
    public (long Start, long End) SelectionBounds => (SelectionStart ?? CommittedRange.Min, SelectionEnd ?? CommittedRange.Max);
    public bool HasSelection => (SelectionStart.HasValue || SelectionEnd.HasValue) && SelectionBounds.End > SelectionBounds.Start;
    public long? LastCursorFrame => CursorFrame ?? lastCursorFrame;
    public event Action? SelectionChanged;
    public event Action? SelectionChanging;
    public event Action? BeforeMove;
    public event Action? TrackMoved;
    public event Action? DragFinished;
    public event Action? BoundaryDragStarted;
    public event Action? PaneLayoutChanged;
    public event Action? ZoomChanged;
    public event Action<int>? OpenSlotRequested;
    public event Action<long>? CursorMoved;
    public static readonly Color[] Colors = [Color.FromArgb(98, 208, 188), Color.FromArgb(147, 172, 240), Color.FromArgb(225, 184, 126), Color.FromArgb(214, 149, 187)];
    public static Color AmplitudeColor(int trackIndex, float level)
        => ShadeWaveformColor(Colors[Math.Abs(trackIndex % Colors.Length)], level);
    readonly Color background = AppTheme.Window, panel = AppTheme.Surface, divider = AppTheme.Border;
    readonly Font small = new("Segoe UI", 12, FontStyle.Regular, GraphicsUnit.Pixel), title = new("Segoe UI Semibold", 13, FontStyle.Regular, GraphicsUnit.Pixel), gridFont = new("Segoe UI", 10, FontStyle.Regular, GraphicsUnit.Pixel);
    readonly List<int> heights = [];
    int viewportHeight = 640, pressedSlot = -1, hoveredSlot, resizeBoundary = -1, resizeOrigin, resizeInitialA, resizeInitialB;
    bool customHeights, moving, pressedLens;
    bool? draggingBoundary;
    double boundaryGrabOffset;
    Point pressed, lastPointer, lastRawPointer, previousRawPointer;
    Point? nudgedPointer;
    bool pointerInPrecision;
    bool mappedPointer, previousPrecision, relativeCapture, manualCursor, directPrecisionPointer;
    double precisionX, previousRawX, pressedAudioX, dragTravel;
    readonly PointerAcceleration pointerAcceleration = new();
    Func<long> pointerClock = System.Diagnostics.Stopwatch.GetTimestamp;
    long previousPointerTick;
    readonly System.Windows.Forms.Timer edgeScroll = new() { Interval = 30 };
    readonly System.Windows.Forms.Timer pointerVisibility = new() { Interval = 30 };
    Action hidePointer = System.Windows.Forms.Cursor.Hide, showPointer = System.Windows.Forms.Cursor.Show;
    bool pointerHidden, pointerOverWave;
    long lastPanTick, edgeStarted;
    long initialOffset, lensCenter;
    long? lastCursorFrame, pressedCursorFrame;
    double magnification = 12;
    (long Min, long Max) frozenRange;
    public float DpiScale => DeviceDpi / 96f;
    int LogicalWidth => (int)(Width / DpiScale);
    public (long Min, long Max) CommittedRange => CalculateRange(Tracks);
    static (long Min, long Max) CalculateRange(IEnumerable<AudioTrack> source)
    {
        var tracks = source.ToArray();
        long min = Math.Min(0, tracks.Select(t => t.Offset).DefaultIfEmpty(0).Min());
        long max = Math.Max(min + 1, tracks.Select(t => t.Offset + t.Frames).DefaultIfEmpty(AudioTrack.Rate).Max());
        return (min, max);
    }
    public (long Min, long Max) Range
    {
        get
        {
            if (moving || draggingBoundary.HasValue) return frozenRange;
            return CalculateRange(Tracks.Select(track => DisplayTrack(track)!));
        }
    }
    public WaveCanvas()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = background; TabStop = true; AccessibleName = "Multitrack waveforms"; FitHeight();
        edgeScroll.Tick += (_, _) => ScrollPrecisionEdge();
        pointerVisibility.Tick += (_, _) => UpdatePointerVisibility((ModifierKeys & Keys.Shift) != 0 && FindForm()?.ContainsFocus == true);
    }
    protected override void Dispose(bool disposing)
    { if (disposing) { RestorePointer(); pointerVisibility.Dispose(); edgeScroll.Dispose(); spectralCache?.Dispose(); ResetRenderCache(); small.Dispose(); title.Dispose(); gridFont.Dispose(); } base.Dispose(disposing); }
    public void SetViewportHeight(int deviceHeight)
    { viewportHeight = Math.Max(1, (int)(deviceHeight / DpiScale)); FitHeight(!customHeights); }
    public void FitHeight(bool reset = true)
    {
        int count = Slots.Count + 1;
        if (reset || heights.Count != count)
        {
            int size = Math.Max(MinPaneHeight, (viewportHeight - (count - 1) * SplitterHeight - 16) / count);
            heights.Clear();
            for (int pane = 0; pane < count; pane++) heights.Add(Math.Max(size, MinimumPaneHeight(pane)));
            customHeights = false;
        }
        else for (int pane = 0; pane < count; pane++) heights[pane] = Math.Max(heights[pane], MinimumPaneHeight(pane));
        Height = (int)((heights.Sum() + (count - 1) * SplitterHeight + 16) * DpiScale);
        PaneLayoutChanged?.Invoke(); Invalidate();
    }
    int MinimumPaneHeight(int pane) => pane > 0 && pane <= Slots.Count && Slots[pane - 1] is { Track: not null, ViewMode: AudioViewMode.Spectrogram }
        ? SpectrogramTrackMinPaneHeight : MinPaneHeight;
    public Rectangle GetPaneBounds(int index)
    {
        if (index < 0 || index >= heights.Count) throw new ArgumentOutOfRangeException(nameof(index));
        return new(8, 8 + heights.Take(index).Sum() + index * SplitterHeight, Math.Max(1, LogicalWidth - 16), heights[index]);
    }
    public Rectangle ToDevice(Rectangle b) => new((int)(b.X * DpiScale), (int)(b.Y * DpiScale), (int)(b.Width * DpiScale), (int)(b.Height * DpiScale));
    Rectangle WaveBounds(int pane)
    { var b = GetPaneBounds(pane); return new(LeftGutter + AmplitudeGutter, b.Top + 4, Math.Max(1, LogicalWidth - LeftGutter - 2 * AmplitudeGutter - 12), b.Height - 8); }
    public Rectangle GetWaveBounds(int pane) => WaveBounds(pane);
    public readonly record struct AmplitudeTick(double Percent, float Y, string Label, bool Major);
    AudioTrack? DisplayTrack(AudioTrack? track) => track != null && PreviewSources.TryGetValue(track, out var preview) ? preview : track;
    (float Min, float Max) DisplayAmplitudeBounds(AudioTrack? track)
        => DisplayAmplitudeBounds(track, -1);
    (float Min, float Max) DisplayAmplitudeBounds(AudioTrack? track, int channel)
    {
        if (!AutoMagnify || track == null) return (-1, 1);
        var peak = channel < 0 ? track.QueryPeaks(0, track.Frames) : track.QueryPeaks(0, track.Frames, channel);
        float min = peak.Min * track.Gain, max = peak.Max * track.Gain;
        return max - min > 1e-12f ? (min, max) : (-1, 1);
    }
    public (float Min, float Max) GetAmplitudeRange(int pane)
        => DisplayAmplitudeBounds(DisplayTrack(pane == 0 ? Slots[Math.Clamp(hoveredSlot, 0, Slots.Count - 1)].Track : Slots[pane - 1].Track));
    static float AmplitudeY(float value, Rectangle bounds, (float Min, float Max) range)
        => bounds.Top + Math.Clamp((range.Max - value) / (range.Max - range.Min), 0, 1) * (bounds.Height - 1);
    public IReadOnlyList<AmplitudeTick> GetAmplitudeTicks(int pane)
    {
        if (IsSplitPane(pane)) return Enumerable.Range(0, AudioTrack.Channels).SelectMany(channel => GetAmplitudeTicks(pane, channel)).ToArray();
        return CreateAmplitudeTicks(WaveBounds(pane), GetAmplitudeRange(pane));
    }
    IReadOnlyList<AmplitudeTick> CreateAmplitudeTicks(Rectangle bounds, (float Min, float Max) range)
    {
        double span = range.Max - range.Min;
        double desired = span * 16 / Math.Max(1, bounds.Height - 1), power = Math.Pow(10, Math.Floor(Math.Log10(desired)));
        double step = new[] { 1d, 2, 2.5, 5, 10 }.First(value => value * power >= desired) * power;
        int first = (int)Math.Ceiling(range.Min / step - 1e-6), last = (int)Math.Floor(range.Max / step + 1e-6);
        int labelEvery = Math.Max(1, (int)Math.Ceiling(28 * span / (step * Math.Max(1, bounds.Height - 1))));
        var values = Enumerable.Range(first, last - first + 1).Select(i => (Value: i * step, Major: i % labelEvery == 0)).ToList();
        values.RemoveAll(t => Math.Abs(t.Value - range.Min) < span * 1e-6 || Math.Abs(t.Value - range.Max) < span * 1e-6);
        values.Add((range.Min, true)); values.Add((range.Max, true));
        var ticks = new List<AmplitudeTick>();
        foreach (var item in values.OrderByDescending(t => t.Value))
        {
            double percent = item.Value * 100;
            float y = AmplitudeY((float)item.Value, bounds, range);
            bool edge = Math.Abs(item.Value - range.Min) < span * 1e-6 || Math.Abs(item.Value - range.Max) < span * 1e-6;
            bool major = item.Major && (edge || y >= bounds.Top + 24 && y <= bounds.Bottom - 25);
            string number = Math.Abs(percent) is > 0 and < .001 ? percent.ToString("0.##E+0", CultureInfo.InvariantCulture) : percent.ToString("0.###", CultureInfo.InvariantCulture);
            string label = major ? (percent > 0 ? "+" : "") + number + "%" : "";
            ticks.Add(new(percent, y, label, major));
        }
        return ticks;
    }
    public Rectangle GetBoundaryHandleBounds(bool start, int pane)
        => BoundaryHandleBounds(start, WaveBounds(pane), pane == 0 ? LensRange() : Range);
    Rectangle BoundaryHandleBounds(bool start, Rectangle wave, (long Min, long Max) range)
    {
        if (!HasSelection) return Rectangle.Empty;
        var bounds = SelectionBounds;
        if (wave.Width < 18 || wave.Height < 38) return Rectangle.Empty;
        long frame = start ? bounds.Start : bounds.End;
        if (frame < range.Min || frame > range.Max) return Rectangle.Empty;
        int x = (int)Math.Clamp(X(frame, wave, range.Min, range.Max), wave.Left, wave.Right - 1);
        bool close = Math.Abs(X(bounds.Start, wave, range.Min, range.Max) - X(bounds.End, wave, range.Min, range.Max)) < 38;
        int y = wave.Top + wave.Height / 2 - 18 + (close ? (start ? -19 : 19) : 0);
        return new(Math.Clamp(start ? x - 16 : x, wave.Left, wave.Right - 17), Math.Clamp(y, wave.Top, wave.Bottom - 36), 17, 36);
    }
    public void MoveSelectionBoundary(bool start, long frame, bool precision = false)
    {
        long? before = start ? SelectionStart : SelectionEnd;
        frame = SnapToBeat(frame, precision, manualCursor || (ModifierKeys & (Keys.Alt | Keys.Shift)) != 0);
        var range = Range;
        if (start) SelectionStart = Math.Clamp(frame, range.Min, Math.Max(range.Min, (SelectionEnd ?? range.Max) - 1));
        else SelectionEnd = Math.Clamp(frame, Math.Min(range.Max, (SelectionStart ?? range.Min) + 1), range.Max);
        CursorFrame = start ? SelectionStart : SelectionEnd; lastCursorFrame = CursorFrame; Invalidate();
        if (before != (start ? SelectionStart : SelectionEnd)) SelectionChanging?.Invoke();
    }
    (bool Start, int Pane)? BoundaryAt(Point point)
    {
        if (!HasSelection) return null;
        if (GetNestedPrecisionBounds().Contains(point)) return NestedBoundaryAt(point);
        for (int pane = 0; pane < heights.Count; pane++)
        {
            var wave = WaveBounds(pane); if (!wave.Contains(point)) continue;
            foreach (bool start in new[] { true, false })
            {
                var handle = GetBoundaryHandleBounds(start, pane); if (handle.IsEmpty) continue;
                var hit = handle; hit.Inflate(4, 4); if (hit.Contains(point)) return (start, pane);
            }
            var range = pane == 0 ? LensRange() : Range; var bounds = SelectionBounds;
            float left = Math.Abs(point.X - X(bounds.Start, wave, range.Min, range.Max));
            float right = Math.Abs(point.X - X(bounds.End, wave, range.Min, range.Max));
            if (Math.Min(left, right) <= 4) return (left <= right, pane);
        }
        return null;
    }
    public int SlotAt(Point devicePoint)
    { var p = LogicalPoint(devicePoint); for (int i = 0; i < Slots.Count; i++) if (GetPaneBounds(i + 1).Contains(p)) return i; return -1; }
    public void ResizeBoundary(int index, int delta)
    {
        if (index < 0 || index + 1 >= heights.Count) return;
        int minimumA = MinimumPaneHeight(index), minimumB = MinimumPaneHeight(index + 1);
        int total = Math.Max(heights[index] + heights[index + 1], minimumA + minimumB);
        int first = Math.Clamp(heights[index] + delta, minimumA, total - minimumB);
        heights[index] = first; heights[index + 1] = total - first; customHeights = true; FitHeight(false);
    }
    int SplitterAt(Point p)
    {
        for (int i = 0; i < heights.Count - 1; i++)
        { var b = GetPaneBounds(i); if (p.Y >= b.Bottom && p.Y < b.Bottom + SplitterHeight) return i; }
        return -1;
    }
    (long Min, long Max) LensRange()
    {
        var range = Range;
        long span = Math.Clamp((long)((range.Max - range.Min) / magnification), 1, range.Max - range.Min);
        long center = PrecisionViewCenter();
        bool following = followCursor && !followFitOverride && (CursorFrame ?? lastCursorFrame).HasValue;
        long min = following ? center - span / 2 : Math.Clamp(center - span / 2, range.Min, range.Max - span);
        return (min, min + span);
    }
    public (long Min, long Max) PrecisionViewRange => LensRange();
    public bool NudgeCursor(int direction, bool moveSystemPointer = true)
    {
        var current = CursorFrame ?? lastCursorFrame;
        if (direction == 0 || !current.HasValue || !Tracks.Any(t => t.Frames > 0)) return false;
        var view = FollowCursor ? Range : pointerInPrecision ? PrecisionPointerRange : Range; var wave = WaveBounds(0);
        long next = Math.Clamp(current.Value + Math.Sign(direction), view.Min, view.Max);
        if (next == current.Value) return false;
        return PlaceCursor(next, moveSystemPointer);
    }
    public bool JumpCursor(bool end, bool moveSystemPointer = true)
    {
        if (!Tracks.Any(t => t.Frames > 0)) return false;
        long frame = end ? Range.Max : Range.Min; lensCenter = frame; ResetNestedPrecisionAnchor();
        return PlaceCursor(frame, moveSystemPointer);
    }
    bool PlaceCursor(long next, bool moveSystemPointer)
    {
        ResetFollowCursorPointerRange();
        var view = pointerInPrecision ? PrecisionPointerRange : Range; var wave = WaveBounds(0);
        CursorFrame = lastCursorFrame = next; manualCursor = true; directPrecisionPointer = false;
        int pane = pointerInPrecision ? 0 : Math.Clamp(hoveredSlot + 1, 1, Slots.Count);
        var paneWave = WaveBounds(pane);
        double x = Math.Clamp(X(next, wave, view.Min, view.Max), wave.Left, wave.Right - 1);
        var devicePoint = new Point((int)Math.Round(x * DpiScale), (int)Math.Round(Math.Clamp(lastPointer.Y, paneWave.Top, paneWave.Bottom - 1) * DpiScale));
        var raw = LogicalPoint(devicePoint);
        precisionX = x; previousRawX = devicePoint.X / (double)DpiScale; mappedPointer = true; previousPrecision = pointerInPrecision; previousRawPointer = raw; lastPointer = raw;
        ResetPointerAcceleration();
        if (moveSystemPointer && !pointerInPrecision && IsHandleCreated && Visible && Focused)
        { nudgedPointer = devicePoint; System.Windows.Forms.Cursor.Position = PointToScreen(devicePoint); }
        UpdateEdgeScrolling(raw); CursorMoved?.Invoke(next); Invalidate(); return true;
    }
    public double PrecisePointerX => precisionX;
    public long PointerAudioFrame(bool precision) => FrameAt(precisionX, precision);
    public long ClampCursorToTrack(long frame, int pane)
    {
        var track = DisplayTrack(pane == 0 ? Slots[Math.Clamp(hoveredSlot, 0, Slots.Count - 1)].Track : Slots[Math.Clamp(pane - 1, 0, Slots.Count - 1)].Track);
        return track == null ? frame : Math.Clamp(frame, track.Offset, track.Offset + track.Frames);
    }
    public Point PrecisionPointer(Point logicalPoint, bool shift)
        => MapPointer(logicalPoint.X, logicalPoint.Y, shift, false, false);
    public Point PrecisionPointer(Point logicalPoint, bool shift, bool control)
        => MapPointer(logicalPoint.X, logicalPoint.Y, shift, control, false);
    Point PointerFromDevice(Point point, bool shift) => MapPointer(point.X / (double)DpiScale, point.Y / (double)DpiScale, shift, (ModifierKeys & Keys.Control) != 0, true);
    void ResetPointerAcceleration() { pointerAcceleration.Reset(); previousPointerTick = 0; }
    Point MapPointer(double rawX, double rawY, bool shift, bool control, bool timed)
    {
        Point logicalPoint = new((int)Math.Round(rawX), (int)Math.Round(rawY));
        bool precision = Capture ? pressedLens : GetPaneBounds(0).Contains(logicalPoint);
        BeginFollowCursorPointer(precision); UpdateNestedPrecisionPointer(rawX, rawY);
        directPrecisionPointer = precision;
        if (precision || control)
        {
            precisionX = Math.Clamp(rawX + (precision && Capture && draggingBoundary.HasValue ? boundaryGrabOffset : 0), WaveBounds(0).Left, WaveBounds(0).Right);
            mappedPointer = true; relativeCapture = false; previousPrecision = precision; ResetPointerAcceleration();
            previousRawX = rawX; previousRawPointer = logicalPoint;
            return new((int)Math.Round(precisionX), logicalPoint.Y);
        }
        double gain = shift ? .1 / Magnification : 1 / Math.Sqrt(Magnification / 2);
        if (timed && !shift && mappedPointer && precision == previousPrecision)
        {
            long now = pointerClock();
            double elapsed = previousPointerTick == 0 ? 0 : (now - previousPointerTick) / (double)System.Diagnostics.Stopwatch.Frequency;
            previousPointerTick = now; gain = pointerAcceleration.Update(gain, rawX - previousRawX, elapsed);
        }
        else ResetPointerAcceleration();
        if (Capture && pressedSlot >= 0 && mappedPointer) dragTravel += Math.Abs(rawX - previousRawX);
        if (!mappedPointer || precision != previousPrecision)
        { precisionX = rawX; mappedPointer = true; relativeCapture = false; }
        else if (timed || gain < 1 || (Capture && relativeCapture))
        {
            precisionX += (rawX - previousRawX) * gain;
            if (Capture) relativeCapture = true;
        }
        else if (rawX != previousRawX) precisionX = rawX;
        if (shift) precisionX = Math.Clamp(precisionX, WaveBounds(0).Left, WaveBounds(0).Right - 1);
        previousRawX = rawX; previousRawPointer = logicalPoint; previousPrecision = precision;
        return new((int)Math.Round(precisionX), logicalPoint.Y);
    }
    void FollowPrecisePointer(Point physicalPoint)
    {
        if (pointerInPrecision || Capture && pressedLens || !IsHandleCreated || !Visible || FindForm()?.ContainsFocus != true || resizeBoundary >= 0) return;
        bool fine = (ModifierKeys & Keys.Shift) != 0;
        if (!fine) return;
        var target = new Point((int)Math.Round(precisionX * DpiScale), physicalPoint.Y);
        if (target == physicalPoint) return;
        nudgedPointer = target; previousRawX = target.X / (double)DpiScale; previousRawPointer = LogicalPoint(target); lastRawPointer = previousRawPointer;
        ResetPointerAcceleration();
        System.Windows.Forms.Cursor.Position = PointToScreen(target);
    }
    public bool PanPrecisionAtEdge(int logicalX, double elapsedSeconds, double speed = 1)
    {
        if (!Tracks.Any(t => t.Frames > 0) || !double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0) return false;
        var wave = WaveBounds(0); const int edgeWidth = 24;
        double direction = logicalX < wave.Left + edgeWidth ? -Math.Clamp((wave.Left + edgeWidth - logicalX) / (double)edgeWidth, 0, 1)
            : logicalX > wave.Right - edgeWidth ? Math.Clamp((logicalX - wave.Right + edgeWidth) / (double)edgeWidth, 0, 1) : 0;
        if (direction == 0) return false;
        var view = LensRange(); var range = Range; long span = view.Max - view.Min;
        var activeTrack = DisplayTrack(Slots[Math.Clamp(hoveredSlot, 0, Slots.Count - 1)].Track);
        long allowedMin = range.Min, allowedMax = range.Max - span;
        if (activeTrack != null && activeTrack.Frames >= span)
        { allowedMin = Math.Max(allowedMin, activeTrack.Offset); allowedMax = Math.Min(allowedMax, activeTrack.Offset + activeTrack.Frames - span); }
        if (allowedMax < allowedMin) return false;
        long delta = (long)Math.Round(span * .45 * Math.Min(elapsedSeconds, 1) * direction * Math.Clamp(speed, 0, 1));
        if (delta == 0) delta = Math.Sign(direction);
        long start = Math.Clamp(view.Min + delta, allowedMin, allowedMax);
        if (start == view.Min) return false;
        lensCenter = start + span / 2; Invalidate(); return true;
    }
    void UpdateEdgeScrolling(Point raw)
    {
        lastRawPointer = raw;
        var wave = WaveBounds(0);
        bool active = !FollowCursor && (!Capture || pressedLens) && resizeBoundary < 0 && raw.Y >= wave.Top && raw.Y < wave.Bottom && raw.X >= wave.Left - 24 && raw.X <= wave.Right + 24
            && (raw.X < wave.Left + 24 || raw.X > wave.Right - 24) && Tracks.Any(t => t.Frames > 0);
        if (active)
        {
            Cursor = raw.X < wave.Left + 24 ? Cursors.PanWest : Cursors.PanEast;
            if (!edgeScroll.Enabled) { edgeStarted = Environment.TickCount64; lastPanTick = edgeStarted - 16; edgeScroll.Start(); ScrollPrecisionEdge(); }
        }
        else if (edgeScroll.Enabled) { edgeScroll.Stop(); Cursor = Cursors.Default; }
    }
    void ScrollPrecisionEdge()
    {
        long now = Environment.TickCount64; double elapsed = Math.Clamp((now - lastPanTick) / 1000d, 0, .1); lastPanTick = now;
        double ramp = Math.Clamp((now - edgeStarted) / 500d, 0, 1); ramp = ramp * ramp * (3 - 2 * ramp);
        if (!PanPrecisionAtEdge(lastRawPointer.X, elapsed, .12 + .88 * ramp)) return;
        if (Capture && draggingBoundary.HasValue) MoveSelectionBoundary(draggingBoundary.Value, FrameAt(precisionX, true), true);
        else { CursorFrame = ClampCursorToTrack(SnapToBeat(FrameAt(precisionX, true), true, manualCursor || (ModifierKeys & Keys.Alt) != 0), 0); lastCursorFrame = CursorFrame; }
        if (CursorFrame.HasValue) CursorMoved?.Invoke(CursorFrame.Value);
        Invalidate();
    }
    long FrameAt(double logicalX, bool lens)
    {
        var range = lens ? PrecisionPointerRange : Range; var b = WaveBounds(0);
        return range.Min + (long)Math.Round(Math.Clamp((logicalX - b.X) / (double)b.Width, 0, 1) * (range.Max - range.Min));
    }
    public long SnapToBeat(long frame, bool precision = false, bool bypass = false)
    {
        if (!SmartSnap || bypass || (ModifierKeys & (Keys.Control | Keys.Alt)) != 0 || Slots.Count == 0) return frame;
        var track = Slots[Math.Clamp(hoveredSlot, 0, Slots.Count - 1)].Track;
        var beats = track?.Rhythm == null ? null : track.VisibleRhythmFrames(false);
        var candidates = CuePoints.Select(c => c.Frame).Concat(beats?.Select(b => b + track!.Offset) ?? []);
        long best = frame; long distance = long.MaxValue;
        foreach (long candidate in candidates)
        {
            long delta = Math.Abs(candidate - frame);
            if (delta < distance) { distance = delta; best = candidate; }
        }
        var range = precision ? PrecisionPointerRange : Range;
        long tolerance = Math.Max(1, (long)((range.Max - range.Min) * 12d / WaveBounds(0).Width));
        return distance <= tolerance ? best : frame;
    }
    float X(long frame, Rectangle b, long min, long max) => b.X + (float)((frame - min) / (double)(max - min) * b.Width);
    void Label(Graphics g, string text, Font font, Color color, Rectangle b, bool centered = false)
    {
        using var brush = new SolidBrush(color);
        using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap, LineAlignment = StringAlignment.Center, Alignment = centered ? StringAlignment.Center : StringAlignment.Near };
        g.DrawString(text, font, brush, b, format);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var g = e.Graphics; g.ScaleTransform(DpiScale, DpiScale);
        PrepareSpectrogramCache();
        var range = Range; var zoom = LensRange(); var precision = GetPaneBounds(0);
        hoveredSlot = Math.Clamp(hoveredSlot, 0, Slots.Count - 1);
        if (g.IsVisible(GetPaneBounds(0)))
        { DrawWave(g, WaveBounds(0), zoom.Min, zoom.Max, Slots[hoveredSlot].Track, hoveredSlot); DrawNestedPrecision(g); }
        for (int i = 0; i < Slots.Count; i++)
        {
            if (!g.IsVisible(GetPaneBounds(i + 1))) continue;
            var b = WaveBounds(i + 1); DrawWave(g, b, range.Min, range.Max, Slots[i].Track, i);
            if (Slots[i].Track == null) Label(g, "Drop audio here", small, AppTheme.Muted, new(b.Left + 16, b.Top, b.Width - 32, b.Height), true);
        }
        using var pen = new Pen(divider); using var grip = new Pen(Color.FromArgb(78, 91, 107));
        for (int i = 0; i < heights.Count - 1; i++)
        {
            var b = GetPaneBounds(i); int y = b.Bottom + SplitterHeight / 2;
            g.DrawLine(pen, 16, y, LogicalWidth - 16, y); g.DrawLine(grip, LogicalWidth / 2 - 15, y, LogicalWidth / 2 + 15, y);
        }
    }
    void DrawWaveBase(Graphics g, Rectangle b, long min, long max, AudioTrack? track, int index)
        => DrawWaveBaseColored(g, b, min, max, track, index, WaveformBaseColor(b.Top == WaveBounds(0).Top ? 0 : index + 1, index));
    void DrawWaveBaseColored(Graphics g, Rectangle b, long min, long max, AudioTrack? track, int index, Color waveformColor)
    {
        track = DisplayTrack(track);
        int paneIndex = b.Top == WaveBounds(0).Top ? 0 : index + 1;
        if (GetPaneViewMode(paneIndex) == AudioViewMode.Spectrogram) { DrawSpectrogramBase(g, b, min, max, track, paneIndex); return; }
        using (var fill = new SolidBrush(panel)) g.FillRectangle(fill, b);
        foreach (var lane in WaveLanes(paneIndex, b)) DrawWaveformLaneBase(g, lane.Bounds, min, max, track, index, waveformColor, lane.Channel);
        DrawChannelLaneLabels(g, b, paneIndex);
    }
    void DrawWaveformLaneBase(Graphics g, Rectangle b, long min, long max, AudioTrack? track, int index, Color waveformColor, int channel)
    {
        var amplitudeRange = DisplayAmplitudeBounds(track, channel);
        var amplitudeTicks = CreateAmplitudeTicks(b, amplitudeRange);
        using (var fill = new SolidBrush(panel)) g.FillRectangle(fill, b);
        using (var grid = new Pen(Color.FromArgb(35, 45, 57)))
        {
            int divisions = Math.Max(4, b.Width / 40);
            for (int column = 1; column < divisions; column++) { float x = b.Left + b.Width * column / (float)divisions; grid.Color = column % 5 == 0 ? Color.FromArgb(43, 57, 73) : Color.FromArgb(35, 45, 57); g.DrawLine(grid, x, b.Top, x, b.Bottom); }
            foreach (var tick in amplitudeTicks)
            { grid.Color = tick.Percent == 0 ? divider : tick.Major ? Color.FromArgb(43, 57, 73) : Color.FromArgb(35, 45, 57); g.DrawLine(grid, b.Left, tick.Y, b.Right, tick.Y); }
        }
        var clip = g.Save(); g.SetClip(b);
        if (track != null)
        {
            var snapshot = track.Snapshot();
            int xStart = Math.Max(b.Left, (int)Math.Floor(X(track.Offset, b, min, max)));
            int xEnd = Math.Min(b.Right, (int)Math.Ceiling(X(track.Offset + track.Frames, b, min, max)));
            // One stable display scale per source, shared by its channel and precision view.
            using var pen = new Pen(waveformColor, 1);
            for (int x = xStart; x < xEnd; x++)
            {
                long a = min + (long)((x - b.Left) / (double)b.Width * (max - min)) - track.Offset;
                long z = min + (long)((x + 1 - b.Left) / (double)b.Width * (max - min)) - track.Offset;
                var peak = channel < 0 ? track.QueryPeaks(a, Math.Max(a + 1, z)) : track.QueryPeaks(a, Math.Max(a + 1, z), channel);
                float gain = track.Frames > 0 ? snapshot.Envelope(Math.Clamp((a + z) / 2, 0, track.Frames - 1)) : 0;
                // Brightness retains the audio level even when height is auto-magnified.
                var levelColor = ShadeWaveformColor(waveformColor, Math.Max(Math.Abs(peak.Max * gain), Math.Abs(peak.Min * gain)));
                pen.Color = track.Enabled ? levelColor : Color.FromArgb((levelColor.R + panel.R) / 2, (levelColor.G + panel.G) / 2, (levelColor.B + panel.B) / 2);
                g.DrawLine(pen, x, AmplitudeY(peak.Max * gain, b, amplitudeRange), x, AmplitudeY(peak.Min * gain, b, amplitudeRange));
            }
            if (ShowBeats && track.Rhythm != null) DrawMarkers(g, b, min, max, track, track.VisibleRhythmFrames(false), Color.FromArgb(95, 132, 179, 222), false);
            if (ShowKicks && track.Rhythm != null) DrawMarkers(g, b, min, max, track, track.VisibleRhythmFrames(true), Color.FromArgb(180, 239, 179, 95), true);
        }
        g.Restore(clip);
        foreach (var tick in amplitudeTicks.Where(t => t.Label.Length > 0))
        { int y = Math.Clamp((int)Math.Round(tick.Y) - 7, b.Top, b.Bottom - 14); DrawAxisLabelPair(g, tick.Label, tick.Percent == 0 ? AppTheme.Text : AppTheme.Muted, b, y); }
    }
    void DrawWaveOverlays(Graphics g, Rectangle b, long min, long max, AudioTrack? track, int index)
        => DrawWaveOverlaysCore(g, b, min, max, track, index, false);
    void DrawWaveOverlaysCore(Graphics g, Rectangle b, long min, long max, AudioTrack? track, int index, bool nested)
    {
        var clip = g.Save(); g.SetClip(b);
        DrawZoomContext(g, b, min, max, track, index);
        if (!nested && b.Top == WaveBounds(0).Top) DrawNestedPrecisionContext(g, b, min, max, track, index);
        var spectralHover = DrawSpectralNoteHover(g, b, min, max, track, index, nested);
        if (HasSelection)
        {
            var selection = SelectionBounds;
            float left = X(selection.Start, b, min, max), right = X(selection.End, b, min, max);
            using var fill = new SolidBrush(Color.FromArgb(26, 92, 211, 187)); g.FillRectangle(fill, left, b.Top, right - left, b.Height);
        }
        using (var cueInk = new SolidBrush(Color.FromArgb(193, 165, 234)))
        using (var cueLine = new Pen(Color.FromArgb(105, 193, 165, 234)))
            foreach (var cue in CuePoints.Where(c => c.Frame >= min && c.Frame <= max))
            { float x = Math.Clamp(X(cue.Frame, b, min, max), b.Left, b.Right - 1); g.DrawLine(cueLine, x, b.Bottom - 24, x, b.Bottom); g.FillPolygon(cueInk, new PointF[] { new(x - 4, b.Bottom - 1), new(x + 4, b.Bottom - 1), new(x, b.Bottom - 8) }); }
        if (SelectionStart.HasValue) DrawBoundary(g, b, min, max, SelectionStart.Value, Color.FromArgb(98, 208, 188), 2);
        if (SelectionEnd.HasValue) DrawBoundary(g, b, min, max, SelectionEnd.Value, Color.FromArgb(237, 184, 110), 2);
        if (Playhead.HasValue) DrawBoundary(g, b, min, max, Playhead.Value, Color.FromArgb(130, 218, 227, 238), 1);
        if (CursorFrame.HasValue)
        {
            var pointerRange = PrecisionPointerRange;
            bool atMouse = b.Top == WaveBounds(0).Top && directPrecisionPointer && min == pointerRange.Min && max == pointerRange.Max && CursorFrame.Value == FrameAt(precisionX, true);
            if (atMouse) { using var guide = new Pen(Color.White, 1); float x = (float)Math.Clamp(precisionX, b.Left, b.Right - 1); g.DrawLine(guide, x, b.Top, x, b.Bottom); }
            else DrawBoundary(g, b, min, max, CursorFrame.Value, Color.White, 1);
        }
        g.Restore(clip);
        if (spectralHover is { } hoveredNote) DrawSpectralNoteHoverLabels(g, hoveredNote);
        int pane = b.Top == WaveBounds(0).Top ? 0 : index + 1;
        if (HasSelection) foreach (bool start in new[] { true, false }) DrawSelectionHandle(g, nested ? GetNestedBoundaryHandleBounds(start) : GetBoundaryHandleBounds(start, pane), start);
    }
    void DrawSelectionHandle(Graphics g, Rectangle b, bool start)
    {
        if (b.IsEmpty) return;
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddPolygon(start ? new Point[] { new(b.Right, b.Top), new(b.Left + 6, b.Top), new(b.Left, b.Top + 6), new(b.Left, b.Bottom - 6), new(b.Left + 6, b.Bottom), new(b.Right, b.Bottom) } : new Point[] { new(b.Left, b.Top), new(b.Right - 6, b.Top), new(b.Right, b.Top + 6), new(b.Right, b.Bottom - 6), new(b.Right - 6, b.Bottom), new(b.Left, b.Bottom) });
        using var fill = new SolidBrush(start ? Color.FromArgb(18, 121, 148) : Color.FromArgb(150, 102, 37));
        using var edge = new Pen(start ? Color.FromArgb(67, 220, 239) : Color.FromArgb(255, 207, 127), 1.5f);
        g.FillPath(fill, path); g.DrawPath(edge, path);
        int x = start ? b.Right - 5 : b.Left + 5; g.DrawLine(edge, x, b.Top + 10, x, b.Bottom - 10);
    }
    void DrawBoundary(Graphics g, Rectangle b, long min, long max, long frame, Color color, float width)
    { if (frame < min || frame > max) return; float x = Math.Clamp(X(frame, b, min, max), b.Left, b.Right - 1); using var pen = new Pen(color, width); g.DrawLine(pen, x, b.Top, x, b.Bottom); }
    void DrawMarkers(Graphics g, Rectangle b, long min, long max, AudioTrack track, long[] frames, Color color, bool kick)
    {
        using var pen = new Pen(color, 1); using var brush = new SolidBrush(color);
        int index = Array.BinarySearch(frames, min - track.Offset); if (index < 0) index = ~index;
        int lastX = int.MinValue;
        for (; index < frames.Length; index++)
        {
            long frame = track.Offset + frames[index]; if (frame > max) break;
            int x = (int)X(frame, b, min, max); if (lastX != int.MinValue && x - lastX < 3) continue; lastX = x;
            if (kick) g.FillRectangle(brush, x - 2, b.Bottom - 9, 4, 8);
            else { g.DrawLine(pen, x, b.Top, x, b.Bottom); g.FillRectangle(brush, x - 1, b.Top, 3, 5); }
        }
    }
    public void SetCursorBoundary(bool start)
    {
        var frame = CursorFrame ?? lastCursorFrame;
        if (!frame.HasValue || !Tracks.Any()) return;
        frame = SnapToBeat(frame.Value, pointerInPrecision, manualCursor || (ModifierKeys & (Keys.Alt | Keys.Shift)) != 0);
        if (start) SelectionStart = frame.Value; else SelectionEnd = frame.Value;
        SelectionChanged?.Invoke(); Invalidate();
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); Focus();
        UpdateNestedPrecisionPointer(e.X / (double)DpiScale, e.Y / (double)DpiScale);
        Point raw = LogicalPoint(e.Location);
        bool nestedPress = GetNestedPrecisionBounds().Contains(raw);
        var selectionHandle = BoundaryAt(raw);
        if (selectionHandle.HasValue && e.Button == MouseButtons.Left)
        {
            frozenRange = Range; pressedLens = selectionHandle.Value.Pane == 0; pointerInPrecision = pressedLens; pressedNestedPrecision = pressedLens && nestedPress;
            var range = pressedLens ? PrecisionPointerRange : frozenRange;
            var wave = pressedNestedPrecision ? GetNestedPrecisionBounds() : WaveBounds(selectionHandle.Value.Pane);
            long frame = selectionHandle.Value.Start ? SelectionBounds.Start : SelectionBounds.End;
            precisionX = wave.Left + (frame - range.Min) / (double)Math.Max(1, range.Max - range.Min) * wave.Width;
            boundaryGrabOffset = precisionX - e.X / (double)DpiScale; CursorFrame = lastCursorFrame = frame;
            previousRawX = e.X / (double)DpiScale; previousRawPointer = raw; previousPrecision = pressedLens; mappedPointer = true;
            relativeCapture = false; moving = false; pressedSlot = -1; resizeBoundary = -1; dragTravel = 0; ResetPointerAcceleration();
            draggingBoundary = selectionHandle.Value.Start; lastPointer = new((int)Math.Round(precisionX), raw.Y);
            BoundaryDragStarted?.Invoke(); Capture = true; Cursor = Cursors.SizeWE; return;
        }
        long? exact = manualCursor && CursorFrame.HasValue && Math.Abs(e.X / (double)DpiScale - previousRawX) < .0001 ? CursorFrame : null;
        Point p = PointerFromDevice(e.Location, (ModifierKeys & Keys.Shift) != 0); lastPointer = p; UpdateEdgeScrolling(raw); int boundary = SplitterAt(p);
        if (boundary >= 0 && e.Button == MouseButtons.Left)
        {
            resizeBoundary = boundary; resizeOrigin = p.Y; resizeInitialA = heights[boundary]; resizeInitialB = heights[boundary + 1];
            Capture = true; CursorFrame = null; return;
        }
        int pane = -1; for (int i = 0; i < heights.Count; i++) if (WaveBounds(i).Contains(p)) { pane = i; break; }
        if (pane < 0) return;
        pressedLens = pane == 0; pointerInPrecision = pressedLens; pressedNestedPrecision = pressedLens && nestedPress;
        if (pane > 0 && Slots[pane - 1].Track != null) hoveredSlot = pane - 1;
        CursorFrame = exact ?? ClampCursorToTrack(SnapToBeat(FrameAt(precisionX, pressedLens), pressedLens, manualCursor || (ModifierKeys & (Keys.Alt | Keys.Shift)) != 0), pane);
        if (e.Button == MouseButtons.Right) { SetCursorBoundary(false); pressedNestedPrecision = false; return; }
        if (e.Button != MouseButtons.Left) return;
        pressed = p; pressedAudioX = precisionX; pressedCursorFrame = CursorFrame; dragTravel = 0; pressedSlot = pane - 1;
        if (pressedSlot >= 0 && Slots[pressedSlot].Track != null) initialOffset = Slots[pressedSlot].Track!.Offset;
        frozenRange = Range; Capture = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        UpdateNestedPrecisionPointer(e.X / (double)DpiScale, e.Y / (double)DpiScale);
        var physical = LogicalPoint(e.Location);
        int hoverPane = -1;
        for (int pane = 0; pane < heights.Count; pane++)
            if (WaveBounds(pane).Contains(physical)) { hoverPane = pane; break; }
        pointerOverWave = hoverPane >= 0;
        if (pointerOverWave) pointerVisibility.Start(); else pointerVisibility.Stop();
        UpdatePointerVisibility((ModifierKeys & Keys.Shift) != 0 && FindForm()?.ContainsFocus == true);
        if (nudgedPointer.HasValue)
        {
            bool positionedByKey = e.Location == nudgedPointer.Value; nudgedPointer = null; ResetPointerAcceleration();
            if (positionedByKey) { UpdateSpectralHoverPane(hoverPane); UpdateEdgeScrolling(LogicalPoint(e.Location)); Invalidate(); return; }
        }
        bool shift = (ModifierKeys & Keys.Shift) != 0;
        Point raw = LogicalPoint(e.Location); Point p = PointerFromDevice(e.Location, shift); lastPointer = p; manualCursor = shift;
        UpdateSpectralHoverPane(hoverPane);
        pointerInPrecision = Capture ? pressedLens : WaveBounds(0).Contains(p);
        FollowPrecisePointer(e.Location); UpdateEdgeScrolling(lastRawPointer = previousRawPointer);
        if (Capture && draggingBoundary.HasValue) { MoveSelectionBoundary(draggingBoundary.Value, FrameAt(precisionX, pressedLens), pressedLens); if (CursorFrame.HasValue) CursorMoved?.Invoke(CursorFrame.Value); return; }
        if (Capture && resizeBoundary >= 0)
        {
            int minimumA = MinimumPaneHeight(resizeBoundary), minimumB = MinimumPaneHeight(resizeBoundary + 1);
            int total = Math.Max(resizeInitialA + resizeInitialB, minimumA + minimumB);
            int a = Math.Clamp(resizeInitialA + p.Y - resizeOrigin, minimumA, total - minimumB);
            heights[resizeBoundary] = a; heights[resizeBoundary + 1] = total - a;
            customHeights = true; FitHeight(false); return;
        }
        if (Capture && pressedSlot >= 0 && Slots[pressedSlot].Track != null && (moving || dragTravel >= 5))
        {
            if (!moving) { BeforeMove?.Invoke(); moving = true; }
            Slots[pressedSlot].Track!.Offset = initialOffset + (long)Math.Round((precisionX - pressedAudioX) / WaveBounds(0).Width * (frozenRange.Max - frozenRange.Min));
            Cursor = Cursors.SizeWE; CursorFrame = FrameAt(precisionX, false); TrackMoved?.Invoke(); Invalidate(); return;
        }
        if (!Capture && SplitterAt(p) >= 0) { Cursor = Cursors.HSplit; CursorFrame = null; Invalidate(); return; }
        bool inWave = false;
        for (int i = 0; i < heights.Count; i++)
        {
            if (!WaveBounds(i).Contains(p) && !GetPaneBounds(i).Contains(p)) continue; inWave = true; pointerInPrecision = i == 0;
            if (i > 0) { if (Slots[i - 1].Track != null) hoveredSlot = i - 1; if (!Capture && !shift) lensCenter = FrameAt(precisionX, false); }
            CursorFrame = ClampCursorToTrack(SnapToBeat(FrameAt(precisionX, i == 0), i == 0, shift || (ModifierKeys & Keys.Alt) != 0), i);
            if (i > 0 && !Capture && !shift) lensCenter = CursorFrame.Value;
            lastCursorFrame = CursorFrame; CursorMoved?.Invoke(CursorFrame.Value); break;
        }
        if (!inWave) CursorFrame = null; if (!edgeScroll.Enabled) Cursor = BoundaryAt(raw).HasValue ? Cursors.SizeWE : Cursors.Default; Invalidate();
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e); if (e.Button != MouseButtons.Left || !Capture) return;
        Point p = PointerFromDevice(e.Location, (ModifierKeys & Keys.Shift) != 0);
        if (draggingBoundary.HasValue) { draggingBoundary = null; Capture = false; SelectionChanged?.Invoke(); return; }
        if (resizeBoundary >= 0) { resizeBoundary = -1; Capture = false; return; }
        long chosen = manualCursor && dragTravel == 0 && pressedCursorFrame.HasValue ? pressedCursorFrame.Value : ClampCursorToTrack(SnapToBeat(FrameAt(precisionX, pressedLens), pressedLens, manualCursor || (ModifierKeys & Keys.Alt) != 0), pressedLens ? 0 : Math.Max(1, pressedSlot + 1));
        bool nestedRelease = pressedNestedPrecision;
        bool moved = moving; moving = false; Capture = false;
        pointerInNestedPrecision = nestedRelease && NestedPrecisionAvailable;
        if (moved) DragFinished?.Invoke(); else { CursorFrame = chosen; SetCursorBoundary(true); }
        UpdateNestedPrecisionPointer(e.X / (double)DpiScale, e.Y / (double)DpiScale);
        pressedSlot = -1; Cursor = Cursors.Default; Invalidate();
    }
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture) { pressedNestedPrecision = false; edgeScroll.Stop(); relativeCapture = false; ResetPointerAcceleration(); if (moving) { moving = false; DragFinished?.Invoke(); } draggingBoundary = null; pressedSlot = -1; resizeBoundary = -1; Invalidate(); }
    }
    void UpdatePointerVisibility(bool shift)
    {
        bool hidden = shift && pointerOverWave && !IsDisposed;
        if (hidden == pointerHidden) return;
        if (hidden) hidePointer(); else showPointer();
        pointerHidden = hidden;
    }
    void RestorePointer()
    { pointerOverWave = false; nestedPointerOverPrecision = false; UpdateSpectralHoverPane(-1); if (!Capture) { pointerInNestedPrecision = false; nestedAnchorReady = false; ResetNestedMappingRange(); ResetFollowCursorPointerRange(); } pointerVisibility.Stop(); UpdatePointerVisibility(false); ResetPointerAcceleration(); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey) { ResetPointerAcceleration(); UpdatePointerVisibility(true); }
        if ((e.KeyCode is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey) && pointerOverWave && !pointerInPrecision)
        {
            lastPointer = MapPointer(previousRawX, previousRawPointer.Y, false, true, false);
            int pane = Math.Clamp(hoveredSlot + 1, 1, Slots.Count);
            CursorFrame = lastCursorFrame = ClampCursorToTrack(FrameAt(precisionX, false), pane);
            lensCenter = CursorFrame.Value; CursorMoved?.Invoke(CursorFrame.Value); Invalidate();
        }
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode is Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey) { ResetPointerAcceleration(); UpdatePointerVisibility(false); }
        if (e.KeyCode is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey) ResetPointerAcceleration();
    }
    protected override void OnLostFocus(EventArgs e)
    { RestorePointer(); base.OnLostFocus(e); }
    protected override void OnMouseLeave(EventArgs e) { RestorePointer(); base.OnMouseLeave(e); if (!Capture) { edgeScroll.Stop(); mappedPointer = false; Invalidate(); } }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        Magnification *= e.Delta > 0 ? 1.5 : 1 / 1.5;
        if (pointerInPrecision && CursorFrame.HasValue) { CursorFrame = SnapToBeat(FrameAt(precisionX, true), true, manualCursor || (ModifierKeys & Keys.Alt) != 0); lastCursorFrame = CursorFrame; CursorMoved?.Invoke(CursorFrame.Value); }
    }
    protected override void OnMouseDoubleClick(MouseEventArgs e)
    { base.OnMouseDoubleClick(e); int slot = SlotAt(e.Location); if (e.Button == MouseButtons.Left && slot >= 0 && Slots[slot].Track == null) OpenSlotRequested?.Invoke(slot); }
    Point LogicalPoint(Point point) => new((int)Math.Round(point.X / DpiScale), (int)Math.Round(point.Y / DpiScale));
}
