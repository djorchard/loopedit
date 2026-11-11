using NAudio.Wave;

namespace OrchardSound;

public sealed record InputDevice(int Id, string Name);

public sealed class RecordingFinishedEventArgs(string path, Exception? error, bool limitReached) : EventArgs
{
    public string Path { get; } = path;
    public Exception? Error { get; } = error;
    public bool LimitReached { get; } = limitReached;
}

public sealed class RecordingCapture : IDisposable
{
    public const int SampleRate = 44100;
    public const int BitsPerSample = 16;
    public const int Channels = 1;
    // A captured file must also fit the editor's decoded 48 kHz stereo float budget.
    public const long DefaultMaxDataBytes = AudioFiles.MaxTrackBytes * SampleRate * 2 / (AudioTrack.Rate * 8) / 2 * 2;
    readonly object gate = new();
    readonly Func<int, IWaveIn> inputFactory;
    readonly long maxDataBytes;
    readonly TimeSpan stopTimeout;
    IWaveIn? input;
    WaveFileWriter? writer;
    TaskCompletionSource<string>? completion;
    SynchronizationContext? callerContext;
    Exception? pendingError;
    bool started, finalized, stopRequested, disposed, ownsFile, limitReached;
    long dataBytes;
    string targetPath = "";

    public event EventHandler<RecordingFinishedEventArgs>? Finished;
    public bool IsRecording { get { lock (gate) return started && !finalized; } }
    public string TargetPath { get { lock (gate) return targetPath; } }
    public long DataBytes { get { lock (gate) return dataBytes; } }
    public Task<string> Completion { get { lock (gate) return completion?.Task ?? throw new InvalidOperationException("Recording has not started."); } }

    public RecordingCapture(Func<int, IWaveIn>? inputFactory = null, long? maxDataBytes = null, TimeSpan? stopTimeout = null)
    {
        this.inputFactory = inputFactory ?? (id => new WaveInEvent { DeviceNumber = id, BufferMilliseconds = 50 });
        this.maxDataBytes = (maxDataBytes ?? DefaultMaxDataBytes) / 2 * 2;
        this.stopTimeout = stopTimeout ?? TimeSpan.FromSeconds(5);
        if (this.maxDataBytes < 2 || this.maxDataBytes > DefaultMaxDataBytes) throw new ArgumentOutOfRangeException(nameof(maxDataBytes));
        if (this.stopTimeout <= TimeSpan.Zero || this.stopTimeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(stopTimeout));
    }

    public static InputDevice[] ListInputs()
    {
        var devices = new List<InputDevice>();
        for (int i = 0; i < WaveInEvent.DeviceCount; i++) devices.Add(new(i, WaveInEvent.GetCapabilities(i).ProductName));
        return devices.ToArray();
    }

    public void Start(int inputId, string targetWavPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetWavPath);
        if (inputId < 0) throw new ArgumentOutOfRangeException(nameof(inputId));
        string path = System.IO.Path.GetFullPath(targetWavPath);
        if (!System.IO.Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Recordings must use the .wav extension.", nameof(targetWavPath));
        bool accepted = false;
        try
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (started) throw new InvalidOperationException("Create a new recording capture for each recording.");
                started = true; accepted = true; targetPath = path; callerContext = SynchronizationContext.Current;
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = completion.Task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                input = inputFactory(inputId) ?? throw new IOException("The recording input could not be created.");
                input.WaveFormat = new WaveFormat(SampleRate, BitsPerSample, Channels);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                FileStream? stream = null;
                try
                {
                    stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    ownsFile = true;
                    writer = new WaveFileWriter(stream, input.WaveFormat); stream = null;
                }
                finally { stream?.Dispose(); }
                input.DataAvailable += OnDataAvailable;
                input.RecordingStopped += OnRecordingStopped;
                input.StartRecording();
            }
        }
        catch (Exception error)
        {
            bool finish;
            lock (gate) finish = accepted && started && !finalized;
            if (finish) Finish(error);
            throw;
        }
    }

    void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        bool stop = false;
        lock (gate)
        {
            if (finalized || writer == null) return;
            try
            {
                if (args.BytesRecorded < 0 || args.BytesRecorded > args.Buffer.Length) throw new IOException("The input returned an invalid audio buffer.");
                int length = (int)Math.Min(args.BytesRecorded, maxDataBytes - dataBytes);
                length -= length % 2;
                if (length > 0) { writer.Write(args.Buffer, 0, length); dataBytes += length; }
                if (dataBytes >= maxDataBytes) { limitReached = true; stop = true; }
            }
            catch (Exception error) { pendingError ??= error; stop = true; }
        }
        if (stop) RequestStop();
    }

    void OnRecordingStopped(object? sender, StoppedEventArgs args) => Finish(args.Exception);

    void RequestStop()
    {
        IWaveIn? device; Task<string>? done;
        lock (gate)
        {
            if (finalized || stopRequested) return;
            stopRequested = true; device = input; done = completion?.Task;
        }
        if (device == null) { Finish(new IOException("The recording input is unavailable.")); return; }
        _ = Task.Run(() =>
        {
            try { device.StopRecording(); }
            catch (Exception error) { Finish(error); }
        });
        if (done != null) _ = EnforceStopDeadline(done);
    }

    async Task EnforceStopDeadline(Task<string> done)
    {
        try { await done.WaitAsync(stopTimeout).ConfigureAwait(false); }
        catch (TimeoutException) { Finish(new TimeoutException("The recording input did not stop in time. Captured audio was closed safely.")); }
        catch (Exception) { }
    }

    public async Task<string> StopAsync()
    {
        Task<string> done;
        lock (gate) done = completion?.Task ?? throw new InvalidOperationException("Recording has not started.");
        RequestStop();
        try { return await done.WaitAsync(stopTimeout).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            Finish(new TimeoutException("The recording input did not stop in time. Captured audio was closed safely."));
            return await done.ConfigureAwait(false);
        }
    }

    void Finish(Exception? error, bool notify = true)
    {
        IWaveIn? device; WaveFileWriter? file; TaskCompletionSource<string>? done;
        string path; bool limit, removeEmpty; SynchronizationContext? context;
        lock (gate)
        {
            if (!started || finalized) return;
            finalized = true; error ??= pendingError;
            device = input; input = null; file = writer; writer = null; done = completion;
            path = targetPath; limit = limitReached; context = callerContext;
            if (error == null && dataBytes == 0) error = new IOException("No audio was recorded.");
            removeEmpty = ownsFile && dataBytes == 0;
        }
        if (device != null)
        {
            device.DataAvailable -= OnDataAvailable; device.RecordingStopped -= OnRecordingStopped;
            try { device.Dispose(); } catch (Exception closeError) { error ??= closeError; }
        }
        try { file?.Dispose(); } catch (Exception closeError) { error ??= closeError; }
        if (removeEmpty)
        {
            try { File.Delete(path); } catch (Exception cleanupError) { error ??= cleanupError; }
        }
        if (error == null) done?.TrySetResult(path); else done?.TrySetException(error);
        if (!notify || disposed) return;
        var result = new RecordingFinishedEventArgs(path, error, limit);
        void Raise(object? _) { if (!disposed) Finished?.Invoke(this, result); }
        if (context == null) Raise(null);
        else { try { context.Post(Raise, null); } catch (InvalidOperationException) { } }
    }

    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        Finish(null, false);
    }
}
