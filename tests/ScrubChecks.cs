using System.Reflection;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using OrchardSound;

public static class ScrubChecks
{
    static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run()
    {
        int checks = 0;
        void Check(bool condition, string name)
        { if (!condition) throw new Exception(name); checks++; Console.WriteLine("PASS SCRUB " + name); }
        bool Near(float a, float b) => Math.Abs(a - b) < .00001f;
        void Ready(ScrubProvider source, long version)
        { if (!SpinWait.SpinUntil(() => source.RenderedVersion == version, TimeSpan.FromSeconds(2))) throw new TimeoutException("Scrub render deadline exceeded."); }
        var track = Fixture();
        float[] grain = new float[ScrubProvider.GrainFrames * 2];
        using (var source = new ScrubProvider())
        {
            Array.Fill(grain, 7);
            Check(source.Read(grain, 0, grain.Length) == grain.Length && grain.All(value => value == 0), "scrub is silent before the first cursor movement");
            Check(source.MoveTo([track.Snapshot()], 1000), "a changed cursor schedules a grain"); Ready(source, 1);
            Check(source.BufferedSamples <= grain.Length, "prepared scrub audio stays within one bounded grain");
            source.Read(grain, 0, grain.Length);
            Check(source.Direction == 1 && Near(grain[1000], track.Samples[3000]) && Near(grain[1001], track.Samples[3001]), "forward scrubbing plays stereo audio from the requested position");
            Check(Math.Abs(grain[0]) < Math.Abs(track.Samples[2000]) / 100 && grain[^1] == 0 && grain[^2] == 0, "grain edges are smoothed and end in silence");
            Check(source.Position == 1000 + ScrubProvider.GrainFrames - 1, "scrub position follows consumed forward frames");
            Check(!source.MoveTo([track.Snapshot()], 1000) && source.RenderedVersion == 1, "stationary cursor updates do not replay audio");
            source.Read(grain, 0, grain.Length);
            Check(grain.All(value => value == 0), "a finished grain does not loop or repeat stale samples");

            source.MoveTo([track.Snapshot()], 900); Ready(source, 2); source.Read(grain, 0, grain.Length);
            Check(source.Direction == -1 && Near(grain[600], track.Samples[1200]) && Near(grain[601], track.Samples[1201]), "backward scrubbing reverses frames while preserving left and right channels");
            Check(source.Position == 0 && grain.Skip(901 * 2).All(value => value == 0), "reverse audio stops cleanly at the beginning of the track");

            track.Gain = .5f; source.MoveTo([track.Snapshot()], 2000); Ready(source, 3); source.Read(grain, 0, grain.Length);
            Check(Near(grain[1000], track.Samples[5000] * .5f) && Near(grain[1001], track.Samples[5001] * .5f), "updated source gain is used by the next cursor grain");
            track.Enabled = false; source.MoveTo([track.Snapshot()], 3000); Ready(source, 4); source.Read(grain, 0, grain.Length);
            Check(grain.All(value => value == 0), "disabled tracks are excluded from scrub audio");

            track.Enabled = true; track.Gain = 1;
            for (int i = 0; i < 100; i++) source.MoveTo([track.Snapshot()], (i % 2 == 0 ? 4000 : 2000) + i);
            Ready(source, 104); source.Read(grain, 0, grain.Length);
            Check(source.Direction == -1 && Near(grain[1000], track.Samples[(2099 - 500) * 2]), "rapid direction changes render the latest target without an old-audio backlog");

            source.MoveTo([track.Snapshot()], 20000); Ready(source, 105); source.Read(grain, 0, grain.Length);
            Check(grain.All(value => value == 0), "cursor positions beyond available audio are silent");
            source.Read(grain, 0, grain.Length);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) source.Read(grain, 0, grain.Length);
            Check(GC.GetAllocatedBytesForCurrentThread() == before, "device scrub reads allocate no managed memory");
            source.Dispose(); source.Dispose();
            Check(source.Read(grain, 0, grain.Length) == 0, "scrub disposal is idempotent and closes device reads");
            bool rejected = false;
            try { source.MoveTo([track.Snapshot()], 1); } catch (ObjectDisposedException) { rejected = true; }
            Check(rejected, "disposed scrub workers reject new movement");
        }

