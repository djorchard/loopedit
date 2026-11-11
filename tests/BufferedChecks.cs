using OrchardSound;

public static class BufferedChecks
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAILED BUFFER: " + name); checks++; Console.WriteLine("PASS BUFFER " + name); }
        static AudioTrack Track(string name, params float[] frames)
        {
            float[] data = frames.SelectMany(v => new[] { v, -v }).ToArray();
            return new() { Path = name, Samples = data, Peaks = new(data) };
        }
        static MixProvider Mix(AudioTrack track, bool loop = true, long? start = null) => new([track.Snapshot()], 0, track.Frames, loop, start);
        static void Wait(BufferedPlayback buffer, int samples)
        { if (!SpinWait.SpinUntil(() => buffer.BufferedSamples >= samples, TimeSpan.FromSeconds(2))) throw new TimeoutException("Audio producer did not refill the queue."); }

        var audible = Track("Audible.wav", .1f, .2f, .3f, .4f);
        var disabled = Track("Disabled.wav", .1f, .2f, .3f, .4f); disabled.Enabled = false;
        Check(audible.Enabled && audible.Snapshot().Enabled && !disabled.Snapshot().Enabled, "tracks are enabled by default and snapshots retain mute state");
        var frozen = disabled.Snapshot(); disabled.Enabled = true;
        Check(!frozen.Enabled, "changing a row cannot alter a captured enabled state");
        var mix = new MixProvider([audible.Snapshot(), frozen], 0, 4, false); var data = new float[8]; mix.Read(data, 0, 8);
        Check(data.SequenceEqual(audible.Samples), "unticked audio is not summed into playback");
        disabled.Enabled = false;
        Check(Editing.ExportStem([audible, disabled], 0, 4) == "Audible", "export names exclude disabled tracks");
        disabled.Enabled = true; frozen.ApplyToSource();
        Check(!disabled.Enabled, "Undo snapshots restore the track enabled state");

        using (var buffer = new BufferedPlayback(Mix(audible, start: 2), 20))
        {
            Check(buffer.BufferedSamples == buffer.CapacitySamples && buffer.Position == 2, "startup prefills the queue without advancing the consumed playhead");
            var output = new float[256]; long framesRead = 0; bool exact = true;
            for (int turn = 0; turn < 50; turn++)
            {
                Wait(buffer, output.Length); int n = buffer.Read(output, 0, output.Length);
                for (int i = 0; i < n; i++)
                {
                    long frame = (2 + framesRead + i / 2) % 4;
                    exact &= output[i] == audible.Samples[frame * 2 + i % 2];
                }
                framesRead += n / 2;
            }
            Check(exact && buffer.Underruns == 0, "ring wraps preserve exact loop samples without accumulating or echoing audio");
            long expected = (2 + framesRead) % 4;
            if (expected == 0) expected = 4;
            Check(buffer.Position == expected, "playhead follows consumed loop samples rather than producer render-ahead");
        }

        using (var finite = new BufferedPlayback(Mix(audible, false, 1), 20))
        {
            var output = Enumerable.Repeat(9f, 20).ToArray(); int n = finite.Read(output, 0, output.Length);
            Check(n == 6 && output.Take(6).SequenceEqual(audible.Samples.Skip(2)) && output.Skip(6).All(v => v == 9), "one-shot playback returns only remaining samples at EOF");
            Check(finite.Read(output, 0, output.Length) == 0, "completed source returns EOF instead of replaying stale queued data");
        }

        using (var buffer = new BufferedPlayback(Mix(audible), 20))
        {
            int capacity = buffer.CapacitySamples;
            var output = Enumerable.Repeat(9f, capacity + 32).ToArray();
            Check(buffer.Read(output, 0, output.Length) == output.Length && buffer.Underruns == 1, "temporary underflow keeps one output alive");
            Check(output.Skip(capacity).All(v => v == 0), "underflow emits silence rather than a repeated or delayed copy");
        }

        var positive = Track("Positive.wav", Enumerable.Repeat(.2f, 48000).ToArray());
        var negative = Track("Negative.wav", Enumerable.Repeat(-.2f, 48000).ToArray());
        using (var buffer = new BufferedPlayback(Mix(positive), 20))
        {
            var output = new float[512]; Wait(buffer, output.Length); buffer.Read(output, 0, output.Length);
            long consumed = output.Length / 2;
            buffer.Replace(Mix(negative));
            bool bounded = true, heardNew = false, positionExact = true;
            for (int turn = 0; turn < 20; turn++)
            {
                Wait(buffer, output.Length); buffer.Read(output, 0, output.Length); consumed += output.Length / 2;
                bounded &= output.All(v => Math.Abs(v) <= .20001f);
                heardNew |= output[0] < -.199f;
                positionExact &= buffer.Position == consumed;
            }
            Check(bounded && heardNew, "live source crossfade stays at unity and never adds a second echoed stream");
            Check(positionExact, "live replacement keeps timeline position continuous");
            for (int i = 0; i < 100; i++) buffer.Replace(Mix(i % 2 == 0 ? positive : negative));
            Wait(buffer, output.Length); buffer.Read(output, 0, output.Length);
            Check(output.All(float.IsFinite), "rapid pending replacements remain a single finite audio stream");
        }

        var disposed = new BufferedPlayback(Mix(audible), 20); disposed.Dispose(); disposed.Dispose();
        Check(disposed.Read(new float[4], 0, 4) == 0, "disposing a buffer is idempotent and stops playback data");
        bool rejected = false; try { disposed.Replace(Mix(audible)); } catch (ObjectDisposedException) { rejected = true; }
        Check(rejected, "disposed buffers reject new producers");
        bool invalidSize = false; try { _ = new BufferedPlayback(Mix(audible), 1); } catch (ArgumentOutOfRangeException) { invalidSize = true; }
        Check(invalidSize, "buffer capacity is bounded");

        var concurrent = new BufferedPlayback(Mix(audible), 20);
        Exception? callbackError = null;
        var reader = Task.Run(() =>
        {
            try { var output = new float[64]; while (concurrent.Read(output, 0, output.Length) != 0) Thread.Yield(); }
            catch (Exception error) { callbackError = error; }
        });
        concurrent.Dispose();
        Check(reader.Wait(TimeSpan.FromSeconds(2)) && callbackError == null, "disposing during device reads cannot touch a closed wake handle");

        using (var buffer = new BufferedPlayback(Mix(audible), 20))
        {
            var output = new float[4]; buffer.Read(output, 0, 4);
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10; i++) buffer.Read(output, 0, 4);
            Check(GC.GetAllocatedBytesForCurrentThread() == allocated, "device reads allocate no managed memory");
        }
        Console.WriteLine($"{checks} buffered playback checks passed.");
    }
}
