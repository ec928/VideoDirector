using System;
using System.Collections.Generic;

namespace VideoDirector.Models
{
    /// <summary>
    /// Headless clip/timeline contracts. Free of WinUI so the test project can link them.
    /// </summary>
    public static class ClipRules
    {
        /// <summary>
        /// Story-time end of a clip. OpDuration already includes any additive fade.
        /// </summary>
        public static TimeSpan StoryEnd(TimeSpan start, TimeSpan opDuration)
        {
            if (opDuration < TimeSpan.Zero) opDuration = TimeSpan.Zero;
            return start + opDuration;
        }

        public static TimeSpan LatestStoryEnd(IEnumerable<(TimeSpan start, TimeSpan opDuration)> clips)
        {
            TimeSpan max = TimeSpan.Zero;
            if (clips == null) return max;
            foreach (var c in clips)
            {
                var end = StoryEnd(c.start, c.opDuration);
                if (end > max) max = end;
            }
            return max;
        }

        /// <summary>
        /// Mid is a modification even when Start and End are still identity.
        /// </summary>
        public static bool HasMarkModifications(bool startIsIdentity, bool endIsIdentity, bool hasMid)
            => !startIsIdentity || !endIsIdentity || hasMid;

        /// <summary>
        /// Export mixes source audio at 1x. Any other rate would desync from the recorded picture.
        /// </summary>
        public static bool CanMixExportAudio(double playbackSpeed)
            => Math.Abs(playbackSpeed - 1.0) <= 1e-6;
    }
}
