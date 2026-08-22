using System;
using System.Collections.ObjectModel;

namespace VideoDirector.Models
{
    // A generalized track that can hold clips. Clips on a track never overlap, so at most ONE clip
    // is active at any story time. The engine addresses these generically (track 0 = bottom, track 3 = top).
    public sealed class TimelineTrack : ObservableObject
    {
        public ObservableCollection<CinematicOperation> Clips { get; set; } = new();

        // Where new clips on this track default to sitting. 
        // For Track 1 (index 0), we will default to 0.5, 0.5 (center) and width/height = 1.0 (full screen) 
        // in the ViewModel when creating the track.
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

        // True if this track forces clips to be perfectly adjacent (like the old spine).
        private bool _isGapless = false;
        public bool IsGapless
        {
            get => _isGapless;
            set
            {
                if (SetProperty(ref _isGapless, value))
                {
                    if (value) ResolveOverlaps();
                }
            }
        }

        private bool _isSnappingEnabled = true;
        public bool IsSnappingEnabled
        {
            get => _isSnappingEnabled;
            set => SetProperty(ref _isSnappingEnabled, value);
        }

        private bool _showAudioWaveforms = false;
        public bool ShowAudioWaveforms
        {
            get => _showAudioWaveforms;
            set
            {
                if (SetProperty(ref _showAudioWaveforms, value))
                {
                    // This change requires a timeline rebuild. We'll rely on the VM/View to handle it.
                }
            }
        }

        // Nearest start time (seconds) at which a clip of length `dur` fits WITHOUT overlapping the
        // others on this track.
        // Lays clips out so none starts before the one in front of it ends.
        //
        // IN TICKS, deliberately. This used to run in double seconds and assign through
        // StartTimeSeconds, whose setter is TimeSpan.FromSeconds - and that round trip is lossy.
        // A boundary of 20.2006781s came back one tick short, so the following clip began 100
        // nanoseconds BEFORE its predecessor ended. Both then covered that instant, and the wrong
        // one rendered - with every readout agreeing, because the model really did have it active.
        // Ticks are integers; laying a clip exactly on the previous clip's end is exact, and
        // half-open windows then guarantee only one clip covers any instant.
        public void ResolveOverlaps()
        {
            var sorted = new System.Collections.Generic.List<CinematicOperation>(Clips);
            sorted.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));

            long nextValidStart = 0;

            for (int i = 0; i < sorted.Count; i++)
            {
                var curr = sorted[i];

                if (_isGapless)
                {
                    if (curr.StartTime.Ticks != nextValidStart)
                        curr.StartTime = TimeSpan.FromTicks(nextValidStart);
                }
                else if (curr.StartTime.Ticks < nextValidStart && i > 0)
                {
                    curr.StartTime = TimeSpan.FromTicks(nextValidStart);
                }

                nextValidStart = curr.StartTime.Ticks + curr.OpDuration.Ticks;
            }
        }

        public double ClampToFreeSlot(CinematicOperation moving, double start, double dur)
        {
            start = Math.Max(0, start);

            if (_isGapless)
            {
                // In gapless mode, a new clip just goes to the very end. 
                // If it's an existing clip moving, we let Reorder logic handle it elsewhere, 
                // but for simple drops, append it.
                double maxEnd = 0.0;
                foreach (var other in Clips)
                {
                    if (moving != null && ReferenceEquals(other, moving)) continue;
                    double e = other.StartTimeSeconds + other.OpDuration.TotalSeconds;
                    if (e > maxEnd) maxEnd = e;
                }
                return maxEnd;
            }

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
