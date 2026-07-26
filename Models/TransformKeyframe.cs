using System;

namespace VideoDirector.Models
{
    public class TransformKeyframe
    {
        public TimeSpan Time { get; set; }
        public SpatialMark Transform { get; set; }

        public TransformKeyframe(TimeSpan time, SpatialMark transform)
        {
            Time = time;
            Transform = transform;
        }
    }
}
