namespace VideoDirector.Models
{
    // Every rule about what is on screen and what a mode means, as pure arithmetic on booleans.
    //
    // WHY THIS EXISTS AS ITS OWN FILE: the same class of defect kept recurring. Cinematic mode was
    // tested in five separate places, so fixing one left the others - arming it disabled canvas zoom,
    // hid the inspector in Edit, and collapsed the timeline, none of which it should ever have done.
    // The inspector was hidden during Edit playback by two independent rules, so fixing one half left
    // it broken. Every one of those was a pure function of a handful of flags, and every one shipped
    // because nothing could check them without running the app and looking at it.
    //
    // Deliberately free of Microsoft.UI / Windows types so the test project can compile it directly,
    // exactly like ClipGeometry. DirectorViewModel's properties are thin delegations to these, so
    // there is one definition of each rule rather than one per consumer.
    public static class ChromeRules
    {
        /// <summary>
        /// A performance: cinematic AND playing. THE ONLY THING CINEMATIC CHANGES.
        /// </summary>
        /// <remarks>
        /// Arming cinematic on its own must change nothing at all - not full screen, not the chrome,
        /// not the view lock, not the inspector. A paused frame you are still working on is not a
        /// performance. Everything that used to test the cinematic flag alone routes through here.
        /// </remarks>
        public static bool IsPerforming(bool cinematic, bool playing) => cinematic && playing;

        /// <summary>
        /// Recording locks the chrome away entirely - nothing brings it back until the take ends.
        /// </summary>
        /// <remarks>
        /// This is a LOCK, not a longer auto-hide timeout, and the difference is the whole point.
        /// A recording captures whatever the window is showing, so a playbar summoned by a nudge
        /// of the mouse is in the file for good - and you would not find out until you watched it
        /// back. Every rule below therefore answers false while recording no matter what the other
        /// flags say, and the pointer handler does not even try to wake anything.
        ///
        /// The mouse CURSOR is a separate problem solved elsewhere: the capture session is created
        /// with IsCursorCaptureEnabled = false, so the pointer never reaches the file however much
        /// it moves. This rule is about the chrome the pointer would otherwise summon.
        ///
        /// Esc ends a take. Deliberately Esc alone rather than any key, so a stray press cannot
        /// truncate one.
        /// </remarks>
        public static bool IsRecordingLocked(bool recording) => recording;

        /// <summary>Is the editing chrome up at all.</summary>
        public static bool IsChromeVisible(bool controlsVisible, bool recording)
            => !recording && controlsVisible;

        /// <summary>
        /// Editor furniture - undo, project, export, the panel toggles - as distinct from the
        /// transport. During a performance the transport is all that belongs on screen.
        /// </summary>
        public static bool IsEditorChromeVisible(bool cinematic, bool playing, bool recording)
            => !recording && !IsPerforming(cinematic, playing);

        public static bool IsTrackDockVisible(bool cinematic, bool playing, bool controlsVisible, bool dockOpen, bool recording)
            => !recording && !IsPerforming(cinematic, playing) && controlsVisible && dockOpen;

        /// <summary>The reopen affordance, shown only while the dock is closed.</summary>
        public static bool IsTrackDockReopenVisible(bool controlsVisible, bool dockOpen, bool recording)
            => IsChromeVisible(controlsVisible, recording) && !dockOpen;

        /// <summary>
        /// The inspector panel. Hidden while playing EXCEPT in Edit, where playing is how you watch
        /// the Ken Burns move the panel is used to set.
        /// </summary>
        public static bool IsInspectorVisible(bool cinematic, bool playing, bool editMode,
                                              bool inspectorOpen, bool hasSelection, bool recording)
            => !recording
               && !IsPerforming(cinematic, playing)
               && (!playing || editMode)
               && inspectorOpen
               && hasSelection;

        /// <summary>
        /// Which inspector sections apply to the selected clip.
        /// </summary>
        /// <remarks>
        /// A sound-only clip has no picture, so framing, borders and fades have nothing to act on.
        /// Showing them anyway offers controls that appear to work and change nothing, which is the
        /// same fault as a live volume slider on a silent clip - just in the other direction.
        ///
        /// Timing and volume are all that remain, and both are exactly as meaningful for sound as
        /// for picture.
        /// </remarks>
        public static bool IsMotionSectionVisible(bool editMode, bool hasPicture) => editMode && hasPicture;

        public static bool IsBordersSectionVisible(bool editMode, bool hasPicture) => !editMode && hasPicture;

        public static bool IsTransitionsSectionVisible(bool editMode, bool hasPicture) => !editMode && hasPicture;

        /// <summary>Opacity is a property of a picture. Volume is not, and stays.</summary>
        public static bool IsOpacityRowVisible(bool hasPicture) => hasPicture;

        /// <summary>
        /// The mode badge is a two-way switch. Leaving Edit always works; entering needs a clip.
        /// Playback is not a mode this control switches out of.
        /// </summary>
        public static bool CanToggleEditMode(bool playing, bool editMode, bool hasSelection, bool recording)
            => !recording && !playing && (editMode || hasSelection);
    }
}
