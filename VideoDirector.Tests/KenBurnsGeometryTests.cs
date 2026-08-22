using System;
using System.Collections.Generic;
using System.Linq;
using VideoDirector.Models;
using Xunit;

namespace VideoDirector.Tests
{
    // Regression cover for the Ken Burns framing chain: fit -> box -> content -> motion -> the
    // region of the source frame that ends up on screen.
    //
    // TWO KINDS OF TEST HERE, deliberately separated:
    //
    //   * INVARIANTS hold for any clip, any pane, any placement. They run against whatever
    //     0-Test6.json currently contains as well as against generated values, so they keep working
    //     when the project is re-saved.
    //   * GOLDEN VALUES pin exact numbers, and therefore run against a FROZEN copy of the project
    //     (Fixtures/kenburns-baseline.json). Editing and re-saving 0-Test6.json must not turn the
    //     suite red - that would train everyone to ignore it.
    //
    // What is NOT asserted: that framing stays inside the source frame. Panning outside it is a
    // legitimate thing to author and the resulting black is correct. The bug was never "black
    // appeared", it was "black appeared where the geometry said the box was covered", which is what
    // Content_always_covers_the_box and the allowance checks pin down.
    public class KenBurnsGeometryTests
    {
        // The pane the reported failure was captured at, so the golden numbers are the ones that
        // were actually on screen.
        private const double PaneW = 2053, PaneH = 818;
        private const double SrcW = 1918, SrcH = 804;

        private const double Aspect = 2.3855721393034828;   // 1918x804, the DCP crop
        // Committed with the suite, so a clean checkout can run it.
        private const string Baseline = "Fixtures/kenburns-baseline.json";

        // A working project under Tests\, which is gitignored scratch - present on a dev machine,
        // absent on a fresh clone. Tests that read it skip rather than fail when it is missing:
        // a suite that goes red because someone has not got a personal scratch file teaches people
        // to ignore red.
        private const string Live = "Live/0-Test6.json";

        private static Clip GoldenClip()
        {
            var clip = ProjectFixture.LoadTracks(Baseline)[0][0];
            Assert.Contains("Mandalorian", clip.Name);
            return clip;
        }

        private sealed record Framing(ClipGeometry.GeoRect Box, double ContentW, double ContentH,
                                      double Scale, double Tx, double Ty,
                                      double AllowX, double AllowY, ClipGeometry.GeoRect Seen);

        // The whole chain, in the order ApplyOverlayBox runs it.
        private static Framing Frame(Clip c, double progress, bool editMode = false,
                                     double paneW = PaneW, double paneH = PaneH)
        {
            var fit = ClipGeometry.Fit(c.SourceAspect, paneW, paneH);
            var box = ClipGeometry.Box(fit.W, fit.H, paneW, paneH,
                                       c.PlacementWidth, c.PlacementHeight,
                                       c.PlacementCenterX, c.PlacementCenterY, editMode);
            var (cw, ch) = ClipGeometry.Content(box.W, box.H, c.SourceAspect);

            double pan = ClipGeometry.PanScale(c.PlacementWidth, c.PlacementHeight);
            ClipGeometry.EvaluateMotion(c.StartMark, c.MidMark, c.EndMark, c.CurveProfile,
                                        progress, fit.W * pan, fit.H * pan,
                                        out double s, out double tx, out double ty);

            var (ax, ay) = ClipGeometry.Allowance(cw, ch, box.W, box.H, s);
            var seen = ClipGeometry.SampledSource(cw, ch, box.W, box.H, s, tx, ty, SrcW, SrcH);
            return new Framing(box, cw, ch, s, tx, ty, ax, ay, seen);
        }

        private static IEnumerable<Clip> AllClips(string file)
            => ProjectFixture.Exists(file)
                ? ProjectFixture.LoadTracks(file).SelectMany(t => t).Where(c => c.SourceAspect > 0)
                : Enumerable.Empty<Clip>();

        // ---------------------------------------------------------------- invariants

