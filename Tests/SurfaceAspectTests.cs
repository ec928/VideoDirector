using VideoDirector.Models;
using Xunit;

namespace VideoDirector.Tests
{
    // The shimmering border. A picture that fills its box EXACTLY puts the video texture's edge on
    // the clip boundary; the GPU samples past the end of the surface, and those texels are
    // rewritten every frame, so the hairline changes every frame. Only ever visible during
    // playback. The picture is therefore drawn a pixel proud of its box on every side, so the clip
    // always cuts through interior texels.
    //
    // Regression tests for a bug that was fixed once, reintroduced, and then misdiagnosed FOUR
    // times — a subpixel seam, unguarded per-frame writes, decoder padding, and an overscan sized
    // to that padding. The tests below encode what actually distinguishes the real cause from all
    // four, so the next person does not have to rediscover it.
    public class SurfaceAspectTests
    {
        // The real pair from the user's project: a 1918x804 file decodes as 1920x816.
        private const double PaddedFileAspect = 1918.0 / 804.0;    // 2.38557
        private const double PaddedDecoded = 1920.0 / 816.0;       // 2.35294

        // Magic Faraway Tree: 1920x800, macroblock-aligned, padded by nothing — and it shimmered.
        private const double AlignedAspect = 1920.0 / 800.0;       // 2.4

        // ---- The trigger ------------------------------------------------------------------------

        [Fact]
        public void EqualPlacementFractionsMakeTheBoxFlushWithThePicture()
        {
            // The actual trigger, and the reason it hit so many clips: equal fractions are the
            // common case (0.3 x 0.3 is the default PiP), and they make the box aspect identical
            // to the video aspect, so the picture fills it with zero overflow.
            var box = PlacementBox.Compute(AlignedAspect, 1920, 1080, 0.3, 0.3, 0.5, 0.5);
            Assert.True(SurfaceAspect.EdgeIsFlushWithBox(box.Width / box.Height, AlignedAspect));
        }

        [Fact]
        public void UnequalPlacementFractionsCropAndSoNeverWereAffected()
        {
            // Star Wars on track 4: 0.474 x 0.702. Reported as clean while an equal-fraction clip
            // beside it shimmered — which is what pointed at the real cause.
            var box = PlacementBox.Compute(PaddedDecoded, 1920, 1080, 0.4739, 0.7020, 0.5, 0.5);
            Assert.False(SurfaceAspect.EdgeIsFlushWithBox(box.Width / box.Height, PaddedDecoded));
        }

        [Fact]
        public void AnAlignedFileStillShimmersWhichIsWhatRetiredThePaddingTheory()
        {
            // 1920x800 needs no padding at all, so its file and decoded aspects agree exactly —
            // and it was still affected. Decoder padding cannot have been the cause.
            Assert.Equal(AlignedAspect, SurfaceAspect.ForVideo(AlignedAspect, AlignedAspect), 6);
            var box = PlacementBox.Compute(AlignedAspect, 1920, 1080, 0.3, 0.3, 0.5, 0.5);
            Assert.True(SurfaceAspect.EdgeIsFlushWithBox(box.Width / box.Height, AlignedAspect));
        }

        // ---- The fix ----------------------------------------------------------------------------

        [Fact]
        public void TheInsetKeepsThePictureOverThEdgeEvenWhenFlush()
        {
            // THE fix, in the exact case that used to shimmer.
            var (x, y) = SurfaceAspect.Overhang(576, 240, AlignedAspect);
            Assert.True(x > 0, $"no horizontal overhang: {x}");
            Assert.True(y > 0, $"no vertical overhang: {y}");
        }

