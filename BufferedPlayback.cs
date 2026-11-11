using NAudio.Wave;

namespace OrchardSound;

/// <summary>
/// Bounded render-ahead queue. The device callback copies samples only; mixing is
/// performed by one owned producer. Replacements join the same stream rather
/// than opening another player or mixing a delayed copy into it.
/// </summary>
public sealed class BufferedPlayback : ISampleProvider, IDisposable
{
    const int BlockSamples = 2048;
    const int TransitionFrames = AudioTrack.Rate / 100;
    readonly object gate = new();
    readonly AutoResetEvent wake = new(false);
    readonly Thread producer;
    readonly float[] ring;
    readonly long[] positions, clipMarks;
    readonly long[] audiblePositions;
    readonly float[] peaks;
    readonly float[] block, previousBlock;
    readonly long[] blockPositions;
    MixProvider source;
    MixProvider? pending, fadeFrom;
    bool pendingCrossfade, pendingRetarget, retargeting, complete, disposed;
    int readAt, writeAt, available, transitionAt;
    long position, clippedSamples, underruns, outputFrames;
    long requestedRevision, pendingRevision, activeRevision, appliedRevision;
    float peak;
    Exception? fault;
    public WaveFormat WaveFormat => source.WaveFormat;
    public long Position => Interlocked.Read(ref position);
    public long ClippedSamples => Interlocked.Read(ref clippedSamples);
    public long Underruns => Interlocked.Read(ref underruns);
    public float Peak { get { lock (gate) return peak; } }
    public int BufferedSamples { get { lock (gate) return available; } }
    public int CapacitySamples => ring.Length;
    public long RequestedRevision => Interlocked.Read(ref requestedRevision);
    public long AppliedRevision => Interlocked.Read(ref appliedRevision);

