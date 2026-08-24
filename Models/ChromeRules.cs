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

        /// <summary>Is the editing chrome up at all.</summary>
        public static bool IsChromeVisible(bool controlsVisible) => controlsVisible;

        /// <summary>
        /// Editor furniture - undo, project, export, the panel toggles - as distinct from the
        /// transport. During a performance the transport is all that belongs on screen.
        /// </summary>
        public static bool IsEditorChromeVisible(bool cinematic, bool playing)
            => !IsPerforming(cinematic, playing);

        public static bool IsTrackDockVisible(bool cinematic, bool playing, bool controlsVisible, bool dockOpen)
            => !IsPerforming(cinematic, playing) && controlsVisible && dockOpen;

        /// <summary>The reopen affordance, shown only while the dock is closed.</summary>
        public static bool IsTrackDockReopenVisible(bool controlsVisible, bool dockOpen)
            => IsChromeVisible(controlsVisible) && !dockOpen;

        /// <summary>
        /// The inspector panel. Hidden while playing EXCEPT in Edit, where playing is how you watch
        /// the Ken Burns move the panel is used to set.
        /// </summary>
        public static bool IsInspectorVisible(bool cinematic, bool playing, bool editMode,
                                              bool inspectorOpen, bool hasSelection)
            => !IsPerforming(cinematic, playing)
               && (!playing || editMode)
               && inspectorOpen
               && hasSelection;

        /// <summary>
        /// The mode badge is a two-way switch. Leaving Edit always works; entering needs a clip.
        /// Playback is not a mode this control switches out of.
        /// </summary>
        public static bool CanToggleEditMode(bool playing, bool editMode, bool hasSelection)
            => !playing && (editMode || hasSelection);
    }
}