        // THE regression. Content sized to the box leaves zero surplus, and every pan then runs
        // straight off the edge - that is the shape the failure took on screen.
        [Fact]
        public void Content_always_covers_the_box()
        {
            foreach (var aspect in new[] { 0.5, 1.0, 16 / 9.0, Aspect, 4.0 })
            foreach (var pw in new[] { 0.1, 0.2848, 0.5, 1.0 })
            foreach (var ph in new[] { 0.1, 0.5, 0.8445, 1.0 })
            {
                var fit = ClipGeometry.Fit(aspect, PaneW, PaneH);
                var box = ClipGeometry.Box(fit.W, fit.H, PaneW, PaneH, pw, ph, 0.5, 0.5, false);
                var (cw, ch) = ClipGeometry.Content(box.W, box.H, aspect);

                Assert.True(cw >= box.W - 1e-6, $"aspect {aspect} {pw}x{ph}: content {cw} < box {box.W}");
                Assert.True(ch >= box.H - 1e-6, $"aspect {aspect} {pw}x{ph}: content {ch} < box {box.H}");
                Assert.Equal(aspect, cw / ch, 6);
            }
        }

        // The coupling that makes a mark mean the same thing in the editor and at playback: the
        // content is the fit scaled by exactly PanScale. If these two ever drift the framing lands
        // somewhere other than where it was authored - which is what max(w,h) replaced a per-axis
        // scale to fix.
        [Fact]
        public void PanScale_equals_the_content_to_fit_ratio()
        {
            foreach (var pw in new[] { 0.1, 0.2848, 0.5, 0.9, 1.0 })
            foreach (var ph in new[] { 0.1, 0.3, 0.8445, 1.0 })
            {
                var fit = ClipGeometry.Fit(Aspect, PaneW, PaneH);
                var box = ClipGeometry.Box(fit.W, fit.H, PaneW, PaneH, pw, ph, 0.5, 0.5, false);
                var (cw, ch) = ClipGeometry.Content(box.W, box.H, Aspect);

                double expected = ClipGeometry.PanScale(pw, ph);
                Assert.Equal(expected, cw / fit.W, 9);
                Assert.Equal(expected, ch / fit.H, 9);
            }
        }

        [Fact]
        public void Fit_is_contained_and_keeps_source_aspect()
        {
            foreach (var (pw, ph) in new[] { (2053.0, 818.0), (1280.0, 720.0), (600.0, 1000.0) })
            {
                var fit = ClipGeometry.Fit(Aspect, pw, ph);
                Assert.True(fit.W <= pw + 1e-6 && fit.H <= ph + 1e-6, "fit must stay inside the pane");
                Assert.Equal(Aspect, fit.W / fit.H, 9);
            }
        }

        [Fact]
        public void Edit_mode_frames_against_the_whole_fit_with_no_surplus()
        {
            var f = Frame(GoldenClip(), 0, editMode: true);
            var fit = ClipGeometry.Fit(Aspect, PaneW, PaneH);

            Assert.Equal(fit.W, f.Box.W, 6);
            Assert.Equal(fit.H, f.Box.H, 6);
            Assert.Equal(f.Box.W, f.ContentW, 6);   // box IS the frame in Edit: no surplus by design
            Assert.Equal(f.Box.H, f.ContentH, 6);
        }

        // Marks are fractions of the fit, not pane pixels, so resizing the window must not reframe.
        [Fact]
        public void Framing_is_independent_of_pane_size()
        {
            var c = GoldenClip();
            foreach (var p in new[] { 0.0, 0.25, 0.5, 0.75, 0.99 })
            {
                var big = Frame(c, p);
                var small = Frame(c, p, paneW: PaneW / 2, paneH: PaneH / 2);

                Assert.Equal(big.Seen.X, small.Seen.X, 3);
                Assert.Equal(big.Seen.Y, small.Seen.Y, 3);
                Assert.Equal(big.Seen.W, small.Seen.W, 3);
                Assert.Equal(big.Seen.H, small.Seen.H, 3);
            }
        }

        // The curve is halved at the Mid mark. A sign or indexing slip there shows up as a jump.
        [Fact]
        public void Motion_is_continuous_across_the_mid_split()
        {
            foreach (var c in AllClips(Live).Concat(AllClips(Baseline)))
            {
                var before = Frame(c, 0.4999);
                var after = Frame(c, 0.5001);

                Assert.InRange(Math.Abs(after.Tx - before.Tx), 0, 2);
                Assert.InRange(Math.Abs(after.Ty - before.Ty), 0, 2);
                Assert.InRange(Math.Abs(after.Scale - before.Scale), 0, 0.01);
            }
        }

