using NAudio.Dsp;

namespace OrchardSound;

public enum AudioViewMode { Waveform, Spectrogram }

public sealed class SpectrogramData
{
    public const int FftSize = 4096, HopFrames = 1024, Bands = 192, LowestNote = 12, HighestNote = 132;
    const int MaximumBytes = 64 * 1024 * 1024;
    readonly byte[] levels;
    public int Columns { get; }
    public int ByteSize => levels.Length;
    public byte this[int column, int band] => levels[column * Bands + band];
    SpectrogramData(int columns, byte[] data) { Columns = columns; levels = data; }
    public static double NoteFrequency(double note) => 440 * Math.Pow(2, (note - 69) / 12);
    public static string NoteName(int note) => new[] { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" }[((note % 12) + 12) % 12] + (note / 12 - 1);
    public static SpectrogramData Compute(TrackState state, CancellationToken cancel = default)
        => ComputeCore(state, -1, cancel);
    public static SpectrogramData ComputeChannel(TrackState state, int channel, CancellationToken cancel = default)
    {
        if (channel < 0 || channel >= AudioTrack.Channels) throw new ArgumentOutOfRangeException(nameof(channel));
        return ComputeCore(state, channel, cancel);
    }
    static SpectrogramData ComputeCore(TrackState state, int channel, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        long count = Math.Max(1, (state.Frames + HopFrames - 1) / HopFrames);
        if (count > MaximumBytes / Bands) throw new IOException("This sound is too long for the spectrogram cache.");
        var data = new byte[checked((int)count * Bands)];
        var left = new Complex[FftSize]; Complex[] right = channel < 0 ? new Complex[FftSize] : [];
        var window = Enumerable.Range(0, FftSize).Select(i => (float)FastFourierTransform.HannWindow(i, FftSize)).ToArray();
        float normalization = 2 * FftSize / window.Sum();
        var lowBins = new int[Bands]; var highBins = new int[Bands];
        for (int band = 0; band < Bands; band++)
        {
            double low = NoteFrequency(LowestNote + (HighestNote - LowestNote) * band / (double)Bands);
            double high = NoteFrequency(LowestNote + (HighestNote - LowestNote) * (band + 1) / (double)Bands);
            lowBins[band] = Math.Clamp((int)Math.Floor(low * FftSize / AudioTrack.Rate), 1, FftSize / 2);
            highBins[band] = Math.Clamp((int)Math.Ceiling(high * FftSize / AudioTrack.Rate), lowBins[band], FftSize / 2);
        }
        var ends = new long[state.Segments.Length]; long total = 0;
        for (int i = 0; i < ends.Length; i++) ends[i] = total += state.Segments[i].Length;
        for (int column = 0; column < count; column++)
        {
            cancel.ThrowIfCancellationRequested();
            long first = (long)column * HopFrames - FftSize / 2;
            for (int i = 0; i < FftSize; i++)
            {
                long frame = first + i; float l = 0, r = 0;
                if (frame >= 0 && frame < total)
                {
                    int index = Array.BinarySearch(ends, frame + 1); if (index < 0) index = ~index;
                    long source = state.Segments[index].SourceStart + frame - (index == 0 ? 0 : ends[index - 1]);
                    float fade = 1; foreach (var envelope in state.Fades) fade *= envelope.At(frame);
                    l = state.GetSample(source, channel < 0 ? 0 : channel) * fade;
                    r = channel < 0 ? state.GetSample(source, 1) * fade : 0;
                }
                left[i].X = l * window[i]; left[i].Y = 0;
                if (channel < 0) { right[i].X = r * window[i]; right[i].Y = 0; }
            }
            FastFourierTransform.FFT(true, 12, left);
            if (channel < 0) FastFourierTransform.FFT(true, 12, right);
            for (int band = 0; band < Bands; band++)
            {
                float power = 0;
                for (int bin = lowBins[band]; bin <= highBins[band]; bin++)
                    power = Math.Max(power, channel < 0
                        ? (left[bin].X * left[bin].X + left[bin].Y * left[bin].Y + right[bin].X * right[bin].X + right[bin].Y * right[bin].Y) * .5f
                        : left[bin].X * left[bin].X + left[bin].Y * left[bin].Y);
                double db = 10 * Math.Log10(Math.Max(1e-20, power * normalization * normalization));
                data[column * Bands + band] = (byte)Math.Clamp((int)Math.Round((db + 96) * 255 / 96), 0, 255);
            }
        }
        return new((int)count, data);
    }
}
