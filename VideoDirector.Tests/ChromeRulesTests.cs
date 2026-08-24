using System.Collections.Generic;
using VideoDirector.Models;
using Xunit;

namespace VideoDirector.Tests
{
    // Every one of these covers a defect that actually shipped, and each shipped because the rule
    // was a pure function of a few booleans that nothing could check without running the app.
    public class ChromeRulesTests
    {
        // One state of the world, so a test can say "these two differ only in cinematic".
        private readonly record struct S(bool Cinematic, bool Playing, bool Edit,
                                         bool Controls, bool DockOpen, bool Inspector, bool Selection);

        private static IEnumerable<S> AllStates()
        {
            bool[] b = { false, true };
            foreach (var cine in b)
            foreach (var play in b)
            foreach (var edit in b)
            foreach (var ctrl in b)
            foreach (var dock in b)
            foreach (var insp in b)
            foreach (var sel in b)
                yield return new S(cine, play, edit, ctrl, dock, insp, sel);
        }

        private static (bool editorChrome, bool dock, bool reopen, bool inspector, bool canToggle) Evaluate(S s)
            => (ChromeRules.IsEditorChromeVisible(s.Cinematic, s.Playing),
                ChromeRules.IsTrackDockVisible(s.Cinematic, s.Playing, s.Controls, s.DockOpen),
                ChromeRules.IsTrackDockReopenVisible(s.Controls, s.DockOpen),
                ChromeRules.IsInspectorVisible(s.Cinematic, s.Playing, s.Edit, s.Inspector, s.Selection),
                ChromeRules.CanToggleEditMode(s.Playing, s.Edit, s.Selection));

        // THE ONE THAT MATTERS. Arming cinematic while nothing is playing must change nothing at all.
        //
        // This shipped broken three separate times: cinematic on its own took the window full screen,
        // disabled canvas zoom and pan, and hid the inspector in Edit. Each fix caught one caller and
        // left the others, because the flag was tested in five places instead of one.
        [Fact]
        public void ArmingCinematicWhileStoppedChangesNothing()
        {
            foreach (var s in AllStates())
            {
                if (s.Playing) continue;

                var off = Evaluate(s with { Cinematic = false });
                var on  = Evaluate(s with { Cinematic = true });

                Assert.Equal(off, on);
            }
        }

        // A performance is the picture and nothing else.
        [Fact]
        public void PerformanceHidesEveryPieceOfEditorFurniture()
        {
            foreach (var s in AllStates())
            {
                if (!s.Cinematic || !s.Playing) continue;

                Assert.False(ChromeRules.IsEditorChromeVisible(s.Cinematic, s.Playing));
                Assert.False(ChromeRules.IsTrackDockVisible(s.Cinematic, s.Playing, s.Controls, s.DockOpen));
                Assert.False(ChromeRules.IsInspectorVisible(s.Cinematic, s.Playing, s.Edit, s.Inspector, s.Selection));
            }
        }

        // Playing in Edit is how you watch the move you are building, so the panel stays. This was
        // hidden by TWO independent rules - the open flag and the visibility expression - so fixing
        // one of them left it disappearing for the length of every preview.
        [Fact]
        public void InspectorSurvivesEditModePlayback()
        {
            Assert.True(ChromeRules.IsInspectorVisible(
                cinematic: false, playing: true, editMode: true, inspectorOpen: true, hasSelection: true));
        }

        [Fact]
        public void InspectorStepsAsideForOrdinaryPlayback()
        {
            Assert.False(ChromeRules.IsInspectorVisible(
                cinematic: false, playing: true, editMode: false, inspectorOpen: true, hasSelection: true));
        }

        [Fact]
        public void InspectorNeedsSomethingToDescribe()
        {
            Assert.False(ChromeRules.IsInspectorVisible(
                cinematic: false, playing: false, editMode: false, inspectorOpen: true, hasSelection: false));
        }

        // The reopen tab exists only so a closed dock is not unreachable; it must never appear
        // alongside the dock it would reopen.
        [Fact]
        public void ReopenTabAndDockAreNeverBothOnScreen()
        {
            foreach (var s in AllStates())
            {
                bool dock = ChromeRules.IsTrackDockVisible(s.Cinematic, s.Playing, s.Controls, s.DockOpen);
                bool reopen = ChromeRules.IsTrackDockReopenVisible(s.Controls, s.DockOpen);
                Assert.False(dock && reopen);
            }
        }

        // Leaving Edit always works. Entering needs a clip. Playback is not a mode this switches out of.
        [Theory]
        [InlineData(false, true,  false, true)]   // in Edit, nothing selected -> can leave
        [InlineData(false, false, true,  true)]   // in Arrange with a clip    -> can enter
        [InlineData(false, false, false, false)]  // in Arrange, nothing       -> inert
        [InlineData(true,  true,  true,  false)]  // playing                   -> inert
        public void ModeBadgeIsEnabledOnlyWhenItCanDoSomething(
            bool playing, bool edit, bool selection, bool expected)
            => Assert.Equal(expected, ChromeRules.CanToggleEditMode(playing, edit, selection));

        // Ordinary playback outside cinematic leaves the editor alone - only the auto-hide timer
        // takes the chrome, and that is a separate mechanism from these rules.
        [Fact]
        public void OrdinaryPlaybackKeepsTheEditorFurniture()
        {
            Assert.True(ChromeRules.IsEditorChromeVisible(cinematic: false, playing: true));
            Assert.True(ChromeRules.IsTrackDockVisible(
                cinematic: false, playing: true, controlsVisible: true, dockOpen: true));
        }
    }
}
