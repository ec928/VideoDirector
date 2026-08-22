using System;

namespace VideoDirector.Models
{
    // Every rectangle the compositor depends on, as pure arithmetic.
    //
    // WHY THIS EXISTS AS ITS OWN FILE: the Ken Burns black-edge defect took six rounds to find
    // because nothing here could be examined without running the app and looking at it. The maths
    // was in fact correct throughout - the fault was a XAML layout rule - but proving that required
    // a person to load a project, scrub to a timestamp, and describe what they saw. Lifted out, the
    // whole chain (fit -> box -> content -> motion -> sampled source region) is testable against a
    // saved project with no UI, no decoder and no display, so a regression in the numbers is caught
    // by a test run rather than by someone noticing black on a screen.
    //
    // Deliberately free of Microsoft.UI / Windows types so the test project can compile it directly.
    //
    // WHAT THIS CANNOT CATCH: the bug that actually bit. The geometry said "fully covered" while
    // WinUI was layout-clipping the surface to its parent before the RenderTransform ran. No amount
    // of arithmetic testing sees that - it needs the visual tree. ApplyOverlayBox carries a debug
    // assertion for that specific class of fault instead.
    public static class ClipGeometry
    {
        public readonly struct GeoRect
        {
            public readonly double X, Y, W, H;
            public GeoRect(double x, double y, double w, double h) { X = x; Y = y; W = w; H = h; }
            public double Right => X + W;
            public double Bottom => Y + H;
            public override string ToString() => $"({X:F1},{Y:F1}) {W:F1}x{H:F1}";
        }

        // One framing keyframe, stripped of change notification so it can be built in a test.
        public readonly struct Mark
        {
            public readonly double Scale, X, Y;
            public Mark(double scale, double x, double y) { Scale = scale; X = x; Y = y; }
        }

        // The rectangle the source occupies in the pane at scale 1 - the reference every mark is a
        // fraction of. Contained, so the whole frame is visible and never cropped by the pane.
        public static GeoRect Fit(double aspect, double paneW, double paneH)
        {
            if (aspect <= 0 || paneW <= 0 || paneH <= 0) return new GeoRect(0, 0, 0, 0);
            return aspect >= paneW / paneH
                ? new GeoRect(0, 0, paneW, paneW / aspect)
                : new GeoRect(0, 0, paneH * aspect, paneH);
        }

        // The visible output window, in pane coordinates. Edit mode frames against the whole fit;
        // at playback the clip's placement shrinks and positions it.
        public static GeoRect Box(double fitW, double fitH, double paneW, double paneH,
                                  double placeW, double placeH, double centreX, double centreY,
                                  bool editMode)
        {
            double w = fitW * (editMode ? 1.0 : placeW);
            double h = fitH * (editMode ? 1.0 : placeH);
            double cx = editMode ? 0.5 : centreX;
            double cy = editMode ? 0.5 : centreY;
            return new GeoRect(cx * paneW - w / 2, cy * paneH - h / 2, w, h);
        }

        // The surface the frame is drawn onto: the smallest rectangle of SOURCE aspect that covers
        // the box. Bigger than the box on one axis, and that surplus is the only picture a pan has
        // to move into - size the surface to the box instead and every pan runs straight to black.
        public static (double W, double H) Content(double boxW, double boxH, double aspect)
        {
            if (aspect <= 0 || boxH <= 0) return (boxW, boxH);
            return boxW / boxH > aspect ? (boxW, boxW / aspect) : (boxH * aspect, boxH);
        }

        // How far the framing may travel before the box stops being covered. Negative means the
        // content cannot cover the box at all (zoom below 1), so black is unavoidable - which is
        // correct behaviour for a deliberate zoom-out, not a fault.
        public static (double X, double Y) Allowance(double contentW, double contentH,
                                                     double boxW, double boxH, double scale)
            => ((contentW * scale - boxW) / 2, (contentH * scale - boxH) / 2);

