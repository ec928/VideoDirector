using VideoDirector.Models;
using Xunit;

namespace VideoDirector.Tests
{
    // A track is strict: its clips never overlap, so at most one is active at any story time.
    // That is what lets one track own exactly one player and one render surface
    // (ARCHITECTURE.md §5.3 / §7B), and it is the invariant phase C2 must preserve when the
    // spine and overlay collections merge.
    public class TimelineTrackTests
    {
        private static CinematicOperation Clip(double startSec, double durSec)
        {
            // Order matters: SourceDuration and PlaybackSpeed gate the trim setters, which in
            // turn derive OpDuration. Set the source length first or the duration gets clamped.
            var c = new CinematicOperation { SourceDuration = System.TimeSpan.FromSeconds(durSec + 100) };
            c.VideoStartTime = System.TimeSpan.Zero;
            c.VideoEndTime = System.TimeSpan.FromSeconds(durSec);
            c.StartTimeSeconds = startSec;
            return c;
        }

        private static (double start, double end) Span(CinematicOperation c)
            => (c.StartTimeSeconds, c.StartTimeSeconds + c.OpDuration.TotalSeconds);

        // ---- Gapless tracks -----------------------------------------------------------------
        // What used to be Track 1's structural behaviour: clips butt up end-to-end and position is
        // derived from ORDER. It is now just a flag, and these tests are what guarantee the
        // behaviour survived being demoted from a structural law to a property.

        [Fact]
        public void GaplessTrackDerivesStartTimesFromOrder()
        {
            var track = new TimelineTrack { IsGapless = true };
            track.Clips.Add(Clip(999, 5));   // deliberately nonsense start times
            track.Clips.Add(Clip(999, 10));
            track.Clips.Add(Clip(999, 2));

            track.Normalize();

            Assert.Equal(0, track.Clips[0].StartTimeSeconds, 6);
            Assert.Equal(5, track.Clips[1].StartTimeSeconds, 6);
            Assert.Equal(15, track.Clips[2].StartTimeSeconds, 6);
        }

        [Fact]
        public void GaplessTrackHasNoGaps()
        {
            var track = new TimelineTrack { IsGapless = true };
            foreach (var d in new[] { 3.0, 7.0, 1.5, 4.0 }) track.Clips.Add(Clip(0, d));

            track.Normalize();

            for (int i = 1; i < track.Clips.Count; i++)
            {
                var prev = track.Clips[i - 1];
                Assert.Equal(prev.StartTimeSeconds + prev.OpDuration.TotalSeconds,
                             track.Clips[i].StartTimeSeconds, 6);
            }
        }

        [Fact]
        public void GaplessTrackCountsTransitionsAsAdditiveTime()
        {
            // §7C: a clip occupies OpDuration + TransitionDuration of story time.
            var track = new TimelineTrack { IsGapless = true };
            var first = Clip(0, 10);
            first.TransitionDuration = System.TimeSpan.FromSeconds(2);
            track.Clips.Add(first);
            track.Clips.Add(Clip(0, 5));

            track.Normalize();

            Assert.Equal(12, track.Clips[1].StartTimeSeconds, 6);
        }

        [Fact]
        public void ReorderingAGaplessTrackReflowsIt()
        {
            var track = new TimelineTrack { IsGapless = true };
            track.Clips.Add(Clip(0, 10));
            track.Clips.Add(Clip(0, 4));
            track.Normalize();

            track.Clips.Move(0, 1);   // put the short clip first
            track.Normalize();

            Assert.Equal(0, track.Clips[0].StartTimeSeconds, 6);
            Assert.Equal(4, track.Clips[0].OpDuration.TotalSeconds, 6);
            Assert.Equal(4, track.Clips[1].StartTimeSeconds, 6);
        }

        [Fact]
        public void TurningGaplessOffLeavesTheClipsWhereTheyWere()
        {
            var track = new TimelineTrack { IsGapless = true };
            track.Clips.Add(Clip(0, 10));
            track.Clips.Add(Clip(0, 5));
            track.Normalize();

            track.IsGapless = false;   // setter re-normalizes under the new rule

            Assert.Equal(0, track.Clips[0].StartTimeSeconds, 6);
            Assert.Equal(10, track.Clips[1].StartTimeSeconds, 6);
        }

        [Fact]
        public void TurningGaplessOnClosesExistingGaps()
        {
            var track = new TimelineTrack();
            track.Clips.Add(Clip(0, 10));
            track.Clips.Add(Clip(100, 5));   // a big gap, legal on a free track

            track.IsGapless = true;

            Assert.Equal(10, track.Clips[1].StartTimeSeconds, 6);
        }

        [Fact]
        public void ContentEndIsTheLastClipsEnd()
        {
            var track = new TimelineTrack();
            track.Clips.Add(Clip(0, 5));
            track.Clips.Add(Clip(50, 10));

            Assert.Equal(60, track.ContentEnd.TotalSeconds, 6);
        }

