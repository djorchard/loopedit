using System.Reflection;
using NAudio.Wave;
using OrchardSound;

public static class TransportChecks
{
    static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new EditorForm([]);
                _ = form.Handle;
                var canvas = (WaveCanvas)typeof(EditorForm).GetField("canvas", Private)!.GetValue(form)!;
                var samples = new float[48000 * 2];
                canvas.Slots[0].Track = new AudioTrack { Path = "Silent transport fixture.wav", Samples = samples, Peaks = new PeakPyramid(samples) };
                var tracker = new PlayerTracker();
                int checks = 0;
                void Check(bool condition, string name)
                { if (!condition) throw new Exception(name); checks++; Console.WriteLine("PASS TRANSPORT " + name); }
                void Start(long? frame = null) => typeof(EditorForm).GetMethod("StartPlayback", Private)!.Invoke(form, [frame]);
                void Replace(long? frame = null) => typeof(EditorForm).GetMethod("ReplacePlayback", Private)!.Invoke(form, [frame]);
                void Stop() => typeof(EditorForm).GetMethod("StopPlayback", Private)!.Invoke(form, null);
                object? Output() => typeof(EditorForm).GetField("output", Private)!.GetValue(form);
                void Factory(Func<IWavePlayer> factory) => typeof(EditorForm).GetField("outputFactory", Private)!.SetValue(form, factory);
                Factory(() => new FakePlayer(tracker));

                for (int i = 0; i < 100; i++) Start(i * 100);
                Check(tracker.Created.Count == 100 && tracker.Active == 1 && tracker.MaxActive == 1, "100 restarts retain at most one active audio output");
                Check(tracker.Created.Take(99).All(player => player.Disposed), "restarts dispose every replaced player");
                var latest = tracker.Created[^1];
                foreach (var old in tracker.Created.Take(99)) old.RaiseStopped();
                Application.DoEvents();
                Check(ReferenceEquals(Output(), latest) && tracker.Active == 1 && !latest.Disposed, "queued stale completions cannot stop the current player");
                var buffered = typeof(EditorForm).GetField("playbackBuffer", Private)?.GetValue(form) as BufferedPlayback;
                var mix = (MixProvider)typeof(EditorForm).GetField("playing", Private)!.GetValue(form)!;
                Check((buffered?.Position ?? mix.Position) == 9900, "replacement starts at the requested mouse frame");
                int beforeReplacements = tracker.Created.Count;
                for (int i = 0; i < 100; i++)
                {
                    canvas.Slots[0].Track!.Gain = i % 2 == 0 ? .5f : 1;
                    Replace();
                }
                Check(tracker.Created.Count == beforeReplacements && ReferenceEquals(Output(), latest) && tracker.Active == 1 && tracker.MaxActive == 1,
                    "100 live mix replacements retain the same single audio output");
                Check(buffered != null && ReferenceEquals(typeof(EditorForm).GetField("playbackBuffer", Private)!.GetValue(form), buffered) && buffered.Position == 9900 && !latest.Disposed,
                    "live replacements preserve the render queue and consumed playhead");
                latest.Complete(); Application.DoEvents();
                var timer = (System.Windows.Forms.Timer)typeof(EditorForm).GetField("timer", Private)!.GetValue(form)!;
                var play = (IconButton)typeof(EditorForm).GetField("play", Private)!.GetValue(form)!;
                Check(Output() == null && tracker.Active == 0 && latest.Disposed && !timer.Enabled && !play.IsPlaying, "natural completion clears output, timer and play icon");

                Factory(() => new FakePlayer(tracker) { FailInit = true });
                Start(); Application.DoEvents();
                Check(Output() == null && tracker.Active == 0 && tracker.Created[^1].Disposed, "initialization failure releases its player");
                Factory(() => new FakePlayer(tracker) { FailPlay = true });
                Start(); Application.DoEvents();
                Check(Output() == null && tracker.Active == 0 && tracker.Created[^1].Disposed, "play failure releases an already activated player");

