using System;
using System.Collections.Generic;
using VideoDirector.Models;
using Xunit;

namespace VideoDirector.Tests
{
    // The sampler is the definition of what the composite looks like at a given moment. Preview and
    // export are two renderers of it, which is the point: the export drifted from the preview for
    // as long as each carried its own idea of what to draw.
    public class CompositeSamplerTests
    {
        private static CinematicOperation Clip(string name, double durSec, double startSec = 0)
        {
            var c = new CinematicOperation
            {
                FilePath = $@"C:\clips\{name}.mp4",
                SourceDuration = TimeSpan.FromSeconds(durSec + 600)
            };
            c.VideoStartTime = TimeSpan.Zero;
            c.VideoEndTime = TimeSpan.FromSeconds(durSec);
            c.StartTimeSeconds = startSec;
            return c;
        }

        private static List<TimelineTrack> Tracks(params TimelineTrack[] t) => new(t);

        private static TimelineTrack Track(params CinematicOperation[] clips)
        {
            var t = new TimelineTrack();
            foreach (var c in clips) t.Clips.Add(c);
            return t;
        }

        private static TimeSpan At(double sec) => TimeSpan.FromSeconds(sec);

        // ---- What is on screen ---------------------------------------------------------------

        [Fact]
        public void NothingIsSampledWhereATrackIsEmpty()
        {
            var track = Track(Clip("a", 5, startSec: 10));
            Assert.Null(CompositeSampler.SampleTrack(track, 0, At(2)));
            Assert.NotNull(CompositeSampler.SampleTrack(track, 0, At(12)));
            Assert.Null(CompositeSampler.SampleTrack(track, 0, At(20)));
        }

        [Fact]
        public void LayersComeBackInCompositingOrder()
        {
            var tracks = Tracks(Track(Clip("base", 30)), Track(Clip("over", 30)), Track(Clip("top", 30)));
            var layers = CompositeSampler.Sample(tracks, At(5));

            Assert.Equal(3, layers.Count);
            Assert.Equal(0, layers[0].TrackIndex);   // base first
            Assert.Equal(2, layers[2].TrackIndex);   // topmost last, so it draws over
        }

        [Fact]
        public void AHiddenTrackContributesNothing()
        {
            var hidden = Track(Clip("a", 30));
            hidden.IsHidden = true;
            Assert.Empty(CompositeSampler.Sample(Tracks(hidden), At(5)));
        }

        [Fact]
        public void AMutedTrackStillShowsButIsSilent()
        {
            var muted = Track(Clip("a", 30));
            muted.Clips[0].Volume = 1.0f;
            muted.IsMuted = true;

            var layer = CompositeSampler.SampleTrack(muted, 0, At(5));
            Assert.NotNull(layer);
            Assert.Equal(0.0, layer.Value.Volume, 6);
        }

        [Fact]
        public void AClipWithNoFileIsNotSampled()
        {
            var track = new TimelineTrack();
            track.Clips.Add(new CinematicOperation { OpDuration = TimeSpan.FromSeconds(5) });
            Assert.Null(CompositeSampler.SampleTrack(track, 0, At(1)));
        }

        // ---- Where to read the source ----------------------------------------------------------

        [Fact]
        public void SourcePositionFollowsTheClipsOwnSpeed()
        {
            // The reason the export has to go through here: at 2x, five seconds into the clip is
            // ten seconds into the footage. Rendering it at 1x is exactly the bug.
            var clip = Clip("a", 60);
            clip.PlaybackSpeed = 2.0;
            var track = Track(clip);

            var layer = CompositeSampler.SampleTrack(track, 0, At(5));
            Assert.Equal(10, layer.Value.SourcePosition.TotalSeconds, 6);
        }

        [Fact]
        public void SourcePositionStartsAtTheTrimInPoint()
        {
            var clip = Clip("a", 60);
            clip.VideoStartTime = TimeSpan.FromSeconds(20);
            var track = Track(clip);

            var layer = CompositeSampler.SampleTrack(track, 0, At(0));
            Assert.Equal(20, layer.Value.SourcePosition.TotalSeconds, 6);
        }

        [Fact]
        public void AStillHoldsItsFrozenFrame()
        {
            var clip = Clip("a", 60);
            clip.VideoStartTime = TimeSpan.FromSeconds(12);
            clip.PlaybackSpeed = 0;                       // selects Still
            clip.OpDuration = TimeSpan.FromSeconds(8);
            var track = Track(clip);

            foreach (double t in new[] { 0.0, 2.0, 7.9 })
                Assert.Equal(12, CompositeSampler.SampleTrack(track, 0, At(t)).Value.SourcePosition.TotalSeconds, 6);
        }

