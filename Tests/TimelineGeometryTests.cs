using VideoDirector.Views;
using Xunit;

namespace VideoDirector.Tests
{
    // The lane mapping is the one piece of timeline layout whose correctness is invisible in code
    // review and obvious only on screen. These tests are the substitute for looking at it.
    //
    // The app ships 3 overlay tracks, so `Overlays = 3` is the real-world case: lanes run
    // Track 4, Track 3, Track 2, Track 1 from top to bottom.
    public class TimelineGeometryTests
    {
        private const int Overlays = 3;

        // Track index convention: -1 = spine (Track 1), 0..2 = overlay tracks (Track 2..4).
        private const int Track1 = -1, Track2 = 0, Track3 = 1, Track4 = 2;

        [Fact]
        public void TopLaneIsTheTopmostCompositingLayer()
        {
            // Track 4 composites over everything, so it must draw first (lane 0).
            Assert.Equal(0, TimelineGeometry.LaneOfTrack(Track4, Overlays));
            Assert.Equal(1, TimelineGeometry.LaneOfTrack(Track3, Overlays));
            Assert.Equal(2, TimelineGeometry.LaneOfTrack(Track2, Overlays));
            // Track 1 is the base layer, so it draws last (bottom lane).
            Assert.Equal(3, TimelineGeometry.LaneOfTrack(Track1, Overlays));
        }

        [Fact]
        public void RowYIncreasesAsCompositingOrderDecreases()
        {
            double t4 = TimelineGeometry.RowYForTrack(Track4, Overlays);
            double t3 = TimelineGeometry.RowYForTrack(Track3, Overlays);
            double t2 = TimelineGeometry.RowYForTrack(Track2, Overlays);
            double t1 = TimelineGeometry.RowYForTrack(Track1, Overlays);

            Assert.Equal(16, t4);
            Assert.Equal(34, t3);
            Assert.Equal(52, t2);
            Assert.Equal(70, t1);

            // The property that actually matters: higher track number sits higher on screen.
            Assert.True(t4 < t3 && t3 < t2 && t2 < t1);
        }

        [Theory]
        [InlineData(Track1)]
        [InlineData(Track2)]
        [InlineData(Track3)]
        [InlineData(Track4)]
        public void LaneAndTrackAreInverses(int track)
        {
            int lane = TimelineGeometry.LaneOfTrack(track, Overlays);
            Assert.Equal(track, TimelineGeometry.TrackOfLane(lane, Overlays));
        }

        [Theory]
        // A point anywhere inside a lane's pitch band resolves to that lane's track.
        [InlineData(16, Track4)]
        [InlineData(20, Track4)]
        [InlineData(33, Track4)]
        [InlineData(34, Track3)]
        [InlineData(51, Track3)]
        [InlineData(52, Track2)]
        [InlineData(69, Track2)]
        [InlineData(70, Track1)]
        [InlineData(87, Track1)]
        public void TrackAtYResolvesEveryPointInALaneBand(double y, int expected)
        {
            Assert.Equal(expected, TimelineGeometry.TrackAtY(y, Overlays));
        }

        [Fact]
        public void TrackAtYRoundTripsWithRowY()
        {
            foreach (int track in new[] { Track1, Track2, Track3, Track4 })
            {
                double y = TimelineGeometry.RowYForTrack(track, Overlays);
                Assert.Equal(track, TimelineGeometry.TrackAtY(y, Overlays));
                // ...and still resolves at the far edge of the block it draws.
                Assert.Equal(track, TimelineGeometry.TrackAtY(y + TimelineGeometry.BlockH - 1, Overlays));
            }
        }

        [Fact]
        public void NoGapBetweenLanesInHitTesting()
        {
            // Every y from the first lane to the last must resolve to SOME track. The old code
            // tested the spine row over its 16px block but overlay rows over the full 18px pitch,
            // leaving a 2px band that hit nothing and silently fell through to scrub.
            double last = TimelineGeometry.RowYForTrack(Track1, Overlays) + TimelineGeometry.BlockH;
            for (double y = TimelineGeometry.RowTop; y < last; y += 0.5)
            {
                int track = TimelineGeometry.TrackAtY(y, Overlays);
                Assert.InRange(track, -1, Overlays - 1);
            }
        }

