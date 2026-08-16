using VideoDirector.Models;
using Xunit;

namespace VideoDirector.Tests
{
    // The source window, speed and timeline duration satisfy Duration = (Out - In) / Speed, so
    // only two of the three can be free. Editing any of them therefore has to move another — that
    // was always true. What was missing was any statement of WHICH one moves: the rules were
    // implicit and asymmetric, presented as independent-looking number boxes.
    //
    // These tests pin each mode's rule, and the invariant that has to hold whatever you do.
    public class RetimeSolverTests
    {
        private const double Source = 600;

        private static RetimeState State(double inSec, double outSec, double speed, double duration)
            => new RetimeState(inSec, outSec, speed, duration);

        private static void AssertInvariant(RetimeState s, RetimeMode mode, double source)
        {
            Assert.True(s.In >= 0, $"in {s.In} went negative");
            Assert.True(s.Out > s.In, $"window inverted: {s.In}..{s.Out}");
            Assert.True(s.Out - s.In >= RetimeSolver.MinClipSeconds - 1e-9, "window below the minimum");
            if (source > 0) Assert.True(s.Out <= source + 1e-9, $"out {s.Out} escaped the source");
            Assert.True(s.Duration > 0, "duration must be positive");

            if (mode != RetimeMode.Still)
            {
                Assert.InRange(s.Speed, RetimeSolver.MinSpeed, RetimeSolver.MaxSpeed);
                // The constraint itself.
                Assert.Equal(s.Window / s.Speed, s.Duration, 6);
            }
        }

        // ---- Hold source (the default) -------------------------------------------------------

        [Fact]
        public void HoldSourceDerivesDuration()
        {
            Assert.Equal(RetimeField.Duration, RetimeSolver.DerivedField(RetimeMode.HoldSource));
        }

        [Fact]
        public void ChangingSpeedChangesHowLongTheClipRuns()
        {
            // The coupling that reads as surprising but is physically correct: the same footage,
            // played slower, takes longer.
            var s = RetimeSolver.Reconcile(State(0, 60, 0.5, 60), RetimeField.Speed, RetimeMode.HoldSource, Source);

            Assert.Equal(0, s.In, 6);
            Assert.Equal(60, s.Out, 6);      // window untouched
            Assert.Equal(120, s.Duration, 6); // twice as long on the timeline
            AssertInvariant(s, RetimeMode.HoldSource, Source);
        }

        [Fact]
        public void EditingDurationPullsThatMuchFootageFromTheInPoint()
        {
            var s = RetimeSolver.Reconcile(State(100, 200, 1.0, 10), RetimeField.Duration, RetimeMode.HoldSource, Source);

            Assert.Equal(100, s.In, 6);   // in-point held
            Assert.Equal(110, s.Out, 6);
            Assert.Equal(10, s.Duration, 6);
            AssertInvariant(s, RetimeMode.HoldSource, Source);
        }

        [Fact]
        public void EditingDurationAtDoubleSpeedPullsTwiceAsMuchFootage()
        {
            var s = RetimeSolver.Reconcile(State(100, 200, 2.0, 10), RetimeField.Duration, RetimeMode.HoldSource, Source);

            Assert.Equal(120, s.Out, 6);
            Assert.Equal(10, s.Duration, 6);
            AssertInvariant(s, RetimeMode.HoldSource, Source);
        }

        [Fact]
        public void ADurationTheSourceCannotSupplyReportsWhatItActuallyGot()
        {
            // The number on screen must not lie about a length the clip does not have.
            var s = RetimeSolver.Reconcile(State(100, 110, 1.0, 999), RetimeField.Duration, RetimeMode.HoldSource, 120);

            Assert.Equal(120, s.Out, 6);
            Assert.Equal(20, s.Duration, 6);
            AssertInvariant(s, RetimeMode.HoldSource, 120);
        }

        [Fact]
        public void MovingTheInPointRederivesDuration()
        {
            var s = RetimeSolver.Reconcile(State(30, 90, 1.0, 60), RetimeField.In, RetimeMode.HoldSource, Source);
            Assert.Equal(60, s.Duration, 6);
            AssertInvariant(s, RetimeMode.HoldSource, Source);
        }

        // ---- Fit to fill ---------------------------------------------------------------------

        [Fact]
        public void FitToFillDerivesSpeed()
        {
            Assert.Equal(RetimeField.Speed, RetimeSolver.DerivedField(RetimeMode.FitToFill));
        }

        [Fact]
        public void FitToFillStretchesSpeedToTheRequestedLength()
        {
            // "Fill this 10-second slot with 20 seconds of footage" -> play it at 2x.
            var s = RetimeSolver.Reconcile(State(0, 20, 1.0, 10), RetimeField.Duration, RetimeMode.FitToFill, Source);

            Assert.Equal(0, s.In, 6);
            Assert.Equal(20, s.Out, 6);    // window untouched
            Assert.Equal(2.0, s.Speed, 6);
            Assert.Equal(10, s.Duration, 6);
            AssertInvariant(s, RetimeMode.FitToFill, Source);
        }

        [Fact]
        public void FitToFillSlowsFootageDownToFillALongerSlot()
        {
            var s = RetimeSolver.Reconcile(State(0, 10, 1.0, 20), RetimeField.Duration, RetimeMode.FitToFill, Source);
            Assert.Equal(0.5, s.Speed, 6);
            Assert.Equal(20, s.Duration, 6);
            AssertInvariant(s, RetimeMode.FitToFill, Source);
        }

