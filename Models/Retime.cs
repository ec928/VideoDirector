using System;

namespace VideoDirector.Models
{
    // Which of the three related values the app holds steady when another is edited.
    public enum RetimeMode
    {
        // In/Out and Speed are yours; Duration follows. Changing speed changes how long the clip
        // runs, which is what physically happens. The default.
        HoldSource,

        // In/Out and Duration are yours; Speed follows. "Fill this ten-second slot with this
        // footage, whatever speed that takes." Unreachable before this existed.
        FitToFill,

        // The constraint is suspended. A frozen frame has no advancing source window, so Duration
        // is simply how long it is held, with nothing to derive it from.
        Still
    }

    // Which value the user just edited.
    public enum RetimeField { In, Out, Speed, Duration, SourceLength }

    // The four numbers, all in seconds except Speed.
    public readonly struct RetimeState
    {
        public readonly double In, Out, Speed, Duration;

        public RetimeState(double inSec, double outSec, double speed, double durationSec)
        {
            In = inSec; Out = outSec; Speed = speed; Duration = durationSec;
        }

        public double Window => Out - In;

        public RetimeState With(double? inSec = null, double? outSec = null,
                                double? speed = null, double? durationSec = null)
            => new RetimeState(inSec ?? In, outSec ?? Out, speed ?? Speed, durationSec ?? Duration);
    }

    // The one place that keeps source window, speed and timeline duration mutually consistent.
    //
    //     Duration = (Out - In) / Speed
    //
    // Only two of the three can be free, which is why editing any of them has to move another.
    // That was always true; what was missing was any statement of WHICH one moves. The rules used
    // to be implicit and asymmetric — editing Duration silently re-trimmed Out, editing Speed
    // silently changed Duration — presented as five independent-looking number boxes.
    //
    // Pure and WinUI-free, so the rules are testable rather than being inferred from behaviour.
    public static class RetimeSolver
    {
        public const double MinClipSeconds = 0.1;
        public const double MinSpeed = 0.01;
        public const double MaxSpeed = 8.0;

        // Which value this mode DERIVES — the one the user does not author, and which the UI
        // should present as a result rather than an input.
        public static RetimeField? DerivedField(RetimeMode mode) => mode switch
        {
            RetimeMode.HoldSource => RetimeField.Duration,
            RetimeMode.FitToFill => RetimeField.Speed,
            _ => null   // Still: nothing is derived, the constraint does not apply
        };

        public static string Explain(RetimeMode mode) => mode switch
        {
            RetimeMode.HoldSource => "Duration = (Out − In) ÷ Speed",
            RetimeMode.FitToFill => "Speed = (Out − In) ÷ Duration",
            _ => "A still is held for its duration; there is no source window to divide."
        };

