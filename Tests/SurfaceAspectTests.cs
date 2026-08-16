using VideoDirector.Models;
using Xunit;

namespace VideoDirector.Tests
{
    // The shimmering border. A live video surface must be given a box of the DECODER's shape, so
    // UniformToFill overflows by nothing and the grid's clip geometry never cuts a swapchain that
    // is being rewritten at frame rate. Shaping it from the file's real aspect instead — which
    // looks more correct, and was — is what made the edge crawl.
    //
    // These are regression tests for a bug that was fixed once, reintroduced, and then diagnosed
    // wrongly three times (subpixel seam, unguarded writes, an "overscan" correction). The property
    // below is the one that distinguishes the fix from all three.
    public class SurfaceAspectTests
    {
        private static double Aspect(double w, double h) => w / h;

        // The real pair from the user's project: a 1918x804 file decodes as 1920x816.
        private const double FileAspect = 1918.0 / 804.0;      // 2.38557
        private const double DecodedAspect = 1920.0 / 816.0;   // 2.35294

        [Fact]
        public void AVideoBoxTakesTheDecodersShapeNotTheFiles()
        {
            Assert.Equal(DecodedAspect, SurfaceAspect.ForVideo(DecodedAspect, FileAspect), 6);
        }

        [Fact]
        public void AVideoBoxShapedByTheDecoderNeverCropsTheSurface()
        {
            // THE fix. Zero overflow means no clip boundary to re-resolve against a live surface.
            double box = SurfaceAspect.ForVideo(DecodedAspect, FileAspect);
            Assert.False(SurfaceAspect.WouldCropSurface(box, DecodedAspect));
        }

        [Fact]
        public void ShapingAVideoBoxFromTheFileAspectWouldCrop()
        {
            // The regression, stated as a test: this is what the code did while the edge shimmered.
            Assert.True(SurfaceAspect.WouldCropSurface(FileAspect, DecodedAspect));
        }

        [Theory]
        [InlineData(1920, 1080)]   // already macroblock-aligned
        [InlineData(1920, 800)]    // The Magic Faraway Tree — never shimmered
        [InlineData(1280, 720)]
        public void AnAlignedFileHasNothingToReconcile(double w, double h)
        {
            // No padding, so the file and the decoder agree and either choice is the same box.
            // This is exactly why most videos in a project looked fine and a few did not.
            double a = Aspect(w, h);
            Assert.Equal(a, SurfaceAspect.ForVideo(a, a), 6);
            Assert.False(SurfaceAspect.WouldCropSurface(a, a));
        }

        [Fact]
        public void RoundingABoxToWholePixelsWouldReintroduceTheCrop()
        {
            // Why ApplyBoxTo does not snap to whole pixels. Rounding width and height independently
            // moves the box off the surface's aspect, which is the bug in miniature.
            double boxW = 441.37, boxH = boxW / DecodedAspect;
            Assert.False(SurfaceAspect.WouldCropSurface(boxW / boxH, DecodedAspect));

            double roundedAspect = System.Math.Round(boxW) / System.Math.Round(boxH);
            Assert.True(SurfaceAspect.WouldCropSurface(roundedAspect, DecodedAspect),
                "rounding was expected to knock the box off the surface aspect");
        }

        // ---- Stills ---------------------------------------------------------------------------

        [Fact]
        public void AStillProxyUsesTheFilesRealShape()
        {
            // A bitmap has no video surface, so nothing can crawl and the true proportions are free
            // to be used. Arrange therefore frames a clip correctly.
            Assert.Equal(FileAspect, SurfaceAspect.ForStill(FileAspect, DecodedAspect), 6);
        }

        [Fact]
        public void EitherSideFallsBackToTheOtherWhenUnknown()
        {
            // Before the media opens there is no decoded figure; for a clip from an old project
            // there may be no stored file aspect. Neither case may produce a zero-shaped box.
            Assert.Equal(FileAspect, SurfaceAspect.ForVideo(0, FileAspect), 6);
            Assert.Equal(DecodedAspect, SurfaceAspect.ForStill(0, DecodedAspect), 6);
        }

        [Fact]
        public void NothingKnownIsReportedAsNothingRatherThanAGuess()
        {
            Assert.Equal(0, SurfaceAspect.ForVideo(0, 0), 6);
            Assert.Equal(0, SurfaceAspect.ForStill(0, 0), 6);
            // An unknown shape cannot crop, so the caller draws nothing rather than a wrong box.
            Assert.False(SurfaceAspect.WouldCropSurface(0, DecodedAspect));
            Assert.False(SurfaceAspect.WouldCropSurface(FileAspect, 0));
        }
    }
}
