using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using VideoDirector.ViewModels;
using Microsoft.UI.Dispatching;

// VideoPlaybackEngine - where a slot's picture sits: the placement box, the unconstrained surface parent, and the border rectangles including the visible-region test.

namespace VideoDirector.Models
{
    public partial class VideoPlaybackEngine
    {
        // Positions, sizes and clips the placement box (the overlay grid) from the clip's
        // placement fields, shaped to the video aspect. In edit mode the box fills the screen
        // (placement bypassed) so content is framed full-size, identical to Track 1; at
        // playback it is the corner PiP. The grid clips its content so zoomed-in framing can't
        // spill outside the box.
        // Returns TRUE when the box was actually established. It reads _overlayAspect[slot] alone,
        // which is only filled from an OPEN decoder (CacheOverlayAspect), while the slot is marked
        // active the instant activation starts. In that window this bailed and left the grid at the
        // previous clip's size — or unsized — yet ApplyOverlayTransform went straight on to write a
        // full zoom/pan onto it, because its own fit came from op.SourceAspect and succeeded. A
        // transform computed for one rectangle applied to another: picture flung off the box, black
        // where the framing was well inside the frame. Hence both the shared resolver (SourceAspect
        // is in the project file, so the box can be built before any decoder opens) and the bool,
        // so a caller can never transform geometry that was never laid out.
        private bool ApplyOverlayBox(int slot, CinematicOperation overlay, bool editMode)
        {
            // Sound has no rectangle. Laying one out would size a surface that draws nothing and,
            // worse, would report a box that the border and hit-testing then believe in.
            if (overlay != null && overlay.IsAudioOnly) return false;

            var grid = _playerControl.OverlayVisuals[slot].Grid;
            double aspect = AspectOf(overlay, slot);
            double vpW = _playerControl.CanvasWidth;
            double vpH = _playerControl.CanvasHeight;
            if (aspect <= 0 || vpW <= 0 || vpH <= 0) return false;

            // Video fit to viewport (contained), preserving aspect — the "scale 1" reference.
            double fitW, fitH;
            if (aspect >= vpW / vpH) { fitW = vpW; fitH = vpW / aspect; }
            else { fitH = vpH; fitW = vpH * aspect; }

            // Edit mode: box fills the video fit (framing at full size). Arrange: independent
            // width/height so the PiP can be reshaped; the video crop-fills (UniformToFill).
            // THE ANCHOR RULE REACHES A LOADED PROJECT TOO. Placement comes straight off the JSON
            // into the properties, bypassing every writer that applies ContactCentre, so a project
            // saved before the rule - or hand-edited - could open with a clip clean off the canvas
            // and no way to reach it. Correcting it here is the one place that sees a clip after the
            // canvas size is known.
            //
            // Self-limiting rather than a per-frame write: the comparison is false once the value is
            // inside the rule, so this fires at most once per clip and never again.
            double anchoredX = ClipGeometry.ContactCentre(
                overlay.PlacementCenterX, fitW, overlay.PlacementWidth, vpW);
            double anchoredY = ClipGeometry.ContactCentre(
                overlay.PlacementCenterY, fitH, overlay.PlacementHeight, vpH);
            if (Math.Abs(anchoredX - overlay.PlacementCenterX) > 1e-9) overlay.PlacementCenterX = anchoredX;
            if (Math.Abs(anchoredY - overlay.PlacementCenterY) > 1e-9) overlay.PlacementCenterY = anchoredY;

            var box = ClipGeometry.Box(fitW, fitH, vpW, vpH,
                                       overlay.PlacementWidth, overlay.PlacementHeight,
                                       overlay.PlacementCenterX, overlay.PlacementCenterY, editMode);
            double boxW = box.W, boxH = box.H, left = box.X, top = box.Y;

            // NOTE (§7A): this method does GEOMETRY ONLY. Deciding still-vs-video used to live here
            // and silently never fired — the render mode is now set explicitly by SetOverlayRender
            // at each state transition, never as a side effect of laying out a box.

            if (grid.Margin.Left != left || grid.Margin.Top != top)
            {
                grid.Margin = new Microsoft.UI.Xaml.Thickness(left, top, 0, 0);
            }
            // Only resize + reallocate the BOX when its dimensions actually change (avoids
            // per-frame allocation of the clip geometry during playback).
            if (grid.Width != boxW || grid.Height != boxH || _overlayContentAspect[slot] != aspect)
            {
                grid.Width = boxW;
                grid.Height = boxH;
                _overlayContentAspect[slot] = aspect;
            }

            // IN EDIT THE PICTURE IS NEVER CUT (§2.C rule 1). It is the working material there, and a
            // crop that hides everything outside the frame cannot be used to choose a framing.
            // Nothing spills onto anything: EnterEditMode isolates the clip into slot 0 and releases
            // every other slot. What stops the picture being lost is the boundary in ClampFraming,
            // not this crop - the crop was doing two jobs badly, cutting the picture AND being the
            // only thing that limited it.
            //
            // Everywhere else the crop is load-bearing: it is what turns a covering surface into a
            // CROP, and it keeps a 30% PiP inside its own box instead of drawing its whole frame
            // across the canvas over other tracks.
            //
            // Driven by the mode and NOT by the size guard above: switching modes need not change
            // the box, and a stale crop would make this work only sometimes. Both branches compare
            // before assigning, so a steady state allocates nothing - this runs every frame from
            // CompositionTarget.Rendering, where unconditional writes retrigger measure up the tree.
            if (editMode)
            {
                if (grid.Clip != null) grid.Clip = null;
            }
            else if (!(grid.Clip is Microsoft.UI.Xaml.Media.RectangleGeometry rg
                       && rg.Rect.Width == boxW && rg.Rect.Height == boxH))
            {
                grid.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
                {
                    Rect = new Windows.Foundation.Rect(0, 0, boxW, boxH)
                };
            }

            // ---- The surfaces are sized to the FRAME, not to the box. ----
            //
            // UniformToFill into a BOX-sized element crops the frame to the box and DISCARDS
            // the surplus: MediaPlayerElement renders into a swapchain the size of the element,
            // and Image applies a layout clip on overflow. The RenderTransform then pans and
            // zooms that crop — so there is no picture outside it to bring back, and the box
            // behaves as if it were the whole video. That is the bug, and it hits video clips
            // and stills alike because both crop before they transform.
            //
            // Sized to the frame's drawn extent instead, the element's aspect equals the
            // source's, so UniformToFill discards nothing and the whole frame stays available
            // to the transform. The grid's clip above still crops what you see, so the neutral
            // framing is unchanged: at scale 1 with no pan, a centred frame-sized element shows
            // exactly the same crop it always did.
            //
            // THIS RUNS EVERY CALL, deliberately. It used to sit inside the box guard above, which
            // made it a ONE-SHOT: once the grid had its size that branch never ran again, so any
            // later loss of a surface's Width was permanent. The clip then rendered with content
            // exactly the size of its box - zero surplus - and the first pan of the Ken Burns ramp
            // slid it almost entirely out of frame: full picture at t=0, a ~50px strip against a
            // wall of black by the Mid mark. Re-asserting makes the size a function of the current
            // geometry instead of a state that can be silently dropped.
            //
            // The write is delta-guarded, which is what keeps the old warning here honest: this
            // method runs inside the per-frame render handler, and unconditionally writing layout
            // properties on a child from there retriggers measure up the tree and can become a
            // layout loop that starves the UI thread (playback freezes, scrubber goes dead).
            // contentW/H derive deterministically from boxW/boxH and the aspect, so once the box is
            // stable every comparison is false and not one property is touched.
            (double contentW, double contentH) = ClipGeometry.Content(boxW, boxH, aspect);

            // Centre the oversized surface on the box by hand. It used to rely on
            // HorizontalAlignment="Center" inside the box-sized grid, and that is precisely what
            // broke: WinUI hands an overflowing child a LAYOUT CLIP at the parent's size, and
            // RenderTransform is applied AFTER that clip. So the frame was cropped to the 556px box
            // first and only then panned - at pan 518 that leaves 556-518 = 38px of picture and a
            // wall of black, which is exactly what the Mid mark rendered. The surplus the whole
            // frame-sizing scheme exists to preserve was being thrown away one step before it was
            // needed. Inside a Canvas nothing constrains the child, so no layout clip is issued and
            // the transform pans a surface that still holds the entire frame; grid.Clip above then
            // crops at RENDER time, which is a mask rather than a constraint.
            // Publish what the framing may move within, so the interactive pan and wheel can hold
            // the picture inside the box instead of letting it slide off the clipped edge.
            if (editMode)
            {
                _playerControl.FramingContentW = contentW;
                _playerControl.FramingContentH = contentH;
                _playerControl.FramingBoxW = boxW;
                _playerControl.FramingBoxH = boxH;
            }

            double padX = (contentW - boxW) / 2;
            double padY = (contentH - boxH) / 2;

            var surfaces = _playerControl.OverlayVisuals[slot];

            // The defect this area exists to prevent was NOT a maths error - the numbers were right
            // while WinUI layout-clipped the surface to its parent before the RenderTransform ran,
            // discarding the surplus one step before the pan could use it. Arithmetic tests cannot
            // see that, so it is guarded structurally: the surfaces must live somewhere that does
            // not constrain them. A Canvas does not; a sized Grid does.
            System.Diagnostics.Debug.Assert(
                surfaces.Video == null || surfaces.Video.Parent is Microsoft.UI.Xaml.Controls.Canvas,
                "Video surface must sit in a Canvas: a sizing parent layout-clips it before the "
                + "RenderTransform, silently discarding the pan surplus.");
            System.Diagnostics.Debug.Assert(
                surfaces.Still == null || surfaces.Still.Parent is Microsoft.UI.Xaml.Controls.Canvas,
                "Still surface must sit in a Canvas (see above).");

            PlaceSurface(surfaces.Video, -padX, -padY, contentW, contentH);
            PlaceSurface(surfaces.Still, -padX, -padY, contentW, contentH);

            // BORDERS ARE DRAWN IN BorderHost, above every track picture.
            //
            // Not as the Grid's own border: Grid.BorderBrush renders beneath the grid's children,
            // so the video surface covered it and only slivers survived.
            //
            // Not as a child of the clip either: that puts it under the video surface of every
            // HIGHER track, and a shape beneath a video surface is erased rather than blended. A
            // clip at 35% opacity on a higher track wiped the border out completely rather than
            // dimming it to 65%.
            //
            // The cost of being above everything is that stacking can no longer say "something
            // covers this", so that is decided here instead.
            grid.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);

