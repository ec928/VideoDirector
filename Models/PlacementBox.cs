using System;

namespace VideoDirector.Models
{
    // Where a clip's picture sits in the composite, in viewport pixels.
    //
    // Pure geometry, free of WinUI types, so it can be tested without a UI thread — and because it
    // now serves EVERY track. Track 0 used to be implicitly full-frame with no box at all, which
    // is why it could not be made into a PiP.
    //
    // Width/Height are fractions of the video's ASPECT-FIT size, not of the viewport. That is what
    // makes (1, 1, 0.5, 0.5) exactly "full frame" for any source shape, with no aspect maths at
    // the call site.
    public readonly struct PlacementBox
    {
        public readonly double Left, Top, Width, Height;

        public PlacementBox(double left, double top, double width, double height)
        {
            Left = left; Top = top; Width = width; Height = height;
        }

        public bool IsEmpty => Width <= 0 || Height <= 0;

        // aspect: the source's natural width/height. Returns an empty box when anything needed is
        // still unknown, which the caller treats as "do not draw yet" rather than guessing — an
        // assumed aspect silently crops portrait sources into landscape boxes.
        public static PlacementBox Compute(
            double aspect, double viewportW, double viewportH,
            double fracW, double fracH, double centerX, double centerY)
        {
            if (aspect <= 0 || viewportW <= 0 || viewportH <= 0) return default;

            // The video fitted to the viewport, preserving aspect — the "scale 1" reference.
            double fitW, fitH;
            if (aspect >= viewportW / viewportH) { fitW = viewportW; fitH = viewportW / aspect; }
            else { fitH = viewportH; fitW = viewportH * aspect; }

            double boxW = fitW * fracW;
            double boxH = fitH * fracH;
            return new PlacementBox(
                centerX * viewportW - boxW / 2,
                centerY * viewportH - boxH / 2,
                boxW, boxH);
        }

        public static PlacementBox FullFrame(double aspect, double viewportW, double viewportH)
            => Compute(aspect, viewportW, viewportH, 1.0, 1.0, 0.5, 0.5);

        // The fractions that make a clip COVER the whole viewport, cropping whatever does not fit.
        //
        // Fractions are of the aspect-fit size, so (1, 1) means "fit" — letterboxed whenever the
        // source and the output are different shapes. Filling therefore needs a fraction above 1
        // on the short axis, which is why PlacementWidth/Height allow more than 1.
        public static (double fracW, double fracH) FillFractions(
            double aspect, double viewportW, double viewportH)
        {
            if (aspect <= 0 || viewportW <= 0 || viewportH <= 0) return (1.0, 1.0);

            double fitW, fitH;
            if (aspect >= viewportW / viewportH) { fitW = viewportW; fitH = viewportW / aspect; }
            else { fitH = viewportH; fitW = viewportH * aspect; }

            return (viewportW / fitW, viewportH / fitH);
        }
    }
}
