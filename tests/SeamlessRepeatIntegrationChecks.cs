using System.Collections;
using System.Reflection;
using NAudio.Wave;
using OrchardSound;

public static class SeamlessRepeatIntegrationChecks
{
    static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static AudioTrack Track(string name, int frames, float constant = 0)
    {
        var samples = new float[frames * 2];
        for (int frame = 0; frame < frames; frame++)
        { float value = constant == 0 ? .12f + frame / (float)frames * .2f : constant; samples[frame * 2] = value; samples[frame * 2 + 1] = -value / 2; }
        return new() { Path = name, Samples = samples, Peaks = new PeakPyramid(samples) };
    }
    static float[] Audio(AudioTrack track) => Editing.RenderSelection(track, 0, track.Frames);
    static bool Near(float[] first, float[] second) => first.Length == second.Length && first.Zip(second).All(pair => Math.Abs(pair.First - pair.Second) < .00001f);
    static readonly EffectSettings Seamless = new(EffectKind.SeamlessRepeat)
    { SeamlessRepeat = new() { RepeatCount = 3, CrossfadeMilliseconds = 1, SmartSeam = false, AdaptiveCrossfade = false, LoopEndToStart = false } };

    public static async Task Run()
    {
        int checks = 0;
        void Check(bool condition, string name)
        { if (!condition) throw new Exception("FAILED SEAMLESS INTEGRATION: " + name); checks++; Console.WriteLine("PASS SEAMLESS INTEGRATION " + name); }
        const long start = 100, end = 340, outputLength = 624, newEnd = start + outputLength, delta = outputLength - (end - start);
        var source = Track("Repeat scene.wav", 480); var original = Audio(source);
        var inactive = Track("Inactive scene.wav", 64, .04f); inactive.Offset = 600; inactive.Enabled = false; var inactiveAudio = Audio(inactive);
        var single = await EffectPreview.PrepareAsync([source, inactive], [source], start, end, Seamless);
        var expectedAudio = Audio(single.PlaybackTracks[0].Source);
        long[] expectedCues = [100, 292, 484, 724];
        Check(single.ReplacementStates?.Length == 2 && single.SelectionEnd == newEnd && outputLength % (end - start) != 0 && single.PlaybackTracks[0].Frames == source.Frames + delta, "overlapped repetitions return replacement states with a non-integral expanded selection duration");
        Check(single.CueFrames?.SequenceEqual(expectedCues) == true, "repeat cue metadata records the real overlap joins and final end rather than integer source durations");
        Check(single.PlaybackTracks[1].Offset == inactive.Offset + delta && !single.PlaybackTracks[1].Enabled && Near(Audio(single.PlaybackTracks[1].Source), inactiveAudio), "inactive channels shift with the inserted timeline duration without changing their audio or tick state");
        Check(source.Frames == 480 && source.Patches.Length == 0 && inactive.Offset == 600 && Near(Audio(source), original), "seamless preparation leaves the committed scene unchanged");
        var multiply = new EffectSettings(EffectKind.Multiply) { Multiply = new() { CopyCount = 2 } };
        var repeatThenMultiply = await EffectChain.PrepareAsync([source, inactive], [source], start, end, [Seamless, multiply]);
        var multiplyThenRepeat = await EffectChain.PrepareAsync([source, inactive], [source], start, end, [multiply, Seamless]);
        var doubled = expectedAudio.Select((sample, index) => index >= start * 2 && index < newEnd * 2 ? sample * 2 : sample).ToArray();
        Check(repeatThenMultiply.SelectionEnd == newEnd && multiplyThenRepeat.SelectionEnd == newEnd && repeatThenMultiply.AuditionEnd == newEnd && multiplyThenRepeat.AuditionEnd == newEnd
            && Near(Audio(repeatThenMultiply.PlaybackTracks[0].Source), doubled) && Near(Audio(multiplyThenRepeat.PlaybackTracks[0].Source), doubled), "simultaneous Multiply voices preserve Seamless Repeat duration and double only its selected audio in either opening order");
        Check(repeatThenMultiply.PlaybackTracks[1].Offset == 984 && multiplyThenRepeat.PlaybackTracks[1].Offset == 984 && !repeatThenMultiply.PlaybackTracks[1].Enabled && !multiplyThenRepeat.PlaybackTracks[1].Enabled, "stacked voices add no second insertion or inactive-row shift around overlap repetition");
        var impulseSamples = new float[240 * 2]; impulseSamples[239 * 2] = .8f; impulseSamples[239 * 2 + 1] = -.4f;
        var impulse = new AudioTrack { Path = "Repeat tail.wav", Samples = impulseSamples, Peaks = new PeakPyramid(impulseSamples) };
        var after = Track("Inactive after tail.wav", 32, .03f); after.Offset = 500; after.Enabled = false;
        var tailChain = await EffectChain.PrepareAsync([impulse, after], [impulse], 0, 240,
            [new(EffectKind.Delay) { DelaySeconds = .001, Feedback = 0, Wet = .5f, CutTail = false }, multiply, Seamless]);
        var tailAudio = Audio(tailChain.PlaybackTracks[0].Source);
        Check(tailChain.SelectionEnd == 624 && tailChain.AuditionEnd == 672 && tailChain.PlaybackTracks[0].Frames == 672, "an earlier effect tail remains audible after fixed-duration stacked voices and overlap repetition");
        Check(Math.Abs(tailAudio[671 * 2] - .4f) < .00001 && Math.Abs(tailAudio[671 * 2 + 1] + .2f) < .00001 && tailAudio[287 * 2] == 0, "earlier stereo tail samples move to their final timeline positions instead of staying at a stale boundary");
        Check(tailChain.PlaybackTracks[1].Offset == 884 && !tailChain.PlaybackTracks[1].Enabled && impulse.Frames == 240 && after.Offset == 500, "tail chains shift inactive rows while preserving every committed input");

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new EditorForm([]); _ = form.Handle;
                var canvas = (WaveCanvas)typeof(EditorForm).GetField("canvas", Private)!.GetValue(form)!;
                var active = Track("Repeat scene.wav", 480); var raw = active.Samples.ToArray();
                var muted = Track("Inactive scene.wav", 64, .04f); muted.Offset = 600;
                canvas.Slots[0].Track = active;
                var mutedSlot = new ChannelSlot { Track = muted }; mutedSlot.Enabled = false; canvas.Slots.Add(mutedSlot);
                canvas.SelectionStart = start; canvas.SelectionEnd = end;
                var tracker = new PlayerTracker(); int presented = 0;
                typeof(EditorForm).GetField("outputFactory", Private)!.SetValue(form, (Func<IWavePlayer>)(() => new SilentPlayer(tracker)));
                typeof(EditorForm).GetField("effectWindowPresenter", Private)!.SetValue(form, (Action<EffectDialog, EditorForm>)((_, _) => presented++));
                object? Field(string name) => typeof(EditorForm).GetField(name, Private)!.GetValue(form);
                object? Call(string name, params object?[] values) => typeof(EditorForm).GetMethod(name, Private)!.Invoke(form, values.Length == 0 ? null : values);
                EffectDialog[] Windows() => ((IList)Field("effectTools")!).Cast<object>().Select(state => (EffectDialog)state.GetType().GetProperty("Window")!.GetValue(state)!).ToArray();
                IEnumerable<Control> All(Control parent) => parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(All(child)));
                void PumpUntil(Func<bool> complete)
                {
                    var deadline = System.Diagnostics.Stopwatch.StartNew();
                    while (!complete())
                    {
                        Application.DoEvents();
                        if (deadline.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Seamless effect integration timed out. " + ((Control)Field("status")!).Text);
                        Thread.Sleep(1);
                    }
                }
                void Await(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
                void Refresh() => Await((Task)Call("RequestEffectPreview")!);
                EffectDialog Open(EffectKind kind) { Call("OpenEffectTool", kind); return (EffectDialog)Field("effectWindow")!; }
                void Click(EffectDialog window, string name) => typeof(Control).GetMethod("OnClick", Private)!.Invoke(All(window).OfType<Button>().Single(button => button.AccessibleName == name), [EventArgs.Empty]);
                void Configure(EffectDialog window)
                {
                    All(window).OfType<NumericUpDown>().Single(number => number.AccessibleName == "Total repetitions").Value = 3;
                    All(window).OfType<NumericUpDown>().Single(number => number.AccessibleName == "Crossfade ms").Value = 1;
                    All(window).OfType<CheckBox>().Single(option => option.AccessibleName == "Smart seam").Checked = false;
                    All(window).OfType<CheckBox>().Single(option => option.AccessibleName == "Adaptive crossfade").Checked = false;
                    All(window).OfType<CheckBox>().Single(option => option.AccessibleName == "Loop end back to start").Checked = false;
                }
                void Apply(EffectDialog window)
                {
                    Click(window, "Apply effect"); PumpUntil(() => window.IsDisposed && !(bool)Field("busy")!);
                    if (Windows().Length > 0) Refresh();
                }
                EffectPreviewResult Preview() => (EffectPreviewResult)Field("effectPreview")!;
                long[] CuePositions() => canvas.CuePoints.Select(cue => cue.Frame).Distinct().Order().ToArray();
                var selections = (SelectionStore)Field("selections")!;
                selections.Restore([new("Earlier", 20, 20), new("Later", 500, 500)]); Call("SyncCuePoints"); Call("RebuildTrackControls");
                var seamlessWindow = Open(EffectKind.SeamlessRepeat); Configure(seamlessWindow);
                var fadeWindow = Open(EffectKind.FadeIn); Refresh();
                Check(presented == 2 && Windows().SequenceEqual(new[] { seamlessWindow, fadeWindow }), "seamless repeat and a later effect coexist in hidden modeless tools");
                Check(!Near(Audio(Preview().PlaybackTracks[0].Source), expectedAudio) && active.Frames == 480 && CuePositions().SequenceEqual(new long[] { 20, 500 }), "the combined audition includes later effects without committing audio or cues");
                Apply(seamlessWindow);
                Check(active.Frames == 864 && Near(Audio(active), expectedAudio) && canvas.SelectionStart == start && canvas.SelectionEnd == newEnd && Windows().SequenceEqual(new[] { fadeWindow }), "individual Apply commits only the seamless replacement and its exact expanded selection");
                Check(CuePositions().SequenceEqual(new long[] { 20, 100, 292, 484, 724, 884 }), "Apply uses explicit non-integral join cues and shifts existing later cues");
                Check(muted.Offset == 984 && !muted.Enabled && Near(Audio(muted), inactiveAudio) && active.Samples.SequenceEqual(raw), "Apply shifts inactive channels and retains immutable source samples");
                Check(Near(active.ReadVisibleSamples(0, start), original.Take((int)start * 2).ToArray()) && Near(active.ReadVisibleSamples(newEnd, active.Frames), original.Skip((int)end * 2).ToArray()), "overlap insertion preserves both the earlier prefix and later suffix audio");
                Call("Undo"); Refresh();
                Check(Near(Audio(active), original) && muted.Offset == 600 && canvas.SelectionStart == start && canvas.SelectionEnd == end && CuePositions().SequenceEqual(new long[] { 20, 500 }) && Windows().SequenceEqual(new[] { fadeWindow }), "one Undo restores audio, inactive timing, selection and cues while retaining the uncommitted tool");
                Call("Redo"); Refresh();
                Check(Near(Audio(active), expectedAudio) && muted.Offset == 984 && canvas.SelectionEnd == newEnd && CuePositions().SequenceEqual(new long[] { 20, 100, 292, 484, 724, 884 }), "Redo restores non-integral repeat audio and cue metadata together");
                Click(fadeWindow, "Cancel effect");
                Check(Windows().Length == 0 && tracker.Active == 0 && Near(Audio(active), expectedAudio), "cancelling the remaining tool leaves the committed repetition unchanged and stops preview output");
                Call("Undo");
                var multiplyWindow = Open(EffectKind.Multiply);
                All(multiplyWindow).OfType<NumericUpDown>().Single(number => number.AccessibleName == "Total copies").Value = 2;
                var laterRepeat = Open(EffectKind.SeamlessRepeat); Configure(laterRepeat); Refresh();
                Check(Preview().SelectionEnd == newEnd && active.Frames == 480, "opening simultaneous Multiply voices before Seamless Repeat changes only the repeat duration without committing it");
                Apply(laterRepeat);
                Check(active.Frames == 864 && canvas.SelectionEnd == newEnd && Near(Audio(active), expectedAudio) && Windows().SequenceEqual(new[] { multiplyWindow }) && Preview().SelectionEnd == null
                    && form.GetPlaybackBounds() == (start, newEnd) && Near(Audio(Preview().PlaybackTracks[0].Source), doubled), "applying the later repeat commits it directly and rebuilds same-duration stacked voices over that new base once");
                Call("CloseEffectTool", false); Call("StopPlayback");
                Check(tracker.MaxActive == 1 && tracker.Active == 0 && tracker.Created.All(player => player.Disposed), "all repeat-chain scenarios retain and dispose a single output owner");
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(45))) throw new TimeoutException("Seamless repeat integration fixture timed out.");
        if (failure != null) throw new Exception("Seamless repeat integration fixture failed.", failure);
        await ClosedLoopChecks(Check);
        Console.WriteLine($"{checks} seamless repeat integration checks passed. No native window or audio hardware was used.");
    }

    static async Task ClosedLoopChecks(Action<bool, string> Check)
    {
        const long start = 100, end = 340, closedEnd = 292;
        var settings = Seamless.SeamlessRepeat! with { RepeatCount = 1, LoopEndToStart = true };
        var closed = Seamless with { SeamlessRepeat = settings };
        var source = Track("Closed loop scene.wav", 480); var original = Audio(source);
        var after = Track("Disabled after closed loop.wav", 64, .04f); after.Offset = 600; after.Enabled = false;
        var crossing = Track("Disabled crossing closed loop.wav", 140); crossing.Offset = 250; crossing.Enabled = false;
        var inside = Track("Disabled inside closed loop.wav", 80); inside.Offset = 310; inside.Enabled = false;
        var crossingOriginal = Audio(crossing); var insideOriginal = Audio(inside);
        var prepared = await EffectPreview.PrepareAsync([source, after, crossing, inside], [source], start, end, closed);
        var expectedAudio = Audio(prepared.PlaybackTracks[0].Source);
        var expectedCrossing = crossingOriginal.Take(42 * 2).Concat(crossingOriginal.Skip(90 * 2)).ToArray();
        var expectedInside = insideOriginal.Skip(30 * 2).ToArray();
        Check(new SeamlessRepeatSettings().LoopEndToStart && prepared.SelectionEnd == closedEnd && prepared.AuditionEnd == closedEnd && prepared.PlaybackTracks[0].Frames == 432,
            "closed-loop mode defaults on and one repetition contracts the selected audio by its shared overlap");
        Check(prepared.CueFrames?.SequenceEqual(new long[] { start, closedEnd }) == true && prepared.ReplacementStates?.Length == 4,
            "one closed repetition returns ordered start and shortened-end cues in a committable transaction");
        Check(prepared.PlaybackTracks[1].Offset == 552 && !prepared.PlaybackTracks[1].Enabled && Near(Audio(prepared.PlaybackTracks[1].Source), Audio(after)),
            "a disabled later channel moves earlier by the closed-loop contraction without changing audio");
        Check(prepared.PlaybackTracks[2].Offset == 250 && !prepared.PlaybackTracks[2].Enabled && Near(Audio(prepared.PlaybackTracks[2].Source), expectedCrossing),
            "a disabled channel crossing the contracted region preserves its prefix and rejoins its suffix");
        Check(prepared.PlaybackTracks[3].Offset == closedEnd && !prepared.PlaybackTracks[3].Enabled && Near(Audio(prepared.PlaybackTracks[3].Source), expectedInside),
            "a disabled channel beginning inside the contracted region clamps to its new boundary and keeps following audio");
        Check(Near(prepared.PlaybackTracks[0].Source.ReadVisibleSamples(0, start), original.Take((int)start * 2).ToArray())
            && Near(prepared.PlaybackTracks[0].Source.ReadVisibleSamples(closedEnd, 432), original.Skip((int)end * 2).ToArray())
            && source.Frames == 480 && crossing.Frames == 140 && inside.Offset == 310,
            "closed preparation preserves target audio outside the range and leaves all committed rows unchanged");
        var multiply = new EffectSettings(EffectKind.Multiply) { Multiply = new() { CopyCount = 2 } };
        var closedThenMultiply = await EffectChain.PrepareAsync([source, after], [source], start, end, [closed, multiply]);
        var multiplyThenClosed = await EffectChain.PrepareAsync([source, after], [source], start, end, [multiply, closed]);
        var doubledClosed = expectedAudio.Select((sample, index) => index >= start * 2 && index < closedEnd * 2 ? sample * 2 : sample).ToArray();
        Check(closedThenMultiply.SelectionEnd == closedEnd && closedThenMultiply.AuditionEnd == closedEnd && closedThenMultiply.PlaybackTracks[1].Offset == 552
            && multiplyThenClosed.SelectionEnd == closedEnd && multiplyThenClosed.AuditionEnd == closedEnd && multiplyThenClosed.PlaybackTracks[1].Offset == 552
            && Near(Audio(closedThenMultiply.PlaybackTracks[0].Source), doubledClosed) && Near(Audio(multiplyThenClosed.PlaybackTracks[0].Source), doubledClosed),
            "stacked Multiply voices preserve the closed period length and its one inactive-row contraction in either opening order");
        var reversed = await EffectChain.PrepareAsync([source], [source], start, end, [closed, new(EffectKind.Reverse)]);
        var expectedReversed = AudioEffects.Process(expectedAudio.Skip((int)start * 2).Take((int)(closedEnd - start) * 2).ToArray(), AudioTrack.Rate, new(EffectKind.Reverse));
        Check(reversed.SelectionEnd == closedEnd && reversed.AuditionEnd == closedEnd
            && Near(reversed.PlaybackTracks[0].Source.ReadVisibleSamples(start, closedEnd), expectedReversed)
            && Near(reversed.PlaybackTracks[0].Source.ReadVisibleSamples(closedEnd, 432), original.Skip((int)end * 2).ToArray()),
            "a later effect processes only the shortened closed-loop selection and preserves its moved suffix");
        var impulseSamples = new float[240 * 2]; impulseSamples[239 * 2] = .8f; impulseSamples[239 * 2 + 1] = -.4f;
        var impulse = new AudioTrack { Path = "Closed tail.wav", Samples = impulseSamples, Peaks = new PeakPyramid(impulseSamples) };
        var delay = new EffectSettings(EffectKind.Delay) { DelaySeconds = .001, Feedback = 0, Wet = .5f, CutTail = false };
        var earlierTail = await EffectChain.PrepareAsync([impulse], [impulse], 0, 240, [delay, closed, new(EffectKind.FadeOut)]);
        var earlierAudio = Audio(earlierTail.PlaybackTracks[0].Source);
        Check(earlierTail.SelectionEnd == 192 && earlierTail.AuditionEnd == 240 && earlierTail.PlaybackTracks[0].Frames == 240
            && Math.Abs(earlierAudio[239 * 2] - .4f) < .00001 && Math.Abs(earlierAudio[239 * 2 + 1] + .2f) < .00001,
            "an earlier stereo effect tail and its audible end move earlier through closed contraction and survive a later selection effect");
        var constant = Track("Closed later tail.wav", 240, .2f);
        var laterTail = await EffectChain.PrepareAsync([constant], [constant], 0, 240, [closed, delay, new(EffectKind.FadeOut)]);
        var laterAudio = Audio(laterTail.PlaybackTracks[0].Source);
        Check(laterTail.SelectionEnd == 192 && laterTail.AuditionEnd == 240 && laterTail.PlaybackTracks[0].Frames == 240
            && Math.Abs(laterAudio[239 * 2] - .1f) < .00001 && Math.Abs(laterAudio[239 * 2 + 1] + .05f) < .00001,
            "a later delay extends from the shortened closed period and retains its tail beyond a following fade");

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new EditorForm([]); _ = form.Handle;
                object? Field(string name) => typeof(EditorForm).GetField(name, Private)!.GetValue(form);
                object? Call(string name, params object?[] values) => typeof(EditorForm).GetMethod(name, Private)!.Invoke(form, values.Length == 0 ? null : values);
                var canvas = (WaveCanvas)Field("canvas")!;
                var active = Track("Closed loop scene.wav", 480); var raw = active.Samples.ToArray();
                var muted = Track("Disabled after closed loop.wav", 64, .04f); muted.Offset = 600;
                var crosses = Track("Disabled crossing closed loop.wav", 140); crosses.Offset = 250;
                var begins = Track("Disabled inside closed loop.wav", 80); begins.Offset = 310;
                canvas.Slots[0].Track = active;
                foreach (var track in new[] { muted, crosses, begins }) { var slot = new ChannelSlot { Track = track }; slot.Enabled = false; canvas.Slots.Add(slot); }
                canvas.SelectionStart = start; canvas.SelectionEnd = end;
                var tracker = new PlayerTracker();
                typeof(EditorForm).GetField("outputFactory", Private)!.SetValue(form, (Func<IWavePlayer>)(() => new SilentPlayer(tracker)));
                typeof(EditorForm).GetField("effectWindowPresenter", Private)!.SetValue(form, (Action<EffectDialog, EditorForm>)((_, _) => { }));
                IEnumerable<Control> All(Control parent) => parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(All(child)));
                void PumpUntil(Func<bool> complete)
                {
                    var deadline = System.Diagnostics.Stopwatch.StartNew();
                    while (!complete())
                    {
                        Application.DoEvents();
                        if (deadline.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Closed repeat integration timed out. " + ((Control)Field("status")!).Text);
                        Thread.Sleep(1);
                    }
                }
                void Await(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
                void Refresh() => Await((Task)Call("RequestEffectPreview")!);
                void Click(EffectDialog window, string name) => typeof(Control).GetMethod("OnClick", Private)!.Invoke(All(window).OfType<Button>().Single(button => button.AccessibleName == name), [EventArgs.Empty]);
                long[] CuePositions() => canvas.CuePoints.Select(cue => cue.Frame).Distinct().Order().ToArray();
                var selections = (SelectionStore)Field("selections")!;
                selections.Restore([new("Earlier", 20, 20), new("Inside removed time", 320, 320), new("Original end", 340, 340), new("Later", 500, 500)]);
                Call("SyncCuePoints"); Call("RebuildTrackControls");
                Call("OpenEffectTool", EffectKind.SeamlessRepeat); var window = (EffectDialog)Field("effectWindow")!;
                All(window).OfType<NumericUpDown>().Single(number => number.AccessibleName == "Total repetitions").Value = 1;
                All(window).OfType<NumericUpDown>().Single(number => number.AccessibleName == "Crossfade ms").Value = 1;
                All(window).OfType<CheckBox>().Single(option => option.AccessibleName == "Smart seam").Checked = false;
                All(window).OfType<CheckBox>().Single(option => option.AccessibleName == "Adaptive crossfade").Checked = false;
                Refresh();
                Check(window.Settings.SeamlessRepeat!.LoopEndToStart && form.GetPlaybackBounds() == (start, closedEnd) && canvas.SelectionEnd == end && active.Frames == 480,
                    "a default closed preview auditions its shortened bounds while the committed selection stays unchanged");
                var mixer = (MixProvider)Call("CreatePlaybackMix", (object?)start, false)!;
                var heard = new float[((closedEnd - start) * 3 + 17) * 2]; mixer.Read(heard, 0, heard.Length);
                var period = expectedAudio.Skip((int)start * 2).Take((int)(closedEnd - start) * 2).ToArray();
                var expectedHeard = Enumerable.Range(0, heard.Length).Select(index => period[index % period.Length]).ToArray();
                Check(mixer.LoopStartFrame == start && mixer.LoopEndFrame == closedEnd && Near(heard, expectedHeard) && heard.Where((_, index) => index % 2 == 0).All(value => value > 0),
                    "the preview playback mixer repeats the exact shortened period without leaked suffix audio or trailing silence");
                Click(window, "Apply effect"); PumpUntil(() => window.IsDisposed && !(bool)Field("busy")!);
                Check(active.Frames == 432 && Near(Audio(active), expectedAudio) && canvas.SelectionStart == start && canvas.SelectionEnd == closedEnd && active.Samples.SequenceEqual(raw),
                    "Apply commits a contracting one-repeat edit and its shortened selection without rewriting original samples");
                Check(muted.Offset == 552 && crosses.Offset == 250 && Near(Audio(crosses), expectedCrossing) && begins.Offset == closedEnd && Near(Audio(begins), expectedInside)
                    && !muted.Enabled && !crosses.Enabled && !begins.Enabled,
                    "Apply atomically shifts and cuts disabled channels crossing or beginning within removed time");
                Check(CuePositions().SequenceEqual(new long[] { 20, start, closedEnd, 452 }),
                    "Apply maps interior and old-end cues to the contracted boundary and moves later cues earlier");
                Call("Undo");
                Check(Near(Audio(active), original) && canvas.SelectionEnd == end && muted.Offset == 600 && crosses.Offset == 250 && Near(Audio(crosses), crossingOriginal)
                    && begins.Offset == 310 && Near(Audio(begins), insideOriginal) && CuePositions().SequenceEqual(new long[] { 20, 320, 340, 500 }),
                    "one Undo restores the contracted selection, every disabled channel and all original cue positions");
                Call("Redo");
                Check(Near(Audio(active), expectedAudio) && canvas.SelectionEnd == closedEnd && muted.Offset == 552 && Near(Audio(crosses), expectedCrossing)
                    && begins.Offset == closedEnd && Near(Audio(begins), expectedInside) && CuePositions().SequenceEqual(new long[] { 20, start, closedEnd, 452 }),
                    "Redo restores the same closed-loop contraction, inactive audio edits and deduplicated cues");
                Call("StopPlayback");
                canvas.Slots.RemoveRange(1, 3); canvas.Slots[0].Track = Track("Whole closed preview.wav", 240, .2f);
                canvas.SelectionStart = 0; canvas.SelectionEnd = 240; Call("RebuildTrackControls");
                Call("OpenEffectTool", EffectKind.SeamlessRepeat); var wholeWindow = (EffectDialog)Field("effectWindow")!;
                All(wholeWindow).OfType<NumericUpDown>().Single(number => number.AccessibleName == "Total repetitions").Value = 1;
                All(wholeWindow).OfType<NumericUpDown>().Single(number => number.AccessibleName == "Crossfade ms").Value = 1;
                All(wholeWindow).OfType<CheckBox>().Single(option => option.AccessibleName == "Smart seam").Checked = false;
                All(wholeWindow).OfType<CheckBox>().Single(option => option.AccessibleName == "Adaptive crossfade").Checked = false; Refresh();
                var cursorMixer = (MixProvider)Call("CreatePlaybackMix", (object?)0L, true)!;
                var cursorHeard = new float[(192 * 3 + 17) * 2]; cursorMixer.Read(cursorHeard, 0, cursorHeard.Length);
                Check(cursorMixer.StartFrame == 0 && cursorMixer.EndFrame == 192 && cursorMixer.LoopStartFrame == 0 && cursorMixer.LoopEndFrame == 192
                    && cursorHeard.Where((_, index) => index % 2 == 0).All(value => Math.Abs(value - .2f) < .00001)
                    && cursorHeard.Where((_, index) => index % 2 == 1).All(value => Math.Abs(value + .1f) < .00001),
                    "cursor preview playback derives its whole timeline from the contracted audition without retaining the old-end silence interval");
                Click(wholeWindow, "Cancel effect"); Call("StopPlayback");
                Check(tracker.MaxActive == 1 && tracker.Active == 0 && tracker.Created.All(player => player.Disposed),
                    "closed preview and contracting Apply retain and release one fake playback owner");
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(45))) throw new TimeoutException("Closed repeat integration fixture timed out.");
        if (failure != null) throw new Exception("Closed repeat integration fixture failed.", failure);
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