            // The canvas outline is an ARRANGE thing and is hidden in Edit.
            //
            // Two attempts were made to give it a job there - outlining the clip box, then the
            // transformed frame - and both put a rectangle somewhere unrelated to anything on
            // screen. Edit already has the reference it needs: the Start, Mid and End rectangles
            // ARE the framing, and a fourth outline competing with them is noise at best.
            _playerControl.SetCanvasEdgeVisible(!editMode);

            if (overlay.BorderType == BorderType.None || editMode)
            {
                grid.CornerRadius = new Microsoft.UI.Xaml.CornerRadius(0);
                HideBorderRect(slot);
            }
            else
            {
                // The border fades with the picture it frames rather than floating at full
                // strength over a clip that has been faded down.
                double borderOpacity = overlay.IsVideoHidden ? 0.0 : overlay.Opacity;
                var c = overlay.BorderColor;
                if (overlay.BorderType == BorderType.Soft)
                {
                    // The rounded corner stays on the grid as well, so the PICTURE is rounded
                    // too rather than a rounded outline sitting on a square image. Half alpha
                    // is what makes Soft soft. The outline itself is four edges: a single
                    // rounded stroke cannot express the L left by a partial cover.
                    grid.CornerRadius = new Microsoft.UI.Xaml.CornerRadius(16);
                    c = Windows.UI.Color.FromArgb(128, c.R, c.G, c.B);
                }
                else
                    grid.CornerRadius = new Microsoft.UI.Xaml.CornerRadius(0);

                ShowBorderEdges(slot, c, overlay.BorderThickness,
                                left, top, boxW, boxH, borderOpacity);
            }

