using NAudio.Wave;

namespace OrchardSound;

public sealed partial class EditorForm
{
    bool scrubbing;
    ScrubProvider? scrubProvider;
    System.Windows.Forms.Timer? scrubTimer;
    long? lastScrubFrame;
    long lastScrubMovement;

    void UpdateScrub(long frame)
    {
        if (!scrub.Checked || busy || appClosing || changingPlayback || output != null && !scrubbing || lastScrubFrame == frame) return;
        if (!canvas.Tracks.Any(t => t.Enabled && t.Frames > 0)) { DisableScrub(); return; }
        int direction = lastScrubFrame.HasValue && frame < lastScrubFrame.Value ? -1 : 1;
        var audition = EffectPreviewActive ? effectPreview?.PlaybackTracks : null;
        var tracks = audition ?? canvas.Tracks.Select(t => t.Snapshot()).ToArray();
        lastScrubFrame = frame; lastScrubMovement = Environment.TickCount64;
        try
        {
            if (output == null)
            {
                scrubProvider = new ScrubProvider(direction); scrubProvider.MoveTo(tracks, frame); scrubbing = true;
                var device = outputFactory(); output = device;
                device.PlaybackStopped += (_, e) =>
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke(() => { if (output != device) return; StopPlayback(); if (e.Exception != null) status.Text = "Scrub playback failed: " + e.Exception.Message; });
                };
                device.Init(WithNoteAudition(scrubProvider)); device.Play();
                if (output != device || !scrubbing) return;
                scrubTimer ??= CreateScrubTimer(); scrubTimer.Start();
            }
            else scrubProvider?.MoveTo(tracks, frame);
        }
        catch (Exception error) { StopPlayback(); if (!IsDisposed) status.Text = "Cannot scrub audio: " + error.Message; }
    }

    System.Windows.Forms.Timer CreateScrubTimer()
    {
        var idle = new System.Windows.Forms.Timer { Interval = 30 };
        idle.Tick += (_, _) => { if (scrubbing && Environment.TickCount64 - lastScrubMovement >= 150) StopPlayback(); };
        return idle;
    }

    void ResetScrubState()
    {
        scrubbing = false; scrubTimer?.Stop();
        var source = scrubProvider; scrubProvider = null; source?.Dispose();
        if (appClosing || resourcesDisposed) { scrubTimer?.Dispose(); scrubTimer = null; }
    }

    void DisableScrub()
    {
        if (scrubbing) StopPlayback();
        else ResetScrubState();
        lastScrubFrame = null;
    }

    void DisposeScrubResources()
    {
        ResetScrubState(); scrubTimer?.Dispose(); scrubTimer = null;
    }
}
