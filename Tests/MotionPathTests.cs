using VideoDirector.Models;
using Xunit;

namespace VideoDirector.Tests
{
    // How a clip's framing moves over its duration. This lives in one place so the compositor and
    // the framing editor cannot disagree about where the camera is at a given moment — they used
    // to carry separate copies of this arithmetic.
    public class MotionPathTests
    {
        private static SpatialMark Mark(double zoom, double cx = 0.5, double cy = 0.5)
            => new SpatialMark(zoom, cx, cy);

        // ---- Easing --------------------------------------------------------------------------

        [Theory]
        [InlineData(CurveProfile.Linear)]
        [InlineData(CurveProfile.Bezier)]
        [InlineData(CurveProfile.DirectorsArc)]
        public void EveryCurveStartsAtZeroAndEndsAtOne(CurveProfile curve)
        {
            // Whatever the pacing, a move must begin at its start framing and arrive at its end.
            Assert.Equal(0.0, MotionPath.Ease(curve, 0), 6);
            Assert.Equal(1.0, MotionPath.Ease(curve, 1), 6);
        }

        [Theory]
        [InlineData(CurveProfile.Linear)]
        [InlineData(CurveProfile.Bezier)]
        [InlineData(CurveProfile.DirectorsArc)]
        public void EveryCurveMovesForwards(CurveProfile curve)
        {
            double previous = -1;
            for (double p = 0; p <= 1.0001; p += 0.05)
            {
                double eased = MotionPath.Ease(curve, p);
                Assert.True(eased >= previous - 1e-9, $"{curve} went backwards at {p}");
                Assert.InRange(eased, -1e-9, 1 + 1e-9);
                previous = eased;
            }
        }

        [Fact]
        public void LinearIsTheIdentity()
        {
            Assert.Equal(0.25, MotionPath.Ease(CurveProfile.Linear, 0.25), 6);
            Assert.Equal(0.5, MotionPath.Ease(CurveProfile.Linear, 0.5), 6);
        }

        [Fact]
        public void EaseInOutIsSymmetricAndSlowAtBothEnds()
        {
            Assert.Equal(0.5, MotionPath.Ease(CurveProfile.Bezier, 0.5), 6);
            Assert.True(MotionPath.Ease(CurveProfile.Bezier, 0.1) < 0.1);   // slow away
            Assert.True(MotionPath.Ease(CurveProfile.Bezier, 0.9) > 0.9);   // slow in
        }

        [Fact]
        public void EaseOutStartsQuicklyAndSettles()
        {
            Assert.True(MotionPath.Ease(CurveProfile.DirectorsArc, 0.25) > 0.25);
        }

        [Fact]
        public void ProgressOutsideTheClipIsClamped()
        {
            Assert.Equal(0.0, MotionPath.Ease(CurveProfile.Bezier, -3), 6);
            Assert.Equal(1.0, MotionPath.Ease(CurveProfile.Bezier, 7), 6);
        }

        // ---- Sampling ------------------------------------------------------------------------

        [Fact]
        public void AMoveBeginsAtStartAndArrivesAtEnd()
        {
            var start = Mark(1.0, 0.2, 0.2);
            var end = Mark(3.0, 0.8, 0.8);

            var a = MotionPath.Sample(start, null, end, 0.5, CurveProfile.Linear, 0);
            var b = MotionPath.Sample(start, null, end, 0.5, CurveProfile.Linear, 1);

            Assert.Equal(1.0, a.zoom, 6);
            Assert.Equal(0.2, a.centerX, 6);
            Assert.Equal(3.0, b.zoom, 6);
            Assert.Equal(0.8, b.centerX, 6);
        }

        [Fact]
        public void WithoutAMidTheMoveIsAStraightInterpolation()
        {
            var mid = MotionPath.Sample(Mark(1.0), null, Mark(3.0), 0.5, CurveProfile.Linear, 0.5);
            Assert.Equal(2.0, mid.zoom, 6);
        }