        [Theory]
        // A spread of box shapes and source shapes, including the flush case on each.
        [InlineData(576, 240, 2.4)]          // flush
        [InlineData(400, 400, 2.4)]          // box much squarer than source
        [InlineData(1200, 200, 2.4)]         // box much wider than source
        [InlineData(300, 500, 9.0 / 16.0)]   // portrait source
        [InlineData(500, 300, 9.0 / 16.0)]
        [InlineData(880, 550, 2.35294)]
        [InlineData(1920, 1080, 16.0 / 9.0)] // full frame, flush
        [InlineData(7, 5, 1.4)]              // degenerate sizes still overhang
        public void ThePictureOverhangsItsBoxOnBothAxesForAnyShape(
            double boxW, double boxH, double aspect)
        {
            var (x, y) = SurfaceAspect.Overhang(boxW, boxH, aspect);
            Assert.True(x > 0, $"box {boxW}x{boxH} aspect {aspect}: horizontal edge exposed");
            Assert.True(y > 0, $"box {boxW}x{boxH} aspect {aspect}: vertical edge exposed");
        }

        [Theory]
        [InlineData(576, 240, 2.4)]
        [InlineData(880, 550, 2.35294)]
        [InlineData(1920, 1080, 16.0 / 9.0)]
        public void TheOverhangIsATrimNotAVisibleCrop(double boxW, double boxH, double aspect)
        {
            // It must be enough to get off the texture edge and little more — this is picture the
            // user authored being thrown away. Measured as what the inset ADDS: a box reshaped to
            // a very different aspect already crops heavily by the user's own choice, and that is
            // not the inset's doing.
            var (x, y) = SurfaceAspect.Overhang(boxW, boxH, aspect);
            var (x0, y0) = SurfaceAspect.Overhang(boxW, boxH, aspect, inset: 0);

            // One axis is driven to exactly the inset; the other gets a little more.
            Assert.Equal(SurfaceAspect.SurfaceInsetPx, System.Math.Min(x - x0, y - y0), 6);
            Assert.True((x - x0) / boxW < 0.01, $"horizontal trim {(x - x0) / boxW:P2} is too much");
            Assert.True((y - y0) / boxH < 0.01, $"vertical trim {(y - y0) / boxH:P2} is too much");
        }

        [Fact]
        public void WithoutTheInsetAFlushBoxExposesTheEdge()
        {
            // The bug, stated as a test: at inset 0 the flush case has exactly zero overhang, which
            // is the texture edge sitting on the clip boundary.
            var (x, y) = SurfaceAspect.Overhang(576, 240, AlignedAspect, inset: 0);
            Assert.Equal(0, x, 6);
            Assert.Equal(0, y, 6);
        }

        // ---- Which shape a box takes ------------------------------------------------------------

        [Fact]
        public void AVideoBoxTakesTheDecodersShapeSoThePictureIsNotStretched()
        {
            // The surface holds the padded frame, so shaping the box from the file's real aspect
            // stretches the picture by the padding. The padding itself falls inside the inset.
            Assert.Equal(PaddedDecoded, SurfaceAspect.ForVideo(PaddedDecoded, PaddedFileAspect), 6);
        }

        [Fact]
        public void AStillProxyUsesTheFilesRealShape()
        {
            // A bitmap is never rewritten, so its edge cannot shimmer and its true proportions are
            // free to be used. Arrange therefore frames a clip correctly.
            Assert.Equal(PaddedFileAspect, SurfaceAspect.ForStill(PaddedFileAspect, PaddedDecoded), 6);
        }

        [Fact]
        public void EitherSideFallsBackToTheOtherWhenUnknown()
        {
            // Before the media opens there is no decoded figure; a clip from an old project may
            // have no stored file aspect. Neither may produce a zero-shaped box.
            Assert.Equal(PaddedFileAspect, SurfaceAspect.ForVideo(0, PaddedFileAspect), 6);
            Assert.Equal(PaddedDecoded, SurfaceAspect.ForStill(0, PaddedDecoded), 6);
        }

        [Fact]
        public void NothingKnownIsReportedAsNothingRatherThanAGuess()
        {
            Assert.Equal(0, SurfaceAspect.ForVideo(0, 0), 6);
            Assert.Equal(0, SurfaceAspect.ForStill(0, 0), 6);
            Assert.False(SurfaceAspect.EdgeIsFlushWithBox(0, PaddedDecoded));
            Assert.Equal((0.0, 0.0), SurfaceAspect.Overhang(0, 100, PaddedDecoded));
            Assert.Equal((0.0, 0.0), SurfaceAspect.Overhang(100, 100, 0));
        }
    }
}
