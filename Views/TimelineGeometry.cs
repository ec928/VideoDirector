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
    // Lanes therefore run Track 4, Track 3, Track 2, Track 1 top-to-bottom, matching the compositor
    // and standard NLE convention — dragging a clip up moves it up the stack.
    //
    // Track index convention used throughout the app: -1 is the spine (Track 1); 0..n-1 are the
    // overlay tracks (Track 2..n+1). That sentinel goes away with phase C2, which makes all tracks
    // peers; when it does, only this file and its tests should need to change.
    public static class TimelineGeometry
    {
        public const double RulerH = 14;   // scrub strip above the lanes
        public const double RowTop = 16;   // y of the first (topmost) lane
        public const double BlockH = 16;   // drawn height of a clip block
        public const double RowPitch = 18; // lane-to-lane spacing; the 2px surplus is the gutter

        public static int LaneCount(int overlayCount) => 1 + Math.Max(0, overlayCount);

        public static int LaneOfTrack(int trackIndex, int overlayCount)
        {
            overlayCount = Math.Max(0, overlayCount);
            return trackIndex < 0 ? overlayCount : overlayCount - 1 - trackIndex;
        }

        public static int TrackOfLane(int lane, int overlayCount)
        {
            overlayCount = Math.Max(0, overlayCount);
            return lane >= overlayCount ? -1 : overlayCount - 1 - lane;
        }

        public static double RowYForTrack(int trackIndex, int overlayCount)
            => RowTop + LaneOfTrack(trackIndex, overlayCount) * RowPitch;

        // True when y is in the scrub ruler above the lanes.
        public static bool IsRulerY(double y) => y < RowTop;

        // y -> track index. Clamped, so a point above or below the lanes resolves to the nearest
        // one rather than to nothing. Callers that must distinguish the ruler check IsRulerY first.
        public static int TrackAtY(double y, int overlayCount)
        {
            int lane = (int)((y - RowTop) / RowPitch);
            lane = Math.Clamp(lane, 0, Math.Max(0, LaneCount(overlayCount) - 1));
            return TrackOfLane(lane, overlayCount);
        }

        // Covers every lane plus a small bottom margin, otherwise the bottom lane gets clipped.
        public static double BarHeight(int overlayCount)
            => RowTop + Math.Max(1, LaneCount(overlayCount)) * RowPitch + 6;

        // ---- Horizontal extent --------------------------------------------------------------

        public const double MinExtentSeconds = 30;      // an empty project still shows a usable ruler
        public const double MinRunwaySeconds = 10;      // ...and a short one still has room to grow

        // How much time the timeline draws, given where the last clip ends.
        //
        // Deliberately LONGER than the content: the drawn width used to equal the project length
        // exactly, so there was nowhere to drag a clip *to* in order to extend the project. That
        // was survivable only because Track 1 defined the duration and you extended it by appending
        // there. Once every track is a peer (C2) there is no privileged way to make a project
        // longer, and every track hits a wall at the current end.
        public static double ExtentSeconds(double contentEndSeconds)
        {
            if (double.IsNaN(contentEndSeconds) || contentEndSeconds < 0) contentEndSeconds = 0;
            double runway = Math.Max(MinRunwaySeconds, contentEndSeconds * 0.2);
            return Math.Max(MinExtentSeconds, contentEndSeconds + runway);
        }
    }
}
