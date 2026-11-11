namespace OrchardSound;

sealed class SpectrogramCache : IDisposable
{
    sealed class Entry
    {
        public required TrackState State;
        public readonly CancellationTokenSource Cancellation = new();
        public SpectrogramData? Data;
        public string? Error;
        public long Access;
        public void Cancel() { try { Cancellation.Cancel(); } catch (ObjectDisposedException) { } }
    }
    static readonly SemaphoreSlim workers = new(2);
    readonly Dictionary<(AudioTrack Track, int Channel), Entry> entries = [];
    readonly Action changed;
    readonly long maximumBytes;
    readonly object gate = new();
    long revision, access;
    bool disposed;
    public long Revision => Interlocked.Read(ref revision);
    public long CachedBytes { get { lock (gate) return entries.Values.Sum(entry => (long)(entry.Data?.ByteSize ?? 0)); } }
    public SpectrogramCache(Action changed, long maximumBytes = 64L * 1024 * 1024)
    { if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes)); this.changed = changed; this.maximumBytes = maximumBytes; }
    static bool Matches(TrackState a, TrackState b) => ReferenceEquals(a.Source.Samples, b.Source.Samples) && a.Segments.SequenceEqual(b.Segments) && a.Fades.SequenceEqual(b.Fades) && a.Patches.SequenceEqual(b.Patches);
    public SpectrogramData? Get(AudioTrack track, out string? error)
        => GetChannel(track, -1, out error);
    public SpectrogramData? GetChannel(AudioTrack track, int channel, out string? error)
    {
        if (channel < -1 || channel >= AudioTrack.Channels) throw new ArgumentOutOfRangeException(nameof(channel));
        lock (gate)
        {
            if (disposed) { error = null; return null; }
            var state = track.Snapshot(); var key = (track, channel);
            if (!entries.TryGetValue(key, out var entry) || !Matches(entry.State, state))
            {
                entry?.Cancel(); entry = new() { State = state }; entries[key] = entry;
                _ = BuildAsync(key, entry, entry.Cancellation.Token);
            }
            entry.Access = ++access; error = entry.Error; return entry.Data;
        }
    }
    async Task BuildAsync((AudioTrack Track, int Channel) key, Entry entry, CancellationToken token)
    {
        bool entered = false;
        try
        {
            await workers.WaitAsync(token).ConfigureAwait(false); entered = true;
            var data = await Task.Run(() => key.Channel < 0 ? SpectrogramData.Compute(entry.State, token) : SpectrogramData.ComputeChannel(entry.State, key.Channel, token), token).ConfigureAwait(false);
            lock (gate)
            {
                if (disposed || token.IsCancellationRequested || !entries.TryGetValue(key, out var current) || current != entry) return;
                if (data.ByteSize > maximumBytes) entry.Error = "Spectrogram exceeds the cache memory limit.";
                else
                {
                    entry.Data = data;
                    while (entries.Values.Sum(item => (long)(item.Data?.ByteSize ?? 0)) > maximumBytes)
                    {
                        var oldest = entries.Where(pair => pair.Value != entry && pair.Value.Data != null).OrderBy(pair => pair.Value.Access).First();
                        oldest.Value.Cancel(); entries.Remove(oldest.Key);
                    }
                }
                Interlocked.Increment(ref revision);
            }
            Notify();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            lock (gate)
            {
                if (disposed || token.IsCancellationRequested || !entries.TryGetValue(key, out var current) || current != entry) return;
                entry.Error = error.Message; Interlocked.Increment(ref revision);
            }
            Notify();
        }
        finally { if (entered) workers.Release(); entry.Cancellation.Dispose(); }
    }
    void Notify() { try { changed(); } catch (Exception) { /* The view can close while background analysis completes. */ } }
    public void KeepOnly(IEnumerable<AudioTrack> tracks)
    {
        var active = new HashSet<AudioTrack>(tracks, ReferenceEqualityComparer.Instance);
        lock (gate)
            foreach (var key in entries.Keys.Where(key => !active.Contains(key.Track)).ToArray()) { entries[key].Cancel(); entries.Remove(key); }
    }
    public void KeepOnlyChannels(IEnumerable<(AudioTrack Track, int Channel)> channels)
    {
        var active = channels.ToHashSet();
        lock (gate)
            foreach (var key in entries.Keys.Where(key => !active.Contains(key)).ToArray()) { entries[key].Cancel(); entries.Remove(key); }
    }
    public void Dispose()
    { lock (gate) { disposed = true; foreach (var entry in entries.Values) entry.Cancel(); entries.Clear(); } }
}
