using VideoDirector.Models;
using Xunit;

namespace VideoDirector.Tests
{
    // Framing marks say where the camera looks in fractions of the SOURCE FRAME, independent of
    // how big the picture is drawn. These tests exist because the previous design stored raw pixel
    // translations, and the resulting bugs — window resize re-framing every clip, a PiP's framing
    // shifting when its box was resized — were invisible in code and only showed up on screen.
    public class FramingTests
    {
        private const double W = 1600, H = 900;

        [Fact]
        public void AnIdentityMarkProducesNoTransform()
        {
            var (scale, tx, ty) = Framing.ToTransform(new SpatialMark(), W, H);
            Assert.Equal(1.0, scale, 6);
            Assert.Equal(0.0, tx, 6);
            Assert.Equal(0.0, ty, 6);
        }

        [Fact]
        public void ZoomingWhileCentredNeedsNoTranslation()
        {
            var (scale, tx, ty) = Framing.ToTransform(new SpatialMark(2.0, 0.5, 0.5), W, H);
            Assert.Equal(2.0, scale, 6);
            Assert.Equal(0.0, tx, 6);
            Assert.Equal(0.0, ty, 6);
        }

        [Fact]
        public void PointingRightMovesTheContentLeft()
        {
            // Camera looks at the right of the frame, so the picture has to slide left to bring it
            // into view. The sign of this is the single easiest thing to get backwards.
            var (_, tx, _) = Framing.ToTransform(new SpatialMark(1.0, 0.75, 0.5), W, H);
            Assert.True(tx < 0, $"expected a negative translation, got {tx}");
        }

        [Fact]
        public void PointingDownMovesTheContentUp()
        {
            var (_, _, ty) = Framing.ToTransform(new SpatialMark(1.0, 0.5, 0.75), W, H);
            Assert.True(ty < 0, $"expected a negative translation, got {ty}");
        }

        [Theory]
        [InlineData(1.0, 0.5, 0.5)]
        [InlineData(2.0, 0.25, 0.75)]
        [InlineData(1.4, 0.1, 0.9)]
        [InlineData(3.5, 0.62, 0.31)]
        public void TransformRoundTripsBackToTheSameMark(double zoom, double cx, double cy)
        {
            var (scale, tx, ty) = Framing.ToTransform(zoom, cx, cy, W, H);
            var (backZoom, backCx, backCy) = Framing.FromTransform(scale, tx, ty, W, H);

            Assert.Equal(zoom, backZoom, 6);
            Assert.Equal(cx, backCx, 6);
            Assert.Equal(cy, backCy, 6);
        }

        [Fact]
        public void AMarkMeansTheSameFramingAtAnySurfaceSize()
        {
            // THE point of the whole change: resizing the window, or resizing a PiP box, must not
            // alter what a clip is framed on. The pixel transform differs, but the fraction of the
            // frame it selects does not.
            var mark = new SpatialMark(2.0, 0.3, 0.7);

            foreach (var (w, h) in new[] { (640.0, 360.0), (1600.0, 900.0), (3840.0, 2160.0), (500.0, 900.0) })
            {
                var (scale, tx, ty) = Framing.ToTransform(mark, w, h);
                var (zoom, cx, cy) = Framing.FromTransform(scale, tx, ty, w, h);

                Assert.Equal(mark.Zoom, zoom, 6);
                Assert.Equal(mark.CenterX, cx, 6);
                Assert.Equal(mark.CenterY, cy, 6);
            }
        }

        [Fact]
        public void TranslationScalesWithTheSurface()
        {
            // Same mark, surface twice as wide -> twice the pixel translation. This is exactly the
            // relationship the old panScaleX/panScaleY fudge was hand-applying for overlays.
            var mark = new SpatialMark(1.0, 0.25, 0.5);
            var (_, txSmall, _) = Framing.ToTransform(mark, 800, 450);
            var (_, txLarge, _) = Framing.ToTransform(mark, 1600, 900);
            Assert.Equal(txSmall * 2, txLarge, 6);
        }

        [Fact]
        public void ZoomIsClamped()
        {
            Assert.Equal(Framing.MaxZoom, new SpatialMark(999, 0.5, 0.5).Zoom, 6);
            Assert.Equal(Framing.MinZoom, new SpatialMark(-4, 0.5, 0.5).Zoom, 6);
        }

        [Fact]
        public void AZeroSizedSurfaceDoesNotProduceNonsense()
        {
            var (zoom, cx, cy) = Framing.FromTransform(2.0, 100, 100, 0, 0);
            Assert.Equal(2.0, zoom, 6);
            Assert.Equal(0.5, cx, 6);
            Assert.Equal(0.5, cy, 6);
        }

        // ---- Legacy conversion ---------------------------------------------------------------

        [Fact]
        public void AnUnframedLegacyMarkConvertsExactly()
        {
            // The common case by far, and the one that has to be lossless.
            var (zoom, cx, cy) = Framing.FromLegacyMark(1.0, 0, 0);
            Assert.Equal(1.0, zoom, 6);
            Assert.Equal(0.5, cx, 6);
            Assert.Equal(0.5, cy, 6);
        }

        [Fact]
        public void ALegacyMarkKeepsItsZoomAndDirection()
        {
            // A framed one cannot convert exactly — the viewport it was authored against was never
            // recorded — but zoom survives and the pan must at least point the same way.
            var (zoom, cx, cy) = Framing.FromLegacyMark(2.0, -200, 0);
            Assert.Equal(2.0, zoom, 6);
            Assert.True(cx > 0.5, "a negative translation meant the camera looked right");
            Assert.Equal(0.5, cy, 6);
        }

        [Fact]
        public void ALegacyMarkWithNoScaleIsTreatedAsUnzoomed()
        {
            var (zoom, _, _) = Framing.FromLegacyMark(0, 0, 0);
            Assert.Equal(1.0, zoom, 6);
        }

        [Fact]
        public void MigratingAMarkClearsTheLegacyValuesSoItCannotApplyTwice()
        {
            var mark = new SpatialMark { LegacyScale = 2.0, LegacyX = -200, LegacyY = 0 };
            Assert.True(mark.HasLegacyFraming);

            mark.MigrateLegacyFraming();
            double onceX = mark.CenterX;
            Assert.False(mark.HasLegacyFraming);

            mark.MigrateLegacyFraming();   // a second pass must be a no-op
            Assert.Equal(onceX, mark.CenterX, 6);
        }
    }
}
