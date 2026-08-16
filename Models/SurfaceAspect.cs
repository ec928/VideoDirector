namespace VideoDirector.Models
{
    // Two rules about the relationship between a clip's picture and the box that clips it. The
    // second one is the shimmering border, diagnosed properly at last.
    //
    // ---- THE SHIMMERING BORDER -----------------------------------------------------------------
    //
    // A clip's box clips the video with a RectangleGeometry. When the picture exactly fills that
    // box — zero overflow — the video texture's own edge lands ON the clip boundary, and the GPU
    // samples right at the edge of the surface, blending with texels outside it. During playback
    // those out-of-bounds texels are rewritten every frame, so the resulting hairline changes every
    // frame: a shimmering line down the edge. Paused, the surface is static, so the line is static
    // and invisible. "Only when the videos are playing" is the whole tell.
    //
    // A box that crops has no such edge: the boundary cuts through interior texels, which are
    // ordinary picture. So the fix is to guarantee that the picture ALWAYS overfills its box, by
    // insetting the clip a pixel inside the surface. Costs one pixel of crop per edge.
    //
    // The evidence, from the user's own project:
    //   Magic Faraway Tree  placement 0.300 x 0.300  equal  -> box aspect == video aspect
    //                       -> fills exactly, zero overflow -> SHIMMERS
    //   Star Wars           placement 0.474 x 0.702  unequal -> box aspect != video aspect
    //                       -> overflows and is cropped      -> clean
    //
    // Note what this rules out. Magic Faraway Tree is 1920x800: macroblock-aligned, the decoder
    // pads it by nothing. It shimmers anyway. Decoder padding was never the cause, which retires
    // three earlier explanations — a subpixel seam, unguarded per-frame writes, and an "overscan"
    // correction sized to the padding. Placement fractions being EQUAL is the trigger, and equal
    // fractions are the common case, which is why so many clips showed it.
    public static class SurfaceAspect
    {
        // How far the picture must overhang its box on every side. One destination pixel is enough:
        // the sampler only needs to be off the texture edge, and at typical PiP sizes one
        // destination pixel is several source texels anyway.
        public const double SurfaceInsetPx = 1.0;

        // ---- Which shape a box takes -----------------------------------------------------------

        // A LIVE video surface is shaped by the decoder, because the padded frame is what the
        // surface actually holds; shaping it from the file's real aspect stretches the picture by
        // the padding instead. Whatever padding that leaves at the edge is inside the inset above,
        // so it is cropped away rather than displayed. The file's aspect is a fallback for before
        // the media has opened.
        public static double ForVideo(double decodedAspect, double fileAspect)
            => decodedAspect > 0 ? decodedAspect : fileAspect;

        // A STILL proxy is a plain bitmap. It is never rewritten, so its edge cannot shimmer and
        // its true proportions are free to be used.
        public static double ForStill(double fileAspect, double decodedAspect)
            => fileAspect > 0 ? fileAspect : decodedAspect;

        // ---- The condition that shimmers -------------------------------------------------------

        // True when the picture fills the box exactly, so its edge lands on the clip boundary.
        // This is the state the inset exists to prevent.
        public static bool EdgeIsFlushWithBox(double boxAspect, double surfaceAspect,
                                              double tolerance = 0.0005)
        {
            if (boxAspect <= 0 || surfaceAspect <= 0) return false;
            return System.Math.Abs(boxAspect - surfaceAspect) <= tolerance;
        }

        // The picture is drawn into a rectangle inset px larger on every side, then clipped back to
        // the box, so there is always overhang to spare no matter what the two aspects are.
        public static (double width, double height) InsetSurfaceSize(
            double boxW, double boxH, double inset = SurfaceInsetPx)
            => (boxW + 2 * inset, boxH + 2 * inset);

        // How far the picture overhangs the box on each axis, for a surface drawn at the inset size
        // and scaled to cover. Must be strictly positive on BOTH axes, or an edge is exposed.
        public static (double x, double y) Overhang(
            double boxW, double boxH, double surfaceAspect, double inset = SurfaceInsetPx)
        {
            if (boxW <= 0 || boxH <= 0 || surfaceAspect <= 0) return (0, 0);

            var (w, h) = InsetSurfaceSize(boxW, boxH, inset);

            // Cover the inset rectangle, preserving aspect — what UniformToFill does.
            double drawnW, drawnH;
            if (surfaceAspect >= w / h) { drawnH = h; drawnW = h * surfaceAspect; }
            else { drawnW = w; drawnH = w / surfaceAspect; }

            return ((drawnW - boxW) / 2, (drawnH - boxH) / 2);
        }
    }
}
