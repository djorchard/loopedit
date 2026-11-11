using System.ComponentModel;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace OrchardSound;

public sealed partial class WaveCanvas
{
    const int SpectrogramAxisLabelHeight = 14;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public AudioViewMode PrecisionViewMode { get; set; }
    SpectrogramCache? spectralCache;
    public AudioViewMode GetPaneViewMode(int pane) => pane == 0 ? PrecisionViewMode : Slots[pane - 1].ViewMode;
    public readonly record struct NoteTick(int MidiNote, double Frequency, float Y, string Label);
    public IReadOnlyList<NoteTick> GetNoteTicks(int pane)
    {
        if (IsSplitPane(pane)) return GetChannelLaneBounds(pane).SelectMany(CreateNoteTicks).ToArray();
        return CreateNoteTicks(WaveBounds(pane));
    }
    public IReadOnlyList<NoteTick> GetNoteTicks(int pane, int channel)
    {
        if (channel < 0 || channel >= AudioTrack.Channels) throw new ArgumentOutOfRangeException(nameof(channel));
        var lanes = GetChannelLaneBounds(pane);
        return CreateNoteTicks(lanes.Count == 1 ? lanes[0] : lanes[channel]);
    }
    IReadOnlyList<NoteTick> CreateNoteTicks(Rectangle bounds)
    {
        float spacing = (bounds.Height - 1) / (float)(SpectrogramData.HighestNote - SpectrogramData.LowestNote);
        const int endpointStride = SpectrogramData.HighestNote - SpectrogramData.LowestNote;
        int stride = new[] { 1, 2, 3, 6, 12, 24, 36, 60 }.FirstOrDefault(s => s * spacing >= 19, endpointStride);
        return Enumerable.Range(SpectrogramData.LowestNote, SpectrogramData.HighestNote - SpectrogramData.LowestNote + 1)
            .Where(note => note == SpectrogramData.LowestNote || note == SpectrogramData.HighestNote || stride < endpointStride && note % stride == 0)
            .Select(note => new NoteTick(note, SpectrogramData.NoteFrequency(note), (float)SpectrogramNoteY(bounds, note), SpectrogramData.NoteName(note))).ToArray();
    }

    static double SpectrogramNoteY(Rectangle lane, int note) => lane.Top + (SpectrogramData.HighestNote - note)
        * ((lane.Height - 1) / (double)(SpectrogramData.HighestNote - SpectrogramData.LowestNote));

