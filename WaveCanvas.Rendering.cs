using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace OrchardSound;

public sealed partial class WaveCanvas
{
    sealed class WaveRenderKey
    {
        readonly Rectangle bounds;
        readonly long min, max, offset, frames;
        readonly int dpi, index, color;
        readonly AudioViewMode viewMode;
        readonly long spectrumRevision;
        readonly bool magnify, beats, kicks, enabled, splitChannels;
        readonly float gain;
        readonly AudioTrack? track;
        readonly float[]? samples;
        readonly PeakPyramid? peaks;
        readonly Fade[] fades;
        readonly AudioSegment[] segments;
        readonly AudioPatch[] patches;
        readonly double? bpm;
        readonly long[] beatFrames, kickFrames;

        public WaveRenderKey(WaveCanvas canvas, Rectangle bounds, long min, long max, AudioTrack? track, int index)
        {
            this.bounds = bounds; this.min = min; this.max = max; this.index = index;
            int pane = bounds.Top == canvas.WaveBounds(0).Top ? 0 : index + 1;
            viewMode = canvas.GetPaneViewMode(pane);
            splitChannels = canvas.IsSplitPane(pane);
            spectrumRevision = viewMode == AudioViewMode.Spectrogram ? canvas.spectralCache?.Revision ?? 0 : 0;
            dpi = canvas.DeviceDpi; magnify = canvas.AutoMagnify; beats = canvas.ShowBeats; kicks = canvas.ShowKicks;
            color = WaveformBaseColor(pane, index).ToArgb(); this.track = track;
            samples = track?.Samples; peaks = track?.Peaks; offset = track?.Offset ?? 0; frames = track?.Frames ?? 0;
            gain = track?.Gain ?? 0; enabled = track?.Enabled ?? true;
            var snapshot = track?.Snapshot(); fades = snapshot?.Fades ?? []; segments = snapshot?.Segments ?? []; patches = snapshot?.Patches ?? [];
            bpm = track?.Rhythm?.Bpm; beatFrames = track?.Rhythm?.BeatFrames.ToArray() ?? []; kickFrames = track?.Rhythm?.KickFrames.ToArray() ?? [];
        }

        public bool Matches(WaveRenderKey other) => bounds == other.bounds && min == other.min && max == other.max
            && dpi == other.dpi && index == other.index && color == other.color && magnify == other.magnify
            && beats == other.beats && kicks == other.kicks && splitChannels == other.splitChannels && offset == other.offset && frames == other.frames
            && gain.Equals(other.gain) && enabled == other.enabled && viewMode == other.viewMode && spectrumRevision == other.spectrumRevision && ReferenceEquals(track, other.track)
            && ReferenceEquals(samples, other.samples) && ReferenceEquals(peaks, other.peaks)
            && fades.SequenceEqual(other.fades) && segments.SequenceEqual(other.segments) && patches.SequenceEqual(other.patches)
            && bpm == other.bpm && beatFrames.SequenceEqual(other.beatFrames) && kickFrames.SequenceEqual(other.kickFrames);
    }

    sealed record WaveRenderEntry(WaveRenderKey Key, Bitmap Image, RectangleF Destination);
    readonly Dictionary<int, WaveRenderEntry> renderCache = [];
    long renderCacheBuilds;

    public void ResetRenderCache()
    {
        foreach (var entry in renderCache.Values) entry.Image.Dispose();
        renderCache.Clear();
        ResetZoomContextCache();
        ResetNestedPrecisionCaches();
    }

    WaveRenderEntry CreateWaveLayer(WaveRenderKey key, Rectangle bounds, Action<Graphics> paint)
    {
        float scale = DpiScale;
        int left = (int)Math.Floor((bounds.Left - AmplitudeGutter) * scale), top = (int)Math.Floor(bounds.Top * scale);
        int right = (int)Math.Ceiling((bounds.Right + AmplitudeGutter) * scale);
        // GDI+ includes the grid line endpoint at bounds.Bottom. Keep that
        // physical row even when the logical rectangle ends on a pixel.
        int bottom = (int)Math.Ceiling(bounds.Bottom * scale) + 1;
        var image = new Bitmap(Math.Max(1, right - left), Math.Max(1, bottom - top), PixelFormat.Format32bppPArgb);
        try
        {
            image.SetResolution(DeviceDpi, DeviceDpi);
            using var layer = Graphics.FromImage(image);
            layer.Clear(background);
            layer.ScaleTransform(scale, scale);
            // Preserve the original fractional pixel phase at high DPI.
            layer.TranslateTransform(-left / scale, -top / scale);
            paint(layer);
        }
        catch { image.Dispose(); throw; }
        return new(key, image, new RectangleF(left / scale, top / scale, image.Width / scale, image.Height / scale));
    }

    static void DrawWaveLayer(Graphics graphics, WaveRenderEntry entry)
    {
        var state = graphics.Save();
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor; graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.DrawImage(entry.Image, entry.Destination, new RectangleF(0, 0, entry.Image.Width, entry.Image.Height), GraphicsUnit.Pixel);
        graphics.Restore(state);
    }

    void DrawWave(Graphics graphics, Rectangle bounds, long min, long max, AudioTrack? track, int index)
    {
        foreach (int stalePane in renderCache.Keys.Where(pane => pane > Slots.Count).ToArray())
        { renderCache[stalePane].Image.Dispose(); renderCache.Remove(stalePane); }
        PruneZoomContextCache();
        if (!NestedPrecisionVisible) ResetNestedPrecisionCaches();
        int paneIndex = bounds.Top == WaveBounds(0).Top ? 0 : index + 1;
        var key = new WaveRenderKey(this, bounds, min, max, DisplayTrack(track), index);
        if (!renderCache.TryGetValue(paneIndex, out var entry) || !entry.Key.Matches(key))
        {
            var next = CreateWaveLayer(key, bounds, layer => DrawWaveBase(layer, bounds, min, max, track, index));
            entry?.Image.Dispose(); entry = next;
            renderCache[paneIndex] = entry; renderCacheBuilds++;
        }
        DrawWaveLayer(graphics, entry);
        DrawWaveOverlays(graphics, bounds, min, max, track, index);
    }
}
