using System;
using Microsoft.UI.Xaml.Media;

namespace VideoDirector.Models
{
    // The Ken Burns curve in one place, plus the compositor-thread player for it.
    //
    // Why a Composition animation rather than a per-frame transform write: a slow push-in moves
    // the outermost pixel a fraction of a pixel per frame. The default snapshot ramp — scale
    // 1.00 -> 1.25 over 10s — is 0.000417 scale per frame at 60Hz, which on a ~700px box is about
    // 0.15px of motion per frame: roughly seven frames per whole pixel. Writing ScaleX from
    // CompositionTarget.Rendering commits that on the UI thread, so any hitch there (a relayout, a
    // GC, the playhead's per-frame text measure) lands the value a frame late and the image
    // ratchets instead of creeping. Handing the whole ramp to the compositor once makes it
    // vsync-locked and float-precise, independent of what the UI thread is doing.
    //
    // Scope: stills only. Video clips drive the same kind of transform on their
    // MediaPlayerElement, and deliberately so — the two surfaces MUST share one geometry.
    //
    // This drove a Composition visual until it turned out that Composition has no relative pivot:
    // CenterPoint is absolute, and it was being set to half the BOX. An Image with UniformToFill
    // overflows its slot (measured: 1590 wide inside a 536 box), so that pivot sat 527px left of
    // the element's real centre and every framing drifted toward the middle in proportion to
    // |Scale - 1|. RenderTransformOrigin="0.5,0.5" in the XAML is relative and cannot have that
    // bug, which is precisely why the video path never showed it.
    internal static class KenBurnsMotion
    {
        // Eased 0..1 position along the clip's curve. Mirrors the profiles the XAML path applies
        // so a still and a video with identical marks move identically.
        public static double Ease(CurveProfile profile, double progress)
            => ClipGeometry.Ease(profile, progress);

        // Scale + translate at a raw 0..1 progress. The single source of truth for the motion:
        // the live ramp and the paused frame both sample this, so pausing mid-push-in lands on
        // exactly the framing that was on screen.
        //
        public static void Evaluate(CinematicOperation op, double rawProgress,
                                    double panScaleX, double panScaleY,
                                    out double scale, out double translateX, out double translateY)
        {
            scale = 1; translateX = 0; translateY = 0;
            if (op == null) return;

            // Delegates to ClipGeometry so the renderer, the HUD and the tests cannot drift apart:
            // this used to be a second copy of the curve, which is exactly the sort of duplication
            // that lets a readout confidently report numbers the compositor never used.
            ClipGeometry.EvaluateMotion(
                new ClipGeometry.Mark(op.StartMark.Scale, op.StartMark.X, op.StartMark.Y),
                op.MidMark == null ? null : new ClipGeometry.Mark(op.MidMark.Scale, op.MidMark.X, op.MidMark.Y),
                new ClipGeometry.Mark(op.EndMark.Scale, op.EndMark.X, op.EndMark.Y),
                op.CurveProfile, rawProgress, panScaleX, panScaleY,
                out scale, out translateX, out translateY);
        }

        // Converts a mark's translate from the pixels it was captured in into the pixels it is
        // replayed in.
        //
        // Marks are captured in Edit mode, where the box is the whole video fit (fitW x fitH) and
        // the content fills it exactly. At playback the box is (fitW*w, fitH*h) and the content is
        // UniformToFill-ed into it, so it is drawn at width fitW*w when the box is wide (w > h) and
        // fitW*h when the box is tall. The ratio between capture and replay is therefore a SINGLE
        // uniform number — it has to be, since the scale is uniform — and it is max(w, h).
        //
        // The per-axis (w, h) this replaced was only ever right on one axis at a time: on a tall
        // PiP it under-translated X by h/w (about 3x on a 0.29 x 0.85 box), which landed the
        // framing well to the left of whatever was framed in the editor.
        public static double PanScale(CinematicOperation op)
            => op == null ? 1.0 : ClipGeometry.PanScale(op.PlacementWidth, op.PlacementHeight);

        // True if the clip actually moves laterally. A pure push-in (the snapshot default) does
        // not, which lets Start skip the Offset animation entirely — see the note there.
        public static bool Pans(CinematicOperation op)
        {
            if (op == null) return false;
            if (op.StartMark.X != 0 || op.StartMark.Y != 0) return true;
            if (op.EndMark.X != 0 || op.EndMark.Y != 0) return true;
            if (op.MidMark != null && (op.MidMark.X != 0 || op.MidMark.Y != 0)) return true;
            return false;
        }

        // Written straight to the transform, once per frame, from the playback tick.
        //
        // This ran as a Storyboard of 244 keyframes rebuilt on the UI thread, which is a large
        // moving part sitting inside a render handler and it kept taking playback down with it.
        // The video path has always just written its CompositeTransform every frame and has never
        // broken, so the still now does exactly the same thing. Identical mechanism, identical
        // geometry, one less thing that can differ between the two surfaces.
        public static void Apply(CompositeTransform transform, CinematicOperation op, double progress,
                                 double panX, double panY)
        {
            if (transform == null) return;

            Evaluate(op, progress, panX, panY, out double s, out double dx, out double dy);

            if (transform.ScaleX != s) { transform.ScaleX = s; transform.ScaleY = s; }
            if (transform.TranslateX != dx) transform.TranslateX = dx;
            if (transform.TranslateY != dy) transform.TranslateY = dy;
        }

        // Back to identity, so a surface reused for another clip carries no stale framing.
        public static void Reset(CompositeTransform transform)
        {
            if (transform == null) return;

            transform.ScaleX = 1;
            transform.ScaleY = 1;
            transform.TranslateX = 0;
            transform.TranslateY = 0;
        }
    }
}
