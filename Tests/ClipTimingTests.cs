using System;
using VideoDirector.Models;
using Xunit;

namespace VideoDirector.Tests
{
    // The trim/speed/duration constraint, which CinematicOperation keeps consistent at all times:
    //
    //   0 <= VideoStartTime < VideoEndTime <= SourceDuration   (>= 0.1s apart)
    //   OpDuration == (VideoEndTime - VideoStartTime) / PlaybackSpeed     for a video
    //   a still (PlaybackSpeed == 0) has no advancing source window, so OpDuration is an
    //   independent hold time
    //
    // These tests pin BOTH the invariant and the current yield rules — which field moves when
    // another is edited. Phase D4 replaces those rules with an explicit pin, so several of these
    // are expected to change; they exist so that change is visible and deliberate rather than
    // silent. The invariant tests should survive D4 untouched.
    public class ClipTimingTests
    {
        private const double Tol = 1e-6;

        // A video clip. The FilePath matters: IsStill treats a clip with no path (or an image
        // extension) as a still regardless of speed, so an unnamed clip would not behave as video.
        private static CinematicOperation Video(double sourceSec)
            => new CinematicOperation
            {
                FilePath = @"C:\clips\test.mp4",
                SourceDuration = TimeSpan.FromSeconds(sourceSec)
            };

        // ---- The invariant ------------------------------------------------------------------

        [Fact]
        public void DurationAlwaysMatchesTheTrimmedWindowDividedBySpeed()
        {
            var c = Video(60);
            c.VideoStartTime = TimeSpan.FromSeconds(10);
            c.VideoEndTime = TimeSpan.FromSeconds(40);

            foreach (double speed in new[] { 0.5, 1.0, 2.0, 4.0 })
            {
                c.PlaybackSpeed = speed;
                double expected = (c.VideoEndTime - c.VideoStartTime).TotalSeconds / speed;
                Assert.Equal(expected, c.OpDuration.TotalSeconds, 6);
            }
        }

        [Fact]
        public void TrimCannotEscapeTheSourceLength()
        {
            var c = Video(30);
            c.VideoEndTime = TimeSpan.FromSeconds(999);
            Assert.True(c.VideoEndTime.TotalSeconds <= 30 + Tol);

            c.VideoStartTime = TimeSpan.FromSeconds(-5);
            Assert.True(c.VideoStartTime.TotalSeconds >= -Tol);
        }

        [Fact]
        public void OutPointCannotCrossInPoint()
        {
            var c = Video(60);
            c.VideoStartTime = TimeSpan.FromSeconds(20);
            c.VideoEndTime = TimeSpan.FromSeconds(5);   // behind the in-point

            Assert.True(c.VideoEndTime > c.VideoStartTime,
                $"in={c.VideoStartTime} out={c.VideoEndTime}");
            Assert.True((c.VideoEndTime - c.VideoStartTime).TotalSeconds >= 0.1 - Tol,
                "the window must stay at least the minimum clip length");
        }

        [Fact]
        public void InPointCannotCrossOutPoint()
        {
            var c = Video(60);
            c.VideoEndTime = TimeSpan.FromSeconds(10);
            c.VideoStartTime = TimeSpan.FromSeconds(50);   // past the out-point

            Assert.True(c.VideoEndTime > c.VideoStartTime,
                $"in={c.VideoStartTime} out={c.VideoEndTime}");
        }

        [Fact]
        public void LearningTheSourceLengthLaterReClampsTheTrim()
        {
            // Covers clips from older projects saved before SourceDuration was captured: the real
            // length arrives once the media opens and must retroactively bound the trim.
            var c = new CinematicOperation();
            c.VideoEndTime = TimeSpan.FromSeconds(500);
            c.SourceDuration = TimeSpan.FromSeconds(20);

            Assert.True(c.VideoEndTime.TotalSeconds <= 20 + Tol,
                $"out-point {c.VideoEndTime} exceeds the source length that was backfilled");
        }

