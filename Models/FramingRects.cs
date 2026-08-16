using System;

namespace VideoDirector.Models
{
    // A rectangle in viewport pixels.
    public readonly struct ScreenRect
    {
        public readonly double Left, Top, Width, Height;

        public ScreenRect(double left, double top, double width, double height)
        {
            Left = left; Top = top; Width = width; Height = height;
        }

        public double Right => Left + Width;
        public double Bottom => Top + Height;
        public double CenterX => Left + Width / 2;
        public double CenterY => Top + Height / 2;
        public bool IsEmpty => Width <= 0 || Height <= 0;

        public bool Contains(double x, double y)
            => x >= Left && x <= Right && y >= Top && y <= Bottom;
    }

    // Geometry for the framing editor: where the whole source frame sits on screen, and where each
    // keyframe's camera rectangle sits inside it.
    //
    // This is the inverse of the old model. Editing used to mean moving the PICTURE and snapshotting
    // the result, so a mark's rectangle could only be drawn relative to whatever the live transform
    // happened to be — which is why the three rectangles flew apart as soon as you zoomed. Here the
    // frame is fixed on screen and the rectangles are drawn at their true positions inside it, so
    // all three are always visible in a stable relationship.
    //
    // Pure and WinUI-free so it can be tested without a UI thread.
    public static class FramingRects
    {
        // A camera cannot see outside the frame, so the framing editor never goes below 1.
        public const double MinFrameZoom = 1.0;

        // Where the whole source frame is drawn, fitted into the viewport and centred.
        public static ScreenRect FrameOnScreen(double aspect, double viewportW, double viewportH)
        {
            if (aspect <= 0 || viewportW <= 0 || viewportH <= 0) return default;

            double w, h;
            if (aspect >= viewportW / viewportH) { w = viewportW; h = viewportW / aspect; }
            else { h = viewportH; w = viewportH * aspect; }

            return new ScreenRect((viewportW - w) / 2, (viewportH - h) / 2, w, h);
        }

        // The camera rectangle for a mark: it covers 1/zoom of the frame, centred on the mark's
        // centre point.
        public static ScreenRect RectFor(double zoom, double centerX, double centerY, ScreenRect frame)
        {
            if (frame.IsEmpty) return default;
            (zoom, centerX, centerY) = Clamp(zoom, centerX, centerY);

            double w = frame.Width / zoom;
            double h = frame.Height / zoom;
            return new ScreenRect(
                frame.Left + centerX * frame.Width - w / 2,
                frame.Top + centerY * frame.Height - h / 2,
                w, h);
        }

        public static ScreenRect RectFor(SpatialMark mark, ScreenRect frame)
            => mark == null ? default : RectFor(mark.Zoom, mark.CenterX, mark.CenterY, frame);

        // A dragged screen rectangle back to a mark. Zoom comes from how much of the frame's WIDTH
        // the rectangle covers; the aspect is locked by the caller, so width alone is authoritative.
        public static (double zoom, double centerX, double centerY) MarkFor(ScreenRect rect, ScreenRect frame)
        {
            if (frame.IsEmpty || rect.Width <= 0) return (1.0, 0.5, 0.5);

            double zoom = frame.Width / rect.Width;
            double cx = (rect.CenterX - frame.Left) / frame.Width;
            double cy = (rect.CenterY - frame.Top) / frame.Height;
            return Clamp(zoom, cx, cy);
        }

        // Keep the camera inside the frame. At zoom z the camera sees 1/z of the frame, so its
        // centre cannot come closer than half of that to any edge without showing beyond it.
        public static (double zoom, double centerX, double centerY) Clamp(
            double zoom, double centerX, double centerY)
        {
            if (double.IsNaN(zoom) || zoom < MinFrameZoom) zoom = MinFrameZoom;
            if (zoom > Framing.MaxZoom) zoom = Framing.MaxZoom;

            double half = 0.5 / zoom;
            if (double.IsNaN(centerX)) centerX = 0.5;
            if (double.IsNaN(centerY)) centerY = 0.5;

            return (zoom,
                    Math.Clamp(centerX, half, 1 - half),
                    Math.Clamp(centerY, half, 1 - half));
        }

        // Snap a value to a target when within `tolerance`. Used to make a rectangle settle onto
        // the frame's edges and centre lines instead of landing a pixel or two off them.
        public static double SnapTo(double value, double target, double tolerance)
            => Math.Abs(value - target) <= tolerance ? target : value;

        // Snap a mark's centre to the frame centre or, once the camera is against an edge, to that
        // edge. Tolerance is in frame fractions.
        public static (double zoom, double centerX, double centerY) SnapCentre(
            double zoom, double centerX, double centerY, double tolerance)
        {
            double half = 0.5 / zoom;
            centerX = SnapTo(centerX, 0.5, tolerance);
            centerY = SnapTo(centerY, 0.5, tolerance);
            centerX = SnapTo(centerX, half, tolerance);
            centerX = SnapTo(centerX, 1 - half, tolerance);
            centerY = SnapTo(centerY, half, tolerance);
            centerY = SnapTo(centerY, 1 - half, tolerance);
            return Clamp(zoom, centerX, centerY);
        }
    }
}
