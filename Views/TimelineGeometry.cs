using System;

namespace VideoDirector.Views
{
    // Pure timeline row geometry — the mapping between a track, its display lane, and a y
    // coordinate. Deliberately free of WinUI types so it can be tested without a UI thread.
    //
    // This lives apart from VideoDirectorControl for one reason: a lane-mapping error is invisible
    // in code review and obvious only on screen, so it is the one piece of timeline layout that
    // has to be provable without running the app.
    //
    // THE TOP LANE IS THE TOPMOST COMPOSITING LAYER (ARCHITECTURE.md §5.4: z-order is track index).
    // Track 0 is the base layer and draws in the BOTTOM lane; the highest-numbered track draws in
    // the top one. Dragging a clip up a lane therefore moves it up the compositing stack, matching
    // the compositor and standard NLE convention.
    //
    // Track indices are uniform 0..n-1. There is no sentinel for a special track.
    public static class TimelineGeometry
    {
        public const double RulerH = 14;   // scrub strip above the lanes
        public const double RowTop = 16;   // y of the first (topmost) lane
        public const double BlockH = 28;   // drawn height of a clip block
        public const double RowPitch = 32; // lane-to-lane spacing; the 4px surplus is the gutter

        // Width of the fixed label gutter to the left of the lanes. A track header carries an
        // identity chip, a name and four state toggles, none of which fit the 58px it used to be.
        public const double GutterW = 168;

        public static int LaneOfTrack(int trackIndex, int trackCount)
            => Math.Max(1, trackCount) - 1 - trackIndex;

        public static int TrackOfLane(int lane, int trackCount)
            => Math.Max(1, trackCount) - 1 - lane;

        public static double RowYForTrack(int trackIndex, int trackCount)
            => RowTop + LaneOfTrack(trackIndex, trackCount) * RowPitch;

        // True when y is in the scrub ruler above the lanes.
        public static bool IsRulerY(double y) => y < RowTop;

        // y -> track index. Clamped, so a point above or below the lanes resolves to the nearest
        // one rather than to nothing. Callers that must distinguish the ruler check IsRulerY first.
        public static int TrackAtY(double y, int trackCount)
        {
            int lane = (int)((y - RowTop) / RowPitch);
            lane = Math.Clamp(lane, 0, Math.Max(0, Math.Max(1, trackCount) - 1));
            return TrackOfLane(lane, trackCount);
        }

        // Covers every lane plus a small bottom margin, otherwise the bottom lane gets clipped.
        public static double BarHeight(int trackCount)
            => RowTop + Math.Max(1, trackCount) * RowPitch + 6;

        // ---- Horizontal extent --------------------------------------------------------------

        public const double MinExtentSeconds = 30;      // an empty project still shows a usable ruler
        public const double MinRunwaySeconds = 10;      // ...and a short one still has room to grow

        // How much time the timeline draws, given where the last clip ends.
        //
        // Deliberately LONGER than the content: the drawn width used to equal the project length
        // exactly, so there was nowhere to drag a clip *to* in order to extend the project. That
        // was survivable only while one track defined the duration and you extended it by appending
        // there. With peer tracks there is no privileged way to make a project longer, and every
        // track would hit a wall at the current end.
        public static double ExtentSeconds(double contentEndSeconds)
        {
            if (double.IsNaN(contentEndSeconds) || contentEndSeconds < 0) contentEndSeconds = 0;
            double runway = Math.Max(MinRunwaySeconds, contentEndSeconds * 0.2);
            return Math.Max(MinExtentSeconds, contentEndSeconds + runway);
        }
    }
}
