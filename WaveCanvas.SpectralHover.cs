using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace OrchardSound;

public sealed partial class WaveCanvas
{
    public readonly record struct SpectralHoverTarget(Rectangle Lane, Rectangle NoteBounds, int Channel, int MidiNote);
    public static int SpectralHoverMidiNote(Rectangle lane, double y)
    {
        double progress = (y - lane.Top) / Math.Max(1, lane.Height - 1);
        return Math.Clamp((int)Math.Round(SpectrogramData.HighestNote - progress * (SpectrogramData.HighestNote - SpectrogramData.LowestNote)), SpectrogramData.LowestNote, SpectrogramData.HighestNote);
    }
    public static Rectangle SpectralHoverNoteBounds(Rectangle lane, int note)
    {
        if (lane.Width <= 0 || lane.Height <= 0) return Rectangle.Empty;
        note = Math.Clamp(note, SpectrogramData.LowestNote, SpectrogramData.HighestNote);
        double spacing = (lane.Height - 1) / (double)(SpectrogramData.HighestNote - SpectrogramData.LowestNote);
        double center = SpectrogramNoteY(lane, note);
        if (spacing < 1) return new(lane.Left, Math.Clamp((int)Math.Round(center), lane.Top, lane.Bottom - 1), lane.Width, 1);
        int top = Math.Max(lane.Top, (int)Math.Floor(center - spacing / 2)), bottom = Math.Min(lane.Bottom, (int)Math.Ceiling(center + spacing / 2) + 1);
        return new(lane.Left, top, lane.Width, Math.Max(1, bottom - top));
    }
    public static Color SpectralHoverColor(Color original, int intensity, double taper = 1)
    {
        int level = Math.Clamp(intensity, 0, 255);
        double amount = level == 0 ? 0 : Math.Clamp(double.IsFinite(taper) ? taper : 0, 0, 1);
        int White(int component) => (int)Math.Round(component + (level - component) * amount);
        return Color.FromArgb(original.A, White(original.R), White(original.G), White(original.B));
    }
    SpectralHoverTarget? FindSpectralNoteHover(int pane, Rectangle bounds, Point point)
    {
        if (GetPaneViewMode(pane) != AudioViewMode.Spectrogram || !bounds.Contains(point)) return null;
        foreach (var lane in WaveLanes(pane, bounds))
        {
            if (!lane.Bounds.Contains(point)) continue;
            int note = SpectralHoverMidiNote(lane.Bounds, point.Y);
            return new(lane.Bounds, SpectralHoverNoteBounds(lane.Bounds, note), lane.Channel, note);
        }
        return null;
    }
    public SpectralHoverTarget? GetSpectralNoteHover(int pane, Point point, bool nested = false)
    {
        if (pane < 0 || pane > Slots.Count || nested && pane != 0) return null;
        if (!nested && pane == 0 && NestedPrecisionVisible && GetNestedPrecisionBounds().Contains(point)) return null;
        return FindSpectralNoteHover(pane, nested ? GetNestedPrecisionBounds() : WaveBounds(pane), point);
    }

