namespace VideoDirector.Models
{
    public enum CurveProfile
    {
        Linear,
        Bezier,
        DirectorsArc
    }

    // Names kept so existing projects still deserialise; what each one MEANS is now defined by
    // what the engine actually does with it, which until now was nothing at all.
    //
    // Crossfade needs the outgoing and incoming clip on screen together, which the compositor
    // cannot do while one slot holds one clip - so it is deliberately absent from the picker
    // rather than offered and ignored.
    public enum TransitionStyle
    {
        HardSnap,        // none
        Crossfade,       // not implemented, not offered
        CinematicBridge, // fade in from black AND out to black
        DipToColor       // fade out to black at the end
    }

    public enum BorderType
    {
        None,
        Solid,
        Soft,
        FilmStrip
    }
}
