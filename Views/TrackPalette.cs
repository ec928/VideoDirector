using Windows.UI;

namespace VideoDirector.Views
{
    // Per-track identity colours. The SAME colour marks a track's blocks/label on the dashboard AND
    // that track's PiP frame + badge on screen — so colour is the correlation key between a timeline
    // row and its picture in the composite. Spine (Track 1) is its own colour; overlay tracks 2..4
    // each get a distinct hue.
    public static class TrackPalette
    {
        public static readonly Color Spine = C(0x3B, 0x82, 0xF6);   // blue

        private static readonly Color[] Overlays =
        {
            C(0xF5, 0x9E, 0x0B),   // Track 2 — amber
            C(0x10, 0xB9, 0x81),   // Track 3 — emerald
            C(0xA7, 0x8B, 0xFA),   // Track 4 — violet
        };

        public static Color Overlay(int index) => Overlays[((index % Overlays.Length) + Overlays.Length) % Overlays.Length];

        // Readable text colour for a given background (black on light, white on dark).
        public static Color TextOn(Color bg) => Luminance(bg) > 150 ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White;

        // Blend toward white by amount (0..1) — used for the selected-block shade.
        public static Color Lighten(Color c, double amount)
        {
            byte Mix(byte v) => (byte)(v + (255 - v) * amount);
            return C(Mix(c.R), Mix(c.G), Mix(c.B));
        }

        // Same colour at a given alpha — for the faint per-lane row band.
        public static Color At(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

        private static double Luminance(Color c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
        private static Color C(byte r, byte g, byte b) => Color.FromArgb(0xFF, r, g, b);
    }
}