        [Fact]
        public void SourcePositionNeverRunsPastTheOutPoint()
        {
            var clip = Clip("a", 10);
            var track = Track(clip);

            for (double t = 0; t < 10; t += 0.5)
            {
                var layer = CompositeSampler.SampleTrack(track, 0, At(t));
                Assert.True(layer.Value.SourcePosition <= clip.VideoEndTime,
                    $"at {t}s the source position escaped the out-point");
            }
        }

        // ---- Framing -------------------------------------------------------------------------

        [Fact]
        public void FramingIsInterpolatedAcrossTheClip()
        {
            var clip = Clip("a", 10);
            clip.StartMark = new SpatialMark(1.0, 0.5, 0.5);
            clip.EndMark = new SpatialMark(3.0, 0.5, 0.5);
            var track = Track(clip);

            Assert.Equal(1.0, CompositeSampler.SampleTrack(track, 0, At(0)).Value.Zoom, 6);
            Assert.Equal(2.0, CompositeSampler.SampleTrack(track, 0, At(5)).Value.Zoom, 6);
            Assert.Equal(3.0, CompositeSampler.SampleTrack(track, 0, At(9.999)).Value.Zoom, 3);
        }

        [Fact]
        public void FramingUsesTheClipsMidKeyframeAndItsTime()
        {
            var clip = Clip("a", 10);
            clip.StartMark = new SpatialMark(1.0, 0.5, 0.5);
            clip.MidMark = new SpatialMark(5.0, 0.5, 0.5);
            clip.EndMark = new SpatialMark(2.0, 0.5, 0.5);
            clip.MidTime = 0.25;
            var track = Track(clip);

            Assert.Equal(5.0, CompositeSampler.SampleTrack(track, 0, At(2.5)).Value.Zoom, 6);
        }

        [Fact]
        public void PlacementIsCarriedThroughUntouched()
        {
            var clip = Clip("a", 10);
            clip.PlaceAt(0.72, 0.28, size: 0.4);
            var track = Track(clip);

            var layer = CompositeSampler.SampleTrack(track, 0, At(1)).Value;
            Assert.Equal(0.4, layer.PlacementWidth, 6);
            Assert.Equal(0.72, layer.PlacementCenterX, 6);
            Assert.Equal(0.28, layer.PlacementCenterY, 6);
        }

        // ---- Duration and frames ---------------------------------------------------------------

        [Fact]
        public void DurationIsTheLatestEndAcrossEveryTrack()
        {
            var tracks = Tracks(Track(Clip("a", 5)), Track(Clip("b", 4, startSec: 30)));
            Assert.Equal(34, CompositeSampler.Duration(tracks).TotalSeconds, 6);
        }

        [Fact]
        public void FrameTimesCoverTheWholeComposite()
        {
            var tracks = Tracks(Track(Clip("a", 2)));
            var times = new List<TimeSpan>(CompositeSampler.FrameTimes(tracks, 30));

            Assert.Equal(60, times.Count);
            Assert.Equal(0, times[0].TotalSeconds, 6);
            Assert.True(times[^1].TotalSeconds < 2.0);
        }

        [Fact]
        public void AnEmptyProjectRendersNoFrames()
        {
            Assert.Empty(new List<TimeSpan>(CompositeSampler.FrameTimes(Tracks(Track()), 30)));
        }

        [Fact]
        public void EveryFrameOfAProjectSamplesSomething()
        {
            // A gapless base track plus an overlay: no frame should come back empty, or the export
            // would show black where the preview shows picture.
            var baseTrack = Track(Clip("a", 3), Clip("b", 3));
            baseTrack.IsGapless = true;
            baseTrack.Normalize();
            var tracks = Tracks(baseTrack, Track(Clip("ov", 2, startSec: 1)));

            foreach (var t in CompositeSampler.FrameTimes(tracks, 25))
                Assert.NotEmpty(CompositeSampler.Sample(tracks, t));
        }

        [Fact]
        public void ANullTrackListIsHarmless()
        {
            Assert.Empty(CompositeSampler.Sample(null, At(1)));
            Assert.Equal(TimeSpan.Zero, CompositeSampler.Duration(null));
        }
    }
}
