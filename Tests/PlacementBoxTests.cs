using VideoDirector.Models;
using Xunit;

namespace VideoDirector.Tests
{
    // Placement is what makes a clip a PiP. It now applies to EVERY track — track 0 used to be
    // implicitly full-frame with no box at all, which is why it alone could not be moved or
    // resized in the composite.
    public class PlacementBoxTests
    {
        private const double W = 1600, H = 900;   // a 16:9 viewport
        private const double Tol = 1e-6;

        [Fact]
        public void FullFrameFillsAMatchingViewportExactly()
        {
            var box = PlacementBox.FullFrame(16.0 / 9.0, W, H);
            Assert.Equal(0, box.Left, 6);
            Assert.Equal(0, box.Top, 6);
            Assert.Equal(W, box.Width, 6);
            Assert.Equal(H, box.Height, 6);
        }

        [Fact]
        public void FullFrameLetterboxesASourceOfADifferentShape()
        {
            // A 4:3 source in a 16:9 viewport fits by height and is centred horizontally.
            var box = PlacementBox.FullFrame(4.0 / 3.0, W, H);
            Assert.Equal(H, box.Height, 6);
            Assert.Equal(H * 4.0 / 3.0, box.Width, 6);
            Assert.Equal((W - box.Width) / 2, box.Left, 6);
        }

        [Fact]
        public void FullFrameHandlesPortraitSources()
        {
            // The case an assumed 16:9 used to get wrong: a portrait clip must stay portrait.
            var box = PlacementBox.FullFrame(9.0 / 16.0, W, H);
            Assert.Equal(H, box.Height, 6);
            Assert.True(box.Width < box.Height, "a portrait source must produce a portrait box");
        }

        [Fact]
        public void FractionsAreOfTheFittedVideoNotTheViewport()
        {
            // This is what makes (1,1,0.5,0.5) mean "full frame" for any source shape with no
            // aspect maths at the call site.
            var full = PlacementBox.FullFrame(4.0 / 3.0, W, H);
            var half = PlacementBox.Compute(4.0 / 3.0, W, H, 0.5, 0.5, 0.5, 0.5);
            Assert.Equal(full.Width / 2, half.Width, 6);
            Assert.Equal(full.Height / 2, half.Height, 6);
        }

        [Theory]
        [InlineData(0.72, 0.72)]
        [InlineData(0.28, 0.72)]
        [InlineData(0.5, 0.5)]
        public void BoxIsCentredOnItsCentrePoint(double cx, double cy)
        {
            var box = PlacementBox.Compute(16.0 / 9.0, W, H, 0.3, 0.3, cx, cy);
            Assert.Equal(cx * W, box.Left + box.Width / 2, 6);
            Assert.Equal(cy * H, box.Top + box.Height / 2, 6);
        }

        [Fact]
        public void WidthAndHeightAreIndependentSoABoxCanBeReshaped()
        {
            var box = PlacementBox.Compute(16.0 / 9.0, W, H, 0.5, 0.25, 0.5, 0.5);
            Assert.Equal(W * 0.5, box.Width, 6);
            Assert.Equal(H * 0.25, box.Height, 6);
        }

        // ---- Fill (D5) -----------------------------------------------------------------------

        [Fact]
        public void FillingAMatchingShapeIsJustFullFrame()
        {
            var (fw, fh) = PlacementBox.FillFractions(16.0 / 9.0, W, H);
            Assert.Equal(1.0, fw, 6);
            Assert.Equal(1.0, fh, 6);
        }

        [Fact]
        public void FillingANarrowerSourceOverflowsTheWidth()
        {
            // The reason PlacementWidth/Height had to allow more than 1.0. A source narrower than
            // the output is pillarboxed at (1,1) — bars down the sides — so covering it means
            // going past the fit horizontally. True of 4:3 and of portrait alike.
            foreach (double aspect in new[] { 4.0 / 3.0, 9.0 / 16.0 })
            {
                var (fw, fh) = PlacementBox.FillFractions(aspect, W, H);
                Assert.True(fw > 1.0, $"aspect {aspect}: expected to overflow the width, got {fw}");
                Assert.Equal(1.0, fh, 6);
            }
        }

        [Fact]
        public void FillingAWiderSourceOverflowsTheHeight()
        {
            // A source wider than the output is letterboxed instead, so it overflows the other way.
            var (fw, fh) = PlacementBox.FillFractions(2.35, W, H);
            Assert.Equal(1.0, fw, 6);
            Assert.True(fh > 1.0, $"expected to overflow the height, got {fh}");
        }

        [Fact]
        public void AFilledBoxActuallyCoversTheViewport()
        {
            // The property that matters, checked across a spread of source shapes.
            foreach (double aspect in new[] { 0.5, 4.0 / 3.0, 16.0 / 9.0, 2.35, 9.0 / 16.0 })
            {
                var (fw, fh) = PlacementBox.FillFractions(aspect, W, H);
                var box = PlacementBox.Compute(aspect, W, H, fw, fh, 0.5, 0.5);
                Assert.True(box.Width >= W - 1e-6, $"aspect {aspect}: width {box.Width} < {W}");
                Assert.True(box.Height >= H - 1e-6, $"aspect {aspect}: height {box.Height} < {H}");
            }
        }

        [Fact]
        public void FillWithAnUnknownAspectFallsBackToFullFrame()
        {
            var (fw, fh) = PlacementBox.FillFractions(0, W, H);
            Assert.Equal(1.0, fw, 6);
            Assert.Equal(1.0, fh, 6);
        }

        [Fact]
        public void UnknownAspectProducesNoBoxRatherThanAGuess()
        {
            // The caller must draw nothing until the real aspect is known. Guessing 16:9 silently
            // crops portrait sources into landscape boxes.
            Assert.True(PlacementBox.Compute(0, W, H, 1, 1, 0.5, 0.5).IsEmpty);
            Assert.True(PlacementBox.Compute(-1, W, H, 1, 1, 0.5, 0.5).IsEmpty);
        }

        [Fact]
        public void UnknownViewportProducesNoBox()
        {
            Assert.True(PlacementBox.Compute(1.777, 0, H, 1, 1, 0.5, 0.5).IsEmpty);
            Assert.True(PlacementBox.Compute(1.777, W, 0, 1, 1, 0.5, 0.5).IsEmpty);
        }
    }
}
