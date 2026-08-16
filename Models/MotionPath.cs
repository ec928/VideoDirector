using System;

namespace VideoDirector.Models
{
    // How a clip's framing moves over its own duration.
    //
    // One place, so the compositor and the framing editor cannot disagree about where the camera
    // is at a given moment — they previously carried separate copies of this arithmetic, which is
    // exactly the kind of duplication that drifts silently.
    //
    // Pure and WinUI-free, so the easing and the Mid-keyframe split are testable without a UI.
    public static class MotionPath
    {
        // Progress 0..1 through the clip, shaped by its curve.
        public static double Ease(CurveProfile curve, double progress)
        {
            progress = Math.Clamp(progress, 0, 1);
            return curve switch
            {
                // Slow at both ends: the default "considered" move.
                CurveProfile.Bezier => progress < 0.5
                    ? 2 * progress * progress
                    : 1 - Math.Pow(-2 * progress + 2, 2) / 2,
                // Fast start, gentle settle.
                CurveProfile.DirectorsArc => 1 - Math.Pow(1 - progress, 3),
                _ => progress
            };
        }

        // Where the camera is at `progress` through the clip.
        //
        // With a Mid keyframe the move is two segments joined at `midTime`, which is a fraction of
        // the clip's duration. It used to be pinned to the exact middle, so a move could not dwell
        // on one side or the other; midTime makes that position authorable.
        public static (double zoom, double centerX, double centerY) Sample(
            SpatialMark start, SpatialMark mid, SpatialMark end,
            double midTime, CurveProfile curve, double progress)
        {
            if (start == null && end == null) return (1.0, 0.5, 0.5);
            start ??= end;
            end ??= start;

            double eased = Ease(curve, progress);

            SpatialMark from, to;
            double p;

            if (mid == null)
            {
                from = start; to = end; p = eased;
            }
            else
            {
                // Keep the split strictly inside the clip, or a segment would have zero length and
                // the division below would blow up.
                double split = Math.Clamp(double.IsNaN(midTime) ? 0.5 : midTime, 0.01, 0.99);
                if (eased < split)
                {
                    from = start; to = mid;
                    p = eased / split;
                }
                else
                {
                    from = mid; to = end;
                    p = (eased - split) / (1 - split);
                }
            }

            p = Math.Clamp(p, 0, 1);
            return (from.Zoom + (to.Zoom - from.Zoom) * p,
                    from.CenterX + (to.CenterX - from.CenterX) * p,
                    from.CenterY + (to.CenterY - from.CenterY) * p);
        }
    }
}
