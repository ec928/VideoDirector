using System;
using System.Collections.ObjectModel;

namespace VideoDirector.Models
{
    // One upper track (Track 2, 3, 4). Strict: its clips are sequential and never overlap, so at
    // most ONE clip is active at any story time — which is what lets each track own exactly one
    // player/render surface. Simultaneity is expressed by using another track, never by stacking
    // within one. The spine (Track 1) is NOT an OverlayTrack; it keeps its own A/B-roll path.
    public sealed class OverlayTrack : ObservableObject
    {
        public ObservableCollection<CinematicOperation> Clips { get; set; } = new();

        // Where new clips on this track default to sitting (opposing corners per track).
        private double _defaultCenterX = 0.72;
        public double DefaultCenterX
        {
            get => _defaultCenterX;
            set => SetProperty(ref _defaultCenterX, value);
        }

        private double _defaultCenterY = 0.72;
        public double DefaultCenterY
        {
            get => _defaultCenterY;
            set => SetProperty(ref _defaultCenterY, value);
        }

        private string _name = "Track";
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        // Nearest start time (seconds) at which a clip of length `dur` fits WITHOUT overlapping the
        // others on this track. The track is strict — only one clip can be active at a time, so an
        // overlap would silently hide one at playback. Pass the clip being moved so it ignores
        // itself; pass null when placing a brand-new clip.
        public void ResolveOverlaps()
        {
            var sorted = new System.Collections.Generic.List<CinematicOperation>(Clips);
            sorted.Sort((a, b) => a.StartTimeSeconds.CompareTo(b.StartTimeSeconds));
            for (int i = 1; i < sorted.Count; i++)
            {
                var prev = sorted[i - 1];
                var curr = sorted[i];
                double prevEnd = prev.StartTimeSeconds + prev.OpDuration.TotalSeconds;
                if (curr.StartTimeSeconds < prevEnd)
                {
                    curr.StartTimeSeconds = prevEnd;
                }
            }
        }

        public double ClampToFreeSlot(CinematicOperation moving, double start, double dur)
        {
            start = Math.Max(0, start);

            // Occupied spans on this track (excluding the clip being moved), merged so the gaps
            // between them are real.
            var busy = new System.Collections.Generic.List<(double s, double e)>();
            foreach (var other in Clips)
            {
                if (moving != null && ReferenceEquals(other, moving)) continue;
                double s = other.StartTimeSeconds;
                double e = s + other.OpDuration.TotalSeconds;
                if (e > s) busy.Add((s, e));
            }
            if (busy.Count == 0) return start;
            busy.Sort((a, b) => a.s.CompareTo(b.s));

            var merged = new System.Collections.Generic.List<(double s, double e)>();
            foreach (var iv in busy)
            {
                int last = merged.Count - 1;
                if (last >= 0 && iv.s <= merged[last].e)
                    merged[last] = (merged[last].s, Math.Max(merged[last].e, iv.e));
                else merged.Add(iv);
            }

            // Choose the gap that fits and lands closest to where the user asked for.
            double best = double.NaN, bestDistance = double.MaxValue;
            void Consider(double gapStart, double gapEnd)
            {
                if (gapEnd - gapStart < dur) return;   // won't fit in this gap
                double candidate = Math.Clamp(start, gapStart, gapEnd - dur);
                double distance = Math.Abs(candidate - start);
                if (distance < bestDistance) { bestDistance = distance; best = candidate; }
            }

            Consider(0, merged[0].s);
            for (int i = 0; i < merged.Count - 1; i++) Consider(merged[i].e, merged[i + 1].s);
            Consider(merged[merged.Count - 1].e, double.MaxValue);

            // Nothing fits anywhere before the end — park after the last clip.
            return double.IsNaN(best) ? merged[merged.Count - 1].e : best;
        }
    }
}
