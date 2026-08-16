using System;
using System.Text.Json.Serialization;

namespace VideoDirector.Models
{
    // One framing keyframe: where the camera looks, in fractions of the SOURCE FRAME.
    //
    // Deliberately independent of how big the picture is drawn. These used to be raw
    // CompositeTransform values in device pixels — see Framing for why that had to change.
    public class SpatialMark : ObservableObject
    {
        // How much of the frame is visible: 1 = all of it, 2 = half of it in each direction.
        private double _zoom = 1.0;
        public double Zoom
        {
            get => _zoom;
            set => SetProperty(ref _zoom, Math.Clamp(value, Framing.MinZoom, Framing.MaxZoom));
        }

        // Where the camera is pointed, 0..1 across the source frame. (0.5, 0.5) is centred.
        private double _centerX = 0.5;
        public double CenterX
        {
            get => _centerX;
            set => SetProperty(ref _centerX, value);
        }

        private double _centerY = 0.5;
        public double CenterY
        {
            get => _centerY;
            set => SetProperty(ref _centerY, value);
        }

        [JsonIgnore]
        public bool IsIdentity => _zoom == 1.0 && _centerX == 0.5 && _centerY == 0.5;

        public SpatialMark() { }

        public SpatialMark(double zoom, double centerX, double centerY)
        {
            Zoom = zoom;
            CenterX = centerX;
            CenterY = centerY;
        }

        public SpatialMark Clone() => new SpatialMark(_zoom, _centerX, _centerY);

        // ---- Legacy (pre-D1) persisted shape -------------------------------------------------
        // Projects written before framing was normalised stored Scale plus a pixel translation.
        // These properties exist ONLY so those files still deserialize; DirectorViewModel converts
        // them on load and clears LegacyScale, so they are absent from anything saved since.

        public double LegacyScale { get; set; }
        public double LegacyX { get; set; }
        public double LegacyY { get; set; }

        [JsonPropertyName("Scale")]
        public double ScaleCompat { get => LegacyScale; set => LegacyScale = value; }

        [JsonPropertyName("X")]
        public double XCompat { get => LegacyX; set => LegacyX = value; }

        [JsonPropertyName("Y")]
        public double YCompat { get => LegacyY; set => LegacyY = value; }

        [JsonIgnore]
        public bool HasLegacyFraming => LegacyScale > 0;

        // Fold a legacy mark into the normalised fields, then drop it so it is never applied twice.
        public void MigrateLegacyFraming()
        {
            if (!HasLegacyFraming) return;
            var (zoom, cx, cy) = Framing.FromLegacyMark(LegacyScale, LegacyX, LegacyY);
            Zoom = zoom;
            CenterX = cx;
            CenterY = cy;
            LegacyScale = 0;
            LegacyX = 0;
            LegacyY = 0;
        }
    }
}
