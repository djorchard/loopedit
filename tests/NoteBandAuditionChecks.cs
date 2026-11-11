using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using OrchardSound;

public static class NoteBandAuditionChecks
{
    const int Rate = AudioTrack.Rate;

    public static void Run()
    {
        int checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            checks++;
            Console.WriteLine("PASS NOTE BAND " + name);
        }

        float[] samples = [.2f, -.4f, .6f, -.8f, 1, -1];
        var dry = new NoteBandAudition(new ArraySource(samples));
        var destination = new float[12];
        Array.Fill(destination, 7);
        Check(dry.Read(destination, 2, 3) == 3 && dry.Read(destination, 5, 6) == 3
            && dry.Read(destination, 0, 12) == 0, "bypass preserves odd reads, short reads and EOF");
        Check(destination.Skip(2).Take(samples.Length).SequenceEqual(samples)
            && destination.Take(2).Concat(destination.Skip(8)).All(value => value == 7),
            "bypass copies samples exactly at the requested offset");

        double Gain(int note, double testPitch)
        {
            int frames = note == SpectrogramData.LowestNote ? Rate * 5 : Rate * 2;
            var input = Tone(frames, testPitch, testPitch);
            var filtered = new float[input.Length];
            var audition = new NoteBandAudition(new ArraySource(input)) { MidiNote = note };
            audition.Read(filtered, 0, filtered.Length);
            return Rms(filtered, frames - Rate, 0) / Rms(input, frames - Rate, 0);
        }

        Check(Math.Abs(Gain(69, 69) - 1) < .005, "A4 passes at unity gain");
        double halfPower = Math.Sqrt(.5);
        Check(Math.Abs(Gain(69, 68.5) - halfPower) < .01 && Math.Abs(Gain(69, 69.5) - halfPower) < .01,
            "A4 cutoffs are half a semitone below and above the center");
        Check(Gain(69, 68) < .07 && Gain(69, 70) < .07 && Gain(69, 81) < .001,
            "the band rejects neighboring pitches and the octave above");
        Check(Math.Abs(Gain(SpectrogramData.LowestNote, SpectrogramData.LowestNote) - 1) < .03
            && Math.Abs(Gain(SpectrogramData.HighestNote, SpectrogramData.HighestNote) - 1) < .005,
            "both ends of the spectrogram remain stable and audible");

        var stereo = Tone(4096, 69, null);
        var all = new float[stereo.Length];
        var fragmented = new float[stereo.Length];
        new NoteBandAudition(new ArraySource(stereo)) { MidiNote = 69 }.Read(all, 0, all.Length);
        var split = new NoteBandAudition(new ArraySource(stereo)) { MidiNote = 69 };
        for (int offset = 0; offset < fragmented.Length;)
            offset += split.Read(fragmented, offset, Math.Min(97, fragmented.Length - offset));
        Check(all.SequenceEqual(fragmented) && Rms(all, 2048, 0) > .1 && Rms(all, 0, 1) == 0,
            "filter history survives odd chunks and keeps the stereo channels independent");
        Check(Math.Abs(all[2]) < Math.Abs(stereo[2]) / 100,
            "a note selected before the first read starts in-band");

        int fadeFrames = Rate / 100;
        var continuous = Tone(Rate * 3, 69, 81);
        var changed = new float[continuous.Length];
        var live = new NoteBandAudition(new ArraySource(continuous));
        int cursor = Rate + 26;
        live.Read(changed, 0, cursor);
        live.MidiNote = 69;
        live.Read(changed, cursor, Rate);
        Check(Math.Abs(changed[cursor + 1] - continuous[cursor + 1]) < .005,
            "enabling a note starts with a short continuous crossfade");
        Check(Rms(changed.AsSpan(0, cursor + Rate), Rate * 3 / 4, 0) > .1
            && Rms(changed.AsSpan(0, cursor + Rate), Rate * 3 / 4, 1) < .001,
            "a live note change filters the current stream");
        cursor += Rate;
        live.MidiNote = 81;
        live.Read(changed, cursor, Rate);
        Check(Rms(changed.AsSpan(0, cursor + Rate), Rate * 5 / 4, 0) < .001
            && Rms(changed.AsSpan(0, cursor + Rate), Rate * 5 / 4, 1) > .1,
            "moving to a new note changes the passband in both channels");
        cursor += Rate;
        live.MidiNote = null;
        live.Read(changed, cursor, Rate);
        Check(changed.Skip(cursor + fadeFrames * 2).Take(Rate - fadeFrames * 2)
            .SequenceEqual(continuous.Skip(cursor + fadeFrames * 2).Take(Rate - fadeFrames * 2)),
            "leaving the note restores exact dry playback after the fade");