        using (var source = new ScrubProvider())
        {
            track.Offset = 100; track.Gain = .25f; track.Fades = [new Fade(0, 4000, true)];
            var state = track.Snapshot(); source.MoveTo([state], 1100); Ready(source, 1); source.Read(grain, 0, grain.Length);
            var expected = new float[grain.Length]; new MixProvider([state], 1100, 1100 + ScrubProvider.GrainFrames, false).Read(expected, 0, expected.Length);
            Check(Near(grain[1000], expected[1000]) && Near(grain[1001], expected[1001]), "scrubbing respects edit envelopes and track offsets through the normal mixer");
        }

        Exception? raceFailure = null;
        using (var source = new ScrubProvider())
        {
            source.MoveTo([track.Snapshot()], 2000); Ready(source, 1);
            var reader = Task.Run(() =>
            {
                try { var block = new float[512]; for (int i = 0; i < 10000; i++) if (source.Read(block, 0, block.Length) == 0) break; }
                catch (Exception error) { raceFailure = error; }
            });
            source.Dispose();
            Check(reader.Wait(TimeSpan.FromSeconds(2)) && raceFailure == null, "concurrent device reads and disposal close without callback failures");
        }

        var nativeTrack = Fixture();
        using (var source = new ScrubProvider())
        {
            source.MoveTo([nativeTrack.Snapshot()], 1000); Ready(source, 1);
            var converter = new SampleToWaveProvider(source);
            const int byteOffset = 16;
            int byteCount = (ScrubProvider.GrainFrames * 2 + 32) * sizeof(float);
            var bytes = Enumerable.Repeat((byte)0x7f, byteCount + byteOffset + 16).ToArray();
            int read = converter.Read(bytes, byteOffset, byteCount);
            Check(read == byteCount && Near(BitConverter.ToSingle(bytes, byteOffset + 1000 * sizeof(float)), nativeTrack.Samples[3000])
                && Near(BitConverter.ToSingle(bytes, byteOffset + 1001 * sizeof(float)), nativeTrack.Samples[3001]),
                "real NAudio byte conversion plays forward stereo grains through its aliased float buffer");
            Check(bytes.Take(byteOffset).Concat(bytes.Skip(byteOffset + byteCount)).All(value => value == 0x7f)
                && bytes.Skip(byteOffset + ScrubProvider.GrainFrames * 2 * sizeof(float)).Take(32 * sizeof(float)).All(value => value == 0),
                "byte conversion preserves offset sentinels and clears the complete grain underflow");
            source.MoveTo([nativeTrack.Snapshot()], 900); Ready(source, 2);
            read = converter.Read(bytes, byteOffset, byteCount);
            Check(read == byteCount && Near(BitConverter.ToSingle(bytes, byteOffset + 600 * sizeof(float)), nativeTrack.Samples[1200])
                && Near(BitConverter.ToSingle(bytes, byteOffset + 601 * sizeof(float)), nativeTrack.Samples[1201])
                && bytes.Skip(byteOffset + 901 * 2 * sizeof(float)).Take(byteCount - 901 * 2 * sizeof(float)).All(value => value == 0),
                "real NAudio byte conversion reverses stereo frames and clears stale forward-grain bytes");
        }
        var oneFrame = new AudioTrack { Path = "Converter silence.wav", Samples = [.5f, -.5f], Peaks = new PeakPyramid([.5f, -.5f]) };
        var nativeMix = new SampleToWaveProvider(new MixProvider([oneFrame.Snapshot()], 0, 4, false));
        var mixBytes = new byte[16]; nativeMix.Read(mixBytes, 0, mixBytes.Length); nativeMix.Read(mixBytes, 0, mixBytes.Length);
        Check(mixBytes.All(value => value == 0), "direct mixer byte conversion clears all stereo samples after audio ends");
        using (var source = new BufferedPlayback(new MixProvider([nativeTrack.Snapshot()], 0, nativeTrack.Frames, true), 20))
        {
            var converter = new SampleToWaveProvider(source); int count = (source.CapacitySamples + 16) * sizeof(float);
            var bytes = new byte[count];
            lock (typeof(BufferedPlayback).GetField("gate", Private)!.GetValue(source)!)
            {
                converter.Read(bytes, 0, count); converter.Read(bytes, 0, count);
            }
            Check(bytes.All(value => value == 0), "buffered playback clears every aliased float on a forced device underflow");
        }

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new EditorForm([]); _ = form.Handle;
                var canvas = (WaveCanvas)typeof(EditorForm).GetField("canvas", Private)!.GetValue(form)!;
                canvas.Slots[0].Track = Fixture();
                var toggle = (CheckBox)typeof(EditorForm).GetField("scrub", Private)!.GetValue(form)!;
                var play = (IconButton)typeof(EditorForm).GetField("play", Private)!.GetValue(form)!;
                var tracker = new PlayerTracker();
                void Invoke(string name, params object?[] values) => typeof(EditorForm).GetMethod(name, Private)!.Invoke(form, values.Length == 0 ? null : values);
                object? Output() => typeof(EditorForm).GetField("output", Private)!.GetValue(form);
                bool Scrubbing() => (bool)typeof(EditorForm).GetField("scrubbing", Private)!.GetValue(form)!;
                void Factory(Func<IWavePlayer> factory) => typeof(EditorForm).GetField("outputFactory", Private)!.SetValue(form, factory);
                Factory(() => new FakePlayer(tracker));
                Invoke("UpdateScrub", 1000L);
                Check(tracker.Created.Count == 0, "disabled scrub toggle creates no audio output");
                toggle.Checked = true; Invoke("UpdateScrub", 1000L);
                var first = tracker.Created[^1];
                var firstSource = (ScrubProvider)typeof(EditorForm).GetField("scrubProvider", Private)!.GetValue(form)!;
                Check(tracker.Active == 1 && Scrubbing() && !play.IsPlaying && first.Provider != null, "cursor audition shares the single output owner without activating normal playback");
                for (int i = 0; i < 100; i++) Invoke("UpdateScrub", 1001L + i);
                Check(tracker.Created.Count == 1 && tracker.Active == 1 && tracker.MaxActive == 1 && ReferenceEquals(Output(), first), "100 cursor updates reuse one scrub device");
                long lastMovement = (long)typeof(EditorForm).GetField("lastScrubMovement", Private)!.GetValue(form)!;
                Invoke("UpdateScrub", 1100L);
                Check((long)typeof(EditorForm).GetField("lastScrubMovement", Private)!.GetValue(form)! == lastMovement, "unchanged cursor coordinates do not prolong an audition");
                typeof(EditorForm).GetField("lastScrubMovement", Private)!.SetValue(form, Environment.TickCount64 - 200);
                var idle = (System.Windows.Forms.Timer)typeof(EditorForm).GetField("scrubTimer", Private)!.GetValue(form)!;
                typeof(System.Windows.Forms.Timer).GetMethod("OnTick", Private)!.Invoke(idle, [EventArgs.Empty]);
                Application.DoEvents();
                Check(Output() == null && !Scrubbing() && tracker.Active == 0 && first.Disposed && firstSource.Read(grain, 0, grain.Length) == 0, "stationary timeout releases the device and its scrub worker");
                Invoke("UpdateScrub", 1100L);
                Check(tracker.Created.Count == 1, "stationary cursor does not restart after idle timeout");
                Invoke("UpdateScrub", 2200L); toggle.Checked = false; Application.DoEvents();
                Check(Output() == null && tracker.Active == 0 && tracker.Created.All(player => player.Disposed), "turning scrub off stops cursor audio through central cleanup");

