using System.Reflection;
using NAudio.Wave;
using OrchardSound;

public static class LiveMarkerIntegrationChecks
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run()
    {
        int checks = 0; Exception? failure = null;
        void Check(bool condition, string name)
        { if (!condition) throw new Exception("FAILED LIVE MARKER: " + name); checks++; Console.WriteLine("PASS LIVE MARKER " + name); }
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new EditorForm([]); _ = form.Handle;
                object? Field(string name) => typeof(EditorForm).GetField(name, Private)!.GetValue(form);
                object? Call(string name, params object?[] values) => typeof(EditorForm).GetMethod(name, Private)!.Invoke(form, values.Length == 0 ? null : values);
                var canvas = (WaveCanvas)Field("canvas")!; _ = canvas.Handle;
                long pointerTick = System.Diagnostics.Stopwatch.Frequency;
                typeof(WaveCanvas).GetField("pointerClock", Private)!.SetValue(canvas, (Func<long>)(() => pointerTick += System.Diagnostics.Stopwatch.Frequency));
                canvas.Width = (int)Math.Round(1800 * canvas.DpiScale); canvas.SetViewportHeight((int)Math.Round(900 * canvas.DpiScale));
                canvas.Magnification = 2; canvas.SmartSnap = false;
                int frames = canvas.GetWaveBounds(1).Width;
                var samples = Enumerable.Range(0, frames).SelectMany(frame => new[] { (frame + 1f) / (frames + 1) * .8f, -(frame + 1f) / (frames + 1) * .4f }).ToArray();
                var track = new AudioTrack { Path = "Live marker fixture.wav", Samples = samples, Peaks = new PeakPyramid(samples) };
                canvas.Slots[0].Track = track;
                long originalStart = frames / 5, originalEnd = frames * 4 / 5, initialPosition = frames * 3 / 10, laterStart = frames * 2 / 5;
                canvas.SelectionStart = originalStart; canvas.SelectionEnd = originalEnd;
                var tracker = new PlayerTracker();
                typeof(EditorForm).GetField("outputFactory", Private)!.SetValue(form, (Func<IWavePlayer>)(() => new FakePlayer(tracker)));
                Call("StartPlayback", (object?)initialPosition);
                var output = (FakePlayer)Field("output")!; var queue = (BufferedPlayback)Field("playbackBuffer")!;
                MixProvider Playing() => (MixProvider)Field("playing")!;
                bool SameOwner() => ReferenceEquals(Field("output"), output) && ReferenceEquals(Field("playbackBuffer"), queue)
                    && output.PlaybackState == PlaybackState.Playing && !output.Disposed && output.Stops == 0 && output.Plays == 1 && tracker.Created.Count == 1 && tracker.Active == 1;
                void WaitForMix(int requiredFrames = 1)
                {
                    bool Published() => queue.AppliedRevision >= queue.RequestedRevision && queue.BufferedSamples >= requiredFrames * 2;
                    if (!SpinWait.SpinUntil(Published, TimeSpan.FromSeconds(3))) throw new TimeoutException("Live marker replacement did not publish its queued mix.");
                }
                float[] Read(int count)
                { WaitForMix(count); var audio = new float[count * 2]; if (queue.Read(audio, 0, audio.Length) != audio.Length) throw new Exception("Live marker playback returned premature EOF."); return audio; }
                bool Matches(float[] actual, IEnumerable<long> expected)
                    => expected.Select((frame, index) => Math.Abs(actual[index * 2] - samples[frame * 2]) < .00001f && Math.Abs(actual[index * 2 + 1] - samples[frame * 2 + 1]) < .00001f).All(equal => equal);
                Point Device(Point logical) => new((int)Math.Round(logical.X * canvas.DpiScale), (int)Math.Round(logical.Y * canvas.DpiScale));
                void MouseDevice(string method, MouseButtons button, Point point)
                    => typeof(WaveCanvas).GetMethod(method, Private)!.Invoke(canvas, [new MouseEventArgs(button, 1, point.X, point.Y, 0)]);
                void Mouse(string method, MouseButtons button, Point logical) => MouseDevice(method, button, Device(logical));
                Point Begin(bool start, int pane)
                {
                    if (pane == 0) typeof(WaveCanvas).GetField("lensCenter", Private)!.SetValue(canvas, (canvas.SelectionBounds.Start + canvas.SelectionBounds.End) / 2);
                    var handle = canvas.GetBoundaryHandleBounds(start, pane);
                    if (handle.IsEmpty) throw new Exception("The live marker handle is missing.");
                    var point = new Point(handle.Left + handle.Width / 2, handle.Top + handle.Height / 2);
                    Mouse("OnMouseMove", MouseButtons.None, point); Mouse("OnMouseDown", MouseButtons.Left, point);
                    return point;
                }
                Point Move(long frame, int pane, int y)
                {
                    var wave = canvas.GetWaveBounds(pane); var range = pane == 0 ? canvas.PrecisionViewRange : canvas.Range;
                    double grabOffset = (double)typeof(WaveCanvas).GetField("boundaryGrabOffset", Private)!.GetValue(canvas)!;
                    double logicalX = wave.Left + (frame - range.Min) / (double)(range.Max - range.Min) * wave.Width - grabOffset;
                    // Convert once so the fractional handle grab offset survives DPI scaling.
                    var point = new Point((int)Math.Round(logicalX * canvas.DpiScale), (int)Math.Round(y * canvas.DpiScale));
                    MouseDevice("OnMouseMove", MouseButtons.Left, point); return point;
                }
                int changes = 0, finishes = 0;
                canvas.SelectionChanging += () => changes++; canvas.SelectionChanged += () => finishes++;
                Check(SameOwner() && queue.Position == initialPosition, "normal playback begins with one injected output and a known consumed playhead");
                var startPoint = Begin(true, 1);
                Check(canvas.Capture && SameOwner() && queue.Position == initialPosition, "pressing the lower start handle preserves the active output and playback queue");
                var movedStart = Move(laterStart, 1, startPoint.Y); WaitForMix();
                var liveStartMix = Field("playing") as MixProvider;
                bool updatedStart = changes == 1 && finishes == 0 && canvas.SelectionStart == laterStart && liveStartMix != null && liveStartMix.LoopStartFrame == laterStart && liveStartMix.LoopEndFrame == originalEnd && SameOwner();
                if (!updatedStart) Console.WriteLine($"Live start drag: DPI {canvas.DeviceDpi}, expected {laterStart} to {originalEnd}, actual selection {canvas.SelectionStart} to {canvas.SelectionEnd}, mix {liveStartMix?.LoopStartFrame} to {liveStartMix?.LoopEndFrame}, changes {changes}, finishes {finishes}, same owner {SameOwner()}, mapped X {canvas.PrecisePointerX:0.###}.");
                Check(updatedStart,
                    "dragging start updates the playing loop before pointer release without reopening the output");
                Check(queue.Position == initialPosition && Matches(Read(8), Enumerable.Range(0, 8).Select(index => initialPosition + index)),
                    "a playhead before the moved start keeps its next exact stereo samples and continues forward");
                int toWrap = checked((int)(originalEnd - queue.Position + 5)); long beforeWrap = queue.Position;
                var expectedWrap = Enumerable.Range(0, toWrap).Select(index => beforeWrap + index < originalEnd ? beforeWrap + index : laterStart + beforeWrap + index - originalEnd);
                Check(Matches(Read(toWrap), expectedWrap) && queue.Position == laterStart + 5 && SameOwner(),
                    "lead-in playback reaches the selected end once and then wraps to the newly dragged start");
                MouseDevice("OnMouseUp", MouseButtons.Left, movedStart);
                Check(!canvas.Capture && finishes == 1 && queue.Position == laterStart + 5 && SameOwner(),
                    "releasing the start marker commits the gesture while preserving the consumed playhead and output owner");

                var endPoint = Begin(false, 0);
                Check(canvas.Capture && SameOwner(), "pressing the precision-view end handle also preserves active playback");
                long shortEnd = laterStart + 3; var movedEnd = Move(shortEnd, 0, endPoint.Y); WaitForMix();
                Check(changes == 2 && finishes == 1 && canvas.SelectionEnd == shortEnd && Playing().LoopEndFrame == shortEnd && queue.Position == laterStart && SameOwner(),
                    "moving end behind the playhead resets to the exact selected start immediately rather than preserving a modulo offset");
                Check(Matches(Read(2), new[] { laterStart, laterStart + 1 }) && queue.Position == laterStart + 2,
                    "the first stereo samples after shortening the end begin exactly at the selected start");
                movedEnd = Move(laterStart + 1, 0, endPoint.Y); WaitForMix();
                Check(changes == 3 && queue.Position == laterStart && Matches(Read(4), Enumerable.Repeat(laterStart, 4)) && SameOwner(),
                    "another live end move can create a one-sample loop without stale samples or a second output");
                long extendingFrom = queue.Position; movedEnd = Move(originalEnd, 0, endPoint.Y); WaitForMix();
                Check(changes == 4 && canvas.SelectionEnd == originalEnd && queue.Position == extendingFrom && Matches(Read(4), Enumerable.Range(0, 4).Select(index => extendingFrom + index)) && SameOwner(),
                    "extending end continues forward from the existing playhead instead of restarting the loop");
                MouseDevice("OnMouseUp", MouseButtons.Left, movedEnd);
                Check(!canvas.Capture && finishes == 2 && SameOwner(), "releasing the precision end marker retains the same single playback owner");

                long within = queue.Position, earlierStart = frames / 10;
                startPoint = Begin(true, 1); movedStart = Move(earlierStart, 1, startPoint.Y); WaitForMix();
                Check(changes == 5 && queue.Position == within && Matches(Read(4), Enumerable.Range(0, 4).Select(index => within + index)) && SameOwner(),
                    "moving start earlier preserves a playhead already inside the expanded loop");
                MouseDevice("OnMouseUp", MouseButtons.Left, movedStart);
                Check(finishes == 3 && tracker.MaxActive == 1 && track.Offset == 0 && track.Samples.SequenceEqual(samples),
                    "successive marker gestures neither move source audio nor accumulate simultaneous output players");
                Call("StopPlayback"); Application.DoEvents();
                Check(Field("output") == null && tracker.Active == 0 && tracker.Created.All(player => player.Disposed),
                    "explicit Stop releases the one output after every live marker gesture");
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(45))) throw new TimeoutException("Live marker integration fixture deadline exceeded.");
        if (failure != null) throw new Exception("Live marker integration fixture failed.", failure);
        Console.WriteLine($"{checks} live marker integration checks passed. No native window, input injection or audio hardware was used.");
    }

    sealed class PlayerTracker
    { public List<FakePlayer> Created { get; } = []; public int Active, MaxActive; }
    sealed class FakePlayer : IWavePlayer
    {
        readonly PlayerTracker tracker; bool active;
        public bool Disposed { get; private set; }
        public int Plays { get; private set; }
        public int Stops { get; private set; }
        public float Volume { get; set; } = 1;
        public PlaybackState PlaybackState { get; private set; }
        public WaveFormat OutputWaveFormat { get; private set; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public FakePlayer(PlayerTracker tracker) { this.tracker = tracker; tracker.Created.Add(this); }
        public void Init(IWaveProvider provider) => OutputWaveFormat = provider.WaveFormat;
        public void Play() { Plays++; if (!active) { active = true; tracker.Active++; tracker.MaxActive = Math.Max(tracker.MaxActive, tracker.Active); } PlaybackState = PlaybackState.Playing; }
        public void Pause() { Deactivate(); PlaybackState = PlaybackState.Paused; }
        public void Stop() { Stops++; Deactivate(); PlaybackState = PlaybackState.Stopped; PlaybackStopped?.Invoke(this, new StoppedEventArgs()); }
        void Deactivate() { if (active) { active = false; tracker.Active--; } }
        public void Dispose() { Deactivate(); Disposed = true; PlaybackState = PlaybackState.Stopped; }
    }
}
