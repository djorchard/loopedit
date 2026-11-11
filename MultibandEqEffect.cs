namespace OrchardSound;

public sealed record EqBandSettings(double GainDb = 0, double? EndGainDb = null);

public sealed record MultibandEqSettings
{
    public static IReadOnlyList<double> CenterFrequencies { get; } = Array.AsReadOnly(new double[] { 31.25, 62.5, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 });
    public EqBandSettings Hz31 { get; init; } = new();
    public EqBandSettings Hz63 { get; init; } = new();
    public EqBandSettings Hz125 { get; init; } = new();
    public EqBandSettings Hz250 { get; init; } = new();
    public EqBandSettings Hz500 { get; init; } = new();
    public EqBandSettings Hz1000 { get; init; } = new();
    public EqBandSettings Hz2000 { get; init; } = new();
    public EqBandSettings Hz4000 { get; init; } = new();
    public EqBandSettings Hz8000 { get; init; } = new();
    public EqBandSettings Hz16000 { get; init; } = new();
    public EqBandSettings BandAt(int index) => index switch
    {
        0 => Hz31, 1 => Hz63, 2 => Hz125, 3 => Hz250, 4 => Hz500, 5 => Hz1000, 6 => Hz2000, 7 => Hz4000, 8 => Hz8000, 9 => Hz16000,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };
    public MultibandEqSettings WithBand(int index, EqBandSettings band)
    {
        ArgumentNullException.ThrowIfNull(band);
        return index switch
        {
            0 => this with { Hz31 = band }, 1 => this with { Hz63 = band }, 2 => this with { Hz125 = band }, 3 => this with { Hz250 = band },
            4 => this with { Hz500 = band }, 5 => this with { Hz1000 = band }, 6 => this with { Hz2000 = band }, 7 => this with { Hz4000 = band },
            8 => this with { Hz8000 = band }, 9 => this with { Hz16000 = band }, _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
    }
}

public static class MultibandEqEffect
{
    public const double Q = 1.4142135623730951;
    const int CoefficientBlockFrames = 64;
    readonly record struct Coefficients(double B0, double B1, double B2, double A1, double A2)
    {
        public static Coefficients Between(Coefficients first, Coefficients last, double progress) => new(
            first.B0 + (last.B0 - first.B0) * progress, first.B1 + (last.B1 - first.B1) * progress,
            first.B2 + (last.B2 - first.B2) * progress, first.A1 + (last.A1 - first.A1) * progress, first.A2 + (last.A2 - first.A2) * progress);
    }
    struct ChannelState
    {
        double x1, x2, y1, y2;
        public double Transform(double input, Coefficients c)
        {
            double output = c.B0 * input + c.B1 * x1 + c.B2 * x2 - c.A1 * y1 - c.A2 * y2;
            x2 = x1; x1 = input; y2 = y1; y1 = output; return output;
        }
    }
    // RBJ peaking-EQ equations, documented by W3C:
    // https://www.w3.org/TR/audio-eq-cookbook/#formulae
    static Coefficients Peaking(double cosine, double alpha, double gainDb)
    {
        double amplitude = Math.Pow(10, gainDb / 40), divisor = 1 + alpha / amplitude;
        return new((1 + alpha * amplitude) / divisor, -2 * cosine / divisor, (1 - alpha * amplitude) / divisor,
            -2 * cosine / divisor, (1 - alpha / amplitude) / divisor);
    }

    public static void Process(float[] source, float[] result, int rate, MultibandEqSettings settings, bool automationEnabled = true, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(result); ArgumentNullException.ThrowIfNull(settings);
        cancel.ThrowIfCancellationRequested();
        if (source.Length != result.Length || source.Length % 2 != 0 || ReferenceEquals(source, result) || rate is < 1000 or > 384000)
            throw new ArgumentException("Multiband EQ requires separate equal-length stereo buffers and a supported sample rate.");
        if ((long)source.Length * sizeof(float) > AudioFiles.MaxTrackBytes) throw new IOException("The processed track exceeds the 256 MiB audio limit.");
        for (int index = 0; index < MultibandEqSettings.CenterFrequencies.Count; index++)
        {
            var band = settings.BandAt(index);
            if (band == null || !double.IsFinite(band.GainDb) || band.GainDb is < -24 or > 24
                || band.EndGainDb.HasValue && (!double.IsFinite(band.EndGainDb.Value) || band.EndGainDb.Value is < -24 or > 24))
                throw new ArgumentException("Every EQ band must stay between -24 and +24 dB.");
        }
        for (int index = 0; index < source.Length; index++)
        {
            if ((index & 65535) == 0) cancel.ThrowIfCancellationRequested();
            if (!float.IsFinite(source[index])) throw new ArgumentException("The selected sound contains invalid samples.");
            result[index] = source[index];
        }
        int frames = source.Length / 2;
        for (int index = 0; index < MultibandEqSettings.CenterFrequencies.Count; index++)
        {
            cancel.ThrowIfCancellationRequested();
            double frequency = MultibandEqSettings.CenterFrequencies[index]; var band = settings.BandAt(index);
            double endGain = automationEnabled ? band.EndGainDb ?? band.GainDb : band.GainDb;
            if (frequency >= rate / 2d || band.GainDb == 0 && endGain == 0) continue;
            double omega = 2 * Math.PI * frequency / rate, cosine = Math.Cos(omega), alpha = Math.Sin(omega) / (2 * Q);
            double GainAt(int frame) => band.GainDb + (endGain - band.GainDb) * frame / Math.Max(1d, frames - 1);
            var left = new ChannelState(); var right = new ChannelState();
            bool automated = band.GainDb != endGain;
            var constant = Peaking(cosine, alpha, band.GainDb);
            for (int first = 0; first < frames; first += CoefficientBlockFrames)
            {
                cancel.ThrowIfCancellationRequested();
                int last = Math.Min(frames - 1, first + CoefficientBlockFrames - 1);
                var from = automated ? Peaking(cosine, alpha, GainAt(first)) : constant;
                var to = automated ? Peaking(cosine, alpha, GainAt(last)) : constant;
                for (int frame = first; frame <= last; frame++)
                {
                    var coefficients = automated ? Coefficients.Between(from, to, (frame - first) / Math.Max(1d, last - first)) : constant;
                    double l = left.Transform(result[frame * 2], coefficients), r = right.Transform(result[frame * 2 + 1], coefficients);
                    if (!double.IsFinite(l) || !double.IsFinite(r) || Math.Abs(l) > float.MaxValue || Math.Abs(r) > float.MaxValue)
                        throw new IOException("The EQ gain exceeds the supported audio range. Lower one or more band gains.");
                    result[frame * 2] = (float)l; result[frame * 2 + 1] = (float)r;
                }
            }
        }
        cancel.ThrowIfCancellationRequested();
    }
}
