using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace OrchardSound;

public sealed record SessionData(AudioTrack[] Tracks, SavedSelection[] Selections);

public static class SessionFiles
{
    const int MaxTracks = 128, MaxSegments = 4096, MaxFades = 4096, MaxPatches = 8192, MaxDocumentBytes = 8 * 1024 * 1024;
    const long MaxPatchBytes = 512L * 1024 * 1024;
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, MaxDepth = 16 };

    public static async Task SaveAsync(string path, IEnumerable<AudioTrack> tracks, SelectionStore selections, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(tracks); ArgumentNullException.ThrowIfNull(selections);
        string target = Path.GetFullPath(path), folder = Path.GetDirectoryName(target)!;
        var sources = tracks.Take(MaxTracks + 1).Select(t => (Track: t, State: t.Snapshot())).ToArray();
        if (sources.Length > MaxTracks) throw new IOException("The session contains too many tracks.");
        long decodedBytes = sources.Sum(source => source.State.Source.Samples.LongLength * sizeof(float));
        if (decodedBytes > AudioFiles.MaxSessionBytes) throw new IOException("The session exceeds the decoded audio memory limit.");
        if (sources.Any(s => string.Equals(Path.GetFullPath(s.Track.Path), target, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("A session cannot overwrite one of its source sounds.");
        var documents = new List<TrackDocument>(); var patches = new List<(AudioPatch Patch, string Entry)>(); long totalPatchBytes = 0;
        foreach (var (track, state) in sources)
        {
            cancel.ThrowIfCancellationRequested(); string sourcePath = LocalSourcePath(track.Path, folder);
            using var source = OpenSource(sourcePath);
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(source, cancel));
            var payloads = new List<PatchDocument>();
            foreach (var patch in state.Patches ?? [])
            {
                totalPatchBytes += patch.Samples.LongLength * sizeof(float);
                if (totalPatchBytes > MaxPatchBytes || decodedBytes + totalPatchBytes > AudioFiles.MaxSessionBytes || patches.Count >= MaxPatches) throw new IOException("The session contains too much processed audio.");
                string entry = $"patches/t{documents.Count:0000}-p{payloads.Count:0000}.f32";
                payloads.Add(new() { SourceStart = patch.SourceStart, Frames = patch.Frames, Entry = entry, Sha256 = HashSamples(patch.Samples, cancel) }); patches.Add((patch, entry));
            }
            var document = new TrackDocument
            {
                SourcePath = Path.GetRelativePath(folder, sourcePath), SourceSha256 = hash, Offset = state.Offset, Gain = state.Gain, Enabled = state.Enabled,
                Fades = state.Fades.ToArray(), Segments = state.Segments?.ToArray() ?? track.GetSegments(), Patches = payloads.ToArray()
            };
            ValidateTrack(document); documents.Add(document);
        }
        var session = new SessionDocument { Tracks = documents.ToArray(), Selections = selections.Capture() };
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(session, JsonOptions);
        if (manifest.Length > MaxDocumentBytes) throw new IOException("The session document is too large.");
        cancel.ThrowIfCancellationRequested(); Directory.CreateDirectory(folder);
        string temporary = Path.Combine(folder, ".orchardsound-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                using (var archive = new ZipArchive(file, ZipArchiveMode.Create, true))
                {
                    using (var metadata = archive.CreateEntry("session.json", CompressionLevel.Fastest).Open()) await metadata.WriteAsync(manifest, cancel);
                    foreach (var (patch, entry) in patches)
                    {
                        cancel.ThrowIfCancellationRequested(); using var output = archive.CreateEntry(entry, CompressionLevel.Fastest).Open();
                        byte[] block = new byte[65536]; long offset = 0, size = patch.Samples.LongLength * sizeof(float);
                        while (offset < size)
                        {
                            int n = (int)Math.Min(block.Length, size - offset); Buffer.BlockCopy(patch.Samples, (int)offset, block, 0, n);
                            await output.WriteAsync(block.AsMemory(0, n), cancel); offset += n;
                        }
                    }
                }
                await file.FlushAsync(cancel);
            }
            cancel.ThrowIfCancellationRequested(); File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async Task<SessionData> LoadAsync(string path, CancellationToken cancel = default)
    {
        string sessionPath = Path.GetFullPath(path), folder = Path.GetDirectoryName(sessionPath)!;
        using var input = new FileStream(sessionPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (input.Length > MaxPatchBytes + MaxDocumentBytes * 2L) throw new IOException("The session file is too large.");
        byte[] signature = new byte[4]; int n = await input.ReadAsync(signature, cancel); input.Position = 0;
        bool isZip = n == 4 && signature[0] == 'P' && signature[1] == 'K' && signature[2] == 3 && signature[3] == 4;
        if (isZip) ValidateZipEntryCount(input);
        using var archive = isZip ? new ZipArchive(input, ZipArchiveMode.Read, true) : null;
        if (archive != null && (archive.Entries.Count > MaxPatches + 1 || archive.Entries.Select(e => e.FullName).Distinct(StringComparer.Ordinal).Count() != archive.Entries.Count))
            throw new IOException("The session archive has too many or duplicate entries.");
        SessionDocument document;
        try
        {
            if (archive != null)
            {
                var entry = archive.GetEntry("session.json") ?? throw new IOException("The session metadata is missing.");
                if (entry.Length > MaxDocumentBytes) throw new IOException("The session document is too large.");
                using var metadata = entry.Open(); document = await JsonSerializer.DeserializeAsync<SessionDocument>(metadata, JsonOptions, cancel) ?? throw new IOException("The session document is empty.");
            }
            else
            {
                if (input.Length > MaxDocumentBytes) throw new IOException("The session document is too large.");
                document = await JsonSerializer.DeserializeAsync<SessionDocument>(input, JsonOptions, cancel) ?? throw new IOException("The session document is empty.");
            }
        }
        catch (JsonException error) { throw new IOException($"Unable to read this {Branding.Name} session.", error); }
        if (document.Version is not (1 or 2) || document.Tracks == null || document.Selections == null || document.Tracks.Length > MaxTracks)
            throw new IOException("This session format is unsupported or invalid.");
        var selections = new SelectionStore();
        try { selections.Restore(document.Selections); } catch (ArgumentException error) { throw new IOException("The saved selections are invalid.", error); }
        foreach (var source in document.Tracks) ValidateTrack(source);
        var loaded = new List<AudioTrack>(); long decodedBytes = 0, totalPatchBytes = 0; int patchCount = 0;
        foreach (var source in document.Tracks)
        {
            cancel.ThrowIfCancellationRequested(); string sourcePath = LocalSourcePath(source.SourcePath, folder);
            using var guard = OpenSource(sourcePath);
            string actualHash = Convert.ToHexString(await SHA256.HashDataAsync(guard, cancel));
            if (!string.Equals(actualHash, source.SourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The source sound changed since this session was saved: " + Path.GetFileName(sourcePath));
            var track = await AudioFiles.LoadAsync(sourcePath, cancel); decodedBytes += track.Samples.LongLength * sizeof(float);
            if (decodedBytes + totalPatchBytes > AudioFiles.MaxSessionBytes) throw new IOException("The session exceeds the decoded audio memory limit.");
            var audioPatches = new List<AudioPatch>();
            foreach (var patch in source.Patches)
            {
                cancel.ThrowIfCancellationRequested(); totalPatchBytes += patch.Frames * 2 * sizeof(float); patchCount++;
                if (totalPatchBytes > MaxPatchBytes || decodedBytes + totalPatchBytes > AudioFiles.MaxSessionBytes || patchCount > MaxPatches || patch.Frames > AudioTrack.MaxSourceFrames || patch.SourceStart > AudioTrack.MaxSourceFrames - patch.Frames)
                    throw new IOException("Processed audio exceeds the session or source limits.");
                var entry = archive?.GetEntry(patch.Entry) ?? throw new IOException("A processed audio buffer is missing.");
                long size = patch.Frames * 2 * sizeof(float);
                if (entry.Length != size || size > int.MaxValue) throw new IOException("A processed audio buffer has an invalid size.");
                var samples = new float[(int)(patch.Frames * 2)]; byte[] block = new byte[65536]; int offset = 0;
                using var data = entry.Open();
                while (offset < size)
                {
                    int take = (int)Math.Min(block.Length, size - offset); await data.ReadExactlyAsync(block.AsMemory(0, take), cancel);
                    Buffer.BlockCopy(block, 0, samples, offset, take); offset += take;
                }
                if (!string.Equals(HashSamples(samples, cancel), patch.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("A processed audio buffer was damaged or changed.");
                try { audioPatches.Add(new AudioPatch(patch.SourceStart, samples)); } catch (ArgumentException error) { throw new IOException("A processed audio buffer is invalid.", error); }
            }
            try
            {
                track.Patches = audioPatches.ToArray(); track.Segments = source.Segments?.ToArray();
                track.Offset = source.Offset; track.Gain = source.Gain; track.Enabled = source.Enabled; track.Fades = source.Fades.ToList();
            }
            catch (ArgumentException error) { throw new IOException("A saved audio edit refers to an invalid source range.", error); }
            loaded.Add(track);
        }
        return new(loaded.ToArray(), selections.Capture());
    }

    static void ValidateZipEntryCount(FileStream input)
    {
        // Reject excessive or ZIP64 entry lists before ZipArchive allocates them.
        int size = (int)Math.Min(input.Length, 65557); byte[] tail = new byte[size]; input.Position = input.Length - size; input.ReadExactly(tail);
        bool found = false;
        for (int i = tail.Length - 22; i >= 0; i--)
        {
            if (tail[i] != 0x50 || tail[i + 1] != 0x4b || tail[i + 2] != 5 || tail[i + 3] != 6) continue;
            int count = tail[i + 10] | tail[i + 11] << 8;
            if (count > MaxPatches + 1) throw new IOException("The session archive contains too many entries.");
            found = true; break;
        }
        input.Position = 0; if (!found) throw new IOException("The session archive is incomplete.");
    }
    static FileStream OpenSource(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
    static string HashSamples(float[] samples, CancellationToken cancel)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); byte[] block = new byte[65536];
        long size = samples.LongLength * sizeof(float), offset = 0;
        while (offset < size)
        {
            cancel.ThrowIfCancellationRequested(); int n = (int)Math.Min(block.Length, size - offset);
            Buffer.BlockCopy(samples, (int)offset, block, 0, n); hash.AppendData(block, 0, n); offset += n;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    static string LocalSourcePath(string path, string folder)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) throw new IOException("Session sources must be local audio files.");
        string resolved = Path.GetFullPath(path, folder);
        if (resolved.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("Session sources must be local audio files.");
        return resolved;
    }
    static void ValidateTrack(TrackDocument source)
    {
        if (source == null || string.IsNullOrWhiteSpace(source.SourcePath) || source.SourcePath.Length > 32768 || source.SourceSha256 == null || source.SourceSha256.Length != 64 || !source.SourceSha256.All(Uri.IsHexDigit)) throw new IOException("A session source is invalid.");
        if (!float.IsFinite(source.Gain) || source.Gain < 0 || source.Gain > 10 || source.Offset < -SelectionStore.MaxTimelineFrame || source.Offset > SelectionStore.MaxTimelineFrame) throw new IOException("A session track has invalid volume or position.");
        if (source.Fades == null || source.Fades.Length > MaxFades || source.Segments?.Length > MaxSegments || source.Patches == null || source.Patches.Length > MaxPatches) throw new IOException("A session contains too many audio edits.");
        if (source.Segments != null) foreach (var segment in source.Segments)
            if (segment == null || segment.SourceStart < 0 || segment.Length <= 0 || segment.SourceStart > SelectionStore.MaxTimelineFrame || segment.Length > SelectionStore.MaxTimelineFrame) throw new IOException("A saved source segment is invalid.");
        foreach (var fade in source.Fades)
            if (fade == null || fade.Start < -SelectionStore.MaxTimelineFrame || fade.End > SelectionStore.MaxTimelineFrame || fade.End <= fade.Start || !ValidLevel(fade.StartLevel) || !ValidLevel(fade.EndLevel)) throw new IOException("A saved fade is invalid.");
        foreach (var patch in source.Patches)
            if (patch == null || patch.SourceStart < 0 || patch.Frames <= 0 || patch.Frames > AudioTrack.MaxSourceFrames || patch.SourceStart > AudioTrack.MaxSourceFrames - patch.Frames || string.IsNullOrWhiteSpace(patch.Entry) || !patch.Entry.StartsWith("patches/", StringComparison.Ordinal) || patch.Entry.Contains("..") || patch.Sha256 == null || patch.Sha256.Length != 64 || !patch.Sha256.All(Uri.IsHexDigit)) throw new IOException("A processed audio reference is invalid.");
    }
    static bool ValidLevel(float? value) => !value.HasValue || float.IsFinite(value.Value) && value.Value >= 0 && value.Value <= 1;
    sealed class SessionDocument
    {
        public SessionDocument() { }
        public int Version { get; set; } = 2;
        public TrackDocument[] Tracks { get; set; } = [];
        public SavedSelection[] Selections { get; set; } = [];
    }
    sealed class TrackDocument
    {
        public TrackDocument() { }
        public string SourcePath { get; set; } = "";
        public string SourceSha256 { get; set; } = "";
        public long Offset { get; set; }
        public float Gain { get; set; } = 1;
        public bool Enabled { get; set; } = true;
        public Fade[] Fades { get; set; } = [];
        public AudioSegment[]? Segments { get; set; }
        public PatchDocument[] Patches { get; set; } = [];
    }
    sealed class PatchDocument
    {
        public PatchDocument() { }
        public long SourceStart { get; set; }
        public long Frames { get; set; }
        public string Entry { get; set; } = "";
        public string Sha256 { get; set; } = "";
    }
}
