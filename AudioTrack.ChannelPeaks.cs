using System.Runtime.CompilerServices;

namespace OrchardSound;

public sealed partial class AudioTrack
{
    sealed class ChannelPeakSet
    {
        readonly Lazy<PeakPyramid> left, right;
        public ChannelPeakSet(float[] samples)
        { left = new(() => new PeakPyramid(samples, 0)); right = new(() => new PeakPyramid(samples, 1)); }
        public PeakPyramid At(int channel) => channel == 0 ? left.Value : right.Value;
    }

    // Keys follow the lifetime of immutable source or patch arrays. Pyramids
    // summarize the original interleaved data without copying audio samples.
    static readonly ConditionalWeakTable<float[], ChannelPeakSet> channelPeakCache = new();
    static PeakPyramid GetChannelPeaks(float[] samples, int channel)
        => channelPeakCache.GetValue(samples, source => new ChannelPeakSet(source)).At(channel);
}
