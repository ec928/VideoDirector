using VideoDirector.Views;
using Xunit;

namespace VideoDirector.Tests
{
    // The lane mapping is the one piece of timeline layout whose correctness is invisible in code
    // review and obvious only on screen. These tests are the substitute for looking at it.
    //
    // The app ships 4 tracks. Track indices are uniform 0..3 with no sentinel: track 0 is the base
    // layer and draws in the BOTTOM lane; track 3 composites on top and draws in the TOP lane.
    public class TimelineGeometryTests
    {
        private const int Tracks = 4;

        [Fact]
        public void TopLaneIsTheTopmostCompositingLayer()
        {
            // Track 3 composites over everything, so it must draw first (lane 0).
            Assert.Equal(0, TimelineGeometry.LaneOfTrack(3, Tracks));
            Assert.Equal(1, TimelineGeometry.LaneOfTrack(2, Tracks));
            Assert.Equal(2, TimelineGeometry.LaneOfTrack(1, Tracks));
            // Track 0 is the base layer, so it draws last (bottom lane).
            Assert.Equal(3, TimelineGeometry.LaneOfTrack(0, Tracks));
        }

        [Fact]
        public void RowYIncreasesAsCompositingOrderDecreases()
        {
            for (int lane = 0; lane < Tracks; lane++)
                Assert.Equal(TimelineGeometry.RowTop + lane * TimelineGeometry.RowPitch,
                             TimelineGeometry.RowYForTrack(Tracks - 1 - lane, Tracks), 6);

            // The property that actually matters: a higher track sits higher on screen, so
            // dragging a clip up a lane moves it UP the compositing stack.
            for (int t = 1; t < Tracks; t++)
                Assert.True(TimelineGeometry.RowYForTrack(t, Tracks)
                            < TimelineGeometry.RowYForTrack(t - 1, Tracks));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void LaneAndTrackAreInverses(int track)
        {
            int lane = TimelineGeometry.LaneOfTrack(track, Tracks);
            Assert.Equal(track, TimelineGeometry.TrackOfLane(lane, Tracks));
        }

        [Theory]
        // A point anywhere inside a lane's pitch band resolves to that lane's track.
        [InlineData(0.0, 3)]     // top of the top lane
        [InlineData(0.5, 3)]     // half-way down it
        [InlineData(0.99, 3)]    // just before the next lane starts
        [InlineData(1.0, 2)]
        [InlineData(2.0, 1)]
        [InlineData(3.0, 0)]     // bottom lane
        [InlineData(3.9, 0)]
        public void TrackAtYResolvesEveryPointInALaneBand(double lanesDown, int expected)
        {
            double y = TimelineGeometry.RowTop + lanesDown * TimelineGeometry.RowPitch;
            Assert.Equal(expected, TimelineGeometry.TrackAtY(y, Tracks));
        }

        [Fact]
        public void TrackAtYRoundTripsWithRowY()
        {
            for (int track = 0; track < Tracks; track++)
            {
                double y = TimelineGeometry.RowYForTrack(track, Tracks);
                Assert.Equal(track, TimelineGeometry.TrackAtY(y, Tracks));
                // ...and still resolves at the far edge of the block it draws.
                Assert.Equal(track, TimelineGeometry.TrackAtY(y + TimelineGeometry.BlockH - 1, Tracks));
            }
        }

        [Fact]
        public void NoGapBetweenLanesInHitTesting()
        {
            // Every y from the first lane to the last must resolve to SOME track. Track 0 used to
            // be tested over its 16px block while the others were tested over the full 18px pitch,
            // leaving a 2px band that hit nothing and silently fell through to scrub.
            double last = TimelineGeometry.RowYForTrack(0, Tracks) + TimelineGeometry.BlockH;
            for (double y = TimelineGeometry.RowTop; y < last; y += 0.5)
                Assert.InRange(TimelineGeometry.TrackAtY(y, Tracks), 0, Tracks - 1);
        }

        [Fact]
        public void PointsOutsideTheLanesClampToTheNearestLane()
        {
            // Above the lanes (the ruler) clamps to the top lane...
            Assert.Equal(3, TimelineGeometry.TrackAtY(0, Tracks));
            Assert.Equal(3, TimelineGeometry.TrackAtY(-500, Tracks));
            // ...and below them clamps to the bottom lane, rather than resolving to nothing.
            Assert.Equal(0, TimelineGeometry.TrackAtY(5000, Tracks));
        }

        [Fact]
        public void RulerIsOnlyTheStripAboveTheLanes()
        {
            Assert.True(TimelineGeometry.IsRulerY(0));
            Assert.True(TimelineGeometry.IsRulerY(TimelineGeometry.RowTop - 0.01));
            Assert.False(TimelineGeometry.IsRulerY(TimelineGeometry.RowTop));
            Assert.False(TimelineGeometry.IsRulerY(TimelineGeometry.RowTop + 1));
        }

        [Fact]
        public void BarHeightClearsTheBottomLane()
        {
            double bottomOfLastBlock = TimelineGeometry.RowYForTrack(0, Tracks) + TimelineGeometry.BlockH;
            Assert.True(TimelineGeometry.BarHeight(Tracks) >= bottomOfLastBlock,
                "the bar must be tall enough to draw the bottom lane's block");
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(4)]
        [InlineData(9)]
        public void MappingHoldsForAnyTrackCount(int trackCount)
        {
            for (int t = 0; t < trackCount; t++)
            {
                int lane = TimelineGeometry.LaneOfTrack(t, trackCount);
                Assert.InRange(lane, 0, trackCount - 1);
                Assert.Equal(t, TimelineGeometry.TrackOfLane(lane, trackCount));
                Assert.Equal(t, TimelineGeometry.TrackAtY(
                    TimelineGeometry.RowYForTrack(t, trackCount), trackCount));
            }
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
            // extend the project. Without it, no track could ever make one longer.
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
    }
}
