using System.Text.Json.Nodes;
using System.IO.Compression;
using NAudio.Wave;
using OrchardSound;

public static class SelectionChecks
{
    public static async Task Run()
    {
        int checks = 0;
        void Check(bool condition, string name)
        { if (!condition) throw new Exception("FAILED SELECTION: " + name); checks++; Console.WriteLine("PASS SELECTION " + name); }
        static float[] Render(AudioTrack track, long start, long end)
        {
            var result = new float[checked((int)((end - start) * 2))];
            var mix = new MixProvider([track.Snapshot()], start, end, false);
            if (mix.Read(result, 0, result.Length) != result.Length) throw new Exception("Incomplete selection render.");
            return result;
        }
        var store = new SelectionStore();
        store.Save("  Kick  ", 100, 200); store.Save("Tail", 250, 400);
        Check(store.Items.Count == 2 && store.Items[0].Name == "Kick", "named selections trim their display names and remain separate");
        store.Save("kick", 120, 220);
        Check(store.Items.Count == 2 && store.Items[0] == new SavedSelection("kick", 120, 220), "saving an existing name replaces its range case-insensitively");
        var captured = store.Capture(); store.Save("Other", 500, 700); store.Restore(captured);
        Check(store.Items.Count == 2 && store.Items.All(s => s.Name != "Other"), "captured named selections restore independently of later edits");
        Check(store.Remove("KICK") && !store.Remove("Missing") && store.Items.Single().Name == "Tail", "named selections can be removed case-insensitively");
        bool invalidName = false; try { store.Save(" ", 0, 1); } catch (ArgumentException) { invalidName = true; }
        Check(invalidName, "empty selection names are rejected");
        bool emptyRange = false; try { store.Save("Empty", 2, 2); } catch (ArgumentException) { emptyRange = true; }
        Check(emptyRange, "a newly saved selection must contain audio time");
        bool duplicateRestore = false;
        try { store.Restore([new("Same", 1, 2), new("same", 3, 4)]); } catch (ArgumentException) { duplicateRestore = true; }
        Check(duplicateRestore && store.Items.Single().Name == "Tail", "invalid restore preserves the existing named selections");

        store.Restore([new("Before", 10, 40), new("After", 150, 200), new("Across", 20, 180), new("Removed", 70, 90)]);
        store.ApplyDelete(50, 100);
        Check(store.Items[0] == new SavedSelection("Before", 10, 40) && store.Items[1] == new SavedSelection("After", 100, 150), "deleting a section preserves earlier ranges and shifts later ranges");
        Check(store.Items[2] == new SavedSelection("Across", 20, 130), "a saved range crossing a cut retains the surviving audio interval");
        Check(store.Items[3] == new SavedSelection("Removed", 50, 50), "fully removed selections retain their name as an empty range");
        store.Restore([new("Inside", 60, 80), new("Cross", 20, 120), new("Outside", 200, 250)]);
        store.ApplyTrim(50, 100);
        Check(store.Items[0] == new SavedSelection("Inside", 10, 30) && store.Items[1] == new SavedSelection("Cross", 0, 50), "trimming clips and rebases named selections");
        Check(store.Items[2] == new SavedSelection("Outside", 50, 50), "trimmed-away selections remain named and empty");
        store.Restore([new("Negative", -100, -50)]); store.ApplyDelete(-200, -150);
        Check(store.Items[0] == new SavedSelection("Negative", -150, -100), "selection mapping supports negative track time");

        string root = Path.Combine(Path.GetTempPath(), "OrchardSound-selections-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "Source.wav");
            using (var writer = new WaveFileWriter(source, new WaveFormat(48000, 16, 2)))
            {
                for (int frame = 0; frame < 24000; frame++)
                {
                    short value = (short)(Math.Sin(frame * .033) * 6000);
                    writer.WriteByte((byte)value); writer.WriteByte((byte)(value >> 8));
                    writer.WriteByte((byte)-value); writer.WriteByte((byte)(-value >> 8));
                }
            }
            var track = await AudioFiles.LoadAsync(source);
            store.Restore([new("Same sound", 6000, 9000)]);
            float[] expected = Render(track, 6000, 9000);
            TimelineEditing.Delete([track], 2000, 5000); store.ApplyDelete(2000, 5000);
            var bookmark = store.Items.Single();
            Check(Render(track, bookmark.Start, bookmark.End).SequenceEqual(expected), "a saved selection returns the same source samples after an earlier cut");
            store.Save("Tail region", 9000, 12000);
            track.WriteVisibleSamples(3000, [.12f, -.24f, .33f, -.44f]);
            track.Gain = .8f; track.Fades.Add(new Fade(5000, 10000, true)); track.Offset = 1200;
            string session = Path.Combine(root, "Named.orchardsound");
            await SessionFiles.SaveAsync(session, [track], store);
            var restored = await SessionFiles.LoadAsync(session);
            Check(restored.Tracks.Length == 1 && restored.Selections.SequenceEqual(store.Capture()), "session save/load preserves multiple named range data");
            var loaded = restored.Tracks.Single();
            Check(loaded.Offset == track.Offset && loaded.Gain == track.Gain && loaded.Frames == track.Frames && loaded.GetSegments().SequenceEqual(track.GetSegments()) && loaded.Fades.SequenceEqual(track.Fades) && loaded.Patches.Length == track.Patches.Length, "session save/load preserves source edits, offset, gain, fades and processed buffers");
            Check(Render(loaded, loaded.Offset, loaded.Offset + loaded.Frames).SequenceEqual(Render(track, track.Offset, track.Offset + track.Frames)), "reloaded sessions render the identical edited sound");
            string json;
            using (var archive = ZipFile.OpenRead(session))
            using (var reader = new StreamReader(archive.GetEntry("session.json")!.Open())) json = await reader.ReadToEndAsync();
            Check(loaded.ReadVisibleSamples(3000, 3002).SequenceEqual(new[] { .12f, -.24f, .33f, -.44f }), "binary session buffers preserve processed sample values exactly");
            Check(json.Length < 20000 && !json.Contains("\"Samples\""), "session documents retain references and edits without duplicating source audio");
            Check(!Directory.GetFiles(root, ".orchardsound-*").Any(), "successful session saves remove temporary files");
            string damaged = Path.Combine(root, "Damaged.orchardsound");
            using (var originalArchive = ZipFile.OpenRead(session))
            using (var alteredArchive = ZipFile.Open(damaged, ZipArchiveMode.Create))
                foreach (var entry in originalArchive.Entries)
                {
                    using var content = new MemoryStream(); using (var input = entry.Open()) await input.CopyToAsync(content);
                    var bytes = content.ToArray(); if (entry.FullName.StartsWith("patches/")) bytes[0] ^= 1;
                    using var output = alteredArchive.CreateEntry(entry.FullName).Open(); await output.WriteAsync(bytes);
                }
            bool damagedRejected = false;
            try { await SessionFiles.LoadAsync(damaged); } catch (IOException error) { damagedRejected = error.Message.Contains("damaged"); }
            Check(damagedRejected, "altered processed samples are detected before returning a session");
            var emptyState = loaded.Snapshot(); loaded.Segments = [];
            string emptySession = Path.Combine(root, "Empty track.orchardsound"); await SessionFiles.SaveAsync(emptySession, [loaded], store);
            Check((await SessionFiles.LoadAsync(emptySession)).Tracks.Single().Frames == 0, "deleted-to-empty tracks survive session save/load without resurrecting source audio");
            emptyState.ApplyToSource();

            loaded.Enabled = false;
            string disabledSession = Path.Combine(root, "Disabled track.orchardsound");
            await SessionFiles.SaveAsync(disabledSession, [loaded], store);
            var disabledTrack = (await SessionFiles.LoadAsync(disabledSession)).Tracks.Single();
            Check(!disabledTrack.Enabled && Render(disabledTrack, disabledTrack.Offset, disabledTrack.Offset + disabledTrack.Frames).All(sample => sample == 0), "session round trip preserves an unticked track and excludes its playback audio");
            loaded.Enabled = true;

            string legacySession = Path.Combine(root, "Legacy enabled default.orchardsound");
            using (var originalArchive = ZipFile.OpenRead(session))
            using (var legacyArchive = ZipFile.Open(legacySession, ZipArchiveMode.Create))
                foreach (var entry in originalArchive.Entries)
                {
                    using var content = new MemoryStream(); using (var input = entry.Open()) await input.CopyToAsync(content);
                    byte[] bytes = content.ToArray();
                    if (entry.FullName == "session.json")
                    {
                        var legacy = JsonNode.Parse(bytes)!;
                        foreach (var item in legacy["Tracks"]!.AsArray()) item!.AsObject().Remove("Enabled");
                        bytes = System.Text.Encoding.UTF8.GetBytes(legacy.ToJsonString());
                    }
                    using var output = legacyArchive.CreateEntry(entry.FullName).Open(); await output.WriteAsync(bytes);
                }
            Check((await SessionFiles.LoadAsync(legacySession)).Tracks.Single().Enabled, "older sessions without a track tick field retain audible tracks by default");

            byte[] sourceBytes = await File.ReadAllBytesAsync(source);
            bool overwriteRejected = false;
            try { await SessionFiles.SaveAsync(source, [track], store); } catch (IOException) { overwriteRejected = true; }
            Check(overwriteRejected && (await File.ReadAllBytesAsync(source)).SequenceEqual(sourceBytes), "session save cannot overwrite its source audio");
            byte[] savedSession = await File.ReadAllBytesAsync(session);
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel(); bool cancelledSave = false;
                try { await SessionFiles.SaveAsync(session, [track], store, cancelled.Token); } catch (OperationCanceledException) { cancelledSave = true; }
                Check(cancelledSave && (await File.ReadAllBytesAsync(session)).SequenceEqual(savedSession) && !Directory.GetFiles(root, ".orchardsound-*").Any(), "cancelled session save preserves the previous file and cleans scratch output");
            }
            var invalid = JsonNode.Parse(json)!; invalid["Tracks"]![0]!["Segments"]![0]!["SourceStart"] = long.MaxValue;
            string bad = Path.Combine(root, "Invalid.orchardsound"); await File.WriteAllTextAsync(bad, invalid.ToJsonString());
            bool invalidEdit = false;
            try { await SessionFiles.LoadAsync(bad); } catch (IOException) { invalidEdit = true; }
            Check(invalidEdit, "invalid saved source ranges are rejected before returning a session");
            invalid = JsonNode.Parse(json)!; invalid["Tracks"]![0]!["SourcePath"] = @"\\untrusted.invalid\audio\sample.wav";
            await File.WriteAllTextAsync(bad, invalid.ToJsonString()); bool networkRejected = false;
            try { await SessionFiles.LoadAsync(bad); } catch (IOException) { networkRejected = true; }
            Check(networkRejected, "session documents cannot initiate remote file access");
            await File.WriteAllTextAsync(bad, "{\"Version\":900,\"Tracks\":[],\"Selections\":[]}"); bool versionRejected = false;
            try { await SessionFiles.LoadAsync(bad); } catch (IOException) { versionRejected = true; }
            Check(versionRejected, "unknown session versions are rejected");

            sourceBytes[^1] ^= 1; await File.WriteAllBytesAsync(source, sourceBytes);
            bool changedSource = false;
            try { await SessionFiles.LoadAsync(session); } catch (IOException error) { changedSource = error.Message.Contains("changed"); }
            Check(changedSource, "changed source bytes are rejected instead of selecting different audio");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"{checks} selection/session checks passed.");
    }
}
