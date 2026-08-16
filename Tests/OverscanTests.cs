using VideoDirector.Models;
using Xunit;

namespace VideoDirector.Tests
{
    // Media Foundation reports the DECODED frame, padded out to macroblock multiples — a 1918x804
    // video comes back as 1920x816. Shaping a clip's box from that padded figure leaves a strip of
    // codec padding along the edge, and padding pixels change frame to frame, which reads as a
    // shimmering border. Videos whose real dimensions need no padding never showed it.
    //
    // The overscan is the scale that pushes that padding outside the box. These exercise the same
    // arithmetic the engine uses, on real-world dimension pairs.
    public class OverscanTests
    {
        // Mirror of VideoPlaybackEngine.OverscanFor — kept here rather than made public on the
        // engine, which is WinUI-bound and cannot be constructed in a test.
        private static double Overscan(double realAspect, double decodedAspect)
        {
            if (realAspect <= 0 || decodedAspect <= 0) return 1.0;
            double ratio = System.Math.Max(realAspect / decodedAspect, decodedAspect / realAspect);
            return double.IsNaN(ratio) ? 1.0 : System.Math.Clamp(ratio, 1.0, 1.08);
        }

        private static double Aspect(double w, double h) => w / h;

        [Fact]
        public void AnUnpaddedVideoNeedsNoOverscan()
        {
            // 1920x1080 is already a macroblock multiple, so the decoder pads nothing and the
            // picture must be left completely alone. This is the case that always looked fine.
            Assert.Equal(1.0, Overscan(Aspect(1920, 1080), Aspect(1920, 1080)), 6);
        }

        [Fact]
        public void ThePaddedCaseFromTheCodebaseNeedsSome()
        {
            // The example already documented in FitWindow_Click.
            double overscan = Overscan(Aspect(1918, 804), Aspect(1920, 816));
            Assert.True(overscan > 1.0, "padding present but no overscan computed");
            Assert.True(overscan < 1.05, $"correction should be a fraction of a percent, got {overscan}");
        }

        [Theory]
        [InlineData(1918, 804, 1920, 816)]
        [InlineData(1080, 1350, 1088, 1360)]   // portrait, padded both axes
        [InlineData(640, 358, 640, 368)]       // height padded only
        [InlineData(1278, 720, 1280, 720)]     // width padded only
        public void PaddingAlwaysProducesACorrectionThatCropsRatherThanShrinks(
            double realW, double realH, double decW, double decH)
        {
            double overscan = Overscan(Aspect(realW, realH), Aspect(decW, decH));
            // Never below 1: shrinking would pull the picture away from the box edges and show
            // background, which is worse than the padding it is trying to hide.
            Assert.True(overscan >= 1.0);
            Assert.True(overscan <= 1.08);
        }

        [Fact]
        public void TheCorrectionIsSymmetricWhicheverAxisWasPadded()
        {
            // Padding on width and padding on height are the same problem, so the same magnitude
            // of mismatch must produce the same correction either way round.
            double a = Overscan(2.0, 2.1);
            double b = Overscan(2.1, 2.0);
            Assert.Equal(a, b, 6);
        }

        [Fact]
        public void AWildMismatchIsNotTreatedAsPadding()
        {
            // Padding is a few pixels. If the two figures disagree wildly, something else is wrong
            // and cropping by that much would mangle the picture, so it is capped.
            Assert.Equal(1.08, Overscan(16.0 / 9.0, 9.0 / 16.0), 6);
        }

        [Fact]
        public void UnknownDimensionsLeaveThePictureAlone()
        {
            Assert.Equal(1.0, Overscan(0, 1.777), 6);
            Assert.Equal(1.0, Overscan(1.777, 0), 6);
            Assert.Equal(1.0, Overscan(-1, -1), 6);
        }
    }
}