        // A deliberate zoom-out cannot cover the box, and the geometry must SAY so rather than
        // reporting slack it does not have. Negative allowance is how "black here is correct" is
        // distinguished from "black here is a bug".
        [Fact]
        public void Zoom_below_one_reports_negative_slack()
        {
            var fit = ClipGeometry.Fit(Aspect, PaneW, PaneH);
            var box = ClipGeometry.Box(fit.W, fit.H, PaneW, PaneH, 0.2848, 0.8445, 0.5, 0.5, false);
            var (cw, ch) = ClipGeometry.Content(box.W, box.H, Aspect);

            var (_, ayLow) = ClipGeometry.Allowance(cw, ch, box.W, box.H, 0.75);
            var (_, ayOne) = ClipGeometry.Allowance(cw, ch, box.W, box.H, 1.0);
            var (_, ayHigh) = ClipGeometry.Allowance(cw, ch, box.W, box.H, 1.5);

            Assert.True(ayLow < 0);
            Assert.Equal(0, ayOne, 6);      // a 0.844-tall box has exactly no vertical slack at 1x
            Assert.True(ayHigh > 0);
        }

        [Fact]
        public void Sampled_region_shrinks_as_zoom_increases()
        {
            var c = GoldenClip();
            var fit = ClipGeometry.Fit(Aspect, PaneW, PaneH);
            var box = ClipGeometry.Box(fit.W, fit.H, PaneW, PaneH,
                                       c.PlacementWidth, c.PlacementHeight, 0.5, 0.5, false);
            var (cw, ch) = ClipGeometry.Content(box.W, box.H, Aspect);

            double prev = double.MaxValue;
            foreach (var s in new[] { 1.0, 1.5, 2.0, 4.0 })
            {
                var seen = ClipGeometry.SampledSource(cw, ch, box.W, box.H, s, 0, 0, SrcW, SrcH);
                Assert.True(seen.W < prev, $"zooming to {s}x must sample less source, not more");
                prev = seen.W;
            }
        }

        // Wheel-zoom of a framing rectangle must resize it about its own centre.
        //
        // A mark's offset is stored relative to its own zoom - the sampled centre comes out of
        // tx/scale - so changing scale alone slides the framing, by an amount proportional to how
        // far off-centre it already sat. Left-of-frame crept left, right-of-frame crept right.
        // OnSelectedMarkWheel scales X and Y by the same ratio as the scale to hold tx/scale
        // constant; this is that invariant.
        [Fact]
        public void Scaling_a_mark_with_its_offset_keeps_the_sampled_centre_fixed()
        {
            var c = GoldenClip();
            var fit = ClipGeometry.Fit(Aspect, PaneW, PaneH);
            var box = ClipGeometry.Box(fit.W, fit.H, PaneW, PaneH,
                                       c.PlacementWidth, c.PlacementHeight,
                                       c.PlacementCenterX, c.PlacementCenterY, false);
            var (cw, ch) = ClipGeometry.Content(box.W, box.H, Aspect);
            double pan = ClipGeometry.PanScale(c.PlacementWidth, c.PlacementHeight);

            foreach (var (x, y) in new[] { (0.30, 0.12), (-0.25, 0.20), (0.0, 0.0), (0.45, -0.30) })
            foreach (var scale in new[] { 0.8, 1.0, 1.6 })
            foreach (var ratio in new[] { 1.08, 1.0 / 1.08, 2.0, 0.5 })
            {
                var before = ClipGeometry.SampledSource(cw, ch, box.W, box.H,
                                                        scale, x * fit.W * pan, y * fit.H * pan,
                                                        SrcW, SrcH);

                // The wheel handler's rule: offset tracks the scale.
                double s2 = scale * ratio;
                var after = ClipGeometry.SampledSource(cw, ch, box.W, box.H,
                                                       s2, x * ratio * fit.W * pan, y * ratio * fit.H * pan,
                                                       SrcW, SrcH);

                double cx0 = before.X + before.W / 2, cy0 = before.Y + before.H / 2;
                double cx1 = after.X + after.W / 2, cy1 = after.Y + after.H / 2;

                Assert.Equal(cx0, cx1, 6);
                Assert.Equal(cy0, cy1, 6);

                // and it really did resize - otherwise the test would pass on a no-op
                Assert.True(Math.Abs(after.W - before.W) > 1e-9 || Math.Abs(ratio - 1.0) < 1e-9);
            }
        }

