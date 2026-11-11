using System.Diagnostics;
using NAudio.Wave;

namespace OrchardSound;

public static class AudioFiles
{
    public const long MaxTrackBytes = 256L * 1024 * 1024;
    public const long MaxSessionBytes = 512L * 1024 * 1024;
    public static string FindFfmpeg()
    {
        string? custom = Environment.GetEnvironmentVariable("ORCHARD_FFMPEG");
        if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom)) return custom;
        string beside = System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(beside)) return beside;
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            string candidate = System.IO.Path.Combine(dir.Trim().Trim('"'), "ffmpeg.exe");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("FFmpeg is required. Install FFmpeg and add its bin folder to PATH, or set ORCHARD_FFMPEG to ffmpeg.exe.");
    }
    static Process Start(IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(FindFfmpeg())
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new IOException("Could not start FFmpeg.");
    }
    static void Kill(Process process)
    { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } }
    public static async Task<AudioTrack> LoadAsync(string path, CancellationToken cancel = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        using var p = Start(["-v", "error", "-nostdin", "-i", path, "-map", "0:a:0", "-vn", "-f", "f32le", "-ac", "2", "-ar", "48000", "pipe:1"]);
        using var registration = deadline.Token.Register(() => Kill(p));
        var errors = p.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            using var data = new MemoryStream();
            byte[] block = new byte[65536]; int read;
            while ((read = await p.StandardOutput.BaseStream.ReadAsync(block, deadline.Token)) > 0)
            {
                if (data.Length + read > MaxTrackBytes) throw new IOException("This track exceeds the 256 MiB decoded audio limit (about 11 minutes). Trim it before loading.");
                data.Write(block, 0, read);
            }
            await p.WaitForExitAsync(deadline.Token);
            string error = await errors;
            if (p.ExitCode != 0) throw new IOException("Cannot decode this sound. " + error.Trim());
            if (data.Length < 8) throw new IOException("The file contains no audio samples.");
            float[] samples = new float[data.Length / 8 * 2];
            Buffer.BlockCopy(data.GetBuffer(), 0, samples, 0, samples.Length * 4);
            for (int i = 0; i < samples.Length; i++) if (!float.IsFinite(samples[i])) samples[i] = 0;
            return new AudioTrack { Path = System.IO.Path.GetFullPath(path), Samples = samples, Peaks = new PeakPyramid(samples), ImportedCues = WavCueFile.Read(path).Where(c => c.Frame <= samples.LongLength / 2).ToArray() };
        }
        finally
        {
            Kill(p);
            try { await errors; } catch (OperationCanceledException) { }
        }
    }
    public static async Task<(string Path, long Clipped)> ExportAsync(TrackState[] tracks, long start, long end,
        string folder, string stem, string extension, CancellationToken cancel = default, IEnumerable<CuePoint>? cues = null)
    {
        extension = extension.ToLowerInvariant();
        if (!new[] { ".wav", ".mp3", ".flac", ".ogg", ".m4a" }.Contains(extension)) throw new ArgumentException("Unsupported export format.");
        Directory.CreateDirectory(folder);
        string wav = System.IO.Path.Combine(folder, ".orchard-" + Guid.NewGuid().ToString("N") + ".wav");
        string encoded = extension == ".wav" ? wav : System.IO.Path.ChangeExtension(wav, extension);
        var mix = new MixProvider(tracks, start, end, false);
        if ((end - start) * 4 > uint.MaxValue - 100) throw new IOException("Selection is too long for a standard WAV file.");
        try
        {
            await Task.Run(() =>
            {
                using var writer = new WaveFileWriter(wav, new WaveFormat(48000, 16, 2));
                float[] samples = new float[32768]; byte[] bytes = new byte[samples.Length * 2]; int n;
                while ((n = mix.Read(samples, 0, samples.Length)) != 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    for (int i = 0; i < n; i++)
                    {
                        short value = (short)Math.Round(samples[i] < 0 ? samples[i] * 32768 : samples[i] * 32767);
                        bytes[i * 2] = (byte)value; bytes[i * 2 + 1] = (byte)(value >> 8);
                    }
                    writer.Write(bytes, 0, n * 2);
                }
            }, cancel);
            if (extension != ".wav")
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                deadline.CancelAfter(TimeSpan.FromMinutes(3));
                var args = new List<string> { "-v", "error", "-nostdin", "-n", "-i", wav };
                args.AddRange(extension switch
                {
                    ".mp3" => new[] { "-codec:a", "libmp3lame", "-q:a", "2" },
                    ".ogg" => new[] { "-codec:a", "libvorbis", "-q:a", "5" },
                    ".m4a" => new[] { "-codec:a", "aac", "-b:a", "192k" },
                    _ => new[] { "-codec:a", "flac" }
                });
                args.Add(encoded);
                using var p = Start(args);
                using var registration = deadline.Token.Register(() => Kill(p));
                var errors = p.StandardError.ReadToEndAsync(deadline.Token);
                try
                {
                    await p.WaitForExitAsync(deadline.Token);
                    string error = await errors;
                    if (p.ExitCode != 0) throw new IOException("Export failed. " + error.Trim());
                }
                finally { Kill(p); try { await errors; } catch (OperationCanceledException) { } }
            }
            // Atomic rename, with collision retry. Existing files are never overwritten.
            if (extension == ".wav" && cues != null) WavCueFile.Append(encoded, cues.Where(c => c.Frame >= start && c.Frame <= end).Select(c => c with { Frame = c.Frame - start }));
            for (int suffix = 1; ; suffix++)
            {
                cancel.ThrowIfCancellationRequested();
                string target = System.IO.Path.Combine(folder, $"{stem} {suffix}{extension}");
                try { File.Move(encoded, target, false); return (target, mix.ClippedSamples); }
                catch (IOException) when (File.Exists(target)) { }
            }
        }
        finally
        {
            if (File.Exists(wav)) File.Delete(wav);
            if (encoded != wav && File.Exists(encoded)) File.Delete(encoded);
        }
    }
}