                toggle.Checked = true; Invoke("StartPlayback", (object?)3000L);
                var normal = tracker.Created[^1];
                Invoke("UpdateScrub", 3100L); Invoke("DisableScrub");
                Check(ReferenceEquals(Output(), normal) && !Scrubbing() && tracker.Active == 1 && !normal.Disposed, "scrub movement and disabling cannot interrupt or duplicate normal playback");
                Invoke("StopPlayback"); Invoke("UpdateScrub", 3200L);
                var old = tracker.Created[^1]; var oldSource = (ScrubProvider)typeof(EditorForm).GetField("scrubProvider", Private)!.GetValue(form)!;
                Invoke("StartPlayback", (object?)3300L); var replacement = tracker.Created[^1];
                old.RaiseStopped(); Application.DoEvents();
                Check(old.Disposed && oldSource.Read(grain, 0, grain.Length) == 0 && ReferenceEquals(Output(), replacement) && tracker.Active == 1 && tracker.MaxActive == 1, "normal playback replaces scrub ownership and ignores stale scrub completion");
                Invoke("StopPlayback");
                canvas.Slots[0].Enabled = false; int created = tracker.Created.Count; Invoke("UpdateScrub", 4000L);
                Check(Output() == null && tracker.Created.Count == created, "all excluded tracks create no scrub device");
                canvas.Slots[0].Enabled = true;
                Factory(() => new FakePlayer(tracker) { FailInit = true }); Invoke("UpdateScrub", 4100L); Application.DoEvents();
                Check(Output() == null && !Scrubbing() && tracker.Active == 0 && tracker.Created[^1].Disposed, "scrub initialization failure releases worker and device ownership");
                Factory(() => new FakePlayer(tracker) { FailPlay = true }); Invoke("UpdateScrub", 4200L); Application.DoEvents();
                Check(Output() == null && !Scrubbing() && tracker.Active == 0 && tracker.Created[^1].Disposed, "scrub play failure releases an activated device");
                Factory(() => new FakePlayer(tracker)); Invoke("UpdateScrub", 4300L);
                var closingSource = (ScrubProvider)typeof(EditorForm).GetField("scrubProvider", Private)!.GetValue(form)!;
                form.Dispose();
                Check(tracker.Active == 0 && tracker.Created.All(player => player.Disposed) && closingSource.Read(grain, 0, grain.Length) == 0 && typeof(EditorForm).GetField("scrubTimer", Private)!.GetValue(form) == null, "editor disposal closes scrub timers, workers and the output owner");
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("Scrub transport deadline exceeded.");
        if (failure != null) throw new Exception("Scrub transport failed", failure);
        Console.WriteLine($"{checks} scrub checks passed.");
    }

    static AudioTrack Fixture()
    {
        var samples = new float[12000 * 2];
        for (int i = 0; i < samples.Length / 2; i++) { samples[i * 2] = .01f + i * .00002f; samples[i * 2 + 1] = -.3f - i * .00001f; }
        return new AudioTrack { Path = "Scrub fixture.wav", Samples = samples, Peaks = new PeakPyramid(samples) };
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
        public IWaveProvider? Provider { get; private set; }
        public bool Disposed { get; private set; }
        public bool FailInit { get; init; }
        public bool FailPlay { get; init; }
        public float Volume { get; set; } = 1;
        public PlaybackState PlaybackState { get; private set; }
        public WaveFormat OutputWaveFormat { get; private set; } = WaveFormat.CreateIeeeFloatWaveFormat(AudioTrack.Rate, 2);
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public FakePlayer(PlayerTracker tracker) { this.tracker = tracker; tracker.Created.Add(this); }
        public void Init(IWaveProvider provider)
        { if (FailInit) throw new InvalidOperationException("Synthetic scrub init failure"); Provider = provider; OutputWaveFormat = provider.WaveFormat; }
        public void Play()
        {
            if (!active) { active = true; tracker.Active++; tracker.MaxActive = Math.Max(tracker.MaxActive, tracker.Active); }
            PlaybackState = PlaybackState.Playing;
            if (FailPlay) throw new InvalidOperationException("Synthetic scrub play failure");
        }
        public void Pause() { Deactivate(); PlaybackState = PlaybackState.Paused; }
        public void Stop() { Deactivate(); PlaybackState = PlaybackState.Stopped; RaiseStopped(); }
        public void RaiseStopped() => PlaybackStopped?.Invoke(this, new StoppedEventArgs());
        void Deactivate() { if (active) { active = false; tracker.Active--; } }
        public void Dispose() { Deactivate(); Disposed = true; PlaybackState = PlaybackState.Stopped; }
    }
}