        // Marks are captured against the fit but replayed against the content, which is the fit
        // scaled by this. It has to be ONE uniform number because the zoom is uniform, and it is
        // max(w, h) because that is the axis on which Content() lands flush with the box.
        public static double PanScale(double placeW, double placeH) => Math.Max(placeW, placeH);

        public static double Ease(CurveProfile profile, double progress)
        {
            progress = Math.Clamp(progress, 0, 1);
            if (profile == CurveProfile.Bezier)
                return progress < 0.5 ? 2 * progress * progress : 1 - Math.Pow(-2 * progress + 2, 2) / 2;
            if (profile == CurveProfile.DirectorsArc)
                return 1 - Math.Pow(1 - progress, 3);
            return progress;
        }

        // Scale and translate at a raw 0..1 progress. A Mid mark splits the curve into two halves
        // rather than bending it, so Start->Mid and Mid->End each get the full eased ramp.
        public static void EvaluateMotion(Mark start, Mark? mid, Mark end, CurveProfile profile,
                                          double rawProgress, double panScaleX, double panScaleY,
                                          out double scale, out double translateX, out double translateY)
        {
            double eased = Ease(profile, rawProgress);

            if (mid.HasValue)
            {
                if (eased < 0.5) Lerp(start, mid.Value, eased * 2, out scale, out translateX, out translateY);
                else Lerp(mid.Value, end, (eased - 0.5) * 2, out scale, out translateX, out translateY);
            }
            else
            {
                Lerp(start, end, eased, out scale, out translateX, out translateY);
            }

            translateX *= panScaleX;
            translateY *= panScaleY;
        }

        private static void Lerp(Mark a, Mark b, double t, out double scale, out double x, out double y)
        {
            scale = a.Scale + (b.Scale - a.Scale) * t;
            x = a.X + (b.X - a.X) * t;
            y = a.Y + (b.Y - a.Y) * t;
        }

        // ==================== Which clip is on screen ====================
        //
        // Timeline arithmetic in TICKS, because seconds are where this went wrong.
        //
        // A clip's window is half-open: [start, start + duration). Two clips laid end to end
        // therefore never both cover the join. That held right up until a boundary was computed in
        // double seconds and assigned through TimeSpan.FromSeconds, which cost one tick - 100
        // nanoseconds - and left an image starting one tick BEFORE the clip in front of it ended.
        // Selecting the image put the playhead in that 1-tick overlap, both clips covered it, and
        // the resolver returned whichever came first in the list. The wrong clip stayed on screen
        // and every readout agreed with it, because the model really did have it active.
        public static bool Covers(long startTicks, long durationTicks, long t)
            => t >= startTicks && t < startTicks + durationTicks;

        // When two clips both cover the instant, the one that STARTED LATER wins: that is the one
        // the playhead has most recently entered. Order in the collection decides nothing.
        public static bool Supersedes(long candidateStartTicks, long incumbentStartTicks)
            => candidateStartTicks > incumbentStartTicks;

        // The region of the SOURCE frame the box ends up showing, in source pixels.
        //
        // This is the answer to the only question that matters when black appears: is the framing
        // inside the picture or outside it? Anything outside 0..srcW / 0..srcH renders black, and
        // that is a fault only if the framing was supposed to be inside.
        public static GeoRect SampledSource(double contentW, double contentH, double boxW, double boxH,
                                            double scale, double translateX, double translateY,
                                            double srcW, double srcH)
        {
            if (scale <= 0 || contentW <= 0) return new GeoRect(0, 0, 0, 0);

            // The content holds the WHOLE frame drawn at contentW x contentH, so this ratio turns
            // pane pixels into source pixels.
            double f = srcW / contentW;

            double cx = contentW / 2 - translateX / scale;
            double cy = contentH / 2 - translateY / scale;
            double halfW = boxW / (2 * scale);
            double halfH = boxH / (2 * scale);

            return new GeoRect((cx - halfW) * f, (cy - halfH) * f, boxW / scale * f, boxH / scale * f);
        }
    }
}
