namespace OrchardSound;

public sealed partial class WaveCanvas
{
    const int ChannelLaneGap = 4;
    bool IsSplitPane(int pane)
    {
        int slot = pane == 0 ? Math.Clamp(hoveredSlot, 0, Slots.Count - 1) : pane - 1;
        return slot >= 0 && slot < Slots.Count && Slots[slot].SplitChannels;
    }

    (Rectangle Bounds, int Channel)[] WaveLanes(int pane, Rectangle wave)
    {
        if (!IsSplitPane(pane)) return [(wave, -1)];
        int height = Math.Max(1, (wave.Height - ChannelLaneGap) / 2);
        return [(new(wave.Left, wave.Top, wave.Width, height), 0),
            (new(wave.Left, wave.Top + height + ChannelLaneGap, wave.Width, Math.Max(1, wave.Height - height - ChannelLaneGap)), 1)];
    }

    public IReadOnlyList<Rectangle> GetChannelLaneBounds(int pane) => WaveLanes(pane, WaveBounds(pane)).Select(lane => lane.Bounds).ToArray();

    public (float Min, float Max) GetAmplitudeRange(int pane, int channel)
    {
        if (channel < 0 || channel >= AudioTrack.Channels) throw new ArgumentOutOfRangeException(nameof(channel));
        int slot = pane == 0 ? Math.Clamp(hoveredSlot, 0, Slots.Count - 1) : pane - 1;
        return DisplayAmplitudeBounds(DisplayTrack(Slots[slot].Track), channel);
    }

    public IReadOnlyList<AmplitudeTick> GetAmplitudeTicks(int pane, int channel)
    {
        if (channel < 0 || channel >= AudioTrack.Channels) throw new ArgumentOutOfRangeException(nameof(channel));
        var lanes = GetChannelLaneBounds(pane);
        return CreateAmplitudeTicks(lanes.Count == 1 ? lanes[0] : lanes[channel], GetAmplitudeRange(pane, channel));
    }

    void DrawChannelLaneLabels(Graphics graphics, Rectangle wave, int pane)
    {
        if (!IsSplitPane(pane)) return;
        var lanes = WaveLanes(pane, wave);
        using var dividerInk = new Pen(divider, 1);
        graphics.DrawLine(dividerInk, wave.Left, lanes[0].Bounds.Bottom + ChannelLaneGap / 2f, wave.Right, lanes[0].Bounds.Bottom + ChannelLaneGap / 2f);
        using var badge = new SolidBrush(Color.FromArgb(225, panel));
        foreach (var lane in lanes)
        {
            var label = new Rectangle(lane.Bounds.Left + 5, lane.Bounds.Top + 3, 17, 16);
            graphics.FillRectangle(badge, label);
            Label(graphics, lane.Channel == 0 ? "L" : "R", gridFont, AppTheme.Text, label, true);
        }
    }

    void DrawAxisLabelPair(Graphics graphics, string text, Color color, Rectangle wave, int y)
    {
        Label(graphics, text, gridFont, color, new(wave.Right + 7, y, AmplitudeGutter - 7, 14));
        using var brush = new SolidBrush(color);
        using var format = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        graphics.DrawString(text, gridFont, brush, new Rectangle(wave.Left - AmplitudeGutter, y, AmplitudeGutter - 7, 14), format);
    }
}
