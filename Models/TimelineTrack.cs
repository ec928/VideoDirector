using System;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace VideoDirector.Models
{
    // One track. All four are the same thing — there is no privileged "spine" track; what used to
    // be Track 1's special behaviour is now just IsGapless being on.
    //
    // Strict (ARCHITECTURE.md §5.3): a track's clips never overlap, so at most ONE is active at any
    // story time. That is what lets track i own exactly one player and one render surface.
    // Simultaneity is expressed by using another track, never by stacking within one.
    //
    // Position is ALWAYS the clip's absolute StartTime. On a gapless track those start times are
    // derived from clip order by Normalize(); on a free track the user places them. Either way,
    // nothing else in the app has to know which kind of track it is looking at.
    public sealed class TimelineTrack : ObservableObject
    {
        public ObservableCollection<CinematicOperation> Clips { get; set; } = new();

        private string _name = "Track";
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        // Clips butt up end-to-end in list order and gaps are impossible; adding, removing or
        // reordering reflows the rest. This is the behaviour Track 1 used to have structurally.
        // It ships ON for track 0 and OFF for the others, so the default feel is unchanged.
        private bool _isGapless;
        public bool IsGapless
        {
            get => _isGapless;
            set { if (SetProperty(ref _isGapless, value)) Normalize(); }
        }

        // Track-level overrides. The engine honours these; C3 puts them in the track header.
        private bool _isMuted;
        public bool IsMuted { get => _isMuted; set => SetProperty(ref _isMuted, value); }

        private bool _isHidden;
        public bool IsHidden { get => _isHidden; set => SetProperty(ref _isHidden, value); }

        private bool _isLocked;
        public bool IsLocked { get => _isLocked; set => SetProperty(ref _isLocked, value); }

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

        // Latest point at which any clip on this track ends.
        [JsonIgnore]
        public TimeSpan ContentEnd
        {
            get
            {
                var end = TimeSpan.Zero;
                foreach (var clip in Clips)
                    if (clip.EndTimeOnTimeline > end) end = clip.EndTimeOnTimeline;
                return end;
            }
        }

        // The clip visible at story time t, or null if this track is empty then. Strict track ⇒
        // the first match is the only one.
        public CinematicOperation ClipAt(TimeSpan t)
        {
            foreach (var clip in Clips)
                if (clip.IsActiveAt(t)) return clip;
            return null;
        }

        // Bring StartTimes into line with this track's rules. Call after any change to the clip
        // list, or to a clip's duration or transition.
        //
        // Gapless: position is derived from ORDER. Clip i starts where clip i-1 finished, plus that
        // clip's transition (transitions are additive, §7C). This is what used to be implicit in
        // the spine's cumulative walk; making it a real StartTime means the rest of the app can
        // treat every track identically.
        //
        // Free: order means nothing, but clips still must not overlap, so a collision pushes the
        // later clip later.
        public void Normalize()
        {
            if (_isGapless)
            {
                var at = TimeSpan.Zero;
                foreach (var clip in Clips)
                {
                    if (clip.StartTime != at) clip.StartTime = at;
                    at += clip.OpDuration + clip.TransitionDuration;
                }
                return;
            }

            ResolveOverlaps();
        }

        // Push any clip that collides with the one before it to just after it. Sorted by start
        // time, so it settles in one pass.
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

        // Nearest start time (seconds) at which a clip of length `dur` fits WITHOUT overlapping the
        // others on this track. Pass the clip being moved so it ignores itself; pass null when
        // placing a brand-new clip.
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
