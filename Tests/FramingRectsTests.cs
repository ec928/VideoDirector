using VideoDirector.Models;
using Xunit;

namespace VideoDirector.Tests
{
    // The framing editor draws the whole source frame at a fixed place on screen and puts each
    // keyframe's camera rectangle inside it. That inversion is the point of D2: rectangles used to
    // be positioned relative to the live transform, so they flew apart the moment you zoomed and
    // could never be compared to each other.
    public class FramingRectsTests
    {
        private const double VpW = 1600, VpH = 900;
        private static readonly ScreenRect Frame = FramingRects.FrameOnScreen(16.0 / 9.0, VpW, VpH);

        [Fact]
        public void TheFrameFitsTheViewportAndIsCentred()
        {
            Assert.Equal(0, Frame.Left, 6);
            Assert.Equal(0, Frame.Top, 6);
            Assert.Equal(VpW, Frame.Width, 6);
            Assert.Equal(VpH, Frame.Height, 6);
        }

        [Fact]
        public void AFrameOfADifferentShapeIsLetterboxedAndCentred()
        {
            var f = FramingRects.FrameOnScreen(4.0 / 3.0, VpW, VpH);
            Assert.Equal(VpH, f.Height, 6);
            Assert.Equal(VpH * 4.0 / 3.0, f.Width, 6);
            Assert.Equal((VpW - f.Width) / 2, f.Left, 6);
            Assert.Equal(0, f.Top, 6);
        }

        [Fact]
        public void APortraitFrameStaysPortrait()
        {
            var f = FramingRects.FrameOnScreen(9.0 / 16.0, VpW, VpH);
            Assert.True(f.Width < f.Height);
            Assert.Equal(VpH, f.Height, 6);
        }

        [Fact]
        public void AnUnzoomedMarkCoversTheWholeFrame()
        {
            var r = FramingRects.RectFor(new SpatialMark(), Frame);
            Assert.Equal(Frame.Left, r.Left, 6);
            Assert.Equal(Frame.Top, r.Top, 6);
            Assert.Equal(Frame.Width, r.Width, 6);
            Assert.Equal(Frame.Height, r.Height, 6);
        }

        [Fact]
        public void ZoomingHalvesTheRectangleAtDoubleZoom()
        {
            var r = FramingRects.RectFor(2.0, 0.5, 0.5, Frame);
            Assert.Equal(Frame.Width / 2, r.Width, 6);
            Assert.Equal(Frame.Height / 2, r.Height, 6);
            // Still centred on the frame.
            Assert.Equal(Frame.CenterX, r.CenterX, 6);
            Assert.Equal(Frame.CenterY, r.CenterY, 6);
        }

        [Fact]
        public void EveryRectangleStaysInsideTheFrame()
        {
            // The property that makes the editor legible: a camera cannot see outside the picture,
            // so no rectangle may ever escape the frame however it is asked for.
            foreach (double zoom in new[] { 1.0, 1.5, 3.0, 8.0 })
                foreach (double c in new[] { -5.0, -0.2, 0.0, 0.5, 1.0, 1.4, 9.0 })
                {
                    var r = FramingRects.RectFor(zoom, c, c, Frame);
                    Assert.True(r.Left >= Frame.Left - 1e-6, $"zoom {zoom} centre {c}: left escaped");
                    Assert.True(r.Top >= Frame.Top - 1e-6, $"zoom {zoom} centre {c}: top escaped");
                    Assert.True(r.Right <= Frame.Right + 1e-6, $"zoom {zoom} centre {c}: right escaped");
                    Assert.True(r.Bottom <= Frame.Bottom + 1e-6, $"zoom {zoom} centre {c}: bottom escaped");
                }
        }

        [Fact]
        public void TheFramingEditorNeverZoomsOutPastTheWholeFrame()
        {
            // Below 1 the camera would be looking at nothing, so the editor clamps.
            var (zoom, _, _) = FramingRects.Clamp(0.25, 0.5, 0.5);
            Assert.Equal(FramingRects.MinFrameZoom, zoom, 6);
        }

