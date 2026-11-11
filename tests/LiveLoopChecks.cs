using System.Reflection;
using OrchardSound;

public static class LiveLoopChecks
{
    public static async Task Run()
    {
        int checks = 0;
        void Check(bool value, string name)
        { if (!value) throw new Exception("FAILED LIVE LOOP: " + name); checks++; Console.WriteLine("PASS LIVE LOOP " + name); }
        var samples = Enumerable.Range(0, 4000).SelectMany(frame => new[] { (frame + 1) / 10000f, -(frame + 1) / 20000f }).ToArray();
        var track = new AudioTrack { Path = "Live loop fixture.wav", Samples = samples, Peaks = new(samples) };
        MixProvider Mix(long start, long end, long cursor = 250, bool loop = true)
            => new([track.Snapshot()], 0, track.Frames, loop, cursor, start, end);
        static async Task Published(BufferedPlayback buffer, long revision, int frames = 1)
        {
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (buffer.AppliedRevision < revision || buffer.BufferedSamples < frames * 2)
            { if (DateTime.UtcNow >= deadline) throw new TimeoutException("Live loop publication deadline exceeded."); await Task.Delay(1); }
        }
        bool Matches(float[] output, params long[] frames)
            => frames.Select((frame, index) => output[index * 2] == samples[frame * 2] && output[index * 2 + 1] == samples[frame * 2 + 1]).All(equal => equal);
        using (var buffer = new BufferedPlayback(Mix(100, 900), 20))
        {
            int capacity = buffer.CapacitySamples;
            long revision = buffer.Retarget(Mix(500, 900), false); await Published(buffer, revision, capacity / 2);
            Check(buffer.Position == 250 && buffer.AppliedRevision == revision && buffer.BufferedSamples == capacity,
                "moving start ahead publishes a new full queue without consuming or jumping the current playhead");
            var output = new float[1310]; buffer.Read(output, 0, output.Length);
            var expected = Enumerable.Range(250, 650).Select(frame => (long)frame).Concat(Enumerable.Range(500, 5).Select(frame => (long)frame)).ToArray();
            Check(Matches(output, expected) && buffer.Position == 505, "pre-start audio plays forward into the loop and wraps at the exact selected end");
            Check(buffer.TryGetAudiblePosition(651, out long firstWrap) && firstWrap == 501,
                "output clock metadata follows the first new-loop frame after the lead-in");
            revision = buffer.Retarget(Mix(500, 503), false); await Published(buffer, revision, 8);
            Check(buffer.Position == 500, "moving end behind the playhead resets exactly to start before the next device read");
            output = new float[16]; buffer.Read(output, 0, output.Length);
            Check(Matches(output, 500, 501, 502, 500, 501, 502, 500, 501) && buffer.Position == 502,
                "a shortened loop discards old queued samples and has no modulo-offset restart");
            revision = buffer.Retarget(Mix(500, 501), false); await Published(buffer, revision, 8);
            buffer.Read(output, 0, output.Length);
            Check(Matches(output, Enumerable.Repeat(500L, 8).ToArray()) && buffer.Position == 501,
                "one-frame loops remain finite, channel-preserving and correctly positioned");
            revision = buffer.Retarget(Mix(500, 900), false); await Published(buffer, revision, 8);
            buffer.Read(output, 0, output.Length);
            Check(Matches(output, Enumerable.Range(501, 8).Select(frame => (long)frame).ToArray()) && buffer.Position == 509,
                "extending the end preserves the current next frame instead of restarting");
            revision = buffer.Retarget(Mix(100, 900), false); await Published(buffer, revision, 8);
            buffer.Read(output, 0, output.Length);
            Check(Matches(output, Enumerable.Range(509, 8).Select(frame => (long)frame).ToArray()),
                "moving start earlier preserves an existing in-loop playhead");
            for (int index = 0; index < 100; index++) revision = buffer.Retarget(Mix(600 + index, 900), false);
            await Published(buffer, revision, 8); buffer.Read(output, 0, output.Length);
            Check(buffer.AppliedRevision == buffer.RequestedRevision && Matches(output, Enumerable.Range(517, 8).Select(frame => (long)frame).ToArray()),
                "rapid marker moves coalesce to the last revision while retaining pre-start audio");
            long position = buffer.Position; revision = buffer.Retarget(Mix(0, 100), false); await Published(buffer, revision, 8);
            Check(position > 100 && buffer.Position == 0, "an end-only loop resets an out-of-range playhead to the default timeline start");
            buffer.Read(output, 0, output.Length);
            Check(Matches(output, Enumerable.Range(0, 8).Select(frame => (long)frame).ToArray()), "end-only replacement begins at the exact first stereo frame");
            long allocations = GC.GetAllocatedBytesForCurrentThread(); buffer.Read(output, 0, output.Length);
            Check(GC.GetAllocatedBytesForCurrentThread() == allocations, "live replacement leaves the device callback allocation-free");
        }
        using (var buffer = new BufferedPlayback(Mix(100, 900, 1200, false), 20))
        {
            var revision = buffer.Retarget(Mix(100, 300, 1200, false), false); await Published(buffer, revision, 8);
            var output = new float[16]; buffer.Read(output, 0, output.Length);
            Check(Matches(output, Enumerable.Range(1200, 8).Select(frame => (long)frame).ToArray()) && buffer.Position == 1208,
                "explicit cursor one-shots after a loop retain their whole-timeline position");
        }
        using (var buffer = new BufferedPlayback(Mix(100, 900), 20))
        {
            var old = new float[buffer.CapacitySamples]; buffer.Read(old, 0, old.Length);
            long position = buffer.Position, revision = buffer.Retarget(Mix(2000, 3000), false); await Published(buffer, revision, 8);
            var output = new float[16]; buffer.Read(output, 0, output.Length);
            Check(Matches(output, Enumerable.Range((int)position, 8).Select(frame => (long)frame).ToArray()),
                "retargeting after a device drain uses the submitted cursor rather than the producer head");
        }
        Check(track.Patches.Length == 0 && track.Samples.SequenceEqual(samples), "live loop edits leave source audio and edit history data unchanged");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                using var form = new EditorForm([]);
                var canvas = (WaveCanvas)typeof(EditorForm).GetField("canvas", flags)!.GetValue(form)!;
                canvas.Slots[0].Track = track; canvas.SelectionStart = 100; canvas.SelectionEnd = 200;
                MixProvider Live(bool fromCursor, long frame) => (MixProvider)typeof(EditorForm).GetMethod("CreatePlaybackMixCore", flags)!.Invoke(form, [frame, fromCursor, true])!;
                typeof(EditorForm).GetField("playing", flags)!.SetValue(form, Mix(100, 900));
                var retainedLoop = Live(true, 250);
                Check(retainedLoop.Looping && retainedLoop.Position == 100, "a cursor stream already looping restarts exactly at start after a live end passes behind it");
                typeof(EditorForm).GetField("playing", flags)!.SetValue(form, Mix(100, 900, 1200, false));
                var oneShot = Live(true, 1200);
                Check(!oneShot.Looping && oneShot.Position == 1200, "a cursor stream explicitly started after end retains its one-shot intent during live edits");
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(15))) throw new TimeoutException("Live cursor mode fixture deadline exceeded.");
        if (failure != null) throw new Exception("Live cursor mode fixture failed.", failure);
        Console.WriteLine($"{checks} live loop checks passed.");
    }
}
