using System.Reflection;
using OrchardSound;

public static class SilenceIntegrationChecks
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static IEnumerable<Control> All(Control parent) => parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(All(child)));
    public static void Run()
    {
        int checks = 0; Exception? failure = null;
        void Check(bool condition, string name)
        { if (!condition) throw new Exception("FAILED SILENCE UI: " + name); checks++; Console.WriteLine("PASS SILENCE UI " + name); }
        var thread = new Thread(() =>
        {
            try
            {
                using (var dialog = new InsertSilenceDialog(true))
                {
                    _ = dialog.Handle;
                    var number = All(dialog).OfType<NumericUpDown>().Single(); var slider = All(dialog).OfType<TrackBar>().Single();
                    var positions = All(dialog).OfType<ComboBox>().Single();
                    Check(positions.Items.Count == 3 && positions.SelectedIndex == 0 && dialog.DurationFrames == 48000,
                        "silence defaults to one second at the cursor with optional selection boundaries");
                    number.Value = 599.999m;
                    Check(slider.Value == 599999 && dialog.DurationFrames == 28799952, "typing silence duration updates its slider and exact audio frame count immediately");
                    slider.Value = 1;
                    Check(number.Value == .001m && dialog.DurationFrames == 48, "the silence slider covers the same minimum and maximum as its number field");
                    Check(All(dialog).OfType<ThemeButton>().Single(button => button.Text == "Insert").Role == ButtonRole.Primary
                        && All(dialog).OfType<ThemeButton>().Single(button => button.Text == "Cancel").Role == ButtonRole.Secondary,
                        "silence has a primary Insert action and secondary Cancel");
                }
                using (var empty = new InsertSilenceDialog(false))
                    Check(All(empty).OfType<ComboBox>().Single().Items.Count == 1, "an unselected timeline offers only the white cursor insertion position");

                using var form = new EditorForm([]); _ = form.Handle;
                object? Field(string name) => typeof(EditorForm).GetField(name, Private)!.GetValue(form);
                object? Call(string name, params object?[] args) => typeof(EditorForm).GetMethod(name, Private)!.Invoke(form, args.Length == 0 ? null : args);
                var canvas = (WaveCanvas)Field("canvas")!;
                var samples = Enumerable.Range(1, 16).Select(value => value / 20f).ToArray();
                var source = new AudioTrack { Path = "Silence UI.wav", Samples = samples, Peaks = new PeakPyramid(samples) };
                var later = new AudioTrack { Path = "Later.wav", Samples = [.1f, -.1f], Peaks = new PeakPyramid([.1f, -.1f]), Offset = 12 };
                canvas.Slots[0].Track = source; var laterSlot = new ChannelSlot { Track = later }; laterSlot.Enabled = false; canvas.Slots.Add(laterSlot);
                var store = (SelectionStore)Field("selections")!; store.Restore([new("Earlier cue", 1, 1), new("Later cue", 6, 6)]);
                Call("SyncCuePoints"); Call("RebuildTrackControls");
                float[] before = Editing.RenderSelection(source, 0, source.Frames);
                var apply = (Task)Call("ApplySilence", 4L, 3L)!;
                var deadline = System.Diagnostics.Stopwatch.StartNew();
                while (!apply.IsCompleted) { Application.DoEvents(); if (deadline.ElapsedMilliseconds > 10000) throw new TimeoutException("Silence UI deadline."); Thread.Sleep(1); }
                apply.GetAwaiter().GetResult();
                var after = Editing.RenderSelection(source, 0, source.Frames);
                Check(source.Frames == 11 && after.Take(8).SequenceEqual(before.Take(8)) && after.Skip(8).Take(6).All(value => value == 0)
                    && after.Skip(14).SequenceEqual(before.Skip(8)), "Insert silence commits exact silent frames while preserving both sides of the source");
                Check(!later.Enabled && later.Offset == 15 && later.Frames == 1, "global insertion moves unticked later audio while preserving its exclusion");
                Check(canvas.SelectionStart == 4 && canvas.SelectionEnd == 7 && canvas.LastCursorFrame == 4
                    && store.Items.Any(cue => cue.Name == "Earlier cue" && cue.Start == 1)
                    && store.Items.Any(cue => cue.Name == "Later cue" && cue.Start == 9)
                    && canvas.CuePoints.Any(cue => cue.Frame == 4) && canvas.CuePoints.Any(cue => cue.Frame == 7),
                    "insertion selects the new silence and maps existing and boundary cue points");
                Call("Undo");
                Check(source.Frames == 8 && later.Offset == 12 && Editing.RenderSelection(source, 0, source.Frames).SequenceEqual(before)
                    && store.Items.Count == 2 && store.Items.Any(cue => cue.Name == "Later cue" && cue.Start == 6),
                    "one Undo restores all audio offsets and original cues together");
                Call("Redo"); Check(source.Frames == 11 && later.Offset == 15, "Redo restores the complete silence insertion");
                Check(Field("hoverHotkeys") == null && Field("mouseSpeed") == null,
                    "constructing hidden fixtures never installs global hooks or changes Windows pointer speed");
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(35))) throw new TimeoutException("Silence integration fixture exceeded its deadline.");
        if (failure != null) throw new Exception("Silence integration failed.", failure);
        Console.WriteLine($"{checks} silence integration checks passed with hidden controls and no audio devices.");
    }
}