        [Theory]
        [InlineData(1.0, 0.5, 0.5)]
        [InlineData(2.0, 0.25, 0.75)]
        [InlineData(4.0, 0.5, 0.5)]
        [InlineData(1.6, 0.4, 0.6)]
        public void RectangleAndMarkAreInverses(double zoom, double cx, double cy)
        {
            var rect = FramingRects.RectFor(zoom, cx, cy, Frame);
            var (backZoom, backCx, backCy) = FramingRects.MarkFor(rect, Frame);

            Assert.Equal(zoom, backZoom, 6);
            Assert.Equal(cx, backCx, 6);
            Assert.Equal(cy, backCy, 6);
        }

        [Fact]
        public void DraggingARectangleToACornerResolvesToACornerMark()
        {
            // A half-size rectangle pushed hard into the top-left must resolve to exactly the mark
            // whose camera sits in the corner, not to something slightly outside it.
            var half = FramingRects.RectFor(2.0, 0.5, 0.5, Frame);
            var pushed = new ScreenRect(Frame.Left - 500, Frame.Top - 500, half.Width, half.Height);

            var (zoom, cx, cy) = FramingRects.MarkFor(pushed, Frame);
            Assert.Equal(2.0, zoom, 6);
            Assert.Equal(0.25, cx, 6);   // half of 1/zoom from the left edge
            Assert.Equal(0.25, cy, 6);
        }

        [Fact]
        public void ADegenerateRectangleDoesNotProduceNonsense()
        {
            var (zoom, cx, cy) = FramingRects.MarkFor(new ScreenRect(0, 0, 0, 0), Frame);
            Assert.Equal(1.0, zoom, 6);
            Assert.Equal(0.5, cx, 6);
            Assert.Equal(0.5, cy, 6);
        }

        [Fact]
        public void AnUnknownAspectProducesNoFrame()
        {
            Assert.True(FramingRects.FrameOnScreen(0, VpW, VpH).IsEmpty);
            Assert.True(FramingRects.FrameOnScreen(1.777, 0, VpH).IsEmpty);
        }

        // ---- Snapping ------------------------------------------------------------------------

        [Fact]
        public void ANearlyCentredRectangleSnapsToCentre()
        {
            var (_, cx, cy) = FramingRects.SnapCentre(2.0, 0.507, 0.494, 0.02);
            Assert.Equal(0.5, cx, 6);
            Assert.Equal(0.5, cy, 6);
        }

        [Fact]
        public void ANearlyFlushRectangleSnapsToTheEdge()
        {
            // At zoom 2 the camera is flush against the left edge when its centre is at 0.25.
            var (_, cx, _) = FramingRects.SnapCentre(2.0, 0.258, 0.5, 0.02);
            Assert.Equal(0.25, cx, 6);
        }

        [Fact]
        public void SnappingLeavesADeliberatelyOffsetRectangleAlone()
        {
            var (_, cx, _) = FramingRects.SnapCentre(2.0, 0.40, 0.5, 0.02);
            Assert.Equal(0.40, cx, 6);
        }

        [Fact]
        public void SnappingNeverPushesARectangleOutOfTheFrame()
        {
            foreach (double zoom in new[] { 1.0, 2.0, 5.0 })
                foreach (double c in new[] { 0.0, 0.26, 0.5, 0.74, 1.0 })
                {
                    var (z, cx, cy) = FramingRects.SnapCentre(zoom, c, c, 0.05);
                    var r = FramingRects.RectFor(z, cx, cy, Frame);
                    Assert.True(r.Left >= Frame.Left - 1e-6 && r.Right <= Frame.Right + 1e-6);
                    Assert.True(r.Top >= Frame.Top - 1e-6 && r.Bottom <= Frame.Bottom + 1e-6);
                }
        }
    }
}