            // THE FRAME, from the same call site and the same values as the border above, so the
            // two cannot disagree about where a clip is or what covers it. ShowFrameRect sets
            // geometry AND visibility together - splitting those across two code paths is how a
            // frame ends up positioned correctly and collapsed, or visible and stale.
            if (editMode || !ShowClipFrames)
                HideFrameRect(slot);
            else
                ShowFrameRect(slot, left, top, boxW, boxH);

            return true;
        }

        // Size AND position a content surface inside its Canvas, writing only on a real change.
        //
        // NaN-safe: an unset Width reads back as NaN and every comparison against NaN is false, so
        // the explicit IsNaN test is what makes a surface that has LOST its size get re-sized on the
        // next pass instead of being quietly left alone.
        //
        // Delta-guarding matters here: this runs inside the per-frame render handler, and
        // unconditionally writing layout properties from there retriggers measure up the tree and
        // can become a layout loop that starves the UI thread. Every value below derives
        // deterministically from the box and the aspect, so once the box is stable nothing is
        // written at all.
        private static void PlaceSurface(Microsoft.UI.Xaml.FrameworkElement el, double left, double top,
                                         double w, double h)
        {
            if (el == null) return;
            if (double.IsNaN(el.Width) || Math.Abs(el.Width - w) > 0.5) el.Width = w;
            if (double.IsNaN(el.Height) || Math.Abs(el.Height - h) > 0.5) el.Height = h;

            if (Math.Abs(Microsoft.UI.Xaml.Controls.Canvas.GetLeft(el) - left) > 0.5)
                Microsoft.UI.Xaml.Controls.Canvas.SetLeft(el, left);
            if (Math.Abs(Microsoft.UI.Xaml.Controls.Canvas.GetTop(el) - top) > 0.5)
                Microsoft.UI.Xaml.Controls.Canvas.SetTop(el, top);
        }

