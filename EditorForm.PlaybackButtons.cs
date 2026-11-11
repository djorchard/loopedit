namespace OrchardSound;

public sealed partial class EditorForm
{
    void RefreshPlaybackButtons()
    {
        bool active = output != null && !scrubbing;
        void Update(IconButton button, string shortcut, bool isPlaying)
        {
            bool changed = button.IsPlaying != isPlaying; button.IsPlaying = isPlaying;
            string hint = button.IsPlaying ? shortcut + "\nCtrl+Space" : shortcut;
            if (button.Shortcut != hint)
            {
                changed = true;
                button.Shortcut = hint;
                string description = button.AccessibleName + "\n" + IconDescriptions.For(button.Icon) + "\nShortcut: " + hint.Replace('\n', ' ');
                button.AccessibleDescription = description; tips.SetToolTip(button, description);
            }
            if (changed) button.Invalidate();
        }
        Update(play, "Space", active && cursorPlayback); Update(loopPlay, "Shift+Space", active && !cursorPlayback);
    }
}
