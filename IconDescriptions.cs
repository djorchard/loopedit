namespace OrchardSound;

public static class IconDescriptions
{
    public static string For(EditorIcon icon) => icon switch
    {
        EditorIcon.Open => "Open audio files in this channel or add them as new tracks.",
        EditorIcon.Paste => "Paste copied WAV audio or audio files into this channel. Shortcut: Ctrl+V.",
        EditorIcon.Duplicate => "Repeat the selected time range immediately after it on the same tracks.",
        EditorIcon.Cue => "Save the selection boundaries as cue points. Smart snap can reuse them; Ctrl bypasses it.",
        EditorIcon.Help => "Show the keyboard and pointer shortcut guide. F1 opens this guide.",
        EditorIcon.View => "Choose waveform height fitting, estimated beat markers and likely kick markers.",
        EditorIcon.Scrub => "While stopped, audition the cursor position. Move left to hear backwards. Click to turn this option on or off.",
        EditorIcon.RefineLoop => "Blend the heard position into the selection end when Shift+Space restarts playback. Click to turn this option on or off.",
        EditorIcon.AddTrack => "Add an empty channel for another sound or recording.",
        EditorIcon.Record => "Choose an input device and record into the target channel. Click again to stop.",
        EditorIcon.Play => "Play from the cursor and loop the selection when reached. Shift+Space restarts from the selection start; Ctrl+Space stops.",
        EditorIcon.PlayLoop => "Restart from the selection start, or the timeline beginning without a selection. Shortcut: Shift+Space. Ctrl+Space stops.",
        EditorIcon.Silence => "Insert silence and move later audio together.",
        EditorIcon.Start => "Set the start at the waveform guide. Left-click the waveform for the same action.",
        EditorIcon.End => "Set the end at the waveform guide. Right-click the waveform for the same action.",
        EditorIcon.SelectAll => "Select the complete shared timeline across the channels.",
        EditorIcon.ClearSelection => "Clear the selection markers and stop playback. The sounds stay in place.",
        EditorIcon.FadeIn => "Open a movable fade-in tool with live preview, Apply and Cancel.",
        EditorIcon.FadeOut => "Open a movable fade-out tool with live preview, Apply and Cancel.",
        EditorIcon.Undo => "Undo the last audio edit, track move or saved-selection change.",
        EditorIcon.Redo => "Redo the last undone change.",
        EditorIcon.Delete => "Remove the selected time range across the tracks and close the gap.",
        EditorIcon.Trim => "Keep only the selected time range and move it to the beginning.",
        EditorIcon.Effects => "Choose an effect, adjust it while listening, then Apply or Cancel.",
        EditorIcon.Bookmark => "Save the current time range under the name in Saved selections.",
        EditorIcon.SaveSession => $"Save the tracks, edits and cue points in a {Branding.Name} session.",
        EditorIcon.OpenSession => "Open a saved session with its tracks, edits and cue points.",
        EditorIcon.EqualHeights => "Give the precision view and all channels equal height.",
        EditorIcon.Export => "Export to the configured folder, or beside the first loaded sound if none is set.",
        EditorIcon.ExportFolder => "Choose the folder used by future exports. This action does not export audio.",
        EditorIcon.Remove => "Remove this channel from the session. Its source file is kept.",
        _ => "Use this editing action."
    };
}