        private Views.OverlayVisual GetOverlayVisual(int slot)
        {
            if (_playerControl == null) return null;
            var visuals = _playerControl.OverlayVisuals;
            if (visuals == null || slot < 0 || slot >= visuals.Length) return null;
            return visuals[slot];
        }

        // The editing frame for a clip - dashed outline plus its T1..T6 badge. Lives in FrameHost,
        // above the borders and every picture, for the same reason the border does.
        private Microsoft.UI.Xaml.Controls.Grid GetFrameRect(int slot)
        {
            if (_playerControl == null) return null;
            var visuals = _playerControl.OverlayVisuals;
            if (visuals == null || slot < 0 || slot >= visuals.Length) return null;
            return visuals[slot]?.Frame;
        }

        private void HideFrameRect(int slot)
        {
            var frame = GetFrameRect(slot);
            if (frame != null && frame.Visibility != Microsoft.UI.Xaml.Visibility.Collapsed)
                frame.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        }

        /// <summary>Place and show a clip's editing frame. Geometry and visibility set together.</summary>
        /// <remarks>
        /// DRIVEN EXACTLY LIKE THE BORDER, and from the same call site, because the frame has the
        /// same problem: it sits above every picture, so it has no parent to inherit the box from and
        /// stacking can no longer say "something covers this". Splitting those two jobs across two
        /// code paths is how a frame ends up correctly positioned and invisible, or visible and stale.
        ///
        /// It takes the SAME visible region the border does, so a fully opaque clip on a higher track
        /// hides the frame exactly as it hides the border. 100% opacity behaves like 100% opacity,
        /// and the two pieces of chrome cannot disagree about what is covered.
        ///
        /// Writes are delta-guarded: this runs from the per-frame render path.
        /// </remarks>
        private void ShowFrameRect(int slot, double left, double top, double w, double h)
        {
            var frame = GetFrameRect(slot);
            if (frame == null) return;
            if (w <= 0 || h <= 0) { HideFrameRect(slot); return; }

            // Trim to what a higher opaque clip leaves visible, in the frame's own coordinates.
            // A thickness of 1 is used to give the edges area so SubtractStrip processes them.
            var topE = new ClipGeometry.GeoRect(left, top, w, 1);
            var botE = new ClipGeometry.GeoRect(left, top + h, w, 1);
            var leftE = new ClipGeometry.GeoRect(left, top, 1, h);
            var rightE = new ClipGeometry.GeoRect(left + w, top, 1, h);

            var segs = new List<ClipGeometry.GeoRect>(8);
            if (MainWindow.Instance != null && MainWindow.Instance.AlwaysShowFullFrames)
            {
                segs.Add(topE);
                segs.Add(botE);
                segs.Add(leftE);
                segs.Add(rightE);
            }
            else
            {
                OccludeStrip(slot, topE, segs);
                OccludeStrip(slot, botE, segs);
                OccludeStrip(slot, leftE, segs);
                OccludeStrip(slot, rightE, segs);
            }

            if (segs.Count == 0) { HideFrameRect(slot); return; }

            if (frame.Visibility != Microsoft.UI.Xaml.Visibility.Visible)
                frame.Visibility = Microsoft.UI.Xaml.Visibility.Visible;

            if (frame.Width != w) frame.Width = w;
            if (frame.Height != h) frame.Height = h;
            if (Microsoft.UI.Xaml.Controls.Canvas.GetLeft(frame) != left)
                Microsoft.UI.Xaml.Controls.Canvas.SetLeft(frame, left);
            if (Microsoft.UI.Xaml.Controls.Canvas.GetTop(frame) != top)
                Microsoft.UI.Xaml.Controls.Canvas.SetTop(frame, top);

            if (frame.Children.Count > 0 && frame.Children[0] is Microsoft.UI.Xaml.Shapes.Path path)
            {
                var group = new Microsoft.UI.Xaml.Media.GeometryGroup();
                foreach (var s in segs)
                {
                    double sx = s.X - left;
                    double sy = s.Y - top;
                    if (s.W > s.H) // Horizontal segment
                    {
                        group.Children.Add(new Microsoft.UI.Xaml.Media.LineGeometry {
                            StartPoint = new Windows.Foundation.Point(sx, sy),
                            EndPoint = new Windows.Foundation.Point(sx + s.W, sy)
                        });
                    }
                    else // Vertical segment
                    {
                        group.Children.Add(new Microsoft.UI.Xaml.Media.LineGeometry {
                            StartPoint = new Windows.Foundation.Point(sx, sy),
                            EndPoint = new Windows.Foundation.Point(sx, sy + s.H)
                        });
                    }
                }
                path.Data = group;
            }

            // Hide the badge if the top-left corner is occluded
            if (frame.Children.Count > 1 && frame.Children[1] is Microsoft.UI.Xaml.UIElement badge)
            {
                bool badgeVisible = false;
                foreach (var s in segs)
                {
                    if (Math.Abs(s.X - left) < 0.5 && Math.Abs(s.Y - top) < 0.5)
                    {
                        badgeVisible = true;
                        break;
                    }
                }
                badge.Visibility = badgeVisible ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
            }
        }

