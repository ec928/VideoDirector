namespace VideoDirector.Models
{
    // Which shape a placement box must be given, and why the answer differs between a live video
    // surface and a still proxy.
    //
    // THE SHIMMERING BORDER. Media Foundation decodes to macroblock multiples, so a 1918x804 file
    // comes back as 1920x816 and PlaybackSession reports the padded figure. MediaPlayerElement
    // scales from THAT figure, because the padded frame is what the surface actually contains.
    //
    // So if the box is shaped from the file's real aspect, the two disagree: UniformToFill covers
    // the box, overflows on one axis, and the grid's RectangleGeometry clip has to cut the overflow
    // away. Clipping a hardware video swapchain against a XAML geometry is the thing WinUI does
    // badly — the compositor re-resolves that boundary against a surface that is being rewritten at
    // frame rate, and the edge crawls. That is the shimmer.
    //
    // Shape the box from the DECODED aspect and the overflow is zero: the surface lands exactly on
    // the box, the clip cuts nothing, and there is no boundary to re-resolve. It costs the couple of
    // padding pixels along one edge, which is what the app showed for its whole working life.
    //
    // Correlates exactly with what is seen on screen: files that need no padding (1920x800, 1920x1080)
    // never shimmered, and files that do (1918x804) always did.
    public static class SurfaceAspect
    {
        // A LIVE video surface is shaped by the decoder, padding and all. The file's own aspect is
        // only a fallback for before the media has opened.
        public static double ForVideo(double decodedAspect, double fileAspect)
            => decodedAspect > 0 ? decodedAspect : fileAspect;

        // A STILL proxy is a plain bitmap with no video surface, so nothing can crawl and the real
        // shape is free to be correct. Arrange therefore frames a clip to its true proportions.
        public static double ForStill(double fileAspect, double decodedAspect)
            => fileAspect > 0 ? fileAspect : decodedAspect;

        // Whether a box of this shape would force the clip geometry to cut a live surface. The
        // property the fix turns on: for video this must be false.
        public static bool WouldCropSurface(double boxAspect, double surfaceAspect,
                                            double tolerance = 0.0005)
        {
            if (boxAspect <= 0 || surfaceAspect <= 0) return false;
            return System.Math.Abs(boxAspect - surfaceAspect) > tolerance;
        }
    }
}
