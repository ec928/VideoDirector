using System;
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

            bool anyVisible = TryGetVisibleBorderRegion(slot, box, out var visibleBorder);

            if (overlay.BorderType == BorderType.None || editMode || !anyVisible)
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
                switch (overlay.BorderType)
                {
                    case BorderType.FilmStrip:
                        grid.CornerRadius = new Microsoft.UI.Xaml.CornerRadius(0);
                        ShowBorderRect(slot, c, overlay.BorderThickness,
                                       new Microsoft.UI.Xaml.Media.DoubleCollection { 2, 1, 2, 1 }, 0,
                                       left, top, boxW, boxH, borderOpacity, visibleBorder);
                        break;

                    case BorderType.Soft:
                        // The rounded corner stays on the grid as well, so the PICTURE is rounded
                        // too rather than a rounded outline sitting on a square image. Half alpha
                        // is what makes Soft soft.
                        grid.CornerRadius = new Microsoft.UI.Xaml.CornerRadius(16);
                        ShowBorderRect(slot, Windows.UI.Color.FromArgb(128, c.R, c.G, c.B),
                                       overlay.BorderThickness, null, 16,
                                       left, top, boxW, boxH, borderOpacity, visibleBorder);
                        break;

                    default:
                        grid.CornerRadius = new Microsoft.UI.Xaml.CornerRadius(0);
                        ShowBorderRect(slot, c, overlay.BorderThickness, null, 0,
                                       left, top, boxW, boxH, borderOpacity, visibleBorder);
                        break;
                }
            }

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

        // The single border overlay for a clip, whatever its style. A child of the clip's own
        // grid, so its z-layer is the clip's: over this picture, under anything on a higher track.
        // Built once with the rest of the clip's surfaces rather than created on demand.
        private Microsoft.UI.Xaml.Shapes.Rectangle GetBorderRect(int slot)
        {
            if (_playerControl == null) return null;
            var visuals = _playerControl.OverlayVisuals;
            if (visuals == null || slot < 0 || slot >= visuals.Length) return null;
            return visuals[slot]?.Border;
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

        // How much of a border is still visible once higher tracks are taken into account.
        //
        // The border is drawn above every picture so that it survives at all, which means stacking
        // can no longer hide it when something covers the clip it belongs to. So it is worked out
        // here and applied as a clip on the rectangle, giving the border the same behaviour the
        // PICTURE gets for free: present where nothing covers it, gone where something does.
        //
        // A containment test was not enough. A PiP that pokes out past the edge of the full-frame
        // clip above it is not "fully covered", so the whole border drew - including the three
        // quarters of it lying under an opaque clip.
        //
        // Returns false when nothing of it is left.
        private bool TryGetVisibleBorderRegion(int slot, ClipGeometry.GeoRect box,
                                               out ClipGeometry.GeoRect visible)
        {
            visible = box;
            if (box.W <= 0 || box.H <= 0) return false;

            for (int j = slot + 1; j < MaxOverlayTracks; j++)
            {
                var other = _activeOverlay[j];
                if (other == null || other.IsVideoHidden || other.Opacity < 0.999) continue;
                if (!TryGetSlotBox(j, out var ob)) continue;

                // No overlap: this one hides nothing.
                if (ob.Right <= visible.X || ob.X >= visible.Right ||
                    ob.Bottom <= visible.Y || ob.Y >= visible.Bottom) continue;

                // What is left is an L-shape in general, and UIElement.Clip only takes a rectangle,
                // so keep the largest rectangular strip that survives. For the case this exists to
                // handle - a full-frame clip above a PiP that overhangs one edge - the remainder IS
                // that strip, exactly.
                double leftW   = ob.X - visible.X;
                double rightW  = visible.Right - ob.Right;
                double topH    = ob.Y - visible.Y;
                double bottomH = visible.Bottom - ob.Bottom;

                double leftA   = Math.Max(0, leftW)   * visible.H;
                double rightA  = Math.Max(0, rightW)  * visible.H;
                double topA    = Math.Max(0, topH)    * visible.W;
                double bottomA = Math.Max(0, bottomH) * visible.W;

                double best = Math.Max(Math.Max(leftA, rightA), Math.Max(topA, bottomA));
                if (best <= 0) return false; // this clip swallows what was left

                if (best == leftA)
                    visible = new ClipGeometry.GeoRect(visible.X, visible.Y, leftW, visible.H);
                else if (best == rightA)
                    visible = new ClipGeometry.GeoRect(ob.Right, visible.Y, rightW, visible.H);
                else if (best == topA)
                    visible = new ClipGeometry.GeoRect(visible.X, visible.Y, visible.W, topH);
                else
                    visible = new ClipGeometry.GeoRect(visible.X, ob.Bottom, visible.W, bottomH);
            }

            return visible.W > 0 && visible.H > 0;
        }

        private void HideBorderRect(int slot)
        {
            var rect = GetBorderRect(slot);
            if (rect != null) rect.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        }

        // Geometry is passed in because the rect lives in BorderHost and has no parent to inherit
        // it from. These are the SAME box values the clip's grid is sized and positioned with, so
        // the two cannot drift apart.
        //
        // The rect IS the box - no inset. A stroke is centred on its path, so it straddles the box
        // edge, which is what reads as a border sitting on the edge. Insetting it by half the
        // stroke pushed the outline visibly inward and the picture showed all round the outside.
        //
        // Writes are delta-guarded: this runs from the per-frame render path.
        private void ShowBorderRect(int slot, Windows.UI.Color color, double thickness,
                                    Microsoft.UI.Xaml.Media.DoubleCollection dash, double radius,
                                    double left, double top, double w, double h, double opacity,
                                    ClipGeometry.GeoRect visible)
        {
            var rect = GetBorderRect(slot);
            if (rect == null) return;
            if (w <= 0 || h <= 0) { rect.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed; return; }

            rect.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            rect.Stroke = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
            rect.StrokeThickness = thickness;
            rect.RadiusX = radius;
            rect.RadiusY = radius;

            if (rect.Width != w) rect.Width = w;
            if (rect.Height != h) rect.Height = h;
            if (Microsoft.UI.Xaml.Controls.Canvas.GetLeft(rect) != left)
                Microsoft.UI.Xaml.Controls.Canvas.SetLeft(rect, left);
            if (Microsoft.UI.Xaml.Controls.Canvas.GetTop(rect) != top)
                Microsoft.UI.Xaml.Controls.Canvas.SetTop(rect, top);
            if (Math.Abs(rect.Opacity - opacity) > 0.001) rect.Opacity = opacity;

            // Hide the part that a higher opaque clip covers. Expanded by half the stroke on every
            // side that is NOT being trimmed, because the stroke is centred on the path and half of
            // it lies outside the box - clipping to the bare box would shave the whole outline.
            double half = thickness / 2;
            bool trimmed = visible.W < w - 0.5 || visible.H < h - 0.5
                           || visible.X > left + 0.5 || visible.Y > top + 0.5;
            if (!trimmed)
            {
                if (rect.Clip != null) rect.Clip = null;
            }
            else
            {
                double cx = visible.X - left, cy = visible.Y - top;
                double cw = visible.W, ch = visible.H;
                if (cx <= 0.5) { cx -= half; cw += half; }
                if (cy <= 0.5) { cy -= half; ch += half; }
                if (visible.Right >= left + w - 0.5) cw += half;
                if (visible.Bottom >= top + h - 0.5) ch += half;

                rect.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
                {
                    Rect = new Windows.Foundation.Rect(cx, cy, Math.Max(0, cw), Math.Max(0, ch))
                };
            }

            // Null clears the dashes; assigning an empty collection leaves a solid line either way,
            // but clearing keeps the property honest about what the style is.
            if (dash == null) rect.ClearValue(Microsoft.UI.Xaml.Shapes.Shape.StrokeDashArrayProperty);
            else rect.StrokeDashArray = dash;
        }
    }
}