        // The box a slot's active clip occupies in the pane. Same call the clip's own layout uses.
        private bool TryGetSlotBox(int slot, out ClipGeometry.GeoRect box)
        {
            box = default;
            if (slot < 0 || slot >= MaxOverlayTracks) return false;

            var op = _activeOverlay[slot];
            if (op == null) return false;

            double aspect = AspectOf(op, slot);
            double vpW = _playerControl.CanvasWidth, vpH = _playerControl.CanvasHeight;
            if (aspect <= 0 || vpW <= 0 || vpH <= 0) return false;

            var fit = ClipGeometry.Fit(aspect, vpW, vpH);
            box = ClipGeometry.Box(fit.W, fit.H, vpW, vpH,
                                   op.PlacementWidth, op.PlacementHeight,
                                   op.PlacementCenterX, op.PlacementCenterY, editMode: false);
            return true;
        }

        private void HideBorderRect(int slot)
        {
            var v = GetOverlayVisual(slot);
            if (v?.Border != null && v.Border.Visibility != Microsoft.UI.Xaml.Visibility.Collapsed)
                v.Border.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        }

        // Four filled edge strips, not one stroked outline. UIElement.Clip is a single rectangle,
        // so an outline whose visible remainder is an L cannot be expressed — the spanning-axis
        // gate kept the whole border (T3 over T6) and dropping it restored the "small clip in the
        // middle eats three sides" bug. Each edge is already a rectangle, so subtracting an
        // occluder from it is exact.
        private void ShowBorderEdges(int slot, Windows.UI.Color color, double thickness,
                                     double left, double top, double w, double h, double opacity)
        {
            var v = GetOverlayVisual(slot);
            if (v?.Border == null || v.BorderEdges == null) return;
            if (w <= 0 || h <= 0 || thickness <= 0) { HideBorderRect(slot); return; }

            double t = Math.Max(1, thickness);
            double half = t / 2;
            var topE = new ClipGeometry.GeoRect(left - half, top - half, w + t, t);
            var botE = new ClipGeometry.GeoRect(left - half, top + h - half, w + t, t);
            var leftE = new ClipGeometry.GeoRect(left - half, top + half, t, Math.Max(0, h - t));
            var rightE = new ClipGeometry.GeoRect(left + w - half, top + half, t, Math.Max(0, h - t));

            var segs = new List<ClipGeometry.GeoRect>(8);
            OccludeStrip(slot, topE, segs);
            OccludeStrip(slot, botE, segs);
            OccludeStrip(slot, leftE, segs);
            OccludeStrip(slot, rightE, segs);

            if (segs.Count == 0) { HideBorderRect(slot); return; }

            if (v.Border.Visibility != Microsoft.UI.Xaml.Visibility.Visible)
                v.Border.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            if (Math.Abs(v.Border.Opacity - opacity) > 0.001) v.Border.Opacity = opacity;

            var brush = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
            var edges = v.BorderEdges;
            for (int i = 0; i < edges.Length; i++)
            {
                var el = edges[i];
                if (el == null) continue;
                if (i >= segs.Count)
                {
                    if (el.Visibility != Microsoft.UI.Xaml.Visibility.Collapsed)
                        el.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    continue;
                }

                var s = segs[i];
                if (el.Visibility != Microsoft.UI.Xaml.Visibility.Visible)
                    el.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                if (el.Fill != brush) el.Fill = brush;
                if (el.Width != s.W) el.Width = Math.Max(0, s.W);
                if (el.Height != s.H) el.Height = Math.Max(0, s.H);
                if (Microsoft.UI.Xaml.Controls.Canvas.GetLeft(el) != s.X)
                    Microsoft.UI.Xaml.Controls.Canvas.SetLeft(el, s.X);
                if (Microsoft.UI.Xaml.Controls.Canvas.GetTop(el) != s.Y)
                    Microsoft.UI.Xaml.Controls.Canvas.SetTop(el, s.Y);
            }
        }

        private void OccludeStrip(int slot, ClipGeometry.GeoRect strip, List<ClipGeometry.GeoRect> into)
        {
            var cur = new List<ClipGeometry.GeoRect> { strip };
            for (int j = slot + 1; j < MaxOverlayTracks; j++)
            {
                var other = _activeOverlay[j];
                if (other == null || other.IsVideoHidden || other.Opacity < 0.999) continue;
                if (!TryGetSlotBox(j, out var ob)) continue;

                var next = new List<ClipGeometry.GeoRect>(cur.Count * 2);
                foreach (var s in cur)
                    ClipGeometry.SubtractStrip(s, ob, next);
                cur = next;
                if (cur.Count == 0) break;
            }
            into.AddRange(cur);
        }
    }
}
