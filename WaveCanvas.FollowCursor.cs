using System.ComponentModel;

namespace OrchardSound;

public sealed partial class WaveCanvas
{
    bool followCursor, followFitOverride;
    long? followFitCursor;
    (long Min, long Max)? followPointerRange;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool FollowCursor
    {
        get => followCursor;
        set
        {
            if (followCursor == value) return;
            long currentCenter = PrecisionViewCenter();
            followCursor = value; lensCenter = currentCenter; followFitOverride = false;
            ResetFollowCursorPointerRange(); ResetNestedPrecisionAnchor(); Invalidate();
        }
    }

    long PrecisionViewCenter()
    {
        var cursor = CursorFrame ?? lastCursorFrame;
        if (followFitOverride && cursor != followFitCursor) followFitOverride = false;
        return followCursor && !followFitOverride && cursor.HasValue ? cursor.Value : lensCenter;
    }

    (long Min, long Max) PrecisionBasePointerRange => followCursor && followPointerRange.HasValue ? followPointerRange.Value : LensRange();
    void ResetFollowCursorPointerRange() => followPointerRange = null;
    void BeginFollowCursorPointer(bool precision)
    {
        if (!followCursor || !precision) { followPointerRange = null; return; }
        followPointerRange ??= LensRange();
    }
    void OverrideFollowCursorForFit()
    {
        followFitOverride = followCursor; followFitCursor = CursorFrame ?? lastCursorFrame;
        ResetFollowCursorPointerRange();
    }
}
