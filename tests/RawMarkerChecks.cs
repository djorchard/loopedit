using System.Reflection;
using NAudio.Wave;
using OrchardSound;

public static class RawMarkerChecks
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run()
    {
        int checks = 0; Exception? failure = null;
        void Check(bool condition, string name)
        { if (!condition) throw new Exception("FAILED RAW MARKER: " + name); checks++; Console.WriteLine("PASS RAW MARKER " + name); }
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new EditorForm([]); _ = form.Handle;
                object? Field(string name) => typeof(EditorForm).GetField(name, Private)!.GetValue(form);
                object? Call(string name, params object?[] values) => typeof(EditorForm).GetMethod(name, Private)!.Invoke(form, values.Length == 0 ? null : values);
                var canvas = (WaveCanvas)Field("canvas")!; _ = canvas.Handle;
                canvas.Width = (int)Math.Round(1800 * canvas.DpiScale); canvas.SetViewportHeight((int)Math.Round(900 * canvas.DpiScale));
                int frames = canvas.GetWaveBounds(1).Width;
                var samples = Enumerable.Range(0, frames).SelectMany(frame => new[] { .1f + frame / (float)frames * .1f, -.05f }).ToArray();
                var track = new AudioTrack { Path = "Raw marker fixture.wav", Samples = samples, Peaks = new PeakPyramid(samples) };
                var originalSamples = samples.ToArray();
                canvas.Slots[0].Track = track; canvas.Magnification = 64; canvas.SmartSnap = false;
                long start = frames * 3 / 10, end = frames * 7 / 10;
                canvas.SelectionStart = start; canvas.SelectionEnd = end;
                long pointerTick = System.Diagnostics.Stopwatch.Frequency;
                typeof(WaveCanvas).GetField("pointerClock", Private)!.SetValue(canvas, (Func<long>)(() => pointerTick += System.Diagnostics.Stopwatch.Frequency));
                int beforeMoves = 0, starts = 0;
                canvas.BeforeMove += () => beforeMoves++; canvas.BoundaryDragStarted += () => starts++;
                var tracker = new PlayerTracker();
                typeof(EditorForm).GetField("outputFactory", Private)!.SetValue(form, (Func<IWavePlayer>)(() => new FakePlayer(tracker)));
                Call("StartPlayback", (object?)(frames / 2L));
                var output = (FakePlayer)Field("output")!; var queue = (BufferedPlayback)Field("playbackBuffer")!;
                var originalSegments = track.GetSegments().ToArray();
                bool SameOwner() => ReferenceEquals(Field("output"), output) && ReferenceEquals(Field("playbackBuffer"), queue)
                    && !output.Disposed && output.Stops == 0 && output.PlaybackState == PlaybackState.Playing && tracker.Created.Count == 1 && tracker.Active == 1;
                bool SourceUnchanged() => track.Offset == 0 && track.GetSegments().SequenceEqual(originalSegments) && track.Samples.SequenceEqual(originalSamples) && beforeMoves == 0;
                void WaitForMix()
                { if (!SpinWait.SpinUntil(() => queue.AppliedRevision >= queue.RequestedRevision, TimeSpan.FromSeconds(3))) throw new TimeoutException("Raw marker loop update did not publish."); }
                void Mouse(string method, MouseButtons button, Point logical)
                {
                    int x = (int)Math.Round(logical.X * canvas.DpiScale), y = (int)Math.Round(logical.Y * canvas.DpiScale);
                    typeof(WaveCanvas).GetMethod(method, Private)!.Invoke(canvas, [new MouseEventArgs(button, 1, x, y, 0)]);
                }
                foreach (string mode in new[] { "normal zoom", "Shift precision history", "Ctrl absolute override" })
                    foreach (bool selectStart in new[] { true, false })
                    {
                        canvas.SelectionStart = start; canvas.SelectionEnd = end;
                        var playing = (MixProvider)Field("playing")!;
                        if (playing.LoopStartFrame != start || playing.LoopEndFrame != end)
                        { Call("ReplacePlayback", (object?)(frames / 2L)); WaitForMix(); }
                        var wave = canvas.GetWaveBounds(1); var top = canvas.GetWaveBounds(0);
                        var handle = canvas.GetBoundaryHandleBounds(selectStart, 1);
                        var raw = new Point(handle.Left + handle.Width / 2, handle.Top + handle.Height / 2);
                        canvas.PrecisionPointer(new Point(top.Left + 30, top.Top + 30), false);
                        canvas.PrecisionPointer(new Point(wave.Left + 30, raw.Y), false, true);
                        canvas.PrecisionPointer(raw, mode == "Shift precision history");
                        bool guideWasDiverged = Math.Abs(canvas.PrecisePointerX - raw.X) > 30;
                        if (mode == "Ctrl absolute override")
                        {
                            var direct = canvas.PrecisionPointer(raw, true, true);
                            Check(guideWasDiverged && direct == raw && canvas.PrecisePointerX == raw.X,
                                mode + " restores the physical " + (selectStart ? "start" : "end") + " handle point after a lagged guide");
                        }
                        else Check(guideWasDiverged, mode + " creates a physical handle point distinct from its virtual guide");
                        int beforeStarts = starts; long chosen = selectStart ? start : end;
                        Mouse("OnMouseDown", MouseButtons.Left, raw);
                        Check(canvas.Capture && starts == beforeStarts + 1 && Equals(typeof(WaveCanvas).GetField("draggingBoundary", Private)!.GetValue(canvas), selectStart)
                            && (int)typeof(WaveCanvas).GetField("pressedSlot", Private)!.GetValue(canvas)! == -1 && canvas.CursorFrame == chosen && SourceUnchanged() && SameOwner(),
                            mode + " physical " + (selectStart ? "start" : "end") + " press captures its marker before mapping and never arms track movement");
                        var moved = new Point(raw.X + (selectStart ? 60 : -60), raw.Y);
                        Mouse("OnMouseMove", MouseButtons.Left, moved); WaitForMix();
                        long adjusted = (selectStart ? canvas.SelectionStart : canvas.SelectionEnd)!.Value;
                        Check((selectStart ? adjusted > chosen : adjusted < chosen) && (selectStart ? canvas.SelectionEnd == end : canvas.SelectionStart == start)
                            && canvas.Capture && SourceUnchanged() && SameOwner(),
                            mode + " dragging changes only its chosen boundary while preserving audio maps and the single player");
                        Mouse("OnMouseUp", MouseButtons.Left, moved);
                        Check(!canvas.Capture && typeof(WaveCanvas).GetField("draggingBoundary", Private)!.GetValue(canvas) == null && SourceUnchanged() && SameOwner(),
                            mode + " release completes the marker gesture without an offset change or output restart");
                    }
                Call("StopPlayback"); Application.DoEvents();
                Check(SourceUnchanged() && tracker.MaxActive == 1 && tracker.Active == 0 && tracker.Created.All(player => player.Disposed),
                    "all raw handle scenarios preserve source audio and dispose their one output owner");
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(45))) throw new TimeoutException("Raw marker fixture deadline exceeded.");
        if (failure != null) throw new Exception("Raw marker fixture failed.", failure);
        Console.WriteLine($"{checks} raw marker checks passed. Pointer events were invoked on hidden controls without native input or audio hardware.");
    }

    sealed class PlayerTracker
    { public List<FakePlayer> Created { get; } = []; public int Active, MaxActive; }
    sealed class FakePlayer : IWavePlayer
    {
        readonly PlayerTracker tracker; bool active;
        public bool Disposed { get; private set; }
        public int Stops { get; private set; }
        public float Volume { get; set; } = 1;
        public PlaybackState PlaybackState { get; private set; }
        public WaveFormat OutputWaveFormat { get; private set; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public FakePlayer(PlayerTracker tracker) { this.tracker = tracker; tracker.Created.Add(this); }
        public void Init(IWaveProvider provider) => OutputWaveFormat = provider.WaveFormat;
        public void Play() { if (!active) { active = true; tracker.Active++; tracker.MaxActive = Math.Max(tracker.MaxActive, tracker.Active); } PlaybackState = PlaybackState.Playing; }
        public void Pause() { Deactivate(); PlaybackState = PlaybackState.Paused; }
        public void Stop() { Stops++; Deactivate(); PlaybackState = PlaybackState.Stopped; PlaybackStopped?.Invoke(this, new StoppedEventArgs()); }
        void Deactivate() { if (active) { active = false; tracker.Active--; } }
        public void Dispose() { Deactivate(); Disposed = true; PlaybackState = PlaybackState.Stopped; }
    }
}
