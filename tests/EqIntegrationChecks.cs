using System.Collections;
using System.Reflection;
using NAudio.Wave;
using OrchardSound;

public static class EqIntegrationChecks
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    const long Start = 5000, End = 17000, Offset = 1000;
    static AudioTrack Track(string name)
    {
        var samples = new float[24000 * 2];
        for (int frame = 0; frame < samples.Length / 2; frame++)
        {
            double phase = 2 * Math.PI * frame / AudioTrack.Rate;
            samples[frame * 2] = (float)(.06 * Math.Sin(500 * phase) + .04 * Math.Sin(2000 * phase));
            samples[frame * 2 + 1] = (float)(.05 * Math.Sin(125 * phase) + .03 * Math.Sin(8000 * phase));
        }
        return new() { Path = name, Samples = samples, Peaks = new PeakPyramid(samples), Offset = Offset };
    }
    static float[] Audio(AudioTrack track) => Editing.RenderSelection(track, 0, track.Frames);
    static bool Near(float[] first, float[] second) => first.Length == second.Length && first.Zip(second).All(pair => Math.Abs(pair.First - pair.Second) < .00001f);
    static float[] Selected(float[] audio) => audio.Skip((int)(Start - Offset) * 2).Take((int)(End - Start) * 2).ToArray();
    static float[] ReplaceSelection(float[] audio, float[] selected)
    { var result = audio.ToArray(); selected.CopyTo(result, (int)(Start - Offset) * 2); return result; }

    public static async Task Run()
    {
        int checks = 0;
        void Check(bool condition, string name)
        { if (!condition) throw new Exception("FAILED EQ INTEGRATION: " + name); checks++; Console.WriteLine("PASS EQ INTEGRATION " + name); }
        var source = Track("EQ scene.wav"); var muted = Track("Unticked EQ scene.wav"); muted.Enabled = false;
        var original = Audio(source); var originalMuted = Audio(muted);
        var firstSettings = new EffectSettings(EffectKind.MultibandEq)
        { AutomationEnabled = true, MultibandEq = new() { Hz500 = new(6, -6), Hz2000 = new(-3, 3) } };
        var secondSettings = new EffectSettings(EffectKind.MultibandEq)
        { AutomationEnabled = false, MultibandEq = new() { Hz500 = new(-3), Hz8000 = new(6) } };
        var single = await EffectPreview.PrepareAsync([source, muted], [source], Start, End, firstSettings);
        var processed = AudioEffects.Process(Selected(original), AudioTrack.Rate, firstSettings);
        var expectedSingle = ReplaceSelection(original, processed);
        Check(single.Changes.Length == 1 && ReferenceEquals(single.Changes[0].Track, source) && single.Changes[0].VisibleStart == Start - Offset
            && single.Changes[0].Samples.Length == (End - Start) * 2 && single.SelectionEnd == null && single.AuditionEnd == null && single.CueFrames == null && single.ReplacementStates == null,
            "EQ prepares ordinary fixed-duration selected samples without timeline replacements or cue metadata");
        Check(!Near(processed, Selected(original)) && Near(Audio(single.PlaybackTracks[0].Source), expectedSingle)
            && source.Offset == Offset && source.Frames == 24000 && source.Patches.Length == 0 && Near(Audio(source), original),
            "EQ changes the selected frequencies while preserving the committed offset duration and original samples");
        Check(!single.PlaybackTracks[1].Enabled && single.PlaybackTracks[1].Offset == Offset && Near(Audio(single.PlaybackTracks[1].Source), originalMuted) && muted.Patches.Length == 0,
            "unticked EQ rows retain their exact audio timing and playback exclusion");
        var chained = await EffectChain.PrepareAsync([source, muted], [source], Start, End, [firstSettings, secondSettings]);
        var combined = AudioEffects.Process(processed, AudioTrack.Rate, secondSettings);
        Check(chained.SelectionEnd == null && chained.AuditionEnd == null && Near(Audio(chained.PlaybackTracks[0].Source), ReplaceSelection(original, combined))
            && !chained.PlaybackTracks[1].Enabled && Near(Audio(chained.PlaybackTracks[1].Source), originalMuted),
            "two EQ stages process the same fixed range in sequence without shifting unticked rows");

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new EditorForm([]); _ = form.Handle;
                object? Field(string name) => typeof(EditorForm).GetField(name, Private)!.GetValue(form);
                object? Call(string name, params object?[] values) => typeof(EditorForm).GetMethod(name, Private)!.Invoke(form, values.Length == 0 ? null : values);
                var canvas = (WaveCanvas)Field("canvas")!;
                var active = Track("EQ scene.wav"); var raw = active.Samples.ToArray();
                var inactive = Track("Unticked EQ scene.wav");
                canvas.Slots[0].Track = active; var inactiveSlot = new ChannelSlot { Track = inactive }; inactiveSlot.Enabled = false; canvas.Slots.Add(inactiveSlot);
                canvas.SelectionStart = Start; canvas.SelectionEnd = End;
                var tracker = new PlayerTracker(); int presented = 0;
                typeof(EditorForm).GetField("outputFactory", Private)!.SetValue(form, (Func<IWavePlayer>)(() => new FakePlayer(tracker)));
                typeof(EditorForm).GetField("effectWindowPresenter", Private)!.SetValue(form, (Action<EffectDialog, EditorForm>)((_, _) => presented++));
                IEnumerable<Control> All(Control parent) => parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(All(child)));
                NumericUpDown Number(EffectDialog window, string name) => All(window).OfType<NumericUpDown>().Single(number => number.AccessibleName == name);
                CheckBox Option(EffectDialog window, string name) => All(window).OfType<CheckBox>().Single(option => option.AccessibleName == name);
                EffectDialog[] Windows() => ((IList)Field("effectTools")!).Cast<object>().Select(state => (EffectDialog)state.GetType().GetProperty("Window")!.GetValue(state)!).ToArray();
                void PumpUntil(Func<bool> complete)
                {
                    var deadline = System.Diagnostics.Stopwatch.StartNew();
                    while (!complete())
                    {
                        Application.DoEvents();
                        if (deadline.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("EQ integration timed out. " + ((Control)Field("status")!).Text);
                        Thread.Sleep(1);
                    }
                }
                void Await(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
                void Refresh() => Await((Task)Call("RequestEffectPreview")!);
                EffectDialog Open() { Call("OpenEffectTool", EffectKind.MultibandEq); return (EffectDialog)Field("effectWindow")!; }
                void Click(EffectDialog window, string name) => typeof(Control).GetMethod("OnClick", Private)!.Invoke(All(window).OfType<Button>().Single(button => button.AccessibleName == name), [EventArgs.Empty]);
                EffectPreviewResult Preview() => (EffectPreviewResult)Field("effectPreview")!;
                void Apply(EffectDialog window)
                { Click(window, "Apply effect"); PumpUntil(() => window.IsDisposed && !(bool)Field("busy")!); if (Windows().Length > 0) Refresh(); }
                long[] Cues() => canvas.CuePoints.Select(cue => cue.Frame).Order().ToArray();
                var selections = (SelectionStore)Field("selections")!;
                selections.Restore([new("Before EQ", 500, 500), new("Inside EQ", 7000, 7000), new("After EQ", 22000, 22000)]);
                Call("SyncCuePoints"); Call("RebuildTrackControls");
                long[] cues = Cues();
                var first = Open(); Number(first, "500 Hz gain dB").Value = 6; Number(first, "2 kHz gain dB").Value = -3;
                Option(first, "Automate effect parameters").Checked = true;
                Number(first, "End 500 Hz gain dB").Value = -6; Number(first, "End 2 kHz gain dB").Value = 3;
                var frozenFirst = first.Settings;
                var second = Open(); Number(second, "500 Hz gain dB").Value = -3; Number(second, "8 kHz gain dB").Value = 6;
                var frozenSecond = second.Settings;
                Refresh();
                Check(presented == 2 && Windows().SequenceEqual(new[] { first, second }) && !ReferenceEquals(first.Settings.MultibandEq, second.Settings.MultibandEq),
                    "multiple hidden EQ windows retain separate parameter records and live tool instances");
                var output = Field("output"); var queue = (BufferedPlayback)Field("playbackBuffer")!; int created = tracker.Created.Count;
                Number(first, "500 Hz gain dB").Value = 9; Refresh();
                var appliedSettings = first.Settings;
                Check(frozenFirst.MultibandEq!.BandAt(4).GainDb == 6 && frozenFirst.MultibandEq.BandAt(4).EndGainDb == -6
                    && appliedSettings.MultibandEq!.BandAt(4).GainDb == 9 && second.Settings.MultibandEq == frozenSecond.MultibandEq,
                    "editing one EQ window leaves captured settings and every other window's band values unchanged");
                var onlyFirst = AudioEffects.Process(Selected(original), AudioTrack.Rate, appliedSettings);
                var both = AudioEffects.Process(onlyFirst, AudioTrack.Rate, frozenSecond);
                var committed = ReplaceSelection(original, onlyFirst); var audition = ReplaceSelection(original, both);
                Check(Near(Audio(Preview().PlaybackTracks[0].Source), audition) && Near(Audio(active), original) && Cues().SequenceEqual(cues)
                    && form.GetPlaybackBounds() == (Start, End),
                    "live EQ auditions the independent automated and static settings without committing audio selection bounds or cues");
                Check(ReferenceEquals(Field("output"), output) && ReferenceEquals(Field("playbackBuffer"), queue) && tracker.Created.Count == created && tracker.Active == 1,
                    "live EQ parameter changes replace the mix inside the same single output and render queue");
                PumpUntil(() => queue.BufferedSamples == queue.CapacitySamples);
                var oldQueued = new float[queue.CapacitySamples]; queue.Read(oldQueued, 0, oldQueued.Length);
                PumpUntil(() => queue.AppliedRevision >= queue.RequestedRevision && queue.BufferedSamples >= 1200);
                queue.Read(new float[1200], 0, 1200);
                PumpUntil(() => queue.BufferedSamples >= 256);
                long heardFrom = queue.Position; var heard = new float[256]; queue.Read(heard, 0, heard.Length);
                var expectedHeard = new float[256]; new MixProvider(Preview().PlaybackTracks, Start, End, true, heardFrom).Read(expectedHeard, 0, expectedHeard.Length);
                Check(Near(heard, expectedHeard) && ReferenceEquals(Field("output"), output) && tracker.MaxActive == 1,
                    "the existing buffered player emits the latest EQ chain after its bounded queue and transition without summing extra players");
                Option(second, "Live preview").Checked = false; Refresh();
                Check(Near(Audio(Preview().PlaybackTracks[0].Source), committed) && Windows().Length == 2 && second.Settings.MultibandEq == frozenSecond.MultibandEq,
                    "bypassing one EQ preview leaves the other window active and retains bypassed band settings");
                Option(second, "Live preview").Checked = true; Refresh();
                Check(Near(Audio(Preview().PlaybackTracks[0].Source), audition) && Near(Audio(active), original),
                    "reenabling an EQ window restores its same chain contribution without applying it");
                Apply(first);
                Check(Near(Audio(active), committed) && active.Samples.SequenceEqual(raw) && active.Frames == 24000 && active.Offset == Offset
                    && canvas.SelectionStart == Start && canvas.SelectionEnd == End && Cues().SequenceEqual(cues) && Windows().SequenceEqual(new[] { second }),
                    "Apply commits only the chosen EQ window with one fixed-range patch and preserves timeline selection cues and the other tool");
                Check(!inactive.Enabled && inactive.Offset == Offset && inactive.Frames == 24000 && inactive.Patches.Length == 0 && Near(Audio(inactive), originalMuted)
                    && Near(active.ReadVisibleSamples(0, Start - Offset), original.Take((int)(Start - Offset) * 2).ToArray())
                    && Near(active.ReadVisibleSamples(End - Offset, active.Frames), original.Skip((int)(End - Offset) * 2).ToArray()),
                    "EQ Apply preserves unticked rows and both sides of the selected active audio");
                Check(Near(Audio(Preview().PlaybackTracks[0].Source), audition) && second.Settings.MultibandEq == frozenSecond.MultibandEq,
                    "the remaining EQ window rebuilds once over the newly committed base with unchanged parameters");
                Call("Undo"); Refresh();
                var onlySecond = ReplaceSelection(original, AudioEffects.Process(Selected(original), AudioTrack.Rate, frozenSecond));
                Check(Near(Audio(active), original) && Near(Audio(Preview().PlaybackTracks[0].Source), onlySecond) && Windows().SequenceEqual(new[] { second })
                    && Cues().SequenceEqual(cues) && !inactive.Enabled,
                    "one Undo restores the base audio while the independent remaining EQ preview stays open");
                Call("Redo"); Refresh();
                Check(Near(Audio(active), committed) && Near(Audio(Preview().PlaybackTracks[0].Source), audition) && Cues().SequenceEqual(cues),
                    "Redo restores the applied EQ alone and rebuilds the remaining preview over it");
                Click(second, "Cancel effect");
                Check(Windows().Length == 0 && tracker.Active == 0 && Near(Audio(active), committed) && Cues().SequenceEqual(cues),
                    "Cancel discards the final uncommitted EQ without changing the applied effect or cue positions");
                Call("Undo");
                Check(Near(Audio(active), original) && active.Patches.Length == 0 && !inactive.Enabled && Near(Audio(inactive), originalMuted),
                    "one Undo after Cancel removes the one committed EQ and leaves unticked audio untouched");
                Call("Redo"); Call("StopPlayback");
                Check(Near(Audio(active), committed) && tracker.MaxActive == 1 && tracker.Active == 0 && tracker.Created.All(player => player.Disposed),
                    "EQ redo and cleanup preserve the committed result and release every single-owner fake output");
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(45))) throw new TimeoutException("EQ integration fixture deadline exceeded.");
        if (failure != null) throw new Exception("EQ integration fixture failed.", failure);
        Console.WriteLine($"{checks} EQ integration checks passed. No native window or audio hardware was used.");
    }

    sealed class PlayerTracker
    { public List<FakePlayer> Created { get; } = []; public int Active, MaxActive; }
    sealed class FakePlayer : IWavePlayer
    {
        readonly PlayerTracker tracker; bool active;
        public bool Disposed { get; private set; }
        public float Volume { get; set; } = 1;
        public PlaybackState PlaybackState { get; private set; }
        public WaveFormat OutputWaveFormat { get; private set; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public FakePlayer(PlayerTracker tracker) { this.tracker = tracker; tracker.Created.Add(this); }
        public void Init(IWaveProvider provider) => OutputWaveFormat = provider.WaveFormat;
        public void Play() { if (!active) { active = true; tracker.Active++; tracker.MaxActive = Math.Max(tracker.MaxActive, tracker.Active); } PlaybackState = PlaybackState.Playing; }
        public void Pause() { Deactivate(); PlaybackState = PlaybackState.Paused; }
        public void Stop() { Deactivate(); PlaybackState = PlaybackState.Stopped; PlaybackStopped?.Invoke(this, new StoppedEventArgs()); }
        void Deactivate() { if (active) { active = false; tracker.Active--; } }
        public void Dispose() { Deactivate(); Disposed = true; PlaybackState = PlaybackState.Stopped; }
    }
}
