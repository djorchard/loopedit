using System.Numerics;
using NAudio.Wave;

namespace OrchardSound;

/// <summary>Applies the hovered semitone band to an existing playback stream.</summary>
public sealed class NoteBandAudition : ISampleProvider
{
    const int Bypass = -1;
    const int Order = 4;
    readonly ISampleProvider source;
    readonly int channels;
    readonly int transitionFrames;
    Band active;
    Band incoming;
    int requestedNote = Bypass;
    int remainingFrames;
    int channel;
    bool started;

    public WaveFormat WaveFormat { get; }

    public int? MidiNote
    {
        get
        {
            int note = Volatile.Read(ref requestedNote);
            return note == Bypass ? null : note;
        }
        set
        {
            if (value is < SpectrogramData.LowestNote or > SpectrogramData.HighestNote)
                throw new ArgumentOutOfRangeException(nameof(value));
            Volatile.Write(ref requestedNote, value ?? Bypass);
        }
    }

    public NoteBandAudition(ISampleProvider source)
    {
        ArgumentNullException.ThrowIfNull(source);
        this.source = source;
        WaveFormat = source.WaveFormat;
        channels = WaveFormat.Channels;
        transitionFrames = Math.Max(1, WaveFormat.SampleRate / 100);
        active = new(WaveFormat);
        incoming = new(WaveFormat);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset > buffer.Length - count)
            throw new ArgumentOutOfRangeException(nameof(offset));
        int read = source.Read(buffer, offset, count);
        if (read == 0) return 0;

        int note = Volatile.Read(ref requestedNote);
        if (!started)
        {
            active.Tune(note);
            started = true;
        }

        if (remainingFrames == 0 && active.Note == Bypass && note == Bypass)
        {
            channel = (channel + read % channels) % channels;
            return read;
        }

        for (int i = offset; i < offset + read; i++)
        {
            // Apply a new request at a frame boundary. Rapid hover changes wait
            // for the current fade to complete, keeping both channels in sync.
            if (channel == 0 && remainingFrames == 0 && note != active.Note)
            {
                incoming.Tune(note);
                remainingFrames = transitionFrames;
            }

            float sample = buffer[i];
            double output = active.Process(channel, sample);
            if (remainingFrames > 0)
            {
                double blend = (transitionFrames - remainingFrames + 1d) / transitionFrames;
                output += blend * (incoming.Process(channel, sample) - output);
            }
            buffer[i] = (float)output;

            if (++channel < channels) continue;
            channel = 0;
            if (remainingFrames > 0 && --remainingFrames == 0)
                (active, incoming) = (incoming, active);
        }

        active.TrimSilence();
        if (remainingFrames > 0) incoming.TrimSilence();
        return read;
    }

    readonly record struct Section(double Gain, double A1, double A2);

    struct SectionState
    {
        public double First;
        public double Second;
    }

    sealed class Band(WaveFormat format)
    {
        readonly Section[] sections = new Section[Order];
        readonly SectionState[] state = new SectionState[Order * format.Channels];

        public int Note { get; private set; } = Bypass;

        public void Tune(int note)
        {
            Note = note;
            Array.Clear(state);
            if (note == Bypass) return;

            double low = SpectrogramData.NoteFrequency(note - .5);
            double high = Math.Min(SpectrogramData.NoteFrequency(note + .5), format.SampleRate * .499);
            if (high <= low)
            {
                Array.Clear(sections);
                return;
            }

            // Prewarp the cutoffs before mapping the analog Butterworth band-pass
            // to digital second-order sections. Each section rejects DC and Nyquist.
            // https://docs.scipy.org/doc/scipy/reference/generated/scipy.signal.lp2bp_zpk.html
            double warpedLow = Math.Tan(Math.PI * low / format.SampleRate);
            double warpedHigh = Math.Tan(Math.PI * high / format.SampleRate);
            double centerSquared = warpedLow * warpedHigh;
            double halfWidth = (warpedHigh - warpedLow) / 2;
            double centerAngle = 2 * Math.Atan(Math.Sqrt(centerSquared));
            Complex delay = Complex.FromPolarCoordinates(1, -centerAngle);
            Complex delaySquared = delay * delay;
            double numeratorAtCenter = Complex.Abs(1 - delaySquared);

            for (int index = 0; index < Order / 2; index++)
            {
                double angle = Math.PI * (2 * index + 1 + Order) / (2 * Order);
                Complex halfPole = Complex.FromPolarCoordinates(halfWidth, angle);
                Complex root = Complex.Sqrt(halfPole * halfPole - centerSquared);
                sections[2 * index] = MakeSection(halfPole + root);
                sections[2 * index + 1] = MakeSection(halfPole - root);
            }

            // Place the most damped sections first to limit internal peaks.
            for (int i = 1; i < sections.Length; i++)
            {
                int j = i;
                while (j > 0 && sections[j - 1].A2 > sections[j].A2)
                {
                    (sections[j - 1], sections[j]) = (sections[j], sections[j - 1]);
                    j--;
                }
            }

            Section MakeSection(Complex analogPole)
            {
                Complex pole = (1 + analogPole) / (1 - analogPole);
                double a1 = -2 * pole.Real;
                double a2 = pole.Real * pole.Real + pole.Imaginary * pole.Imaginary;
                double gain = Complex.Abs(1 + a1 * delay + a2 * delaySquared) / numeratorAtCenter;
                return new(gain, a1, a2);
            }
        }

        public double Process(int channel, float sample)
        {
            if (Note == Bypass) return sample;
            double output = sample;
            int channelStart = channel * Order;
            for (int index = 0; index < sections.Length; index++)
            {
                var section = sections[index];
                ref var memory = ref state[channelStart + index];
                double input = section.Gain * output;
                output = input + memory.First;
                memory.First = memory.Second - section.A1 * output;
                memory.Second = -input - section.A2 * output;
            }
            return Math.Clamp(output, -1, 1);
        }

        public void TrimSilence()
        {
            // Discard tails below audible precision before they can denormalize.
            for (int i = 0; i < state.Length; i++)
            {
                if (Math.Abs(state[i].First) < 1e-20) state[i].First = 0;
                if (Math.Abs(state[i].Second) < 1e-20) state[i].Second = 0;
            }
        }
    }
}
