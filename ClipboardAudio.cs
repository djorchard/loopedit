namespace OrchardSound;

public static class ClipboardAudio
{
    static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    { ".wav", ".mp3", ".flac", ".ogg", ".opus", ".m4a", ".aac", ".aif", ".aiff", ".wma", ".mp4" };

    public static string[] GetFiles(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop, false) || data.GetData(DataFormats.FileDrop, false) is not string[] files) return [];
        return files.Where(path => Extensions.Contains(System.IO.Path.GetExtension(path)) && File.Exists(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static object? GetWaveData(IDataObject data)
    {
        foreach (string format in new[] { DataFormats.WaveAudio, "RIFF" })
        {
            if (!data.GetDataPresent(format, false)) continue;
            object? value = data.GetData(format, false);
            if (value is Stream or byte[]) return value;
        }
        return null;
    }

    public static string StorageFolder(IEnumerable<AudioTrack> tracks)
    {
        var first = tracks.FirstOrDefault();
        if (first != null) return System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(first.Path))!;
        string music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        if (string.IsNullOrEmpty(music)) music = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music");
        return System.IO.Path.Combine(music, Branding.Name + " Clipboard");
    }

    public static async Task<AudioTrack> LoadWaveAsync(object audio, string folder, long sessionBytes = 0, CancellationToken cancel = default)
    {
        if (sessionBytes < 0) throw new ArgumentOutOfRangeException(nameof(sessionBytes));
        if (sessionBytes >= AudioFiles.MaxSessionBytes) throw new IOException("The session is full. Remove a track before adding more audio.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        string path = await WriteWaveAsync(audio, folder, deadline.Token);
        try
        {
            var track = await Task.Run(() => AudioFiles.LoadAsync(path, deadline.Token), deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            if (sessionBytes + track.Samples.LongLength * sizeof(float) > AudioFiles.MaxSessionBytes)
                throw new IOException("The session is full. Remove a track before adding more audio.");
            return track;
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    public static async Task<string> WriteWaveAsync(object audio, string folder, CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        using var bytes = audio is byte[] value ? new MemoryStream(value, false) : null;
        var input = bytes ?? audio as Stream ?? throw new IOException("The clipboard does not contain WAV audio.");
        if (!input.CanRead) throw new IOException("The clipboard audio cannot be read.");
        long? previous = input.CanSeek ? input.Position : null;
        string? path = null;
        try
        {
            if (input.CanSeek)
            {
                if (input.Length > AudioFiles.MaxTrackBytes) throw new IOException("Clipboard audio exceeds the 256 MiB input limit.");
                input.Position = 0;
            }
            byte[] header = new byte[12];
            await input.ReadExactlyAsync(header, cancel);
            if (!header.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !header.AsSpan(8, 4).SequenceEqual("WAVE"u8))
                throw new IOException("The clipboard audio is not a RIFF WAV file.");
            Directory.CreateDirectory(folder);
            FileStream output;
            for (int number = 1; ; number++)
            {
                cancel.ThrowIfCancellationRequested();
                string candidate = System.IO.Path.Combine(folder, $"Clipboard {number}.wav");
                try
                {
                    output = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
                    path = candidate; break;
                }
                catch (IOException) when (File.Exists(candidate)) { }
            }
            await using (output)
            {
                await output.WriteAsync(header, cancel);
                byte[] block = new byte[65536];
                long total = header.Length;
                int read;
                while ((read = await input.ReadAsync(block, cancel)) > 0)
                {
                    if (total + read > AudioFiles.MaxTrackBytes) throw new IOException("Clipboard audio exceeds the 256 MiB input limit.");
                    await output.WriteAsync(block.AsMemory(0, read), cancel); total += read;
                }
                cancel.ThrowIfCancellationRequested();
            }
            return path;
        }
        catch
        {
            if (path != null) File.Delete(path);
            throw;
        }
        finally
        {
            if (previous.HasValue) input.Position = previous.Value;
        }
    }
}