        [Fact]
        public void PointsOutsideTheLanesClampToTheNearestLane()
        {
            // Above the lanes (the ruler) clamps to the top lane...
            Assert.Equal(Track4, TimelineGeometry.TrackAtY(0, Overlays));
            Assert.Equal(Track4, TimelineGeometry.TrackAtY(-500, Overlays));
            // ...and below them clamps to the bottom lane, rather than resolving to nothing.
            Assert.Equal(Track1, TimelineGeometry.TrackAtY(5000, Overlays));
        }

        [Fact]
        public void RulerIsOnlyTheStripAboveTheLanes()
        {
            Assert.True(TimelineGeometry.IsRulerY(0));
            Assert.True(TimelineGeometry.IsRulerY(TimelineGeometry.RowTop - 0.01));
            Assert.False(TimelineGeometry.IsRulerY(TimelineGeometry.RowTop));
            Assert.False(TimelineGeometry.IsRulerY(70));
        }

        [Fact]
        public void BarHeightClearsTheBottomLane()
        {
            double bottomOfLastBlock = TimelineGeometry.RowYForTrack(Track1, Overlays) + TimelineGeometry.BlockH;
            Assert.True(TimelineGeometry.BarHeight(Overlays) >= bottomOfLastBlock,
                "the bar must be tall enough to draw the bottom lane's block");
        }

        // ---- Horizontal extent ---------------------------------------------------------------

        [Fact]
        public void AnEmptyProjectStillGetsAUsableTimeline()
        {
            // It used to draw nothing at all below a zero-length project: no ruler, no lanes,
            // just four floating track labels beside a blank strip.
            Assert.True(TimelineGeometry.ExtentSeconds(0) >= TimelineGeometry.MinExtentSeconds);
        }

        [Fact]
        public void ExtentAlwaysLeavesRoomPastTheLastClip()
        {
            // The property that matters: there is always somewhere to drag a clip TO in order to
            // extend the project. Without it, no track but the spine could ever make one longer.
            foreach (double contentEnd in new[] { 0, 1, 30, 120, 3600, 20000.0 })
            {
                double extent = TimelineGeometry.ExtentSeconds(contentEnd);
                Assert.True(extent > contentEnd,
                    $"content ends at {contentEnd} but the timeline only draws {extent}");
            }
        }

        [Fact]
        public void ExtentGrowsWithTheProject()
        {
            Assert.True(TimelineGeometry.ExtentSeconds(600) > TimelineGeometry.ExtentSeconds(60));
        }

        [Fact]
        public void ExtentToleratesNonsenseInput()
        {
            Assert.True(TimelineGeometry.ExtentSeconds(-5) >= TimelineGeometry.MinExtentSeconds);
            Assert.True(TimelineGeometry.ExtentSeconds(double.NaN) >= TimelineGeometry.MinExtentSeconds);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(8)]
        public void MappingHoldsForAnyTrackCount(int overlays)
        {
            Assert.Equal(overlays + 1, TimelineGeometry.LaneCount(overlays));

            // The spine is always the bottom lane, whatever the overlay count.
            Assert.Equal(overlays, TimelineGeometry.LaneOfTrack(-1, overlays));

            for (int t = 0; t < overlays; t++)
            {
                int lane = TimelineGeometry.LaneOfTrack(t, overlays);
                Assert.InRange(lane, 0, overlays - 1);
                Assert.Equal(t, TimelineGeometry.TrackOfLane(lane, overlays));
                // Higher track index is always nearer the top.
                Assert.True(TimelineGeometry.RowYForTrack(t, overlays)
                            < TimelineGeometry.RowYForTrack(-1, overlays));
            }
        }
    }
}