        // Apply an edit and return a state that satisfies the constraint.
        //
        // `sourceLength` bounds the window; pass 0 or less when it is not known yet, and the
        // window is left unbounded until it is (clips loaded from older projects learn it late).
        public static RetimeState Reconcile(
            RetimeState current, RetimeField changed, RetimeMode mode, double sourceLength)
        {
            double limit = sourceLength > 0 ? sourceLength : double.PositiveInfinity;

            double inSec = current.In;
            double outSec = current.Out;
            double speed = current.Speed;
            double duration = current.Duration;

            // ---- Still: the constraint is suspended --------------------------------------
            if (mode == RetimeMode.Still)
            {
                // A frozen frame only needs an in-point; the out-point is a formality kept just
                // past it so the window stays valid. Pull the in-point back if it sits so close to
                // the end of the source that there is no room for one.
                double stillMax = double.IsInfinity(limit) ? SafeOr(inSec, 0) + MinClipSeconds : limit;
                inSec = Math.Clamp(SafeOr(inSec, 0), 0, Math.Max(0, stillMax - MinClipSeconds));
                outSec = inSec + MinClipSeconds;
                duration = Math.Max(MinClipSeconds, SafeOr(duration, 1));
                return new RetimeState(inSec, outSec, 0, duration);
            }

            speed = Math.Clamp(SafeOr(speed, 1), MinSpeed, MaxSpeed);
            duration = Math.Max(MinClipSeconds, SafeOr(duration, MinClipSeconds));

            // The window is validated on EVERY path, not only when it was the thing edited. A
            // speed or duration edit arriving on top of an already-invalid window would otherwise
            // pass it straight through.
            (inSec, outSec) = NormalizeWindow(inSec, outSec, changed == RetimeField.In, limit);

            switch (changed)
            {
                case RetimeField.In:
                case RetimeField.Out:
                case RetimeField.SourceLength:
                    if (mode == RetimeMode.FitToFill) speed = SpeedFor(outSec - inSec, duration);
                    else duration = DurationFor(outSec - inSec, speed);
                    break;

                case RetimeField.Speed:
                    // In FitToFill speed is derived, so a speed edit means the user wants that
                    // speed: honour it by re-trimming the window, which is the only way to keep
                    // the duration they asked for.
                    if (mode == RetimeMode.FitToFill)
                    {
                        outSec = ClampOut(inSec, inSec + duration * speed, limit);
                        (inSec, outSec) = NormalizeWindow(inSec, outSec, false, limit);
                        speed = SpeedFor(outSec - inSec, duration);
                    }
                    else
                    {
                        duration = DurationFor(outSec - inSec, speed);
                    }
                    break;

                case RetimeField.Duration:
                    if (mode == RetimeMode.FitToFill)
                    {
                        // Window is yours; speed stretches to fill the requested length.
                        speed = SpeedFor(outSec - inSec, duration);
                    }
                    else
                    {
                        // Hold source: pull `duration x speed` of footage from the in-point. If the
                        // source runs out, report the length actually achieved rather than the one
                        // requested — the number on screen must not lie.
                        outSec = ClampOut(inSec, inSec + duration * speed, limit);
                        (inSec, outSec) = NormalizeWindow(inSec, outSec, false, limit);
                        duration = DurationFor(outSec - inSec, speed);
                    }
                    break;
            }

            // Final pass: make the constraint hold EXACTLY.
            //
            // The clamps above can otherwise break it silently. Asking to fill a 0.1s slot with
            // 7.5s of footage wants 75x, which clamps to the 8x maximum — and then the clip no
            // longer runs for the duration the field claims. Whichever value the mode derives is
            // recomputed last, from the values that actually survived clamping, so the number on
            // screen is always the one the clip really has.
            if (mode == RetimeMode.FitToFill)
            {
                speed = SpeedFor(outSec - inSec, duration);
                duration = (outSec - inSec) / speed;
            }
            else
            {
                duration = (outSec - inSec) / speed;
                if (duration < MinClipSeconds)
                {
                    // The window is too short to run for the minimum length at this speed; take
                    // more source rather than report a duration the clip does not have.
                    outSec = ClampOut(inSec, inSec + MinClipSeconds * speed, limit);
                    (inSec, outSec) = NormalizeWindow(inSec, outSec, false, limit);
                    duration = (outSec - inSec) / speed;
                }
            }

            return new RetimeState(inSec, outSec, speed, duration);
        }

        // Switching mode must not change what the clip currently does — it only changes what moves
        // NEXT time something is edited. The one exception is Still, which has no window to speak of.
        public static RetimeState OnModeChanged(RetimeState current, RetimeMode mode, double sourceLength)
            => mode == RetimeMode.Still
                ? Reconcile(current, RetimeField.Duration, mode, sourceLength)
                : Reconcile(current.With(speed: current.Speed <= 0 ? 1 : current.Speed),
                            RetimeField.Out, mode, sourceLength);

        private static double SafeOr(double value, double fallback)
            => double.IsNaN(value) || double.IsInfinity(value) ? fallback : value;

        private static double DurationFor(double window, double speed)
            => Math.Max(MinClipSeconds, window / Math.Max(MinSpeed, speed));

        private static double SpeedFor(double window, double duration)
            => Math.Clamp(window / Math.Max(MinClipSeconds, duration), MinSpeed, MaxSpeed);

        private static double ClampOut(double inSec, double outSec, double limit)
            => Math.Min(double.IsInfinity(limit) ? outSec : limit, outSec);

        // Keep 0 <= In < Out <= limit, at least MinClipSeconds apart. Whichever endpoint the user
        // just moved is honoured; the other yields.
        private static (double inSec, double outSec) NormalizeWindow(
            double inSec, double outSec, bool changedIn, double limit)
        {
            double max = double.IsInfinity(limit) ? Math.Max(inSec, outSec) + MinClipSeconds : limit;
            inSec = Math.Clamp(SafeOr(inSec, 0), 0, max);
            outSec = Math.Clamp(SafeOr(outSec, max), 0, max);

            if (outSec - inSec < MinClipSeconds)
            {
                if (changedIn)
                {
                    outSec = Math.Min(max, inSec + MinClipSeconds);
                    inSec = Math.Max(0, outSec - MinClipSeconds);
                }
                else
                {
                    inSec = Math.Max(0, outSec - MinClipSeconds);
                    outSec = Math.Min(max, inSec + MinClipSeconds);
                }
            }
            return (inSec, outSec);
        }
    }
}