        [Fact]
        public void DurationIsNeverZeroOrNegative()
        {
            var c = Video(60);
            c.VideoStartTime = TimeSpan.FromSeconds(10);
            c.VideoEndTime = TimeSpan.FromSeconds(10);   // degenerate window

            Assert.True(c.OpDuration.TotalSeconds > 0);
        }

        // ---- Stills -------------------------------------------------------------------------

        [Fact]
        public void SpeedZeroMakesAClipAStill()
        {
            var c = Video(60);
            Assert.False(c.IsStill);
            c.PlaybackSpeed = 0;
            Assert.True(c.IsStill);
        }

        [Fact]
        public void AStillHoldsForExactlyTheDurationYouSet()
        {
            // The behaviour phase D4 makes explicit as its own mode, instead of a side effect of
            // speed being zero.
            var c = Video(60);
            c.PlaybackSpeed = 0;
            c.OpDuration = TimeSpan.FromSeconds(7);

            Assert.Equal(7, c.OpDuration.TotalSeconds, 6);
        }

        [Fact]
        public void AStillsDurationSurvivesTrimEdits()
        {
            var c = Video(60);
            c.PlaybackSpeed = 0;
            c.OpDuration = TimeSpan.FromSeconds(7);

            c.VideoStartTime = TimeSpan.FromSeconds(3);   // moving the frozen frame...

            Assert.Equal(7, c.OpDuration.TotalSeconds, 6);   // ...must not change the hold time
        }

        [Fact]
        public void SpeedIsNeverNegative()
        {
            var c = Video(60);
            c.PlaybackSpeed = -2;
            Assert.True(c.PlaybackSpeed >= 0);
        }

        // ---- Current yield rules (expected to change in D4) ---------------------------------

        [Fact]
        public void EditingDurationRetrimsTheOutPoint()
        {
            // "Make this exactly 10s" pulls 10s x speed of source from the in-point. This is what
            // makes a precise segment extractable from a long source by typing a number.
            var c = Video(600);
            c.VideoStartTime = TimeSpan.FromSeconds(100);
            c.PlaybackSpeed = 1.0;

            c.OpDuration = TimeSpan.FromSeconds(10);

            Assert.Equal(110, c.VideoEndTime.TotalSeconds, 6);
            Assert.Equal(100, c.VideoStartTime.TotalSeconds, 6);   // in-point held
        }

        [Fact]
        public void EditingDurationAtDoubleSpeedPullsTwiceAsMuchSource()
        {
            var c = Video(600);
            c.VideoStartTime = TimeSpan.FromSeconds(100);
            c.PlaybackSpeed = 2.0;

            c.OpDuration = TimeSpan.FromSeconds(10);

            Assert.Equal(120, c.VideoEndTime.TotalSeconds, 6);
            Assert.Equal(10, c.OpDuration.TotalSeconds, 6);
        }

        [Fact]
        public void EditingDurationBeyondTheSourceReportsTheLengthItActuallyGot()
        {
            // The displayed number must stay honest when the request is capped by the source.
            var c = Video(120);
            c.VideoStartTime = TimeSpan.FromSeconds(100);
            c.PlaybackSpeed = 1.0;

            c.OpDuration = TimeSpan.FromSeconds(60);   // only 20s of source remain

            Assert.Equal(120, c.VideoEndTime.TotalSeconds, 6);
            Assert.Equal(20, c.OpDuration.TotalSeconds, 6);
        }

        [Fact]
        public void ChangingSpeedChangesDurationAndHoldsTheSourceWindow()
        {
            // The coupling that reads as surprising but is physically correct: same source window,
            // played slower, takes longer. D4 keeps this as the default but makes it visible, and
            // adds a fit-to-fill mode where duration is held and speed is derived instead.
            var c = Video(600);
            c.VideoStartTime = TimeSpan.FromSeconds(0);
            c.VideoEndTime = TimeSpan.FromSeconds(60);

            c.PlaybackSpeed = 0.5;

            Assert.Equal(60, c.VideoEndTime.TotalSeconds, 6);   // window untouched
            Assert.Equal(120, c.OpDuration.TotalSeconds, 6);    // duration doubled
        }
    }
}