    SpectralHoverTarget? DrawSpectralNoteHover(Graphics graphics, Rectangle bounds, long min, long max, AudioTrack? track, int index, bool nested)
    {
        if ((!pointerOverWave && !nestedPointerOverPrecision) || max <= min || DisplayTrack(track) is not AudioTrack displayed || displayed.Frames <= 0) return null;
        int pane = nested || bounds.Top == WaveBounds(0).Top ? 0 : index + 1;
        if (!nested && pane == 0 && NestedPrecisionVisible && GetNestedPrecisionBounds().Contains(previousRawPointer)) return null;
        var target = FindSpectralNoteHover(pane, bounds, previousRawPointer);
        if (!target.HasValue) return null;
        var hover = target.Value;
        var data = spectralCache?.GetChannel(displayed, hover.Channel, out _);
        if (data == null) return hover;
        int width = Math.Max(1, Math.Min(bounds.Width, 2048)), height = hover.NoteBounds.Height;
        var pixels = new byte[checked(width * height * 4)];
        double adjustment = displayed.Gain > 0 ? 20 * Math.Log10(displayed.Gain) * 255 / 96 : -255;
        double spacing = (hover.Lane.Height - 1) / (double)(SpectrogramData.HighestNote - SpectrogramData.LowestNote);
        double center = SpectrogramNoteY(hover.Lane, hover.MidiNote);
        for (int x = 0; x < width; x++)
        {
            long from = min + (long)((max - min) * x / (double)width) - displayed.Offset;
            long to = min + (long)((max - min) * (x + 1d) / width) - displayed.Offset;
            if (to <= 0 || from >= displayed.Frames) continue;
            int first = Math.Clamp((int)Math.Floor(from / (double)SpectrogramData.HopFrames), 0, data.Columns - 1);
            int last = Math.Clamp((int)Math.Floor(Math.Max(from, to - 1) / (double)SpectrogramData.HopFrames), first, data.Columns - 1);
            for (int y = 0; y < height; y++)
            {
                double rowY = hover.NoteBounds.Top + y;
                double note = SpectrogramData.HighestNote - (rowY - hover.Lane.Top) / Math.Max(1, hover.Lane.Height - 1) * (SpectrogramData.HighestNote - SpectrogramData.LowestNote);
                int band = Math.Clamp((int)((note - SpectrogramData.LowestNote) / (SpectrogramData.HighestNote - SpectrogramData.LowestNote) * SpectrogramData.Bands), 0, SpectrogramData.Bands - 1);
                int level = 0; for (int column = first; column <= last; column++) level = Math.Max(level, data[column, band]);
                if (level == 0) continue;
                level = Math.Clamp((int)Math.Round(level + adjustment), 0, 255);
                if (level == 0) continue;
                double taper = spacing < 1 ? 1 : Math.Clamp(1 - Math.Abs(rowY - center) / Math.Max(.5, spacing / 2), 0, 1);
                int alpha = (int)Math.Round(taper * (displayed.Enabled ? 1 : .5) * 255);
                int at = (y * width + x) * 4; pixels[at] = pixels[at + 1] = pixels[at + 2] = (byte)level; pixels[at + 3] = (byte)alpha;
            }
        }
        using var image = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var memory = image.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { Marshal.Copy(pixels, 0, memory.Scan0, pixels.Length); } finally { image.UnlockBits(memory); }
        var state = graphics.Save(); graphics.SetClip(hover.NoteBounds, CombineMode.Intersect);
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor; graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.DrawImage(image, hover.NoteBounds); graphics.Restore(state);
        return hover;
    }

    void DrawSpectralNoteHoverLabels(Graphics graphics, SpectralHoverTarget hover)
    {
        var lane = hover.Lane;
        double noteY = SpectrogramNoteY(lane, hover.MidiNote);
        int height = Math.Min(18, lane.Height);
        int top = SpectrogramLabelTop(lane, noteY, height);
        const int gap = 6, width = 36;
        var left = new Rectangle(lane.Left - gap - width, top, width, height);
        var right = new Rectangle(lane.Right + gap, top, width, height);

        // The base layer contains the normal axis labels. Clear each label that
        // touches a hover label so partial glyphs cannot show around its edges.
        using var clear = new SolidBrush(background);
        foreach (var tick in CreateNoteTicks(lane))
        {
            int tickTop = SpectrogramLabelTop(lane, tick.Y, SpectrogramAxisLabelHeight);
            if (tickTop >= top + height || tickTop + SpectrogramAxisLabelHeight <= top) continue;
            graphics.FillRectangle(clear, lane.Left - AmplitudeGutter, tickTop, AmplitudeGutter - 7, SpectrogramAxisLabelHeight);
            graphics.FillRectangle(clear, lane.Right + 7, tickTop, AmplitudeGutter - 7, SpectrogramAxisLabelHeight);
        }

        string label = SpectrogramData.NoteName(hover.MidiNote);
        using var fill = new SolidBrush(AppTheme.Elevated);
        using var outline = new Pen(AppTheme.Border);
        using var guide = new Pen(AppTheme.Muted);
        void DrawLabel(Rectangle bounds)
        {
            graphics.FillRectangle(fill, bounds);
            graphics.DrawRectangle(outline, bounds.Left, bounds.Top, bounds.Width - 1, bounds.Height - 1);
            Label(graphics, label, gridFont, AppTheme.Text, bounds, true);
        }
        float labelY = top + (height - 1) / 2f;
        graphics.DrawLine(guide, left.Right - 1, labelY, lane.Left, (float)noteY);
        graphics.DrawLine(guide, lane.Right, (float)noteY, right.Left, labelY);
        DrawLabel(left);
        DrawLabel(right);
    }
}
