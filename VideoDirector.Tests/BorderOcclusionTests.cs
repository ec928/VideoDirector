using System.Collections.Generic;
using VideoDirector.Models;
using Xunit;

namespace VideoDirector.Tests
{
    public class BorderOcclusionTests
    {
        private static ClipGeometry.GeoRect R(double x, double y, double w, double h)
            => new ClipGeometry.GeoRect(x, y, w, h);

        [Fact]
        public void NoOverlap_KeepsTheStrip()
        {
            var into = new List<ClipGeometry.GeoRect>();
            ClipGeometry.SubtractStrip(R(0, 0, 100, 8), R(200, 200, 10, 10), into);
            Assert.Single(into);
            Assert.Equal(100, into[0].W);
        }

        [Fact]
        public void FullCover_RemovesTheStrip()
        {
            var into = new List<ClipGeometry.GeoRect>();
            ClipGeometry.SubtractStrip(R(0, 0, 100, 8), R(-1, -1, 200, 20), into);
            Assert.Empty(into);
        }

        [Fact]
        public void CoverLeftOfHorizontal_LeavesTheRight()
        {
            var into = new List<ClipGeometry.GeoRect>();
            ClipGeometry.SubtractStrip(R(0, 0, 100, 8), R(0, 0, 40, 8), into);
            Assert.Single(into);
            Assert.Equal(40, into[0].X, 3);
            Assert.Equal(60, into[0].W, 3);
        }

        [Fact]
        public void CoverMiddleOfHorizontal_LeavesTwoStubs()
        {
            var into = new List<ClipGeometry.GeoRect>();
            ClipGeometry.SubtractStrip(R(0, 0, 100, 8), R(40, 0, 20, 8), into);
            Assert.Equal(2, into.Count);
        }

        [Fact]
        public void SmallClipInTheMiddle_MissesThePerimeter()
        {
            // A 20x20 occluder sitting in the interior of a 200x100 box does not touch
            // any 8px edge strip, so the old "eat three sides" bug cannot fire.
            var box = R(0, 0, 200, 100);
            var occluder = R(90, 40, 20, 20);
            double t = 8;
            var top = R(box.X, box.Y, box.W, t);
            var bot = R(box.X, box.Bottom - t, box.W, t);
            var left = R(box.X, box.Y + t, t, box.H - 2 * t);
            var right = R(box.Right - t, box.Y + t, t, box.H - 2 * t);

            foreach (var strip in new[] { top, bot, left, right })
            {
                var into = new List<ClipGeometry.GeoRect>();
                ClipGeometry.SubtractStrip(strip, occluder, into);
                Assert.Single(into);
            }
        }

        [Fact]
        public void PartialCornerCover_TrimsOnlyTheCoveredEdges()
        {
            // T6 covers T3's lower-left, spanning neither full axis of T3.
            var t3 = R(100, 100, 200, 120);
            var t6 = R(40, 140, 180, 120);
            double th = 8;
            var top = R(t3.X, t3.Y, t3.W, th);
            var bot = R(t3.X, t3.Bottom - th, t3.W, th);
            var left = R(t3.X, t3.Y + th, th, t3.H - 2 * th);
            var right = R(t3.Right - th, t3.Y + th, th, t3.H - 2 * th);

            var topLeft = new List<ClipGeometry.GeoRect>();
            ClipGeometry.SubtractStrip(top, t6, topLeft);
            Assert.Single(topLeft); // T6 is below the top edge

            var botLeft = new List<ClipGeometry.GeoRect>();
            ClipGeometry.SubtractStrip(bot, t6, botLeft);
            Assert.Single(botLeft);
            Assert.True(botLeft[0].X > t3.X); // left of bottom is gone

            var leftLeft = new List<ClipGeometry.GeoRect>();
            ClipGeometry.SubtractStrip(left, t6, leftLeft);
            Assert.True(leftLeft.Count <= 1);
            if (leftLeft.Count == 1)
                Assert.True(leftLeft[0].H < left.H); // shortened

            var rightLeft = new List<ClipGeometry.GeoRect>();
            ClipGeometry.SubtractStrip(right, t6, rightLeft);
            Assert.Single(rightLeft); // T6 does not reach the right edge
        }
    }
}
