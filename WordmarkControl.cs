using System.Drawing.Drawing2D;

namespace OrchardSound;

public sealed class WordmarkControl : Control
{
    readonly Bitmap artwork = LoadArtwork();

    public WordmarkControl()
    {
        Size = new(170, 74);
        Margin = new(7, 3, 8, 3);
        AccessibleName = Branding.Name;
        AccessibleRole = AccessibleRole.StaticText;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
    }

    static Bitmap LoadArtwork()
    {
        using var stream = typeof(WordmarkControl).Assembly.GetManifestResourceStream(Branding.Name + ".Wordmark")
            ?? throw new InvalidOperationException("The embedded wordmark artwork is missing.");
        using var source = Image.FromStream(stream);
        return new Bitmap(source);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using var background = new SolidBrush(Parent?.BackColor ?? AppTheme.Surface);
        e.Graphics.FillRectangle(background, ClientRectangle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        OnPaintBackground(e);
        float scale = Math.Min(ClientSize.Width * .94f / artwork.Width, ClientSize.Height * .76f / artwork.Height);
        if (scale <= 0) return;

        float width = artwork.Width * scale;
        float height = artwork.Height * scale;
        var destination = new RectangleF((ClientSize.Width - width) / 2, (ClientSize.Height - height) / 2, width, height);
        var graphics = e.Graphics;
        var state = graphics.Save();
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var source = new RectangleF(0, 0, artwork.Width, artwork.Height);
        graphics.DrawImage(artwork, destination, source, GraphicsUnit.Pixel);
        graphics.Restore(state);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) artwork.Dispose();
        base.Dispose(disposing);
    }
}
