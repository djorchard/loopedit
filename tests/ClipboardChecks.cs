using System.Reflection;
using System.Runtime.InteropServices;
using OrchardSound;

public static class ClipboardChecks
{
    static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "loopsmith-clipboard-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                int checks = 0;
                void Check(bool condition, string name)
                { if (!condition) throw new Exception(name); checks++; Console.WriteLine("PASS CLIPBOARD " + name); }
                void Await(Task task)
                {
                    var deadline = System.Diagnostics.Stopwatch.StartNew();
                    while (!task.IsCompleted)
                    {
                        Application.DoEvents();
                        if (deadline.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Clipboard operation deadline exceeded.");
                        Thread.Sleep(1);
                    }
                    task.GetAwaiter().GetResult();
                }
                T Result<T>(Task<T> task) { Await(task); return task.Result; }
                void Reject(Task task, string name)
                {
                    try { Await(task); }
                    catch (Exception error) when (error is IOException or OperationCanceledException)
                    { Check(true, name); return; }
                    throw new Exception("Expected clipboard import rejection: " + name);
                }

                byte[] wav = WaveBytes();
                string source = System.IO.Path.Combine(root, "Copied.wav"); File.WriteAllBytes(source, wav);
                string other = System.IO.Path.Combine(root, "Notes.txt"); File.WriteAllText(other, "not audio");
                var fileDrop = new FakeDataObject(DataFormats.FileDrop, new[] { source, source, other, System.IO.Path.Combine(root, "Missing.wav") });
                Check(ClipboardAudio.GetFiles(fileDrop).SequenceEqual(new[] { source }), "copied audio files are filtered and deduplicated");
                var byteData = new FakeDataObject(DataFormats.WaveAudio, wav);
                Check(ReferenceEquals(ClipboardAudio.GetWaveData(byteData), wav), "WaveAudio byte arrays are accepted without clipboard access");
                using var stream = new MemoryStream(wav); stream.Position = stream.Length;
                Check(ReferenceEquals(ClipboardAudio.GetWaveData(new FakeDataObject("RIFF", stream)), stream), "RIFF audio streams are accepted");
                Check(ClipboardAudio.GetWaveData(new FakeDataObject(DataFormats.Text, "hello")) == null, "text is not treated as audio");

                string first = Result(ClipboardAudio.WriteWaveAsync(wav, root));
                string second = Result(ClipboardAudio.WriteWaveAsync(stream, root));
                Check(System.IO.Path.GetFileName(first) == "Clipboard 1.wav" && System.IO.Path.GetFileName(second) == "Clipboard 2.wav" && File.ReadAllBytes(first).SequenceEqual(wav) && File.ReadAllBytes(second).SequenceEqual(wav), "raw audio is retained in unique numbered WAV files");
                Check(stream.Position == stream.Length && stream.CanRead, "borrowed audio streams are rewound and restored without disposal");
                int saved = Directory.GetFiles(root).Length;
                Reject(ClipboardAudio.WriteWaveAsync(new byte[12], root), "invalid WAV headers are rejected");
                using (var oversized = new OversizedStream()) Reject(ClipboardAudio.WriteWaveAsync(oversized, root), "oversized audio is rejected before copying");
                using (var broken = new BrokenStream(wav)) Reject(ClipboardAudio.WriteWaveAsync(broken, root), "copy failures are surfaced");
                using (var cancel = new CancellationTokenSource())
                { cancel.Cancel(); Reject(ClipboardAudio.WriteWaveAsync(wav, root, cancel.Token), "cancelled clipboard imports stop before writing"); }
                Check(Directory.GetFiles(root).Length == saved && File.ReadAllBytes(first).SequenceEqual(wav), "failed and cancelled imports remove only their own partial file");

                var decoded = Result(ClipboardAudio.LoadWaveAsync(wav, root));
                Check(decoded.Frames == 48 && decoded.Enabled && File.Exists(decoded.Path), "retained clipboard WAV decodes through the normal audio loader");
                saved = Directory.GetFiles(root).Length;
                Reject(ClipboardAudio.LoadWaveAsync(wav, root, AudioFiles.MaxSessionBytes - 4), "decoded clipboard audio respects the session memory limit");
                Reject(ClipboardAudio.LoadWaveAsync("RIFF\0\0\0\0WAVE"u8.ToArray(), root), "undecodable RIFF audio is rejected");
                Check(Directory.GetFiles(root).Length == saved, "decoding and session-limit failures remove their retained source");
                Check(ClipboardAudio.StorageFolder(new[] { decoded }) == root && System.IO.Path.GetFileName(ClipboardAudio.StorageFolder([])) == "LoopEdit Clipboard" && System.IO.Path.IsPathFullyQualified(ClipboardAudio.StorageFolder([])), "clipboard storage uses a source folder or the Music clipboard folder");

                using var form = new EditorForm([]); _ = form.Handle;
                var canvas = (WaveCanvas)typeof(EditorForm).GetField("canvas", Private)!.GetValue(form)!;
                var status = (Label)typeof(EditorForm).GetField("status", Private)!.GetValue(form)!;
                var reader = typeof(EditorForm).GetField("clipboardReader", Private)!;
                var paste = typeof(EditorForm).GetMethod("PasteAudio", Private)!;
                void Paste(ChannelSlot? slot = null) => Await((Task)paste.Invoke(form, [slot])!);
                int reads = 0;
                reader.SetValue(form, (Func<IDataObject?>)(() => { reads++; return new FakeDataObject(DataFormats.Text, "hello"); }));
                Application.DoEvents();
                Check(reads == 0, "clipboard reads happen only after an explicit paste");
                Paste(); Check(reads == 1 && status.Text.Contains("No audio") && !canvas.Tracks.Any(), "unsupported clipboard content reports a quiet status message");
                reader.SetValue(form, (Func<IDataObject?>)(() => throw new ExternalException("Synthetic clipboard lock")));
                Paste(); Check(status.Text.Contains("Clipboard is busy") && canvas.Enabled, "locked clipboard reports retry guidance without a popup");
                reader.SetValue(form, (Func<IDataObject?>)(() => null));
                Paste(); Check(status.Text.Contains("Clipboard is empty"), "empty clipboard reports a status message");

                canvas.Slots[0].Track = decoded;
                var target = new ChannelSlot(); canvas.Slots.Add(target);
                reader.SetValue(form, (Func<IDataObject?>)(() => byteData));
                Paste(target);
                var pasted = target.Track;
                Check(pasted != null && pasted.Frames == 48 && canvas.Slots.Count == 2 && System.IO.Path.GetDirectoryName(pasted.Path) == root && File.Exists(pasted.Path), "WAV paste fills the requested empty channel and retains its source beside existing audio");
                Paste(target);
                Check(ReferenceEquals(target.Track, pasted) && canvas.Slots.Count == 3 && canvas.Tracks.Count() == 3, "pasting into an occupied channel adds a track without replacing its audio");
                reader.SetValue(form, (Func<IDataObject?>)(() => fileDrop));
                Paste();
                Check(canvas.Tracks.Count() == 4 && canvas.Tracks.Last().Path == source, "copied audio files use the existing multitrack file loader");
                int before = reads;
                reader.SetValue(form, (Func<IDataObject?>)(() => { reads++; return byteData; }));
                typeof(EditorForm).GetField("busy", Private)!.SetValue(form, true);
                Paste();
                typeof(EditorForm).GetField("busy", Private)!.SetValue(form, false);
                Check(reads == before && canvas.Tracks.Count() == 4, "paste does not read or alter audio while another operation is busy");
                Console.WriteLine($"{checks} clipboard checks passed.");
            }
            catch (Exception error) { failure = error; }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(45))) throw new TimeoutException("Clipboard fixture deadline exceeded.");
        if (failure != null) throw new Exception("Clipboard fixture failed", failure);
    }

    static byte[] WaveBytes()
    {
        using var data = new MemoryStream();
        using var writer = new BinaryWriter(data);
        writer.Write("RIFF"u8); writer.Write(36 + 48 * 4); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((short)1); writer.Write((short)2); writer.Write(48000); writer.Write(192000); writer.Write((short)4); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(48 * 4);
        for (int i = 0; i < 48; i++) { writer.Write((short)3276); writer.Write((short)-3276); }
        return data.ToArray();
    }

    sealed class OversizedStream : MemoryStream
    { public override long Length => AudioFiles.MaxTrackBytes + 1; }
    sealed class BrokenStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= 12) throw new IOException("Synthetic read failure");
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
    // Deliberately model legacy clipboard providers that expose only IDataObject.
#pragma warning disable WFO1001
    sealed class FakeDataObject(string format, object value) : IDataObject
    {
        readonly Dictionary<string, object?> values = new(StringComparer.Ordinal) { [format] = value };
        public object? GetData(string format, bool autoConvert) => values.GetValueOrDefault(format);
        public object? GetData(string format) => GetData(format, false);
        public object? GetData(Type format) => GetData(format.FullName!);
        public bool GetDataPresent(string format, bool autoConvert) => values.ContainsKey(format);
        public bool GetDataPresent(string format) => GetDataPresent(format, false);
        public bool GetDataPresent(Type format) => GetDataPresent(format.FullName!);
        public string[] GetFormats(bool autoConvert) => values.Keys.ToArray();
        public string[] GetFormats() => GetFormats(false);
        public void SetData(string format, bool autoConvert, object? data) => values[format] = data;
        public void SetData(string format, object? data) => SetData(format, false, data);
        public void SetData(Type format, object? data) => SetData(format.FullName!, data);
        public void SetData(object? data) { if (data != null) SetData(data.GetType(), data); }
    }
#pragma warning restore WFO1001
}
