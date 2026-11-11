using NAudio.Wave;

namespace OrchardSound;

/// <summary>Renders only the latest cursor grain on one worker. Device reads copy prepared stereo audio.</summary>
public sealed class ScrubProvider : ISampleProvider, IDisposable
{
    public const int GrainFrames = AudioTrack.Rate * 60 / 1000;
    const int RampFrames = AudioTrack.Rate * 5 / 1000;
    readonly object gate = new();
    readonly AutoResetEvent wake = new(false);
    readonly Thread worker;
    float[] ready = new float[GrainFrames * 2], render = new float[GrainFrames * 2];
    TrackState[] pendingTracks = [];
    long requestedFrame, requestVersion, renderedVersion, position;
    long? previousFrame;
    int requestedDirection, readyDirection, readyCount, readAt;
    long readyFrame;
    float lastLeft, lastRight;
    bool disposed, workerExited;
    Exception? fault;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(AudioTrack.Rate, AudioTrack.Channels);
    public long Position { get { lock (gate) return position; } }
    public long RenderedVersion { get { lock (gate) return renderedVersion; } }
    public int BufferedSamples { get { lock (gate) return readyCount - readAt; } }
    public int Direction { get { lock (gate) return requestedDirection; } }

    public ScrubProvider(int initialDirection = 1)
    {
        requestedDirection = initialDirection < 0 ? -1 : 1;
        worker = new Thread(Produce) { IsBackground = true, Name = Branding.Name + " cursor scrub render" };
        worker.Start();
    }

    public bool MoveTo(IEnumerable<TrackState> tracks, long frame)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        var captured = tracks.ToArray();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (previousFrame == frame) return false;
            if (previousFrame.HasValue) requestedDirection = frame < previousFrame.Value ? -1 : 1;
            previousFrame = frame; pendingTracks = captured; requestedFrame = frame; requestVersion++;
            wake.Set(); return true;
        }
    }

    void Produce()
    {
        try
        {
            while (true)
            {
                TrackState[] tracks;
                long frame, version;
                int direction;
                lock (gate)
                {
                    if (disposed) return;
                    tracks = pendingTracks; frame = requestedFrame; direction = requestedDirection; version = requestVersion;
                }
                if (version == renderedVersion) { wake.WaitOne(30); continue; }
                int count = RenderGrain(tracks, frame, direction, render);
                float left, right;
                lock (gate)
                {
                    if (disposed) return;
                    if (version != requestVersion) continue;
                    left = lastLeft; right = lastRight;
                }
                int frames = count / 2;
                for (int i = 0; i < frames; i++)
                {
                    float attack = Math.Min(1, (i + 1f) / RampFrames);
                    float release = Math.Min(1, (frames - 1f - i) / RampFrames);
                    render[i * 2] = (left * (1 - attack) + render[i * 2] * attack) * release;
                    render[i * 2 + 1] = (right * (1 - attack) + render[i * 2 + 1] * attack) * release;
                }
                lock (gate)
                {
                    if (disposed) return;
                    if (version != requestVersion) continue;
                    (ready, render) = (render, ready);
                    readyFrame = frame; readyDirection = direction; readyCount = count; readAt = 0; renderedVersion = version;
                }
            }
        }
        catch (Exception error) { lock (gate) { fault = error; readyCount = readAt = 0; } }
        finally { lock (gate) { workerExited = true; if (disposed) wake.Dispose(); } }
    }

    static int RenderGrain(TrackState[] tracks, long frame, int direction, float[] buffer)
    {
        if (tracks.Length == 0) return 0;
        long minimum = Math.Min(0, tracks.Min(t => t.Offset));
        long maximum = Math.Max(minimum + 1, tracks.Max(t => t.Offset + t.Frames));
        if (frame < minimum || frame >= maximum) return 0;
        int frames = (int)Math.Min(GrainFrames, direction > 0 ? maximum - frame : frame - minimum + 1);
        long start = direction > 0 ? frame : frame - frames + 1;
        var mix = new MixProvider(tracks, start, start + frames, false);
        int count = mix.Read(buffer, 0, frames * 2);
        if (direction < 0)
            for (int a = 0, b = count - 2; a < b; a += 2, b -= 2)
            {
                (buffer[a], buffer[b]) = (buffer[b], buffer[a]);
                (buffer[a + 1], buffer[b + 1]) = (buffer[b + 1], buffer[a + 1]);
            }
        return count;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(offset));
        count -= count % 2;
        lock (gate)
        {
            if (disposed) return 0;
            if (fault != null) throw new IOException("Cursor scrub rendering failed.", fault);
            int copied = Math.Min(count, readyCount - readAt);
            for (int i = 0; i < copied; i++) buffer[offset + i] = ready[readAt + i];
            readAt += copied;
            if (copied > 0)
            {
                lastLeft = buffer[offset + copied - 2]; lastRight = buffer[offset + copied - 1];
                position = readyFrame + (readAt / 2 - 1) * readyDirection;
            }
            if (copied < count)
            { for (int i = copied; i < count; i++) buffer[offset + i] = 0; lastLeft = lastRight = 0; }
            return count;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; pendingTracks = []; readyCount = readAt = 0;
            if (workerExited) wake.Dispose(); else wake.Set();
        }
        if (Thread.CurrentThread != worker) worker.Join(1000);
    }
}