        [Fact]
        public void ClipAtFindsTheClipVisibleAtAGivenTime()
        {
            var track = new TimelineTrack();
            var a = Clip(0, 10);
            var b = Clip(20, 10);
            track.Clips.Add(a);
            track.Clips.Add(b);

            Assert.Same(a, track.ClipAt(System.TimeSpan.FromSeconds(5)));
            Assert.Same(b, track.ClipAt(System.TimeSpan.FromSeconds(25)));
            Assert.Null(track.ClipAt(System.TimeSpan.FromSeconds(15)));   // in the gap
            Assert.Null(track.ClipAt(System.TimeSpan.FromSeconds(99)));
        }

        // ---- Free tracks ---------------------------------------------------------------------

        [Fact]
        public void ResolveOverlapsPushesCollidingClipsLater()
        {
            var track = new TimelineTrack();
            track.Clips.Add(Clip(0, 10));
            track.Clips.Add(Clip(5, 10));   // overlaps the first by 5s

            track.ResolveOverlaps();

            Assert.Equal(0, track.Clips[0].StartTimeSeconds, 3);
            Assert.Equal(10, track.Clips[1].StartTimeSeconds, 3);
        }

        [Fact]
        public void ResolveOverlapsLeavesNonOverlappingClipsAlone()
        {
            var track = new TimelineTrack();
            track.Clips.Add(Clip(0, 5));
            track.Clips.Add(Clip(20, 5));   // a real gap, which is legal on an overlay track

            track.ResolveOverlaps();

            Assert.Equal(0, track.Clips[0].StartTimeSeconds, 3);
            Assert.Equal(20, track.Clips[1].StartTimeSeconds, 3);
        }

        [Fact]
        public void NoTwoClipsOverlapAfterResolve()
        {
            var track = new TimelineTrack();
            foreach (var s in new[] { 0.0, 1.0, 2.0, 3.0, 4.0 })
                track.Clips.Add(Clip(s, 10));   // all mutually overlapping

            track.ResolveOverlaps();

            var spans = new System.Collections.Generic.List<(double s, double e)>();
            foreach (var c in track.Clips) spans.Add(Span(c));
            spans.Sort((a, b) => a.s.CompareTo(b.s));
            for (int i = 1; i < spans.Count; i++)
                Assert.True(spans[i].s >= spans[i - 1].e - 1e-6,
                    $"clip {i} starts at {spans[i].s} but the previous one ends at {spans[i - 1].e}");
        }

        [Fact]
        public void ClampToFreeSlotKeepsTheRequestedStartWhenNothingIsInTheWay()
        {
            var track = new TimelineTrack();
            track.Clips.Add(Clip(0, 5));

            Assert.Equal(20, track.ClampToFreeSlot(null, 20, 5), 3);
        }

        [Fact]
        public void ClampToFreeSlotFitsAClipIntoAGap()
        {
            var track = new TimelineTrack();
            track.Clips.Add(Clip(0, 10));
            track.Clips.Add(Clip(20, 10));   // gap is [10, 20]

            // Asking for 12 with a 5s clip fits inside the gap and is honoured exactly.
            Assert.Equal(12, track.ClampToFreeSlot(null, 12, 5), 3);
        }

        [Fact]
        public void ClampToFreeSlotPushesOutOfAnOccupiedSpan()
        {
            var track = new TimelineTrack();
            track.Clips.Add(Clip(0, 10));

            // Asking to start at 5 collides; the nearest legal placement is either 0-length-back
            // or after the existing clip. Whatever it picks, it must not overlap.
            double start = track.ClampToFreeSlot(null, 5, 5);
            Assert.True(start >= 10 - 1e-6 || start + 5 <= 0 + 1e-6,
                $"placement at {start} overlaps the clip occupying [0,10]");
        }

        [Fact]
        public void ClampToFreeSlotIgnoresTheClipBeingMoved()
        {
            var track = new TimelineTrack();
            var moving = Clip(0, 10);
            track.Clips.Add(moving);

            // The only occupant is the clip we are moving, so its own span must not block it.
            Assert.Equal(3, track.ClampToFreeSlot(moving, 3, 10), 3);
        }

        [Fact]
        public void ClampToFreeSlotParksAfterTheLastClipWhenNothingFits()
        {
            var track = new TimelineTrack();
            track.Clips.Add(Clip(0, 10));
            track.Clips.Add(Clip(12, 10));   // gaps are [10,12] and [22,inf)

            // A 5s clip cannot fit in the 2s gap, so it lands in the open run after the last clip.
            double start = track.ClampToFreeSlot(null, 11, 5);
            Assert.True(start >= 22 - 1e-6, $"expected placement at or after 22, got {start}");
        }

        [Fact]
        public void ClampToFreeSlotNeverReturnsANegativeStart()
        {
            var track = new TimelineTrack();
            track.Clips.Add(Clip(10, 5));

            Assert.True(track.ClampToFreeSlot(null, -50, 5) >= 0);
        }
    }
}