        var hot = new float[Rate * 2];
        for (int frame = 0; frame < Rate; frame++)
            hot[frame * 2] = hot[frame * 2 + 1] = Math.Sign(Math.Sin(2 * Math.PI * 440 * frame / Rate));
        var limiter = new NoteBandAudition(new ArraySource(hot)) { MidiNote = 69 };
        var bounded = new float[hot.Length];
        limiter.Read(bounded, 0, bounded.Length);
        Check(bounded.All(sample => float.IsFinite(sample) && Math.Abs(sample) <= 1)
            && bounded.Any(sample => Math.Abs(sample) == 1), "loud narrow-band transients remain finite and bounded");

        var adapter = new SampleToWaveProvider(new NoteBandAudition(new ArraySource(stereo)) { MidiNote = 69 });
        var bytes = new byte[all.Length * sizeof(float) + 24];
        Array.Fill(bytes, (byte)0x7f);
        Check(adapter.Read(bytes, 12, all.Length * sizeof(float)) == all.Length * sizeof(float)
            && Enumerable.Range(0, all.Length).All(i => BitConverter.ToSingle(bytes, 12 + i * sizeof(float)) == all[i])
            && bytes.Take(12).Concat(bytes.TakeLast(12)).All(value => value == 0x7f),
            "NAudio's float buffer adapter preserves filtered samples and byte offsets");

        var reusable = new NoteBandAudition(new ArraySource(continuous)) { MidiNote = 69 };
        var block = new float[1024];
        reusable.Read(block, 0, block.Length);
        reusable.MidiNote = 70;
        reusable.Read(block, 0, block.Length);
        long allocations = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 96; i++)
        {
            reusable.MidiNote = i % 2 == 0 ? 69 : 81;
            reusable.Read(block, 0, block.Length);
        }
        Check(GC.GetAllocatedBytesForCurrentThread() == allocations, "repeated retuning allocates no managed memory in audio reads");
        Console.WriteLine($"{checks} note band audition checks passed.");
    }

    static float[] Tone(int frames, double left, double? right)
    {
        var result = new float[frames * 2];
        double leftStep = 2 * Math.PI * SpectrogramData.NoteFrequency(left) / Rate;
        double rightStep = right.HasValue ? 2 * Math.PI * SpectrogramData.NoteFrequency(right.Value) / Rate : 0;
        for (int i = 0; i < frames; i++)
        {
            result[i * 2] = (float)(.25 * Math.Sin(leftStep * i));
            result[i * 2 + 1] = (float)(.25 * Math.Sin(rightStep * i));
        }
        return result;
    }

    static double Rms(ReadOnlySpan<float> samples, int startFrame, int channel)
    {
        double sum = 0;
        for (int i = startFrame * 2 + channel; i < samples.Length; i += 2)
            sum += samples[i] * samples[i];
        return Math.Sqrt(sum / (samples.Length / 2 - startFrame));
    }

    sealed class ArraySource(float[] samples) : ISampleProvider
    {
        int position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            int read = Math.Min(count, samples.Length - position);
            for (int i = 0; i < read; i++) buffer[offset + i] = samples[position + i];
            position += read;
            return read;
        }
    }
}