    public BufferedPlayback(MixProvider source, int bufferMilliseconds = 250)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (bufferMilliseconds < 20 || bufferMilliseconds > 2000) throw new ArgumentOutOfRangeException(nameof(bufferMilliseconds));
        this.source = source; position = source.Position;
        int frames = Math.Max(1, AudioTrack.Rate * bufferMilliseconds / 1000);
        ring = new float[frames * 2]; positions = new long[frames]; peaks = new float[frames]; clipMarks = new long[frames];
        int scratch = Math.Max(BlockSamples, ring.Length);
        block = new float[scratch]; previousBlock = new float[scratch]; blockPositions = new long[scratch / 2];
        // This is a bounded history of exact output positions, not an assumed
        // device latency. Older device counters are rejected after expiry.
        audiblePositions = new long[Math.Max(AudioTrack.Rate * 4, frames * 2)];
        audiblePositions[0] = position;
        // The memory-only mixer can fill its initial queue without waiting for
        // a clock or audio device. Playback therefore starts with real data.
        while (available < ring.Length && !complete) RenderOne();
        producer = new Thread(Produce) { IsBackground = true, Name = Branding.Name + " audio render-ahead" };
        producer.Start();
    }

    public void Replace(MixProvider replacement, bool crossfade = true)
        => QueueReplacement(replacement, crossfade, false);

    public long Retarget(MixProvider replacement, bool crossfade = true)
        => QueueReplacement(replacement, crossfade, true);

    long QueueReplacement(MixProvider replacement, bool crossfade, bool retarget)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (ReferenceEquals(replacement, source) || ReferenceEquals(replacement, pending)) return requestedRevision;
            pending = replacement; pendingCrossfade = crossfade; pendingRetarget = retarget || pendingRetarget || retargeting;
            pendingRevision = ++requestedRevision;
            wake.Set(); return pendingRevision;
        }
    }

    public bool TryGetAudiblePosition(long outputFrame, out long audioFrame)
    {
        lock (gate)
        {
            audioFrame = 0;
            if (disposed || outputFrame < 0 || outputFrame > outputFrames
                || outputFrames - outputFrame >= audiblePositions.Length) return false;
            audioFrame = audiblePositions[(int)(outputFrame % audiblePositions.Length)];
            return true;
        }
    }

    void RecordOutputFrame()
    {
        outputFrames++;
        audiblePositions[(int)(outputFrames % audiblePositions.Length)] = position;
    }

    void Produce()
    {
        try
        {
            while (true)
            {
                bool render;
                lock (gate)
                {
                    if (disposed) return;
                    render = retargeting || pendingRetarget || available < ring.Length && (!complete || pending != null);
                }
                if (render) RenderOne(); else wake.WaitOne(50);
            }
        }
        catch (Exception error) { lock (gate) { fault = error; complete = true; } }
    }

    void RenderOne()
    {
        MixProvider current; int count; bool retarget; long revision, before;
        lock (gate)
        {
            if (disposed) return;
            if (!retargeting && !pendingRetarget && available == ring.Length) return;
            if (pending != null)
            {
                var prior = source;
                source = pending; retargeting = pendingRetarget; activeRevision = pendingRevision;
                source.Seek(Normalize(source, retargeting ? position : prior.Position));
                fadeFrom = pendingCrossfade ? prior : null; transitionAt = 0;
                pending = null; pendingRetarget = false; complete = false; fault = null;
            }
            current = source; retarget = retargeting; revision = activeRevision;
            if (retarget)
            {
                current.Seek(Normalize(current, position)); fadeFrom?.Seek(position); transitionAt = 0;
            }
            before = current.Position;
            count = retarget ? ring.Length : Math.Min(BlockSamples, ring.Length - available); count -= count % 2;
        }
        long clippedBefore = current.ClippedSamples;
        int produced = current.ReadWithPositions(block, 0, count, blockPositions);
        long clipped = current.ClippedSamples - clippedBefore;
        float blockPeak = current.Peak;
        var previous = fadeFrom;
        if (previous != null && produced > 0)
        {
            int oldRead = previous.Read(previousBlock, 0, produced);
            if (oldRead < produced) Array.Clear(previousBlock, oldRead, produced - oldRead);
            blockPeak = Math.Max(blockPeak, previous.Peak);
            for (int frame = 0; frame < produced / 2 && transitionAt < TransitionFrames; frame++, transitionAt++)
            {
                float blend = (transitionAt + 1f) / TransitionFrames;
                for (int ch = 0; ch < 2; ch++)
                { int i = frame * 2 + ch; block[i] = previousBlock[i] * (1 - blend) + block[i] * blend; }
            }
            if (transitionAt >= TransitionFrames) fadeFrom = null;
        }
        lock (gate)
        {
            if (disposed) return;
            if (pendingRetarget && pendingRevision > revision) return;
            int skip = 0;
            if (retarget)
            {
                long desired = Normalize(current, position);
                if (desired != before)
                {
                    int match = -1;
                    for (int frame = 0; frame + 1 < produced / 2; frame++) if (blockPositions[frame] == desired) { match = frame + 1; break; }
                    if (match < 0) return;
                    skip = match * 2;
                }
                readAt = writeAt = available = 0; retargeting = false; position = desired;
            }
            for (int sample = skip; sample < produced; sample += 2)
            {
                ring[writeAt] = block[sample]; ring[writeAt + 1] = block[sample + 1];
                int frameIndex = writeAt / 2;
                positions[frameIndex] = blockPositions[sample / 2]; peaks[frameIndex] = blockPeak;
                clipMarks[frameIndex] = sample + 2 == produced ? clipped : 0;
                writeAt = (writeAt + 2) % ring.Length;
            }
            available += produced - skip; Interlocked.Exchange(ref appliedRevision, revision);
            if (produced < count) complete = true;
        }
    }

    static long Normalize(MixProvider provider, long frame) => provider.Looping && frame >= provider.LoopEndFrame
        ? provider.LoopStartFrame : Math.Clamp(frame, provider.StartFrame, provider.EndFrame);

    public int Read(float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(offset));
        count -= count % 2;
        int copied;
        lock (gate)
        {
            if (disposed) return 0;
            if (fault != null && available == 0) throw new IOException("Audio buffering failed.", fault);
            copied = Math.Min(count, available); copied -= copied % 2;
            float nextPeak = 0;
            for (int sample = 0; sample < copied; sample += 2)
            {
                buffer[offset + sample] = ring[readAt]; buffer[offset + sample + 1] = ring[readAt + 1];
                int frameIndex = readAt / 2; position = positions[frameIndex];
                RecordOutputFrame();
                nextPeak = Math.Max(nextPeak, peaks[frameIndex]); clippedSamples += clipMarks[frameIndex];
                readAt = (readAt + 2) % ring.Length;
            }
            available -= copied; peak = nextPeak;
            if (copied < count && !complete)
            {
                // Never replay a stale region on underflow. Silence keeps the
                // one device alive while the producer catches up.
                for (int sample = copied; sample < count; sample++) buffer[offset + sample] = 0;
                for (int sample = copied; sample < count; sample += 2) RecordOutputFrame();
                underruns++; copied = count;
            }
            wake.Set();
        }
        return copied;
    }

    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; pending = null; fadeFrom = null; }
        wake.Set();
        if (Thread.CurrentThread != producer && !producer.Join(1000)) return;
        wake.Dispose();
    }
}
