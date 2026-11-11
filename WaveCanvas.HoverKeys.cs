namespace OrchardSound;

public sealed partial class WaveCanvas
{
    internal void DispatchHoverModifier(Keys key, bool down)
    {
        var args = new KeyEventArgs(key | ModifierKeys);
        if (down) OnKeyDown(args); else OnKeyUp(args);
    }
}