        [Fact]
        public void TrimmingInFitToFillKeepsTheSlotAndRederivesSpeed()
        {
            // The duration is what the user is protecting here, so the window change moves speed.
            var s = RetimeSolver.Reconcile(State(0, 40, 2.0, 20), RetimeField.Out, RetimeMode.FitToFill, Source);
            Assert.Equal(20, s.Duration, 6);
            Assert.Equal(2.0, s.Speed, 6);
            AssertInvariant(s, RetimeMode.FitToFill, Source);
        }

        // ---- Still ---------------------------------------------------------------------------

        [Fact]
        public void AStillHasNothingDerived()
        {
            Assert.Null(RetimeSolver.DerivedField(RetimeMode.Still));
        }

        [Fact]
        public void AStillIsHeldForExactlyTheDurationYouAsk()
        {
            var s = RetimeSolver.Reconcile(State(12, 300, 1.0, 7), RetimeField.Duration, RetimeMode.Still, Source);

            Assert.Equal(7, s.Duration, 6);
            Assert.Equal(0, s.Speed, 6);      // frozen
            Assert.Equal(12, s.In, 6);        // the frame being held
            AssertInvariant(s, RetimeMode.Still, Source);
        }

        [Fact]
        public void MovingAStillsFrameDoesNotChangeHowLongItIsHeld()
        {
            var s = RetimeSolver.Reconcile(State(40, 300, 0, 7), RetimeField.In, RetimeMode.Still, Source);
            Assert.Equal(7, s.Duration, 6);
            Assert.Equal(40, s.In, 6);
        }

        [Fact]
        public void SwitchingToStillKeepsTheDurationTheClipAlreadyHad()
        {
            var s = RetimeSolver.OnModeChanged(State(0, 60, 1.0, 60), RetimeMode.Still, Source);
            Assert.Equal(60, s.Duration, 6);
            Assert.Equal(0, s.Speed, 6);
        }

        [Fact]
        public void SwitchingBackFromStillRestoresAPlayableSpeed()
        {
            var still = RetimeSolver.Reconcile(State(10, 300, 1.0, 5), RetimeField.Duration, RetimeMode.Still, Source);
            var moving = RetimeSolver.OnModeChanged(still, RetimeMode.HoldSource, Source);

            Assert.True(moving.Speed > 0, "a moving clip cannot be frozen");
            AssertInvariant(moving, RetimeMode.HoldSource, Source);
        }

        // ---- Invariants ----------------------------------------------------------------------

        [Theory]
        [InlineData(RetimeMode.HoldSource)]
        [InlineData(RetimeMode.FitToFill)]
        [InlineData(RetimeMode.Still)]
        public void TheInvariantSurvivesNonsenseInput(RetimeMode mode)
        {
            var nonsense = new[]
            {
                State(-50, -10, -3, -8),
                State(900, 100, 0, 0),
                State(double.NaN, double.NaN, double.NaN, double.NaN),
                State(0, 0, 0, 0),
                State(599.99, 600, 8, 0.001),
            };

            foreach (var s in nonsense)
                foreach (RetimeField field in new[] { RetimeField.In, RetimeField.Out, RetimeField.Speed, RetimeField.Duration })
                    AssertInvariant(RetimeSolver.Reconcile(s, field, mode, Source), mode, Source);
        }

        [Fact]
        public void AnUnknownSourceLengthDoesNotBoundTheWindowToZero()
        {
            // Clips from older projects learn their real length only once the media opens.
            var s = RetimeSolver.Reconcile(State(0, 30, 1.0, 30), RetimeField.Out, RetimeMode.HoldSource, 0);
            Assert.Equal(30, s.Out, 6);
            Assert.Equal(30, s.Duration, 6);
        }

        [Fact]
        public void LearningTheSourceLengthReClampsTheWindow()
        {
            var s = RetimeSolver.Reconcile(State(0, 500, 1.0, 500), RetimeField.SourceLength, RetimeMode.HoldSource, 20);
            Assert.True(s.Out <= 20 + 1e-9);
            AssertInvariant(s, RetimeMode.HoldSource, 20);
        }

        [Fact]
        public void SwitchingModeDoesNotChangeWhatTheClipCurrentlyDoes()
        {
            // Mode says what moves NEXT time, not what the clip is doing now.
            var before = RetimeSolver.Reconcile(State(0, 60, 2.0, 30), RetimeField.Speed, RetimeMode.HoldSource, Source);
            var after = RetimeSolver.OnModeChanged(before, RetimeMode.FitToFill, Source);

            Assert.Equal(before.In, after.In, 6);
            Assert.Equal(before.Out, after.Out, 6);
            Assert.Equal(before.Duration, after.Duration, 6);
            Assert.Equal(before.Speed, after.Speed, 6);
        }

        [Fact]
        public void EveryModeExplainsItself()
        {
            foreach (RetimeMode m in new[] { RetimeMode.HoldSource, RetimeMode.FitToFill, RetimeMode.Still })
                Assert.False(string.IsNullOrWhiteSpace(RetimeSolver.Explain(m)));
        }
    }
}
