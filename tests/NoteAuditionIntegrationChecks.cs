using System.Reflection;
using NAudio.Wave;
using OrchardSound;

public static class NoteAuditionIntegrationChecks
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void Run()
    {
        int checks = 0; Exception? failure = null;
        void Check(bool ok, string name)
        { if (!ok) throw new Exception("FAILED NOTE AUDITION: " + name); checks++; Console.WriteLine("PASS NOTE AUDITION " + name); }
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new EditorForm([]); _ = form.Handle;
                object? Field(string name) => typeof(EditorForm).GetField(name, Private)!.GetValue(form);
                object? Call(string name, params object?[] args) => typeof(EditorForm).GetMethod(name, Private)!.Invoke(form, args);
                var canvas = (WaveCanvas)Field("canvas")!;
                var raw = new float[AudioTrack.Rate * 4];
                for (int i = 0; i < raw.Length / 2; i++)
                {
                    double t = 2 * Math.PI * i / AudioTrack.Rate;
                    raw[2 * i] = (float)(.25 * Math.Sin(440 * t) + .25 * Math.Sin(880 * t));
                    raw[2 * i + 1] = (float)(.1 * Math.Sin(440 * t) - .1 * Math.Sin(880 * t));
                }
                var track = new AudioTrack { Path = "Note audition.wav", Samples = raw, Peaks = new(raw) };
                var original = raw.ToArray();
                canvas.Slots[0].Track = track; canvas.Slots.Add(new()); Call("RebuildTrackControls");
                canvas.Width = (int)Math.Round(1500 * canvas.DpiScale);
                canvas.SetViewportHeight((int)Math.Round(1260 * canvas.DpiScale));
                var trackPanels = (List<Control>)Field("trackControls")!;
                Panel precision = (Panel)Field("zoomControls")!;
                CheckBox Option(Control panel) => panel.Controls.OfType<CheckBox>().Single(box => box.Text == "Hear hovered note");
                void Spectrogram(Control panel) => panel.Controls.OfType<ComboBox>().Single().SelectedIndex = (int)AudioViewMode.Spectrogram;
                Spectrogram(trackPanels[0]); Spectrogram(trackPanels[1]); Spectrogram(precision);
                Option(trackPanels[0]).Checked = true; Option(trackPanels[1]).Checked = true;
                void Mouse(string method, EventArgs args) => typeof(WaveCanvas).GetMethod(method, Private)!.Invoke(canvas, [args]);
                void Hover(Rectangle bounds, int midi)
                {
                    int y = bounds.Top + (int)Math.Round((bounds.Height - 1) * (SpectrogramData.HighestNote - midi) / (double)(SpectrogramData.HighestNote - SpectrogramData.LowestNote));
                    var x = bounds.Left + bounds.Width / 2;
                    Mouse("OnMouseMove", new MouseEventArgs(MouseButtons.None, 0, (int)Math.Round(x * canvas.DpiScale), (int)Math.Round(y * canvas.DpiScale), 0));
                }
                NoteBandAudition? Monitor() => (NoteBandAudition?)Field("noteAudition");
                var players = new List<ProbePlayer>();
                typeof(EditorForm).GetField("outputFactory", Private)!.SetValue(form, (Func<IWavePlayer>)(() =>
                { var player = new ProbePlayer(); players.Add(player); return player; }));
                Hover(canvas.GetWaveBounds(1), 69); Call("StartPlayback", 0L);
                var monitor = Monitor()!; var output = players.Single(); var provider = output.Provider!;
                Check(monitor.MidiNote == 69 && output.PlaybackState == PlaybackState.Playing,
                    "starting playback over a checked spectrogram selects A4 before the output opens");
                var buffer = (BufferedPlayback)Field("playbackBuffer")!;
                var data = new byte[1024 * 2 * sizeof(float)]; double a = 0, b = 0;
                for (int block = 0; block < 30; block++)
                {
                    if (!SpinWait.SpinUntil(() => buffer.BufferedSamples >= data.Length / sizeof(float), TimeSpan.FromSeconds(3)))
                        throw new TimeoutException("Note audition render queue did not refill.");
                    if (provider.Read(data, 0, data.Length) != data.Length) throw new IOException("Note audition output returned an incomplete block.");
                    if (block < 10) continue;
                    for (int i = 0; i < 1024; i++)
                    {
                        double phase = 2 * Math.PI * (block * 1024 + i) / AudioTrack.Rate;
                        double value = BitConverter.ToSingle(data, i * 2 * sizeof(float));
                        a += value * Math.Sin(440 * phase); b += value * Math.Sin(880 * phase);
                    }
                }
                Check(Math.Abs(a) > 800 && Math.Abs(b) < Math.Abs(a) / 30, "the actual NAudio byte adapter emits the filtered note, not the dry mix");
                Check(track.Samples.SequenceEqual(original) && track.Patches.Length == 0, "audition creates no edited source samples or patches");
                Hover(canvas.GetWaveBounds(1), 81);
                Check(monitor.MidiNote == 81 && players.Count == 1 && ReferenceEquals(output.Provider, provider),
                    "moving to A5 retunes the same output and preserves the playing position owner");
                Hover(canvas.GetWaveBounds(2), 69);
                Check(monitor.MidiNote == null, "a checked empty spectrogram does not filter another track's mix");
                Hover(canvas.GetWaveBounds(0), 69);
                Check(monitor.MidiNote == null, "precision has its own independent audition checkbox");
                Option(precision).Checked = true;
                Check(monitor.MidiNote == 69, "checking precision immediately applies its current base note");
                var nested = canvas.GetNestedPrecisionBounds();
                Check(!nested.IsEmpty, "the nested spectrogram is available in the tall precision fixture");
                Hover(nested, 84);
                Check(monitor.MidiNote == 84, $"the purple precision overlay auditions its own C6 row (actual MIDI {monitor.MidiNote})");
                Option(precision).Checked = false;
                Check(monitor.MidiNote == null && ReferenceEquals(Field("output"), output), "unticking restores the full mix without a restart");
                Hover(canvas.GetWaveBounds(1), 69); Mouse("OnMouseLeave", EventArgs.Empty);
                Check(monitor.MidiNote == null && ReferenceEquals(Field("output"), output), "leaving the spectrogram restores full ongoing playback");
                Call("StopPlayback"); ((CheckBox)Field("scrub")!).Checked = true;
                Hover(canvas.GetWaveBounds(1), 69);
                Check(players.Count == 2 && output.Disposed && Monitor()?.MidiNote == 69 && (bool)Field("scrubbing")!,
                    "scrub starts with the hovered note through the same single output ownership path");
                Mouse("OnMouseLeave", EventArgs.Empty);
                Check(Monitor() == null && players[^1].Disposed, "leaving stops the scrub provider and releases its filter");
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(40))) throw new TimeoutException("Note audition integration fixture exceeded its deadline.");
        if (failure != null) throw new Exception("Note audition integration failed.", failure);
        Console.WriteLine($"{checks} note audition integration checks passed. No visible UI or audio hardware was used.");
    }

    sealed class ProbePlayer : IWavePlayer
    {
        public IWaveProvider? Provider { get; private set; }
        public PlaybackState PlaybackState { get; private set; }
        public float Volume { get; set; } = 1;
        public WaveFormat OutputWaveFormat => Provider!.WaveFormat;
        public bool Disposed { get; private set; }
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public void Init(IWaveProvider provider) => Provider = provider;
        public void Play() => PlaybackState = PlaybackState.Playing;
        public void Pause() => PlaybackState = PlaybackState.Paused;
        public void Stop() { PlaybackState = PlaybackState.Stopped; PlaybackStopped?.Invoke(this, new StoppedEventArgs()); }
        public void Dispose() { Disposed = true; PlaybackState = PlaybackState.Stopped; }
    }
}
