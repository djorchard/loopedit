using System.Collections;
using System.Reflection;
using NAudio.Wave;
using OrchardSound;

public static class MultiEffectChecks
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static async Task Run()
    {
        int checks = 0;
        void Check(bool condition, string name)
        { if (!condition) throw new Exception("FAILED MULTI EFFECT: " + name); checks++; Console.WriteLine("PASS MULTI EFFECT " + name); }
        static AudioTrack Track(params float[] frames)
        {
            var samples = frames.SelectMany(value => new[] { value, -value / 2 }).ToArray();
            return new() { Path = "Effect chain fixture.wav", Samples = samples, Peaks = new PeakPyramid(samples) };
        }
        static float[] Audio(AudioTrack track) => Editing.RenderSelection(track, 0, track.Frames);
        static float[] AudioResult(EffectPreviewResult result) => Audio(result.PlaybackTracks[0].Source);
        static bool Near(float[] first, float[] second) => first.Length == second.Length && first.Zip(second).All(pair => Math.Abs(pair.First - pair.Second) < .00001f);
        var source = Track(.1f, .2f, .3f, .4f); var initial = Audio(source);
        var fade = new EffectSettings(EffectKind.FadeIn); var reverse = new EffectSettings(EffectKind.Reverse);
        var openingOrder = await EffectChain.PrepareAsync([source], [source], 0, 4, [fade, reverse]);
        var oppositeOrder = await EffectChain.PrepareAsync([source], [source], 0, 4, [reverse, fade]);
        Check(Near(AudioResult(openingOrder), AudioEffects.Process(AudioEffects.Process(initial, 48000, fade), 48000, reverse)), "preview stages process audio in opening order");
        Check(!Near(AudioResult(openingOrder), AudioResult(oppositeOrder)), "noncommuting effects retain their distinct chain order");
        Check(source.Patches.Length == 0 && source.Fades.Count == 0 && Audio(source).SequenceEqual(initial) && openingOrder.Changes.Length == 0, "combined audition never commits any stage or mutates the source");
        var duplicate = await EffectChain.PrepareAsync([source], [source], 0, 4, [reverse, reverse]);
        Check(Near(AudioResult(duplicate), initial), "two instances of the same effect both contribute to preview");
        var other = Track(.05f, .05f, .05f, .05f); other.Enabled = false;
        var targeted = await EffectChain.PrepareAsync([source, other], [source], 0, 4, [fade, reverse]);
        Check(!targeted.PlaybackTracks[1].Enabled && targeted.PlaybackTracks[1].Source.Patches.Length == 0 && Audio(targeted.PlaybackTracks[1].Source).SequenceEqual(Audio(other)), "untargeted muted rows retain audio and tick state through a chain");
        var snapshot = source.Snapshot();
        var frozenTask = EffectChain.PrepareAsync([source], [source], 0, 4, [fade, reverse]);
        source.Offset = 50; source.Gain = 0; source.Segments = [];
        var frozen = await frozenTask;
        Check(frozen.PlaybackTracks[0].Offset == 0 && frozen.PlaybackTracks[0].Gain == 1 && Near(AudioResult(frozen), AudioResult(openingOrder)), "chain input is frozen synchronously before source changes can race its workers");
        snapshot.ApplyToSource();
        var one = Track(.8f);
        var tails = await EffectChain.PrepareAsync([one], [one], 0, 1,
            [new(EffectKind.Delay) { DelaySeconds = .001, Feedback = 0, Wet = .5f, CutTail = false }, new(EffectKind.Maximize) { TargetPeak = 1, SelectionOnly = false }]);
        Check(tails.PlaybackTracks[0].Frames == 49 && tails.AuditionEnd == 49 && AudioResult(tails).Max() == 1 && one.Frames == 1, "later whole-track stages process earlier virtual tails and retain their audition bounds");
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel(); bool rejected = false;
            try { await EffectChain.PrepareAsync([source], [source], 0, 4, [reverse], cancel.Token); } catch (OperationCanceledException) { rejected = true; }
            Check(rejected && Audio(source).SequenceEqual(initial), "cancelled chains cannot commit source audio");
        }
        var repeated = await EffectChain.PrepareAsync([source], [source], 0, 4,
            [new(EffectKind.Multiply) { Multiply = new MultiplySettings { CopyCount = 2 } }, reverse]);
        var repeatedExpected = AudioEffects.Process(initial.Select(value => value * 2).ToArray(), 48000, reverse);
        Check(repeated.SelectionEnd == null && repeated.AuditionEnd == null && Near(AudioResult(repeated), repeatedExpected) && source.Frames == 4,
            "simultaneous Multiply voices retain the scope processed by later preview stages");
        var shiftedTail = await EffectChain.PrepareAsync([one], [one], 0, 1,
            [new(EffectKind.Delay) { DelaySeconds = .001, Feedback = 0, Wet = .5f, CutTail = false },
                new(EffectKind.Multiply) { Multiply = new MultiplySettings { CopyCount = 2 } }, new(EffectKind.FadeOut)]);
        var shiftedAudio = AudioResult(shiftedTail);
        Check(shiftedTail.SelectionEnd == null && shiftedTail.AuditionEnd == 49 && shiftedTail.PlaybackTracks[0].Frames == 49
            && Math.Abs(shiftedAudio[48 * 2] - .4f) < .00001 && shiftedAudio[47 * 2] == 0 && shiftedAudio[0] == 0,
            "Multiply leaves earlier effect tails at their original positions while later selection effects keep the original range");
        Check(shiftedTail.PlaybackTracks.Sum(state => state.Source.Samples.LongLength * sizeof(float) + state.Patches.Sum(patch => patch.Samples.LongLength * sizeof(float))) <= AudioFiles.MaxSessionBytes,
            "duration-changing chains retain the combined source and patch session budget");

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new EditorForm([]); _ = form.Handle;
                var canvas = (WaveCanvas)typeof(EditorForm).GetField("canvas", Private)!.GetValue(form)!;
                var track = Track(.1f, .2f, .3f, .4f); canvas.Slots[0].Track = track; canvas.SelectionStart = 0; canvas.SelectionEnd = 4;
                var tracker = new PlayerTracker(); int presented = 0;
                typeof(EditorForm).GetField("outputFactory", Private)!.SetValue(form, (Func<IWavePlayer>)(() => new SilentPlayer(tracker)));
                typeof(EditorForm).GetField("effectWindowPresenter", Private)!.SetValue(form, (Action<EffectDialog, EditorForm>)((_, _) => presented++));
                object? Field(string name) => typeof(EditorForm).GetField(name, Private)!.GetValue(form);
                object? Call(string name, params object?[] arguments) => typeof(EditorForm).GetMethod(name, Private)!.Invoke(form, arguments.Length == 0 ? null : arguments);
                EffectDialog[] Windows() => ((IList)Field("effectTools")!).Cast<object>().Select(state => (EffectDialog)state.GetType().GetProperty("Window")!.GetValue(state)!).ToArray();
                void PumpUntil(Func<bool> complete)
                {
                    var deadline = System.Diagnostics.Stopwatch.StartNew();
                    while (!complete())
                    {
                        Application.DoEvents();
                        if (deadline.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Effect chain UI deadline exceeded.");
                        Thread.Sleep(1);
                    }
                }
                void Await(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
                void Refresh() => Await((Task)Call("RequestEffectPreview")!);
                EffectDialog Open(EffectKind kind) { Call("OpenEffectTool", kind); return (EffectDialog)Field("effectWindow")!; }
                IEnumerable<Control> All(Control parent) => parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(All(child)));
                void Click(EffectDialog window, string name)
                { var button = All(window).OfType<Button>().Single(control => control.AccessibleName == name); typeof(Control).GetMethod("OnClick", Private)!.Invoke(button, [EventArgs.Empty]); }
                void Apply(EffectDialog window) { Click(window, "Apply effect"); PumpUntil(() => window.IsDisposed && !(bool)Field("busy")!); RefreshIfOpen(); }
                void RefreshIfOpen() { if (Windows().Length > 0) Refresh(); }
                EffectPreviewResult Preview() => (EffectPreviewResult)Field("effectPreview")!;

                var fadeWindow = Open(EffectKind.FadeIn); var reverseWindow = Open(EffectKind.Reverse); Refresh();
                Check(presented == 2 && Windows().Length == 2 && !fadeWindow.IsDisposed && !reverseWindow.IsDisposed, "opening another effect keeps each modeless tool instance alive without showing the fixture");
                Check(Near(AudioResult(Preview()), AudioResult(openingOrder)) && track.Patches.Length == 0, "all open windows contribute in opening order to a non-destructive preview");
                var initialOutput = Field("output"); Refresh();
                Check(ReferenceEquals(initialOutput, Field("output")) && tracker.Active == 1 && tracker.MaxActive == 1, "chain parameter refreshes replace one mix inside a single output owner");
                Apply(reverseWindow);
                var reversed = AudioEffects.Process(initial, 48000, reverse);
                Check(Near(Audio(track), reversed) && Windows().SequenceEqual(new[] { fadeWindow }) && !fadeWindow.IsDisposed && fadeWindow.Enabled, "Apply commits only that window directly against base audio and keeps other tools open");
                Check(Near(AudioResult(Preview()), AudioResult(oppositeOrder)), "remaining windows rebuild over the new base after a later effect is applied first");
                Call("Undo"); Refresh();
                Check(Near(Audio(track), initial) && Windows().SequenceEqual(new[] { fadeWindow }), "one Undo restores the one committed effect while preserving uncommitted windows");
                Call("Redo"); Refresh();
                Check(Near(Audio(track), reversed), "Redo restores only the effect that Apply committed");
                Click(fadeWindow, "Cancel effect");
                Check(Windows().Length == 0 && Field("effectWindow") == null && tracker.Active == 0 && Near(Audio(track), reversed), "Cancel closes only its instance without committing it and restores the original stopped state");
                Call("Undo");

                var earlier = Open(EffectKind.Reverse); var active = Open(EffectKind.FadeIn); Refresh();
                Apply(earlier);
                Check(earlier.IsDisposed && !active.IsDisposed && Field("effectWindow") == active && Near(Audio(track), reversed), "Apply on an earlier inactive window commits that instance rather than the active window");
                Click(active, "Cancel effect"); Call("Undo");
                var sameA = Open(EffectKind.Reverse); var sameB = Open(EffectKind.Reverse); Refresh();
                Check(sameA != sameB && Windows().Length == 2 && Near(AudioResult(Preview()), initial), "multiple windows of the same effect remain independent preview stages");
                Click(sameA, "Cancel effect"); Refresh();
                Check(!sameB.IsDisposed && Windows().Length == 1 && Near(AudioResult(Preview()), reversed) && Near(Audio(track), initial), "cancelling one duplicate effect rebuilds the surviving instance without changing source audio");
                Call("CloseEffectTool", false);

                var enabled = Open(EffectKind.FadeIn); var disabled = Open(EffectKind.Reverse); Refresh();
                All(disabled).OfType<CheckBox>().Single(control => control.AccessibleName == "Live preview").Checked = false; Refresh();
                var faded = AudioEffects.Process(initial, 48000, fade);
                Check((bool)typeof(EditorForm).GetProperty("EffectPreviewActive", Private)!.GetValue(form)! && Near(AudioResult(Preview()), faded), "an active window with preview disabled does not suppress other enabled stages");
                Call("StartPlayback", (object?)0L); var heard = new float[initial.Length]; ((BufferedPlayback)Field("playbackBuffer")!).Read(heard, 0, heard.Length);
                Check(Near(heard, faded), "normal playback uses the enabled chain even when the active tool is bypassed");
                Call("CloseEffectTool", false);

                var maximize = Open(EffectKind.Maximize); var cancelled = Open(EffectKind.Reverse);
                var peak = All(maximize).OfType<NumericUpDown>().Single(control => control.AccessibleName == "Target peak %");
                peak.Value = 50; peak.Value = 100; peak.Value = 25;
                Click(cancelled, "Cancel effect"); Refresh();
                Check(Windows().SequenceEqual(new[] { maximize }) && Math.Abs(AudioResult(Preview()).Max() - .25f) < .00001 && Near(Audio(track), initial), "rapid edits and a closed window publish only the latest surviving chain generation");
                peak.Value = 50; Call("CloseEffectTool", false); Await(Task.Delay(180));
                Check(maximize.IsDisposed && Windows().Length == 0 && Field("effectPreview") == null && tracker.Active == 0, "close-all cancels pending work and releases every effect window and output");

                Call("StartPlayback", (object?)0L); var resume = Open(EffectKind.Reverse); Refresh(); Call("CloseEffectTool", true);
                Check(resume.IsDisposed && Field("effectWindow") == null && tracker.Active == 1 && Near(Audio(track), initial), "closing all previews restores a previously playing normal source without hidden commits");
                Call("StopPlayback");
                Check(tracker.MaxActive == 1 && tracker.Created.All(player => player.Disposed), "every multi-window scenario retains and ultimately disposes one output owner");
                var multiply = Open(EffectKind.Multiply); var followingFade = Open(EffectKind.FadeIn); Refresh(); Apply(multiply);
                Check(track.Frames == 4 && canvas.SelectionStart == 0 && canvas.SelectionEnd == 4 && Near(Audio(track), initial.Select(value => value * 4).ToArray())
                    && Windows().SequenceEqual(new[] { followingFade }), "applying Multiply commits simultaneous voices and preserves other windows on the same range");
                Check(canvas.CuePoints.Count == 0, "Multiply Apply leaves cue points unchanged because it inserts no time");
                Call("Undo"); Refresh();
                Check(track.Frames == 4 && canvas.SelectionEnd == 4 && canvas.CuePoints.Count == 0 && Windows().SequenceEqual(new[] { followingFade }),
                    "one Undo restores the stacked audio while retaining the selection, cue points and other previews");
                Call("Redo"); Refresh();
                Check(track.Frames == 4 && canvas.SelectionEnd == 4 && canvas.CuePoints.Count == 0 && Near(Audio(track), initial.Select(value => value * 4).ToArray()), "Redo restores stacked voices without changing timeline duration or cues");
                Call("CloseEffectTool", false);
                var closingA = Open(EffectKind.Reverse); var closingB = Open(EffectKind.Delay);
                form.Dispose(); Await(Task.Delay(180));
                Check(closingA.IsDisposed && closingB.IsDisposed && tracker.Active == 0, "editor disposal closes all modeless effect instances and pending preview jobs");
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(45))) throw new TimeoutException("Multi-effect fixture deadline exceeded.");
        if (failure != null) throw new Exception("Multi-effect fixture failed", failure);
        Console.WriteLine($"{checks} multi-effect checks passed.");
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
