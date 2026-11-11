using System.Collections;
using System.Reflection;
using NAudio.Wave;
using OrchardSound;

public static class LoopRefinementChecks
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                int checks = 0;
                void Check(bool value, string name) { if (!value) throw new Exception("FAILED REFINEMENT: " + name); checks++; Console.WriteLine("PASS REFINEMENT " + name); }
                static AudioTrack Track(int frames, float level = .2f)
                {
                    var samples = Enumerable.Repeat(level, frames * 2).ToArray();
                    return new() { Path = "Synthetic refinement.wav", Samples = samples, Peaks = new(samples) };
                }
                static void Wait(BufferedPlayback buffer, int samples)
                { if (!SpinWait.SpinUntil(() => buffer.BufferedSamples >= samples, TimeSpan.FromSeconds(2))) throw new TimeoutException("Refinement producer did not refill."); }
                static void Consume(BufferedPlayback buffer, int frames)
                {
                    var samples = new float[512];
                    while (frames > 0) { int next = Math.Min(frames, samples.Length / 2); Wait(buffer, next * 2); if (buffer.Read(samples, 0, next * 2) != next * 2) throw new Exception("Unexpected buffer EOF."); frames -= next; }
                }
                static object? Field(EditorForm form, string name) => typeof(EditorForm).GetField(name, Private)!.GetValue(form);
                static void Set(EditorForm form, string name, object? value) => typeof(EditorForm).GetField(name, Private)!.SetValue(form, value);
                static void Call(EditorForm form, string name) => typeof(EditorForm).GetMethod(name, Private)!.Invoke(form, null);
                static int StackCount(EditorForm form, string name) => ((ICollection)Field(form, name)!).Count;
                static void ClearStack(EditorForm form, string name) { var stack = Field(form, name)!; stack.GetType().GetMethod("Clear")!.Invoke(stack, null); }

                var shortTrack = Track(20);
                using (var loop = new BufferedPlayback(new([shortTrack.Snapshot()], 3, 8, true, 6), 20))
                {
                    Check(loop.TryGetAudiblePosition(0, out long initial) && initial == 6, "device frame zero maps to the requested initial audio position");
                    loop.Read(new float[40], 0, 40);
                    bool exact = true;
                    for (long frame = 0; frame <= 20; frame++)
                    {
                        long next = 6 + frame;
                        if (next > 8) { long wrapped = (next - 3) % 5; next = wrapped == 0 ? 8 : 3 + wrapped; }
                        exact &= loop.TryGetAudiblePosition(frame, out long actual) && actual == next;
                    }
                    Check(exact, "device history maps every sample boundary across repeated short-loop wraps");
                    Check(!loop.TryGetAudiblePosition(-1, out _) && !loop.TryGetAudiblePosition(21, out _), "negative and unsubmitted device frame counters are rejected");
                }
                using (var finite = new BufferedPlayback(new([shortTrack.Snapshot()], 3, 8, false, 6), 20))
                {
                    int read = finite.Read(new float[20], 0, 20);
                    Check(read == 4 && finite.TryGetAudiblePosition(2, out long end) && end == 8 && !finite.TryGetAudiblePosition(3, out _), "one-shot EOF maps only frames actually returned to the output");
                }
                var replacementTrack = Track(5000, .7f);
                using (var changing = new BufferedPlayback(new([Track(5000).Snapshot()], 100, 1000, true, 900), 20))
                {
                    changing.Replace(new([replacementTrack.Snapshot()], 3000, 4000, true), crossfade: false);
                    var sample = new float[2]; bool changed = false, oldRetained = true; long count = 0;
                    for (; count < changing.CapacitySamples + 2048;)
                    {
                        Wait(changing, 2); changing.Read(sample, 0, 2); count++;
                        if (sample[0] < .6f) continue;
                        changed = changing.TryGetAudiblePosition(count, out long firstNew) && firstNew == 3001;
                        oldRetained = changing.TryGetAudiblePosition(1, out long firstOld) && firstOld == 901
                            && changing.TryGetAudiblePosition(count - 1, out long lastOld) && lastOld >= 100 && lastOld <= 1000;
                        break;
                    }
                    Check(changed && oldRetained, "replacement history retains queued old positions and begins the new loop at its exact first frame");
                }
                using (var silence = new BufferedPlayback(new([shortTrack.Snapshot()], 0, 4, true), 20))
                {
                    int realFrames = silence.CapacitySamples / 2, returnedFrames = realFrames + 8;
                    var output = new float[returnedFrames * 2]; silence.Read(output, 0, output.Length);
                    Check(silence.TryGetAudiblePosition(realFrames, out long beforeSilence)
                        && silence.TryGetAudiblePosition(returnedFrames, out long afterSilence) && beforeSilence == afterSilence
                        && output.Skip(realFrames * 2).All(v => v == 0), "silent underrun output frames hold the last real audio position");
                    Wait(silence, 2); silence.Read(new float[2], 0, 2);
                    long resumed = beforeSilence == 4 ? 1 : beforeSilence + 1;
                    Check(silence.TryGetAudiblePosition(returnedFrames + 1, out long actual) && actual == resumed,
                        "audio after an underrun advances from the held source position without counting silence twice");
                }
                using (var history = new BufferedPlayback(new([shortTrack.Snapshot()], 0, 4, true), 20))
                {
                    int frames = AudioTrack.Rate * 5; history.Read(new float[frames * 2], 0, frames * 2);
                    Check(!history.TryGetAudiblePosition(0, out _) && history.TryGetAudiblePosition(frames, out _), "audible position history expires old counters while retaining the current output frame");
                }

                using (var form = new EditorForm([]))
                {
                    var canvas = (WaveCanvas)Field(form, "canvas")!; canvas.Slots[0].Track = Track(20000);
                    var option = (CheckBox)Field(form, "refineEnd")!;
                    canvas.SelectionStart = 100; canvas.SelectionEnd = 8000; canvas.CursorFrame = 4000;
                    Call(form, "RefineLoopEndOnRestart");
                    Check(!option.Checked && canvas.SelectionEnd == 8000 && StackCount(form, "undo") == 0, "loop refinement starts off and leaves the end and Undo unchanged");
                    option.Checked = true; canvas.SelectionEnd = null; canvas.CursorFrame = 5000;
                    int selectionEvents = 0; canvas.SelectionChanged += () => selectionEvents++;
                    Call(form, "RefineLoopEndOnRestart");
                    Check(canvas.SelectionEnd == 5000 && StackCount(form, "undo") == 1 && selectionEvents == 0 && Field(form, "output") == null,
                        "the first tap sets the end once without selection recursion or automatic playback");
                    Call(form, "Undo");
                    Check(canvas.SelectionEnd == null && canvas.SelectionStart == 100 && StackCount(form, "undo") == 0 && StackCount(form, "redo") == 1,
                        "one Undo restores the selection from before the refinement tap");
                    canvas.SelectionEnd = 8000; canvas.CursorFrame = 4000; ClearStack(form, "undo");
                    Call(form, "RefineLoopEndOnRestart");
                    Check(canvas.SelectionEnd == 7000 && StackCount(form, "undo") == 1, "later taps retain 75 percent of the old end and add 25 percent of the new tap");
                    canvas.SelectionStart = 0; canvas.SelectionEnd = 101; canvas.CursorFrame = 104;
                    Call(form, "RefineLoopEndOnRestart"); Check(canvas.SelectionEnd == 102, "weighted ends round to the nearest whole sample frame");
                    canvas.SelectionStart = 5000; canvas.SelectionEnd = 6000; canvas.CursorFrame = 0;
                    Call(form, "RefineLoopEndOnRestart"); Check(canvas.SelectionEnd == 5001, "refined ends cannot cross the selection start");
                    canvas.SelectionStart = null; canvas.SelectionEnd = null; canvas.CursorFrame = 30000;
                    Call(form, "RefineLoopEndOnRestart"); Check(canvas.SelectionEnd == 20000, "a first tap cannot extend beyond the available timeline");
                    canvas.Slots[0].Track!.Offset = -2000; canvas.SelectionEnd = null; canvas.CursorFrame = -3000;
                    Call(form, "RefineLoopEndOnRestart"); Check(canvas.SelectionEnd == -1999, "an unset start uses the negative timeline minimum when clamping the end");
                }
                using (var form = new EditorForm([]))
                {
                    var canvas = (WaveCanvas)Field(form, "canvas")!; var track = Track(20000); canvas.Slots[0].Track = track;
                    ((CheckBox)Field(form, "refineEnd")!).Checked = true;
                    var mix = new MixProvider([track.Snapshot()], 100, 9000, true, 100);
                    var buffer = new BufferedPlayback(mix, 20); Consume(buffer, 3000);
                    var clock = new ClockPlayer { Bytes = 1000 * 4, OutputWaveFormat = new WaveFormat(AudioTrack.Rate, 16, 2) };
                    Set(form, "playing", mix); Set(form, "playbackBuffer", buffer); Set(form, "output", clock);
                    canvas.SelectionStart = 100; canvas.SelectionEnd = 9000; canvas.CursorFrame = 6000;
                    Call(form, "RefineLoopEndOnRestart");
                    Check(canvas.SelectionEnd == 7025 && buffer.Position == 3100,
                        "native device bytes use the actual output BlockAlign and audible history rather than callback-ahead or cursor position");
                    canvas.SelectionEnd = 9000; clock.Bytes = 999999 * 4;
                    Call(form, "RefineLoopEndOnRestart"); Check(canvas.SelectionEnd == 8250, "an unavailable history mapping uses the cursor rather than guessing device latency");
                    canvas.SelectionEnd = 9000; clock.FailPosition = true;
                    Call(form, "RefineLoopEndOnRestart"); Check(canvas.SelectionEnd == 7525, "a device that cannot report position falls back to callback consumption");
                    canvas.SelectionEnd = 9000; clock.FailPosition = false; clock.Bytes = 1000 * 4; Set(form, "scrubbing", true);
                    Call(form, "RefineLoopEndOnRestart"); Check(canvas.SelectionEnd == 8250, "scrub playback is not mistaken for the normal audible loop playhead");
                    Set(form, "scrubbing", false); canvas.SelectionEnd = 9000; ClearStack(form, "undo");
                    Set(form, "outputFactory", new Func<IWavePlayer>(() => new SilentPlayer()));
                    Call(form, "RestartLoop");
                    Check(canvas.SelectionEnd == 7025 && clock.Disposed && StackCount(form, "undo") == 1
                        && Field(form, "output") is SilentPlayer && ((BufferedPlayback)Field(form, "playbackBuffer")!).Position == 100,
                        "restart refines from the old audible playhead before disposing it and starts one new loop at its beginning");
                }
                using (var form = new EditorForm([]))
                {
                    var canvas = (WaveCanvas)Field(form, "canvas")!; var track = Track(20000); canvas.Slots[0].Track = track;
                    ((CheckBox)Field(form, "refineEnd")!).Checked = true;
                    var mix = new MixProvider([track.Snapshot()], 100, 9000, true, 100);
                    var buffer = new BufferedPlayback(mix, 20); Consume(buffer, 1000);
                    Set(form, "playing", mix); Set(form, "playbackBuffer", buffer); Set(form, "output", new SilentPlayer());
                    canvas.SelectionStart = 100; canvas.SelectionEnd = null; canvas.CursorFrame = 6000;
                    Call(form, "RefineLoopEndOnRestart");
                    Check(canvas.SelectionEnd == 1100, "outputs without IWavePosition use consumed callback frames for their first tap");
                }
                Console.WriteLine($"{checks} loop refinement checks passed. No audio hardware was opened.");
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(45))) throw new TimeoutException("Loop refinement fixture deadline exceeded.");
        if (failure != null) throw new Exception("Loop refinement fixture failed", failure);
    }

    class SilentPlayer : IWavePlayer
    {
        public bool Disposed { get; private set; }
        public float Volume { get; set; } = 1;
        public PlaybackState PlaybackState { get; private set; }
        public WaveFormat OutputWaveFormat { get; set; } = WaveFormat.CreateIeeeFloatWaveFormat(AudioTrack.Rate, 2);
        public event EventHandler<StoppedEventArgs>? PlaybackStopped { add { } remove { } }
        public void Init(IWaveProvider provider) { }
        public void Play() => PlaybackState = PlaybackState.Playing;
        public void Pause() => PlaybackState = PlaybackState.Paused;
        public void Stop() => PlaybackState = PlaybackState.Stopped;
        public void Dispose() { Disposed = true; PlaybackState = PlaybackState.Stopped; }
    }
    sealed class ClockPlayer : SilentPlayer, IWavePosition
    {
        public long Bytes { get; set; }
        public bool FailPosition { get; set; }
        public long GetPosition() => FailPosition ? throw new InvalidOperationException("Synthetic position failure") : Bytes;
    }
}
