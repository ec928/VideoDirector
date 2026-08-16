using System;
using System.Collections.Generic;

namespace VideoDirector.Models
{
    // What one track contributes to the picture at a given story time.
    public readonly struct LayerSample
    {
        public readonly int TrackIndex;
        public readonly CinematicOperation Clip;

        // Where to read the source. Already accounts for the clip's speed, so a 2x clip is twice
        // as far into its footage as the wall clock would suggest.
        public readonly TimeSpan SourcePosition;

        // Framing at this instant, in source-frame terms (see Framing).
        public readonly double Zoom, CenterX, CenterY;

        // Where the picture sits in the output, as fractions of its aspect-fit size.
        public readonly double PlacementWidth, PlacementHeight, PlacementCenterX, PlacementCenterY;

        public readonly double Opacity;
        public readonly double Volume;

        public LayerSample(int trackIndex, CinematicOperation clip, TimeSpan sourcePosition,
                           double zoom, double centerX, double centerY,
                           double placementWidth, double placementHeight,
                           double placementCenterX, double placementCenterY,
                           double opacity, double volume)
        {
            TrackIndex = trackIndex; Clip = clip; SourcePosition = sourcePosition;
            Zoom = zoom; CenterX = centerX; CenterY = centerY;
            PlacementWidth = placementWidth; PlacementHeight = placementHeight;
            PlacementCenterX = placementCenterX; PlacementCenterY = placementCenterY;
            Opacity = opacity; Volume = volume;
        }
    }

    // THE definition of what the composite looks like at a given story time.
    //
    // This exists so the preview and the export cannot disagree. They are two renderers of the
    // same thing, and the export has historically drifted from the preview — no Ken Burns, no
    // speed, no transitions — because each carried its own idea of what to draw. Anything that
    // decides *what* is on screen belongs here; a renderer decides only *how* to draw it.
    //
    // Pure and WinUI-free, so it is testable and so a renderer of any kind can consume it.
    public static class CompositeSampler
    {
        // Total length of the composite: the latest point any track finishes.
        public static TimeSpan Duration(IReadOnlyList<TimelineTrack> tracks)
        {
            var end = TimeSpan.Zero;
            if (tracks == null) return end;
            foreach (var track in tracks)
            {
                if (track == null) continue;
                var trackEnd = track.ContentEnd;
                if (trackEnd > end) end = trackEnd;
            }
            return end;
        }

        // What this track shows at story time t, or null if nothing.
        public static LayerSample? SampleTrack(TimelineTrack track, int trackIndex, TimeSpan t)
        {
            if (track == null || track.IsHidden) return null;

            var clip = track.ClipAt(t);
            if (clip == null || string.IsNullOrWhiteSpace(clip.FilePath)) return null;

            var offset = t - clip.StartTime;
            if (offset < TimeSpan.Zero) offset = TimeSpan.Zero;

            // How far into the footage we are. A still does not advance, so it holds its in-point;
            // otherwise the source runs at the clip's own speed. This is the same rule the live
            // compositor uses to seek an overlay player.
            double advance = clip.IsStill ? 0 : Math.Max(0, clip.PlaybackSpeed);
            var sourcePosition = clip.VideoStartTime + TimeSpan.FromSeconds(offset.TotalSeconds * advance);

            // Never read past the trimmed out-point; hold the last frame instead.
            if (sourcePosition > clip.VideoEndTime) sourcePosition = clip.VideoEndTime;

            double progress = clip.OpDuration.TotalSeconds > 0
                ? offset.TotalSeconds / clip.OpDuration.TotalSeconds
                : 0;
            var (zoom, cx, cy) = MotionPath.Sample(
                clip.StartMark, clip.MidMark, clip.EndMark, clip.MidTime, clip.CurveProfile, progress);

            return new LayerSample(
                trackIndex, clip, sourcePosition,
                zoom, cx, cy,
                clip.PlacementWidth, clip.PlacementHeight,
                clip.PlacementCenterX, clip.PlacementCenterY,
                Math.Clamp(clip.Opacity, 0, 1),
                track.IsMuted ? 0.0 : Math.Clamp(clip.Volume, 0, 1));
        }

        // Every layer on screen at story time t, in compositing order: track 0 first (the base),
        // higher tracks over it (ARCHITECTURE.md §5.4). A renderer draws them in this order.
        public static List<LayerSample> Sample(IReadOnlyList<TimelineTrack> tracks, TimeSpan t)
        {
            var layers = new List<LayerSample>();
            if (tracks == null) return layers;

            for (int i = 0; i < tracks.Count; i++)
            {
                var sample = SampleTrack(tracks[i], i, t);
                if (sample.HasValue) layers.Add(sample.Value);
            }
            return layers;
        }

        // The story times of every frame in the composite, at the given rate. The renderer walks
        // these; putting it here keeps rounding in one place rather than in each renderer.
        public static IEnumerable<TimeSpan> FrameTimes(IReadOnlyList<TimelineTrack> tracks, double fps)
        {
            if (fps <= 0) yield break;
            long frames = (long)Math.Ceiling(Duration(tracks).TotalSeconds * fps);
            for (long i = 0; i < frames; i++)
                yield return TimeSpan.FromSeconds(i / fps);
        }
    }
}