                int before = tracker.Created.Count;
                Factory(() => new FakePlayer(tracker) { OnPlay = () => Start(12000) });
                Start(6000);
                Check(tracker.Created.Count == before + 1 && tracker.Active == 1 && tracker.MaxActive == 1, "synchronous restart reentrancy cannot create a second player");
                Stop(); Application.DoEvents();
                Check(Output() == null && tracker.Active == 0 && tracker.Created[^1].Disposed, "stop disposes the final active output");

                Factory(() => new FakePlayer(tracker) { FailStop = true });
                Start(); Stop(); Application.DoEvents();
                Check(Output() == null && tracker.Active == 0 && tracker.Created[^1].Disposed, "stop failure still disposes the native output owner");

                Factory(() => new FakePlayer(tracker) { FailDispose = true });
                Start();
                var failingBuffer = (BufferedPlayback)typeof(EditorForm).GetField("playbackBuffer", Private)!.GetValue(form)!;
                Stop(); Application.DoEvents();
                Check(Output() == null && tracker.Active == 0 && tracker.Created[^1].Disposed && !timer.Enabled && !play.IsPlaying && failingBuffer.Read(new float[2], 0, 2) == 0,
                    "device disposal failure still closes the render queue and transport state");

                Factory(() => new FakePlayer(tracker));
                Start(); var current = tracker.Created[^1];
                current.Complete(new InvalidOperationException("Synthetic device error")); Application.DoEvents();
                Check(Output() == null && tracker.Active == 0 && current.Disposed, "current device error cleans up its output");
                Check(tracker.Created.All(player => player.Disposed), "all fake outputs are disposed after the transport scenarios");
                Console.WriteLine($"{checks} transport checks passed.");
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("Transport fixture deadline exceeded.");
        if (failure != null) throw new Exception("Transport fixture failed", failure);
    }

    sealed class PlayerTracker
    {
        public List<FakePlayer> Created { get; } = [];
        public int Active, MaxActive;
    }
    sealed class FakePlayer : IWavePlayer
    {
        readonly PlayerTracker tracker;
        bool active;
        public bool Disposed { get; private set; }
        public bool FailInit { get; init; }
        public bool FailPlay { get; init; }
        public bool FailStop { get; init; }
        public bool FailDispose { get; init; }
        public Action? OnPlay { get; init; }
        public float Volume { get; set; } = 1;
        public PlaybackState PlaybackState { get; private set; }
        public WaveFormat OutputWaveFormat { get; private set; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public FakePlayer(PlayerTracker tracker) { this.tracker = tracker; tracker.Created.Add(this); }
        public void Init(IWaveProvider provider)
        { if (FailInit) throw new InvalidOperationException("Synthetic init failure"); OutputWaveFormat = provider.WaveFormat; }
        public void Play()
        {
            if (Disposed) throw new ObjectDisposedException(nameof(FakePlayer));
            if (!active) { active = true; tracker.Active++; tracker.MaxActive = Math.Max(tracker.MaxActive, tracker.Active); }
            PlaybackState = PlaybackState.Playing;
            OnPlay?.Invoke();
            if (FailPlay) throw new InvalidOperationException("Synthetic play failure");
        }
        public void Pause() { Deactivate(); PlaybackState = PlaybackState.Paused; }
        public void Stop()
        {
            if (FailStop) throw new InvalidOperationException("Synthetic stop failure");
            Deactivate(); PlaybackState = PlaybackState.Stopped; RaiseStopped();
        }
        public void RaiseStopped() => PlaybackStopped?.Invoke(this, new StoppedEventArgs());
        public void Complete(Exception? error = null)
        { Deactivate(); PlaybackState = PlaybackState.Stopped; PlaybackStopped?.Invoke(this, new StoppedEventArgs(error)); }
        void Deactivate() { if (active) { active = false; tracker.Active--; } }
        public void Dispose()
        {
            Deactivate(); Disposed = true; PlaybackState = PlaybackState.Stopped;
            if (FailDispose) throw new InvalidOperationException("Synthetic disposal failure");
        }
    }
}