        // The bug it replaced: scaling WITHOUT tracking the offset moves an off-centre rectangle.
        [Fact]
        public void Scaling_a_mark_alone_would_move_an_off_centre_framing()
        {
            var c = GoldenClip();
            var fit = ClipGeometry.Fit(Aspect, PaneW, PaneH);
            var box = ClipGeometry.Box(fit.W, fit.H, PaneW, PaneH,
                                       c.PlacementWidth, c.PlacementHeight,
                                       c.PlacementCenterX, c.PlacementCenterY, false);
            var (cw, ch) = ClipGeometry.Content(box.W, box.H, Aspect);
            double pan = ClipGeometry.PanScale(c.PlacementWidth, c.PlacementHeight);

            double tx = 0.30 * fit.W * pan;
            var before = ClipGeometry.SampledSource(cw, ch, box.W, box.H, 1.0, tx, 0, SrcW, SrcH);
            var after = ClipGeometry.SampledSource(cw, ch, box.W, box.H, 1.6, tx, 0, SrcW, SrcH);

            double cx0 = before.X + before.W / 2, cx1 = after.X + after.W / 2;
            Assert.True(Math.Abs(cx1 - cx0) > 1.0,
                "the old behaviour must actually drift, or this suite is not pinning anything");
        }

        // ================= which clip is on screen =================
        //
        // Real numbers, from Tests/0-Test6.json. Selecting the image showed the previous clip; the
        // model genuinely had that clip active, which is why the HUD, the canvas and the track
        // manager all agreed with each other and all disagreed with the inspector.
        private const long Clip1Start = 0L,          Clip1Dur = 102006781L;   // video
        private const long Clip2Start = 102006781L,  Clip2Dur = 100000000L;   // freeze frame
        private const long ImageStart = 202006780L,  ImageDur = 100000000L;   // the .jpg

        [Fact]
        public void The_project_really_did_overlap_by_one_tick()
        {
            // 100 nanoseconds, lost to a double-seconds round trip through TimeSpan.FromSeconds.
            Assert.Equal(1L, (Clip2Start + Clip2Dur) - ImageStart);
        }

        [Fact]
        public void At_a_clip_start_the_clip_starting_there_wins_even_when_windows_overlap()
        {
            long t = ImageStart;   // what SelectClip sets the playhead to

            // Both cover the instant. That is the whole bug: it is not that the resolver was asked
            // the wrong question, it is that two clips were legitimately valid answers.
            Assert.True(ClipGeometry.Covers(Clip2Start, Clip2Dur, t));
            Assert.True(ClipGeometry.Covers(ImageStart, ImageDur, t));

            // The later start is the one the playhead has just entered, so it must win —
            // regardless of collection order, which is what the old resolver keyed off.
            Assert.True(ClipGeometry.Supersedes(ImageStart, Clip2Start));
            Assert.False(ClipGeometry.Supersedes(Clip2Start, ImageStart));
        }

        [Fact]
        public void Resolution_does_not_depend_on_collection_order()
        {
            var clips = new (long start, long dur, string name)[]
            {
                (Clip1Start, Clip1Dur, "video"),
                (Clip2Start, Clip2Dur, "freeze"),
                (ImageStart, ImageDur, "image"),
            };

            static string Resolve((long start, long dur, string name)[] set, long t)
            {
                string best = null; long bestStart = 0;
                foreach (var c in set)
                {
                    if (!ClipGeometry.Covers(c.start, c.dur, t)) continue;
                    if (best == null || ClipGeometry.Supersedes(c.start, bestStart))
                    { best = c.name; bestStart = c.start; }
                }
                return best;
            }

            Assert.Equal("image", Resolve(clips, ImageStart));

            // Reversed, and shuffled: same answer. The old rule returned whichever came first.
            Assert.Equal("image", Resolve(clips.Reverse().ToArray(), ImageStart));
            Assert.Equal("image", Resolve(new[] { clips[1], clips[2], clips[0] }, ImageStart));
        }

        [Fact]
        public void Half_open_windows_keep_a_clean_join_unambiguous()
        {
            // Laid exactly end to end — the state ResolveOverlaps now produces in ticks — only one
            // clip covers the join.
            long aStart = 0, aDur = 102006781;
            long bStart = aStart + aDur, bDur = 100000000;

            Assert.False(ClipGeometry.Covers(aStart, aDur, bStart));
            Assert.True(ClipGeometry.Covers(bStart, bDur, bStart));

            // and the instant before the join belongs to the first clip alone
            Assert.True(ClipGeometry.Covers(aStart, aDur, bStart - 1));
            Assert.False(ClipGeometry.Covers(bStart, bDur, bStart - 1));
        }

