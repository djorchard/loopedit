using NAudio.Wave;
using OrchardSound;

public static class RecordingChecks
{
    static IEnumerable<Control> All(Control root) => root.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(All(child)));
    public static async Task Run()
    {
        int checks = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception("FAILED RECORDING: " + name); checks++; Console.WriteLine("PASS RECORDING " + name); }
        string root = Path.Combine(Path.GetTempPath(), "OrchardRecording-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string FilePath(string name) => Path.Combine(root, name + ".wav");
        long WaveLength(string path) { using var reader = new WaveFileReader(path); return reader.Length; }
        async Task<bool> Fails<T>(Task task) where T : Exception
        { try { await task; return false; } catch (T) { return true; } }
        try
        {
            var first = new FakeInput(); int selectedId = -1;
            using (var capture = new RecordingCapture(id => { selectedId = id; return first; }))
            {
                int finishedCount = 0;
                var finished = new TaskCompletionSource<RecordingFinishedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
                capture.Finished += (_, args) => { Interlocked.Increment(ref finishedCount); finished.TrySetResult(args); };
                Check(first.StartCount == 0, "constructing capture does not open an input device");
                capture.Start(3, FilePath("plain"));
                Check(selectedId == 3 && first.WaveFormat.SampleRate == 44100 && first.WaveFormat.BitsPerSample == 16 && first.WaveFormat.Channels == 1 && capture.IsRecording, "explicit start chooses the requested input and PCM recording format");
                first.Emit([0, 0, 1, 0, 255, 127, 0, 128]);
                string path = await capture.StopAsync(); var result = await finished.Task.WaitAsync(TimeSpan.FromSeconds(1));
                using var reader = new WaveFileReader(path);
                Check(reader.Length == 8 && reader.WaveFormat.SampleRate == 44100 && reader.WaveFormat.BitsPerSample == 16 && reader.WaveFormat.Channels == 1, "stop flushes a valid WAV header and captured data");
                Check(first.Disposed && !capture.IsRecording && result.Path == path && result.Error == null && !result.LimitReached, "normal stop releases the device and reports the actual file");
                await capture.StopAsync();
                Check(first.StopCount == 1 && finishedCount == 1, "repeated stops are idempotent and finish once");
            }

            var limited = new FakeInput();
            using (var capture = new RecordingCapture(_ => limited, maxDataBytes: 8))
            {
                var finished = new TaskCompletionSource<RecordingFinishedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
                capture.Finished += (_, args) => finished.TrySetResult(args);
                capture.Start(0, FilePath("limited")); limited.Emit(new byte[32]);
                string path = await capture.Completion.WaitAsync(TimeSpan.FromSeconds(1)); var result = await finished.Task.WaitAsync(TimeSpan.FromSeconds(1));
                Check(capture.DataBytes == 8 && WaveLength(path) == 8 && result.LimitReached && limited.Disposed, "recording stops cleanly at its aligned byte budget");
                limited.Emit(new byte[16]); Check(WaveLength(path) == 8, "late input callbacks cannot change a finished recording");
            }
            Check(RecordingCapture.DefaultMaxDataBytes * AudioTrack.Rate * 8 / (RecordingCapture.SampleRate * 2) <= AudioFiles.MaxTrackBytes, "default recording budget also fits decoded editor audio");
            var stuckAtLimit = new FakeInput { IgnoreStop = true };
            using (var capture = new RecordingCapture(_ => stuckAtLimit, maxDataBytes: 4, stopTimeout: TimeSpan.FromMilliseconds(100)))
            {
                string path = FilePath("stuck at limit"); capture.Start(0, path); stuckAtLimit.Emit(new byte[16]);
                Check(await Fails<TimeoutException>(capture.Completion.WaitAsync(TimeSpan.FromSeconds(1))) && stuckAtLimit.Disposed && WaveLength(path) == 4, "automatic budget stops also enforce a device deadline");
            }

            var duplicate = new FakeInput();
            using (var capture = new RecordingCapture(_ => duplicate))
            {
                capture.Start(0, FilePath("single start")); duplicate.Emit(new byte[4]);
                bool rejected = false; try { capture.Start(0, FilePath("second start")); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected && capture.IsRecording && duplicate.StartCount == 1 && !duplicate.Disposed, "duplicate start rejects the request while preserving the active recording");
                await capture.StopAsync();
            }

            string existing = FilePath("existing"); await File.WriteAllBytesAsync(existing, [9, 8, 7]);
            var collision = new FakeInput();
            using (var capture = new RecordingCapture(_ => collision))
            {
                bool rejected = false; try { capture.Start(0, existing); } catch (IOException) { rejected = true; }
                Check(rejected && (await File.ReadAllBytesAsync(existing)).SequenceEqual(new byte[] { 9, 8, 7 }) && collision.StartCount == 0 && collision.Disposed, "an existing file is never overwritten or removed");
            }

            var failedStart = new FakeInput { FailStart = true };
            using (var capture = new RecordingCapture(_ => failedStart))
            {
                string path = FilePath("start failure"); bool rejected = false;
                try { capture.Start(0, path); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected && failedStart.Disposed && !File.Exists(path) && !capture.IsRecording, "start failure releases the input and removes its empty file");
            }

            var deviceError = new FakeInput();
            using (var capture = new RecordingCapture(_ => deviceError))
            {
                string path = FilePath("device failure"); capture.Start(0, path); deviceError.Emit(new byte[4]); deviceError.Complete(new IOException("Synthetic input error"));
                Check(await Fails<IOException>(capture.Completion) && deviceError.Disposed && WaveLength(path) == 4, "device errors preserve a closed usable partial WAV");
            }

            var noStop = new FakeInput { IgnoreStop = true };
            using (var capture = new RecordingCapture(_ => noStop, stopTimeout: TimeSpan.FromMilliseconds(100)))
            {
                string path = FilePath("timeout"); capture.Start(0, path); noStop.Emit(new byte[4]);
                Check(await Fails<TimeoutException>(capture.StopAsync()) && noStop.Disposed && WaveLength(path) == 4, "a nonresponsive input has a bounded stop and closes captured data");
            }

            var stopError = new FakeInput { FailStop = true };
            using (var capture = new RecordingCapture(_ => stopError))
            {
                string path = FilePath("stop failure"); capture.Start(0, path); stopError.Emit(new byte[4]);
                Check(await Fails<InvalidOperationException>(capture.StopAsync()) && stopError.Disposed && WaveLength(path) == 4, "a stop error still releases its input and WAV writer");
            }

            var empty = new FakeInput();
            using (var capture = new RecordingCapture(_ => empty))
            {
                string path = FilePath("empty"); capture.Start(0, path);
                Check(await Fails<IOException>(capture.StopAsync()) && !File.Exists(path), "empty recordings report no audio and leave no unusable file");
            }

            var disposedInput = new FakeInput();
            var disposedCapture = new RecordingCapture(_ => disposedInput);
            string disposedPath = FilePath("disposed"); disposedCapture.Start(0, disposedPath); disposedInput.Emit(new byte[6]); disposedCapture.Dispose(); disposedCapture.Dispose();
            Check(disposedInput.Disposed && !disposedCapture.IsRecording && await disposedCapture.Completion == disposedPath && WaveLength(disposedPath) == 6, "dispose is idempotent and closes the active capture");

            int unusedFactories = 0;
            using (var neverStarted = new RecordingCapture(_ => { unusedFactories++; return new FakeInput(); })) { }
            Check(unusedFactories == 0, "disposing an unused capture never opens a recording input");
            Exception? dialogFailure = null;
            var dialogThread = new Thread(() =>
            {
                try
                {
                    using var emptyDialog = new RecordingDeviceDialog([]);
                    Check(emptyDialog.SelectedInput == null && !All(emptyDialog).OfType<Button>().Single(b => b.AccessibleName == "Start recording").Enabled, "an empty device picker cannot start recording");
                    using var dialog = new RecordingDeviceDialog([new InputDevice(7, "Synthetic input A"), new InputDevice(9, "Synthetic input B")]);
                    All(dialog).OfType<ComboBox>().Single(combo => combo.AccessibleName == "Recording input device").SelectedIndex = 1;
                    Check(dialog.SelectedInput?.Id == 9, "the device picker returns the selected input without opening it");
                    Check(dialog.SelectedDestination is { Slot: null, Name: "New track" }, "the compatible single-argument picker defaults to a new track destination");
                    static ChannelSlot Loaded(bool enabled)
                    {
                        float[] samples = [.1f, -.1f];
                        return new() { Track = new AudioTrack { Path = "Synthetic destination.wav", Samples = samples, Peaks = new(samples), Enabled = enabled } };
                    }
                    var disabled = Loaded(false); var enabled = Loaded(true); var emptySlot = new ChannelSlot();
                    RecordingDestination[] choices = [new("Track 1", disabled), new("Track 2", enabled), new("Track 3", emptySlot), new("New track", null)];
                    using var targeted = new RecordingDeviceDialog([new InputDevice(1, "Synthetic input")], choices);
                    var destinations = All(targeted).OfType<ComboBox>().Single(combo => combo.AccessibleName == "Recording destination");
                    var action = All(targeted).OfType<Button>().Single(button => button.AccessibleName == "Start recording");
                    Check(ReferenceEquals(targeted.SelectedDestination?.Slot, emptySlot), "recording defaults to an existing empty channel before an enabled loaded channel");
                    destinations.SelectedIndex = 1;
                    Check(ReferenceEquals(targeted.SelectedDestination?.Slot, enabled) && action.Enabled, "the picker returns the chosen channel without recording or creating audio");
                    destinations.SelectedIndex = 3;
                    Check(targeted.SelectedDestination?.Slot == null && targeted.SelectedDestination?.Name == "New track", "new-track selection is explicit and remains distinct from existing channels");
                    destinations.SelectedIndex = -1; Check(!action.Enabled, "recording stays disabled until a destination is selected");
                    destinations.SelectedIndex = 0;
                    All(targeted).OfType<ComboBox>().Single(combo => combo.AccessibleName == "Recording input device").SelectedIndex = -1;
                    Check(!action.Enabled, "choosing a destination cannot bypass the missing-input guard");
                    using var noEmpty = new RecordingDeviceDialog([new InputDevice(1, "Synthetic input")], [choices[0], choices[1], choices[3]]);
                    Check(ReferenceEquals(noEmpty.SelectedDestination?.Slot, enabled), "without an empty channel the first enabled channel becomes the default destination");
                    using var excluded = new RecordingDeviceDialog([new InputDevice(1, "Synthetic input")], [choices[0], choices[3]]);
                    Check(excluded.SelectedDestination?.Slot == null, "new track is the default when every existing channel is excluded");
                    Check(destinations.BackColor == AppTheme.InputBackground && destinations.ForeColor == AppTheme.Text,
                        "recording destinations share the dark input palette");
                }
                catch (Exception error) { dialogFailure = error; }
            });
            dialogThread.SetApartmentState(ApartmentState.STA); dialogThread.Start();
            if (!dialogThread.Join(TimeSpan.FromSeconds(10))) throw new TimeoutException("Recording dialog fixture timed out.");
            if (dialogFailure != null) throw new Exception("Recording dialog fixture failed.", dialogFailure);
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"{checks} silent recording checks passed.");
    }

    sealed class FakeInput : IWaveIn
    {
        public WaveFormat WaveFormat { get; set; } = new(44100, 16, 1);
        public bool FailStart { get; init; }
        public bool FailStop { get; init; }
        public bool IgnoreStop { get; init; }
        public bool Disposed { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public void StartRecording() { StartCount++; if (FailStart) throw new InvalidOperationException("Synthetic start failure"); }
        public void StopRecording() { StopCount++; if (FailStop) throw new InvalidOperationException("Synthetic stop failure"); if (!IgnoreStop) Complete(); }
        public void Emit(byte[] bytes) => DataAvailable?.Invoke(this, new WaveInEventArgs(bytes, bytes.Length));
        public void Complete(Exception? error = null) => RecordingStopped?.Invoke(this, new StoppedEventArgs(error));
        public void Dispose() => Disposed = true;
    }
}
