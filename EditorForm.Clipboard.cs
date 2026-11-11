using System.Runtime.InteropServices;

namespace OrchardSound;

public sealed partial class EditorForm
{
    Func<IDataObject?> clipboardReader = Clipboard.GetDataObject;

    async Task PasteAudio(ChannelSlot? target = null)
    {
        if (busy || appClosing) return;
        try
        {
            var data = clipboardReader();
            if (data == null) { status.Text = "Clipboard is empty. Copy an audio file or WAV audio, then paste."; return; }
            var files = ClipboardAudio.GetFiles(data);
            if (files.Length > 0) { await LoadFiles(files, target); return; }
            var audio = ClipboardAudio.GetWaveData(data);
            if (audio == null) { status.Text = "No audio on the clipboard. Copy an audio file or WAV audio, then paste."; return; }
            busy = true; canvas.Enabled = false; StopPlayback(); status.Text = "Loading clipboard audio...";
            var track = await ClipboardAudio.LoadWaveAsync(audio, ClipboardAudio.StorageFolder(canvas.Tracks), canvas.Tracks.Sum(t => t.Samples.LongLength * sizeof(float) + t.Patches.Sum(p => p.Samples.LongLength * sizeof(float))), lifetime.Token);
            ChannelSlot? slot = target != null && canvas.Slots.Contains(target) && target.Track == null ? target : null;
            slot ??= target == null ? canvas.Slots.FirstOrDefault(s => s.Track == null) : null;
            busy = false; Remember(); busy = true;
            if (slot == null) { slot = new(); canvas.Slots.Add(slot); }
            slot.Track = track; ImportTrackCues(track); canvas.FitHeight(); RebuildTrackControls(); SizeCanvas(); SelectionUpdated(false); PruneAudioHistory();
            _ = RefreshRhythmAsync(); status.Text = "Added " + System.IO.Path.GetFileName(track.Path) + ".";
        }
        catch (ExternalException) { if (!IsDisposed) status.Text = "Clipboard is busy. Try pasting again."; }
        catch (OperationCanceledException) { if (!IsDisposed) status.Text = "Clipboard import cancelled or timed out."; }
        catch (Exception error) { if (!IsDisposed) status.Text = "Could not paste audio: " + error.Message; }
        finally
        {
            busy = false;
            if (!IsDisposed) { canvas.Enabled = true; if (effectWindow != null) _ = RequestEffectPreview(); }
        }
    }
}