        // ---------------------------------------------------------------- golden values
        //
        // Exact framing for the frozen clip: 2.39:1 source in a 0.285 x 0.844 box. These are the
        // numbers the compositor produced once the layout-clip defect was fixed; any change to the
        // maths moves them.

        [Fact]
        public void Golden_geometry_matches_the_frozen_project()
        {
            var f = Frame(GoldenClip(), 0);

            Assert.Equal(555.7355, f.Box.W, 3);
            Assert.Equal(690.7834, f.Box.H, 3);
            Assert.Equal(1647.9137, f.ContentW, 3);
            Assert.Equal(690.7834, f.ContentH, 3);
        }

        [Theory]
        //          progress  scale      tx       ty       allowX   allowY
        [InlineData(0.000,    1.0692,   -604.9,  -212.1,   603.1,    23.9)]
        [InlineData(0.250,    1.0052,    -53.6,   -68.3,   550.4,     1.8)]
        [InlineData(0.500,    0.9412,    497.7,    75.5,   497.7,   -20.3)]
        [InlineData(0.750,    1.2218,    608.4,   106.7,   728.8,    76.6)]
        public void Golden_motion_matches_the_frozen_project(double progress, double scale,
                                                             double tx, double ty,
                                                             double allowX, double allowY)
        {
            var f = Frame(GoldenClip(), progress);

            Assert.Equal(scale, f.Scale, 3);
            Assert.Equal(tx, f.Tx, 1);
            Assert.Equal(ty, f.Ty, 1);
            Assert.Equal(allowX, f.AllowX, 1);
            Assert.Equal(allowY, f.AllowY, 1);
        }

        // THE failure signature, as a property rather than a fact about one clip.
        //
        // The defect on screen was a framing that the geometry said was covered rendering as a
        // ~50px strip against black. So: whenever a pan is within its allowance, the sampled region
        // MUST lie inside the source frame. If that ever stops holding, "inside the allowance" has
        // stopped meaning "fully covered" and the readout is lying again.
        //
        // Asserted across the placement shapes and zooms that matter, not just the one clip - the
        // frozen project's Mid mark happens to sit a fraction OUTSIDE its allowance, which is a
        // legitimate authored framing and exactly the sort of data-dependent detail a regression
        // test must not encode.
        [Fact]
        public void Framing_within_its_allowance_is_always_fully_covered()
        {
            foreach (var (pw, ph) in new[] { (0.2848, 0.8445), (1.0, 1.0), (0.9, 0.2), (0.3, 0.3) })
            foreach (var scale in new[] { 1.0, 1.25, 2.0, 3.0 })
            {
                var fit = ClipGeometry.Fit(Aspect, PaneW, PaneH);
                var box = ClipGeometry.Box(fit.W, fit.H, PaneW, PaneH, pw, ph, 0.5, 0.5, false);
                var (cw, ch) = ClipGeometry.Content(box.W, box.H, Aspect);
                var (ax, ay) = ClipGeometry.Allowance(cw, ch, box.W, box.H, scale);
                if (ax < 0 || ay < 0) continue;   // zoom-out: black is correct, nothing to prove

                foreach (var fx in new[] { -1.0, -0.5, 0.0, 0.5, 1.0 })
                foreach (var fy in new[] { -1.0, 0.0, 1.0 })
                {
                    var seen = ClipGeometry.SampledSource(cw, ch, box.W, box.H,
                                                          scale, ax * fx, ay * fy, SrcW, SrcH);

                    Assert.True(seen.X >= -0.01,
                        $"{pw}x{ph} @{scale}x pan {ax * fx:F1}: samples {seen.X:F1}px left of frame");
                    Assert.True(seen.Right <= SrcW + 0.01,
                        $"{pw}x{ph} @{scale}x pan {ax * fx:F1}: samples {seen.Right - SrcW:F1}px past right");
                    Assert.True(seen.Y >= -0.01,
                        $"{pw}x{ph} @{scale}x pan {ay * fy:F1}: samples {seen.Y:F1}px above frame");
                    Assert.True(seen.Bottom <= SrcH + 0.01,
                        $"{pw}x{ph} @{scale}x pan {ay * fy:F1}: samples {seen.Bottom - SrcH:F1}px below");
                }
            }
        }
    }
}