    static int SpectrogramLabelTop(Rectangle lane, double noteY, int height)
        => Math.Clamp((int)Math.Round(noteY) - height / 2, lane.Top, Math.Max(lane.Top, lane.Bottom - height));
    void PrepareSpectrogramCache()
    {
        spectralCache ??= new SpectrogramCache(() =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(() => { if (!IsDisposed) Invalidate(); }); } catch (InvalidOperationException) { }
        });
        var active = new List<(AudioTrack Track, int Channel)>();
        void Include(AudioTrack track, bool split)
        { if (split) { active.Add((track, 0)); active.Add((track, 1)); } else active.Add((track, -1)); }
        if (PrecisionViewMode == AudioViewMode.Spectrogram && DisplayTrack(Slots[Math.Clamp(hoveredSlot, 0, Slots.Count - 1)].Track) is AudioTrack precision) Include(precision, IsSplitPane(0));
        foreach (var slot in Slots.Where(slot => slot.ViewMode == AudioViewMode.Spectrogram))
            if (DisplayTrack(slot.Track) is AudioTrack track) Include(track, slot.SplitChannels);
        spectralCache.KeepOnlyChannels(active);
    }
    static Color SpectrumColor(int intensity, bool enabled)
    {
        var stops = new[] { AppTheme.Surface, Color.FromArgb(28, 72, 110), Color.FromArgb(23, 139, 150), AppTheme.Accent, Color.FromArgb(246, 190, 117) };
        float position = Math.Clamp(intensity, 0, 255) / 255f * 4; int index = Math.Min(3, (int)position); float blend = position - index;
        int Component(int a, int b) => (int)Math.Round(a + (b - a) * blend);
        var color = Color.FromArgb(Component(stops[index].R, stops[index + 1].R), Component(stops[index].G, stops[index + 1].G), Component(stops[index].B, stops[index + 1].B));
        return enabled ? color : Color.FromArgb((color.R + AppTheme.Surface.R) / 2, (color.G + AppTheme.Surface.G) / 2, (color.B + AppTheme.Surface.B) / 2);
    }
    void DrawSpectrogramBase(Graphics graphics, Rectangle bounds, long min, long max, AudioTrack? track, int pane)
    {
        using (var fill = new SolidBrush(panel)) graphics.FillRectangle(fill, bounds);
        foreach (var lane in WaveLanes(pane, bounds)) DrawSpectrogramLane(graphics, lane.Bounds, min, max, track, lane.Channel);
        DrawChannelLaneLabels(graphics, bounds, pane);
    }
    void DrawSpectrogramLane(Graphics graphics, Rectangle bounds, long min, long max, AudioTrack? track, int channel)
    {
        using (var fill = new SolidBrush(panel)) graphics.FillRectangle(fill, bounds);
        if (track != null)
        {
            var data = spectralCache!.GetChannel(track, channel, out string? error);
            if (data == null) Label(graphics, error == null ? "Preparing spectrogram..." : "Spectrogram unavailable", small, AppTheme.Muted, bounds, true);
            else
            {
                int width = Math.Max(1, Math.Min(bounds.Width, 2048)), height = SpectrogramData.Bands;
                var pixels = new byte[width * height * 4];
                var colors = Enumerable.Range(0, 256).Select(level => SpectrumColor(level, track.Enabled)).ToArray();
                double adjustment = track.Gain > 0 ? 20 * Math.Log10(track.Gain) * 255 / 96 : -255;
                for (int x = 0; x < width; x++)
                {
                    long from = min + (long)((max - min) * (x / (double)width)) - track.Offset;
                    long to = min + (long)((max - min) * ((x + 1d) / width)) - track.Offset;
                    int first = Math.Clamp((int)Math.Floor(from / (double)SpectrogramData.HopFrames), 0, data.Columns - 1);
                    int last = Math.Clamp((int)Math.Floor(Math.Max(from, to - 1) / (double)SpectrogramData.HopFrames), first, data.Columns - 1);
                    bool inside = to > 0 && from < track.Frames;
                    for (int band = 0; band < height; band++)
                    {
                        int level = 0;
                        if (inside) for (int column = first; column <= last; column++) level = Math.Max(level, data[column, band]);
                        var color = colors[inside && level > 0 ? Math.Clamp((int)Math.Round(level + adjustment), 0, 255) : 0];
                        int index = ((height - 1 - band) * width + x) * 4;
                        pixels[index] = color.B; pixels[index + 1] = color.G; pixels[index + 2] = color.R; pixels[index + 3] = 255;
                    }
                }
                using var image = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                var memory = image.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try { Marshal.Copy(pixels, 0, memory.Scan0, pixels.Length); } finally { image.UnlockBits(memory); }
                var state = graphics.Save(); graphics.InterpolationMode = InterpolationMode.NearestNeighbor; graphics.PixelOffsetMode = PixelOffsetMode.Half;
                graphics.DrawImage(image, bounds); graphics.Restore(state);
            }
        }
        using var grid = new Pen(Color.FromArgb(85, AppTheme.Border));
        int divisions = Math.Max(4, bounds.Width / 40);
        for (int column = 1; column < divisions; column++) { float x = bounds.Left + bounds.Width * column / (float)divisions; graphics.DrawLine(grid, x, bounds.Top, x, bounds.Bottom); }
        float pitchSpacing = (bounds.Height - 1) / (float)(SpectrogramData.HighestNote - SpectrogramData.LowestNote);
        int minorStep = pitchSpacing >= 3 ? 1 : pitchSpacing >= 1.5 ? 3 : 12;
        using (var minor = new Pen(Color.FromArgb(35, AppTheme.Border)))
            for (int note = SpectrogramData.LowestNote; note <= SpectrogramData.HighestNote; note += minorStep)
            { float y = bounds.Top + (SpectrogramData.HighestNote - note) * pitchSpacing; graphics.DrawLine(minor, bounds.Left, y, bounds.Right, y); }
        foreach (var tick in CreateNoteTicks(bounds))
        {
            graphics.DrawLine(grid, bounds.Left, tick.Y, bounds.Right, tick.Y);
            int y = SpectrogramLabelTop(bounds, tick.Y, SpectrogramAxisLabelHeight);
            DrawAxisLabelPair(graphics, tick.Label, AppTheme.Muted, bounds, y);
        }
    }
}
