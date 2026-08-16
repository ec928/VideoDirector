using System;

namespace VideoDirector.Models
{
    // Conversion between a framing MARK and the render transform that realises it.
    //
    // A mark says where the camera looks, in fractions of the source frame:
    //   Zoom     how much of the frame is visible — 1 = all of it, 2 = half of it each way
    //   Center   where the camera is pointed, 0..1 across the frame; (0.5, 0.5) is centred
    //
    // Nothing about it depends on the size of the surface it will be shown on. That is the whole
    // point: marks used to be raw CompositeTransform translations in device pixels, captured at
    // whatever size the window happened to be, so resizing the window silently re-framed every
    // clip — and an overlay's framing shifted when its PiP box was resized, which the code
    // compensated for with a fudge factor that multiplied the translation by the box fractions.
    // Expressing the mark in source-frame terms removes both problems at the source.
    //
    // Pure and WinUI-free so it can be tested without a UI thread. The caller applies the result
    // to a CompositeTransform whose RenderTransformOrigin is the element centre (0.5, 0.5).
    public static class Framing
    {
        public const double MinZoom = 0.1;
        public const double MaxZoom = 10.0;

        // Mark -> transform, for a surface of the given size.
        //
        // Scaling happens about the element centre, so a source point at fraction (cx, cy) sits
        // Zoom * (c - 0.5) * size away from the centre afterwards; translating by the negative of
        // that brings it back to the middle of the view.
        public static (double scale, double translateX, double translateY) ToTransform(
            double zoom, double centerX, double centerY, double surfaceW, double surfaceH)
        {
            zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
            return (zoom,
                    -zoom * (centerX - 0.5) * surfaceW,
                    -zoom * (centerY - 0.5) * surfaceH);
        }

        public static (double scale, double translateX, double translateY) ToTransform(
            SpatialMark mark, double surfaceW, double surfaceH)
            => mark == null
                ? (1.0, 0.0, 0.0)
                : ToTransform(mark.Zoom, mark.CenterX, mark.CenterY, surfaceW, surfaceH);

        // Transform -> mark. The inverse of the above, used when the user frames by dragging the
        // picture and the result has to be stored.
        public static (double zoom, double centerX, double centerY) FromTransform(
            double scale, double translateX, double translateY, double surfaceW, double surfaceH)
        {
            double zoom = Math.Clamp(scale, MinZoom, MaxZoom);
            if (surfaceW <= 0 || surfaceH <= 0) return (zoom, 0.5, 0.5);
            return (zoom,
                    0.5 - translateX / (zoom * surfaceW),
                    0.5 - translateY / (zoom * surfaceH));
        }

        // Convert a pre-normalisation mark, whose X/Y were raw pixel translations.
        //
        // An identity mark (scale 1, no translation) converts exactly, and that is the vast
        // majority of them. A framed one cannot convert exactly, because the viewport size it was
        // authored against was never recorded — the best available guess is a nominal 16:9 surface.
        // Such clips may need their framing re-checked; see IMPLEMENTATION-PLAN.md phase D1.
        public const double LegacyReferenceWidth = 1920;
        public const double LegacyReferenceHeight = 1080;

        public static (double zoom, double centerX, double centerY) FromLegacyMark(
            double scale, double x, double y)
        {
            if (scale <= 0) scale = 1.0;
            if (scale == 1.0 && x == 0 && y == 0) return (1.0, 0.5, 0.5);   // exact
            return FromTransform(scale, x, y, LegacyReferenceWidth, LegacyReferenceHeight);
        }
    }
}
