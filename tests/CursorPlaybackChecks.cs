using System.Reflection;
using NAudio.Wave;
using OrchardSound;

public static class CursorPlaybackChecks
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run()
    {
        int checks = 0;
        void Check(bool condition, string name)
        { if (!condition) throw new Exception("FAILED CURSOR PLAYBACK: " + name); checks++; Console.WriteLine("PASS CURSOR PLAYBACK " + name); }
        var samples = Enumerable.Range(0, 10).SelectMany(frame => new[] { (frame + 1) * .05f, -(frame + 1) * .025f }).ToArray();
        var track = new AudioTrack { Path = "Cursor fixture.wav", Samples = samples, Peaks = new PeakPyramid(samples) };
        MixProvider Mix(long cursor, bool loop = true) => new([track.Snapshot()], 0, 10, loop, cursor, 2, 5);
        bool Matches(float[] audio, params int[] frames) => audio.Length >= frames.Length * 2 && frames.Select((frame, index) => audio[index * 2] == samples[frame * 2] && audio[index * 2 + 1] == samples[frame * 2 + 1]).All(value => value);
        var preroll = Mix(0); var data = new float[24];
        Check(preroll.Read(data, 0, data.Length) == data.Length && Matches(data, 0, 1, 2, 3, 4, 2, 3, 4, 2, 3, 4, 2), "cursor before the loop plays its lead-in once then wraps within the selection");
        var inside = Mix(3); inside.Read(data, 0, data.Length);
        Check(Matches(data, 3, 4, 2, 3, 4, 2, 3, 4, 2, 3, 4, 2), "cursor inside the loop starts there and wraps to the selection start");
        var after = Mix(6, false); Array.Fill(data, 9);
        Check(after.Read(data, 0, data.Length) == 8 && Matches(data, 6, 7, 8, 9) && after.Position == 10 && data.Skip(8).All(value => value == 9), "cursor after the loop plays once to the full timeline end");
        Check(after.Read(data, 0, data.Length) == 0, "after-loop playback stays stopped at EOF");
        var endpoint = Mix(10, false);
        Check(endpoint.Position == 10 && endpoint.Read(data, 0, data.Length) == 0, "cursor at the full timeline end does not restart from its beginning");
        var endOnly = new MixProvider([track.Snapshot()], 0, 10, true, 3, 0, 5); endOnly.Read(data, 0, data.Length);
        Check(Matches(data, 3, 4, 0, 1, 2, 3, 4, 0, 1, 2, 3, 4), "end-only selections use the timeline start as their loop start");

        long PositionAt(long outputFrame) => outputFrame <= 5 ? outputFrame : 3 + (outputFrame - 6) % 3;
        using (var buffer = new BufferedPlayback(Mix(0), 20))
        {
            bool exact = buffer.TryGetAudiblePosition(0, out long initial) && initial == 0;
            var frame = new float[2];
            for (int i = 1; i <= 30; i++)
            {
                buffer.Read(frame, 0, 2);
                exact &= buffer.Position == PositionAt(i) && buffer.TryGetAudiblePosition(i, out long audible) && audible == PositionAt(i);
            }
            Check(exact, "buffer and audible-device history map every lead-in and loop-wrap frame exactly");
        }
        using (var buffer = new BufferedPlayback(Mix(0), 20))
        {
            buffer.Replace(new MixProvider([track.Snapshot() with { Gain = .5f }], 0, 10, true, 0, 2, 5));
            var frame = new float[2]; bool exact = true, bounded = true;
            for (int i = 1; i <= buffer.CapacitySamples / 2 + 600; i++)
            {
                if (!SpinWait.SpinUntil(() => buffer.BufferedSamples >= 2, TimeSpan.FromSeconds(2))) throw new TimeoutException("Cursor replacement render deadline exceeded.");
                buffer.Read(frame, 0, 2);
                exact &= buffer.Position == PositionAt(i) && buffer.TryGetAudiblePosition(i, out long audible) && audible == PositionAt(i);
                bounded &= Math.Abs(frame[0]) <= .25001f;
            }
            Check(exact && bounded, "live replacement preserves cursor lead-in history and loop phase without summing delayed copies");
        }
        using (var buffer = new BufferedPlayback(Mix(0), 20))
        {
            int realFrames = buffer.CapacitySamples / 2, requestedFrames = realFrames + 8; var output = new float[requestedFrames * 2];
            buffer.Read(output, 0, output.Length);
            Check(buffer.TryGetAudiblePosition(realFrames, out long beforeSilence) && beforeSilence == PositionAt(realFrames)
                && buffer.TryGetAudiblePosition(requestedFrames, out long afterSilence) && afterSilence == beforeSilence && output.Skip(realFrames * 2).All(value => value == 0),
                "underflow silence holds the exact consumed loop position in audible history");
        }

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new EditorForm([]); _ = form.Handle;
                var canvas = (WaveCanvas)typeof(EditorForm).GetField("canvas", Private)!.GetValue(form)!; canvas.Slots[0].Track = track;
                var tracker = new PlayerTracker();
                typeof(EditorForm).GetField("outputFactory", Private)!.SetValue(form, (Func<IWavePlayer>)(() => new SilentPlayer(tracker)));
                void Call(string name, params object?[] values) => typeof(EditorForm).GetMethod(name, Private)!.Invoke(form, values.Length == 0 ? null : values);
                object? Field(string name) => typeof(EditorForm).GetField(name, Private)!.GetValue(form);
                BufferedPlayback Buffer() => (BufferedPlayback)Field("playbackBuffer")!;
                MixProvider Playing() => (MixProvider)Field("playing")!;
                void Key(Keys key)
                {
                    var message = Message.Create(IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero);
                    object[] args = [message, key];
                    if (!(bool)typeof(EditorForm).GetMethod("ProcessCmdKey", Private)!.Invoke(form, args)!) throw new Exception("Playback shortcut was not handled.");
                }
                canvas.SelectionStart = 2; canvas.SelectionEnd = 5; canvas.CursorFrame = 8;
                Key(Keys.Shift | Keys.Space);
                var cursorButton = (IconButton)Field("play")!; var selectionButton = (IconButton)Field("loopPlay")!;
                Check(Buffer().Position == 2 && Playing().Looping && selectionButton.IsPlaying && !cursorButton.IsPlaying && selectionButton.Shortcut.Contains("Ctrl+Space"), "Shift+Space starts at the selection beginning and highlights its button with a stop hint");
                Key(Keys.Control | Keys.Space); Check(Field("output") == null && tracker.Active == 0 && !(bool)Field("effectAutoPlay")! && !cursorButton.IsPlaying && !selectionButton.IsPlaying, "Ctrl+Space stops the output and clears both play modes and automatic preview playback");
                canvas.SelectionStart = null; canvas.SelectionEnd = 5; canvas.CursorFrame = 8;
                Key(Keys.Shift | Keys.Space);
                Check(Buffer().Position == 0 && Playing().Looping && Playing().LoopStartFrame == 0, "Shift+Space handles an end-only selection from its default start");
                Key(Keys.Control | Keys.Space);
                canvas.SelectionStart = 2; canvas.SelectionEnd = 5; canvas.CursorFrame = 0;
                Key(Keys.Space);
                Check(Buffer().Position == 0 && Playing().StartFrame == 0 && Playing().EndFrame == 10 && Playing().LoopStartFrame == 2 && Playing().LoopEndFrame == 5 && cursorButton.IsPlaying && !selectionButton.IsPlaying && cursorButton.Shortcut.Contains("Ctrl+Space"),
                    "Space retains whole playback bounds separately from the selected loop");
                var six = new float[12]; Buffer().Read(six, 0, six.Length);
                Check(Matches(six, 0, 1, 2, 3, 4, 2), "Space before selection audibly reaches its end before looping");
                var device = Field("output"); var queue = Buffer();
                Call("ReplacePlayback", (object?)0L);
                Check(ReferenceEquals(Field("output"), device) && ReferenceEquals(Buffer(), queue) && Playing().StartFrame == 0 && Playing().LoopStartFrame == 2,
                    "live cursor-mode changes retain one device and the lead-in capable stream");
                canvas.CursorFrame = 3; Key(Keys.Space);
                Check(Buffer().Position == 3 && Playing().Looping, "Space inside selection restarts exactly at the cursor");
                int beforeRepeats = tracker.Created.Count; bool retriggered = true;
                for (int repeat = 0; repeat < 10; repeat++) { Key(Keys.Space); retriggered &= Buffer().Position == 3 && tracker.Active == 1; }
                Check(retriggered && tracker.Created.Count == beforeRepeats + 10 && tracker.MaxActive == 1, "repeated Space keydowns retrigger the cursor without toggling off or overlapping output owners");
                canvas.CursorFrame = 7; Key(Keys.Space);
                Check(Buffer().Position == 7 && !Playing().Looping && Playing().EndFrame == 10, "Space after selection bypasses that loop and plays the remaining file");
                Call("ReplacePlayback", (object?)7L);
                Check(!Playing().Looping && Playing().StartFrame == 0 && Playing().EndFrame == 10, "live changes preserve after-loop one-shot mode");
                canvas.SelectionStart = null; canvas.SelectionEnd = null; canvas.CursorFrame = 4;
                Key(Keys.Space);
                Check(Buffer().Position == 4 && !Playing().Looping, "Space without a selection plays once from the cursor");
                canvas.CursorFrame = 10; Key(Keys.Space);
                Check(Buffer().Position == 10 && Buffer().Read(new float[2], 0, 2) == 0, "Space at timeline end returns EOF without wrapping");
                canvas.SelectionStart = 2; canvas.SelectionEnd = 5; canvas.CursorFrame = 8;
                Key(Keys.Shift | Keys.Space);
                Check(Buffer().Position == 2 && Playing().Looping, "Shift+Space retains selection-start restart behavior");
                canvas.SelectionStart = null; canvas.SelectionEnd = null; canvas.CursorFrame = 4;
                Key(Keys.Shift | Keys.Space);
                Check(Buffer().Position == 0 && !Playing().Looping, "Shift+Space without a selection restarts from the timeline beginning");
                Key(Keys.Control | Keys.Space);
                var scrub = (CheckBox)Field("scrub")!; scrub.Checked = true; Call("UpdateScrub", 3L);
                canvas.CursorFrame = 0; Key(Keys.Space);
                Check(Buffer().Position == 0 && !(bool)Field("scrubbing")! && tracker.Active == 1 && tracker.MaxActive == 1,
                    "Space replaces scrub ownership with one normal output");
                Key(Keys.Control | Keys.Space);
                Check(tracker.Active == 0 && tracker.Created.All(player => player.Disposed), "all shortcut scenarios release their single output owners");
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("Cursor shortcut fixture deadline exceeded.");
        if (failure != null) throw new Exception("Cursor shortcut fixture failed", failure);
        Console.WriteLine($"{checks} cursor playback checks passed.");
    }

    sealed class PlayerTracker
    { public List<SilentPlayer> Created { get; } = []; public int Active, MaxActive; }
    sealed class SilentPlayer : IWavePlayer
    {
        readonly PlayerTracker tracker; bool active;
        public bool Disposed { get; private set; }
        public float Volume { get; set; } = 1;
        public PlaybackState PlaybackState { get; private set; }
        public WaveFormat OutputWaveFormat { get; private set; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public SilentPlayer(PlayerTracker tracker) { this.tracker = tracker; tracker.Created.Add(this); }
        public void Init(IWaveProvider provider) => OutputWaveFormat = provider.WaveFormat;
        public void Play() { if (!active) { active = true; tracker.Active++; tracker.MaxActive = Math.Max(tracker.MaxActive, tracker.Active); } PlaybackState = PlaybackState.Playing; }
        public void Pause() { Deactivate(); PlaybackState = PlaybackState.Paused; }
        public void Stop() { Deactivate(); PlaybackState = PlaybackState.Stopped; PlaybackStopped?.Invoke(this, new StoppedEventArgs()); }
        void Deactivate() { if (active) { active = false; tracker.Active--; } }
        public void Dispose() { Deactivate(); Disposed = true; PlaybackState = PlaybackState.Stopped; }
    }
}
