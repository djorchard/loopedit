namespace OrchardSound;

public sealed partial class EditorForm
{
    void LayoutAddTrackOverlay()
    {
        if (canvas.Slots.Count == 0) { addTrackOverlay.Visible = false; return; }
        float scale = canvas.DpiScale;
        var pane = canvas.ToDevice(canvas.GetPaneBounds(canvas.Slots.Count));
        int side = (int)Math.Round(18 * scale), bottom = (int)Math.Round(8 * scale), gap = (int)Math.Round(6 * scale);
        addTrackOverlay.Padding = new((int)Math.Round(10 * scale), (int)Math.Round(2 * scale), (int)Math.Round(10 * scale), (int)Math.Round(2 * scale));
        int width = Math.Min((int)Math.Round((WaveCanvas.LeftGutter - 36) * scale),
            Math.Max((int)Math.Round(112 * scale), TextRenderer.MeasureText(addTrackOverlay.Text, addTrackOverlay.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + addTrackOverlay.Padding.Horizontal + (int)Math.Round(4 * scale)));
        int height = Math.Max((int)Math.Round(28 * scale), addTrackOverlay.Font.Height + addTrackOverlay.Padding.Vertical + (int)Math.Round(4 * scale));
        int headerLeft = trackControls.LastOrDefault()?.Left ?? pane.Left + side;
        addTrackOverlay.Bounds = new(headerLeft, pane.Bottom - bottom - height, width, height);
        int headerBottom = trackControls.LastOrDefault()?.Bottom ?? pane.Top;
        var viewport = new Rectangle(-canvas.Left, -canvas.Top, scroll.ClientSize.Width, scroll.ClientSize.Height);
        bool room = addTrackOverlay.Top >= headerBottom + gap && viewport.Contains(addTrackOverlay.Bounds);
        addTrackOverlay.Visible = !busy && room;
        if (room) addTrackOverlay.BringToFront();
    }
}