        [Fact]
        public void TheMoveArrivesAtTheMidKeyframeAtItsOwnTime()
        {
            // The point of MidTime: the Mid framing happens where you put it, not at the halfway
            // mark. At 0.25 the move should be exactly on the Mid keyframe a quarter of the way in.
            var start = Mark(1.0);
            var mid = Mark(5.0);
            var end = Mark(2.0);

            var atMid = MotionPath.Sample(start, mid, end, 0.25, CurveProfile.Linear, 0.25);
            Assert.Equal(5.0, atMid.zoom, 6);
        }

        [Fact]
        public void MidTimeChangesWhichSegmentTheClipIsInHalfWayThrough()
        {
            // Keyframes are deliberately asymmetric: with start and end equal, the two cases below
            // meet at the same value by arithmetic coincidence and the test proves nothing.
            var start = Mark(1.0);
            var mid = Mark(5.0);
            var end = Mark(2.0);

            // Mid late in the clip: half way through we are still on the FIRST segment, climbing
            // from start towards Mid.
            var late = MotionPath.Sample(start, mid, end, 0.8, CurveProfile.Linear, 0.5);
            Assert.InRange(late.zoom, 1.0, 5.0);
            Assert.True(late.zoom < 5.0, "should not have reached Mid yet");

            // Mid early: half way through we are on the SECOND segment, descending towards end.
            var early = MotionPath.Sample(start, mid, end, 0.2, CurveProfile.Linear, 0.5);
            Assert.InRange(early.zoom, 2.0, 5.0);

            Assert.NotEqual(late.zoom, early.zoom, 3);
        }

        [Fact]
        public void ADefaultMidTimeBehavesLikeTheOldHardwiredMidpoint()
        {
            // Existing projects have no MidTime stored, so they default to 0.5 and must play
            // exactly as they did before it existed.
            var atHalf = MotionPath.Sample(Mark(1.0), Mark(4.0), Mark(2.0), 0.5, CurveProfile.Linear, 0.5);
            Assert.Equal(4.0, atHalf.zoom, 6);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(1.0)]
        [InlineData(-5.0)]
        [InlineData(42.0)]
        [InlineData(double.NaN)]
        public void ANonsenseMidTimeStillProducesAUsableMove(double midTime)
        {
            // A zero-length segment would divide by zero, so the split is kept strictly inside.
            for (double p = 0; p <= 1.0001; p += 0.1)
            {
                var (zoom, cx, cy) = MotionPath.Sample(Mark(1.0), Mark(3.0), Mark(2.0), midTime, CurveProfile.Linear, p);
                Assert.False(double.IsNaN(zoom) || double.IsInfinity(zoom), $"zoom broke at {p}");
                Assert.InRange(zoom, 1.0, 3.0);
                Assert.False(double.IsNaN(cx) || double.IsNaN(cy));
            }
        }

        [Fact]
        public void AMoveNeverOvershootsItsKeyframes()
        {
            // Whatever the pacing, the camera stays within the range the keyframes describe.
            foreach (CurveProfile curve in new[] { CurveProfile.Linear, CurveProfile.Bezier, CurveProfile.DirectorsArc })
                for (double p = 0; p <= 1.0001; p += 0.02)
                {
                    var (zoom, _, _) = MotionPath.Sample(Mark(1.0), Mark(4.0), Mark(2.0), 0.35, curve, p);
                    Assert.InRange(zoom, 1.0 - 1e-9, 4.0 + 1e-9);
                }
        }

        [Fact]
        public void AMissingKeyframeDoesNotCrashTheMove()
        {
            var onlyEnd = MotionPath.Sample(null, null, Mark(2.0), 0.5, CurveProfile.Linear, 0.5);
            Assert.Equal(2.0, onlyEnd.zoom, 6);

            var nothing = MotionPath.Sample(null, null, null, 0.5, CurveProfile.Linear, 0.5);
            Assert.Equal(1.0, nothing.zoom, 6);
            Assert.Equal(0.5, nothing.centerX, 6);
        }
    }
}
