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

// VideoPlaybackEngine - what the track manager and direct manipulation ask for: layouts, PiP presets, borders, opacity, and dragging a box or a mark.

namespace VideoDirector.Models
{
    public partial class VideoPlaybackEngine
    {
        // ---- Arrange mode: drag / wheel the PiP under the cursor (the hit slot) ----

        // ==================== Layout prototypes ====================
        //
        // A layout arranges the clips that are ON SCREEN AT THE PLAYHEAD. That is the answer to
        // "which clips?" - the question a spatial arrangement has to settle before it can mean
        // anything on a timeline where tracks hold clips of different lengths. It arranges what you
        // are looking at, which is also the only set you can judge by eye.
        //
        // Deliberately a one-shot: it writes each clip's placement and stops. It is NOT reflow -
        // when one of these clips ends, its cell simply goes empty rather than the survivors
        // rearranging. Whether that is acceptable is the thing these prototypes exist to find out.
        private void OnLayoutRequested(object? sender, string layout)
        {
            if (_mode != EditorMode.Arrange) return;

            var live = new System.Collections.Generic.List<(int slot, CinematicOperation clip)>();
            for (int i = 0; i < MaxOverlayTracks; i++)
                if (_activeOverlay[i] != null) live.Add((i, _activeOverlay[i]));
            if (live.Count == 0) return;

            var cells = LayoutCells(layout, live.Count);
            if (cells == null) return;

            for (int i = 0; i < live.Count && i < cells.Length; i++)
                PlaceInCell(live[i].slot, live[i].clip, cells[i]);

            _viewModel.RecordIfChanged();
            Invalidate();
        }

        // Cells in PANE fractions: centre x, centre y, width, height.
        //
        // DERIVED FROM THE COUNT, never a fixed table. The first cut hardcoded two cells for "side"
        // and "stack", so with four clips on screen it arranged two of them and left the other two
        // wherever they already were - a half-applied layout on top of the old one, which looked
        // like nothing at all. A layout has to account for every clip it claims to arrange.
        //
        // side  = one row, N columns.  stack = N rows, one column.  grid = the squarest fit.
        //
        // The gutter is subtracted from each cell rather than inserted between them, so the outer
        // edge gets the same breathing room as the inner joins and the block stays centred.
        private static (double cx, double cy, double w, double h)[] LayoutCells(string layout, int count)
        {
            if (count <= 0) return null;
            const double Gutter = 0.02;

            int rows, cols;
            switch (layout)
            {
                case "side":  rows = 1; cols = count; break;
                case "stack": rows = count; cols = 1; break;
                case "grid":
                    cols = (int)Math.Ceiling(Math.Sqrt(count));
                    rows = (int)Math.Ceiling(count / (double)cols);
                    break;
                default: return null;
            }

            double cellW = 1.0 / cols, cellH = 1.0 / rows;
            double w = cellW - Gutter * (1 + 1.0 / cols);
            double h = cellH - Gutter * (1 + 1.0 / rows);
            if (w <= 0 || h <= 0) return null;

            var cells = new (double, double, double, double)[count];
            for (int i = 0; i < count; i++)
            {
                int row = i / cols;
                int col = i % cols;

                // A short last row is centred, so the odd one out reads as deliberate rather than
                // as a gap where a clip should have been.
                int inThisRow = Math.Min(cols, count - row * cols);
                double rowOffset = (cols - inThisRow) * cellW / 2.0;

                cells[i] = (rowOffset + (col + 0.5) * cellW,
                            (row + 0.5) * cellH,
                            w, h);
            }
            return cells;
        }

        // Put one clip in one cell, at the largest size that keeps its own shape.
        //
        // NOTHING IS CROPPED. A box whose PlacementWidth equals its PlacementHeight already has the
        // source's aspect - that is why the default 0.3 x 0.3 corner PiPs look right - because the
        // box is measured against the clip's own fit rectangle. So the cell is filled by the
        // largest source-shaped rectangle that fits inside it, and the remainder of the cell is
        // left empty rather than the picture being cut to fill it.
        //
        // Note the mixed units the placement model uses: width and height are fractions of the
        // clip's FIT, while the centre is a fraction of the PANE. The conversion here is what keeps
        // a cell expressed in pane terms from silently meaning something different per clip.
        private void PlaceInCell(int slot, CinematicOperation clip, (double cx, double cy, double w, double h) cell)
        {
            double vpW = _playerControl.CanvasWidth, vpH = _playerControl.CanvasHeight;
            if (vpW <= 0 || vpH <= 0) return;
            if (!TryGetMarkSpace(clip, out double fitW, out double fitH) || fitW <= 0 || fitH <= 0) return;

            double cellW = cell.w * vpW, cellH = cell.h * vpH;
            double aspect = fitW / fitH;

            // Largest rectangle of the source's aspect that fits the cell.
            double boxW = cellW, boxH = cellW / aspect;
            if (boxH > cellH) { boxH = cellH; boxW = cellH * aspect; }

            clip.PlacementWidth = Math.Clamp(boxW / fitW, 0.05, 1.0);
            clip.PlacementHeight = Math.Clamp(boxH / fitH, 0.05, 1.0);
            clip.PlacementCenterX = Math.Clamp(cell.cx, 0, 1);
            clip.PlacementCenterY = Math.Clamp(cell.cy, 0, 1);
        }

        // The source's real pixel dimensions: the decoder for a video, the baked bitmap for a
        // still. Shared with the telemetry readout so the two cannot disagree about how big a clip
        // actually is.
        private bool TryGetSourcePixelSize(int slot, CinematicOperation clip, out double w, out double h)
        {
            w = 0; h = 0;
            if (clip == null) return false;

            var session = _overlayPlayer[slot]?.PlaybackSession;
            if (!clip.IsStill && session != null && session.NaturalVideoWidth > 0)
            {
                w = session.NaturalVideoWidth; h = session.NaturalVideoHeight;
            }
            else if (clip.StillFrame != null && clip.StillFrame.PixelWidth > 0)
            {
                w = clip.StillFrame.PixelWidth; h = clip.StillFrame.PixelHeight;
            }
            return w > 0 && h > 0;
        }

        /// <summary>
        /// Resize a PiP to a fraction of the frame, in place.
        /// </summary>
        /// <remarks>
        /// Position is preserved, not reset. Resizing and repositioning are separate decisions, and
        /// a preset that silently flung the clip back to a corner would undo placement work every
        /// time you changed its size. The centre is only nudged when the new box would hang off the
        /// frame, and full screen re-centres because there is nowhere else for it to be.
        /// </remarks>
        private void OnPipSizeRequested(object? sender, (int slot, string preset) e)
        {
            if (_mode != EditorMode.Arrange) return;
            var overlay = _activeOverlay[e.slot];
            if (overlay == null) return;

            // ONE BASIS FOR THE PERCENTAGES, and it is the window.
            //
            // 100% is the whole frame scaled to the window - full screen - and 75, 50, 33 and 25
            // are fractions of that, so 75% is always three-quarters of 100%. They say nothing
            // about the source's resolution, and they are not supposed to.
            //
            // "actual" is the odd one out on purpose: it sizes the box to the source's own pixels,
            // which is a different measure entirely. That is why the menu calls it Actual Size and
            // gives it no number - labelling it "100%" alongside a "75%" measured against the
            // window produced the absurdity that 75% could render LARGER than 100%.
            double fraction;
            if (string.Equals(e.preset, "fill", StringComparison.OrdinalIgnoreCase))
            {
                // Cover the canvas: grow until the SHORTER axis reaches it, so nothing is left
                // uncovered and the overhang on the other axis is cropped. This is the one thing
                // the percentages cannot express, because they stop at the clip's own fit.
                if (!TryGetMarkSpace(overlay, out double fw, out double fh) || fw <= 0 || fh <= 0) return;
                double cw = _playerControl.CanvasWidth, ch = _playerControl.CanvasHeight;
                if (cw <= 0 || ch <= 0) return;

                fraction = Math.Max(cw / fw, ch / fh);
                overlay.PlacementWidth = fraction;
                overlay.PlacementHeight = fraction;
                overlay.PlacementCenterX = 0.5;
                overlay.PlacementCenterY = 0.5;

                _dispatcher.TryEnqueue(() => ApplyOverlayBox(e.slot, overlay, false));
                _viewModel.RecordIfChanged();
                return;
            }

            if (string.Equals(e.preset, "actual", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryGetSourcePixelSize(e.slot, overlay, out double nativeW, out _)) return;
                if (!TryGetMarkSpace(overlay, out double fitW, out _) || fitW <= 0) return;

                // Over 1.0 means the source is bigger than the window can show whole, so 1:1 is out
                // of reach and the clamp below lands on Full Screen - as close as the placement
                // model gets. Stated rather than hidden: the setter caps at 1.0.
                fraction = nativeW / fitW;
            }
            else if (!double.TryParse(e.preset, System.Globalization.NumberStyles.Float,
                                      System.Globalization.CultureInfo.InvariantCulture, out fraction))
            {
                return;
            }

            double f = Math.Clamp(fraction, 0.05, 1.0);
            overlay.PlacementWidth = f;
            overlay.PlacementHeight = f;

            if (f >= 1.0)
            {
                overlay.PlacementCenterX = 0.5;
                overlay.PlacementCenterY = 0.5;
            }
            else
            {
                // Keep it on screen: the centre can sit no closer to an edge than half the box.
                overlay.PlacementCenterX = Math.Clamp(overlay.PlacementCenterX, f / 2, 1 - f / 2);
                overlay.PlacementCenterY = Math.Clamp(overlay.PlacementCenterY, f / 2, 1 - f / 2);
            }

            _dispatcher.TryEnqueue(() => ApplyOverlayBox(e.slot, overlay, false));
            _viewModel.RecordIfChanged();
        }



        private void OnEditClipRequested(object? sender, int slot)
        {
            if (_mode != EditorMode.Arrange) return;
            var overlay = _activeOverlay[slot];
            if (overlay != null)
            {
                BeginEdit(overlay, EditTarget.Start);
            }
        }

        private void OnBorderTypeRequested(object? sender, (int Slot, Models.BorderType Type) args)
        {
            if (_mode != EditorMode.Arrange) return;
            var overlay = _activeOverlay[args.Slot];
            if (overlay != null)
            {
                overlay.BorderType = args.Type;
                _dispatcher.TryEnqueue(() => ApplyOverlayBox(args.Slot, overlay, false));
                _viewModel.RecordIfChanged();
            }
        }

        private void OnBorderColorRequested(object? sender, (int Slot, Windows.UI.Color Color) args)
        {
            if (_mode != EditorMode.Arrange) return;
            var overlay = _activeOverlay[args.Slot];
            if (overlay != null)
            {
                overlay.BorderColor = args.Color;
                _dispatcher.TryEnqueue(() => ApplyOverlayBox(args.Slot, overlay, false));
                _viewModel.RecordIfChanged();
            }
        }

        private void OnBorderThicknessRequested(object? sender, (int Slot, double Thickness) args)
        {
            if (_mode != EditorMode.Arrange) return;
            var overlay = _activeOverlay[args.Slot];
            if (overlay != null)
            {
                overlay.BorderThickness = args.Thickness;
                _dispatcher.TryEnqueue(() => ApplyOverlayBox(args.Slot, overlay, false));
                _viewModel.RecordIfChanged();
            }
        }
        private void OnHideRequested(object? sender, int slot)
        {
            if (_mode != EditorMode.Arrange) return;
            var overlay = _activeOverlay[slot];
            if (overlay != null)
            {
                overlay.IsVideoHidden = !overlay.IsVideoHidden;
                _viewModel.RecordIfChanged();
                _dispatcher.TryEnqueue(() => RefreshComposite());
            }
        }

        private void OnLockRequested(object? sender, int slot)
        {
            if (_mode != EditorMode.Arrange) return;
            var overlay = _activeOverlay[slot];
            if (overlay != null)
            {
                overlay.IsLocked = !overlay.IsLocked;
                _viewModel.RecordIfChanged();
                _dispatcher.TryEnqueue(() => RefreshComposite());
            }
        }
        private void OnOpacityRequested(object? sender, (int Slot, float Opacity) args)
        {
            if (_mode != EditorMode.Arrange) return;
            var overlay = _activeOverlay[args.Slot];
            if (overlay != null)
            {
                overlay.Opacity = args.Opacity;
                _viewModel.RecordIfChanged();
                _dispatcher.TryEnqueue(() => RefreshComposite());
            }
        }

        private void OnOverlayBoxPointerPressed(object? sender, int slot)
        {
            if (_mode != EditorMode.Arrange) return;
            var overlay = _activeOverlay[slot];
            if (overlay != null) _viewModel.SelectedClip = overlay;
        }

        private void OnOverlayBoxDragged(object? sender, (int slot, Views.BoxGrab grab, double dx, double dy) e)
        {
            if (_mode != EditorMode.Arrange) return;
            // §7A invariant: never manipulate a live video surface. While ACTIVELY playing the PiP
            // IS that surface (handles hidden for the same reason). Paused counts as Arrange.
            if (IsActivelyPlaying) return;
            var overlay = _activeOverlay[e.slot];
            if (overlay == null) return;
            double vpW = _playerControl.CanvasWidth, vpH = _playerControl.CanvasHeight;
            if (vpW <= 0 || vpH <= 0) return;

            // Interior grab = translate the whole box.
            if (e.grab == Views.BoxGrab.Move)
            {
                overlay.PlacementCenterX += e.dx / vpW;
                overlay.PlacementCenterY += e.dy / vpH;
                ApplyOverlayBox(e.slot, overlay, false);
                return;
            }

            // Edge/corner grab = reshape. Work in pixels: move only the grabbed edges, keep the
            // opposite edges anchored, then convert back to independent width/height + centre.
            double aspect = AspectOf(overlay, e.slot);
            if (aspect <= 0) return;
            double fitW, fitH;
            if (aspect >= vpW / vpH) { fitW = vpW; fitH = vpW / aspect; }
            else { fitH = vpH; fitW = vpH * aspect; }

            double boxW = fitW * overlay.PlacementWidth;
            double boxH = fitH * overlay.PlacementHeight;
            double cxPx = overlay.PlacementCenterX * vpW;
            double cyPx = overlay.PlacementCenterY * vpH;
            double left = cxPx - boxW / 2, right = cxPx + boxW / 2;
            double top = cyPx - boxH / 2, bottom = cyPx + boxH / 2;

            var g = e.grab;
            bool moveLeft = g == Views.BoxGrab.Left || g == Views.BoxGrab.TopLeft || g == Views.BoxGrab.BottomLeft;
            bool moveRight = g == Views.BoxGrab.Right || g == Views.BoxGrab.TopRight || g == Views.BoxGrab.BottomRight;
            bool moveTop = g == Views.BoxGrab.Top || g == Views.BoxGrab.TopLeft || g == Views.BoxGrab.TopRight;
            bool moveBottom = g == Views.BoxGrab.Bottom || g == Views.BoxGrab.BottomLeft || g == Views.BoxGrab.BottomRight;

            const double minPx = 24;
            if (moveLeft) left = Math.Min(left + e.dx, right - minPx);
            if (moveRight) right = Math.Max(right + e.dx, left + minPx);
            if (moveTop) top = Math.Min(top + e.dy, bottom - minPx);
            if (moveBottom) bottom = Math.Max(bottom + e.dy, top + minPx);

            overlay.PlacementWidth = (right - left) / fitW;
            overlay.PlacementHeight = (bottom - top) / fitH;
            overlay.PlacementCenterX = ((left + right) / 2) / vpW;
            overlay.PlacementCenterY = ((top + bottom) / 2) / vpH;
            ApplyOverlayBox(e.slot, overlay, false);
        }

        private void OnWysiwygBoxGrabbed(object? sender, string markType)
        {
            if (_mode != EditorMode.Edit || _viewModel.SelectedClip == null) return;
            var op = _viewModel.SelectedClip as CinematicOperation;
            if (op == null) return;

            if (_editPreviewPlaying) StopEditPreview();

            // Grabbing a rectangle selects it. Without this the app had no idea which keyframe you
            // were working on: the canvas seeked to it but left CurrentEditTarget alone, and there
            // was no selection state at all for the highlight or the wheel to key off.
            SetSelectedMark(markType switch
            {
                "Start" => EditTarget.Start,
                "Mid" => EditTarget.Mid,
                "End" => EditTarget.End,
                _ => (EditTarget?)null
            });

            if (markType == "Start") SeekActiveOperation(op.VideoStartTime);
            else if (markType == "Mid" && op.MidMark != null) 
            {
                var midTime = op.VideoStartTime + TimeSpan.FromSeconds((op.VideoEndTime - op.VideoStartTime).TotalSeconds / 2);
                SeekActiveOperation(midTime);
            }
            else if (markType == "End")
            {
                var endSeek = op.VideoEndTime;
                // Unconditionally back off slightly from the end trim point to guarantee we hit a visible frame.
                // If it's the end of the file, this avoids EOS. If it's a trim, it shows the last included frame.
                if (endSeek.TotalMilliseconds > 100)
                {
                    endSeek -= TimeSpan.FromMilliseconds(100);
                    if (endSeek < op.VideoStartTime) endSeek = op.VideoStartTime;
                }
                SeekActiveOperation(endSeek);
            }

            // Poke the decoder so the paused frame updates immediately. A bitmap still has no
            // decoder to poke and never changes frame, so there is nothing to do for one.
            if (RenderModeFor(op) != OverlayRender.Still)
                _overlayPlayer[0]?.StepForwardOneFrame();
        }

        // The single place selection changes, so the view model, the control's wheel routing and
        // the on-screen highlight can never disagree.
        public void SetSelectedMark(EditTarget? target)
        {
            if (_mode != EditorMode.Edit) target = null;
            if (_viewModel.SelectedMark == target) return;

            _viewModel.SelectedMark = target;
            _playerControl.IsMarkSelected = target.HasValue;
            UpdateWysiwygOverlay();
            if (target.HasValue) PopMarkRect(target.Value);
        }

        // A single ease-out pop on selection, not a loop. A rectangle that keeps flashing while you
        // are judging a framing is noise; one short acknowledgement then a solid, static highlight
        // is what reads as deliberate.
        private void PopMarkRect(EditTarget target)
        {
            var scale = target switch
            {
                EditTarget.Start => _playerControl.WysiwygStartPop,
                EditTarget.Mid => _playerControl.WysiwygMidPop,
                _ => _playerControl.WysiwygEndPop
            };
            if (scale == null) return;

            var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            foreach (var prop in new[] { "ScaleX", "ScaleY" })
            {
                var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                {
                    From = 1.03,
                    To = 1.0,
                    Duration = new Microsoft.UI.Xaml.Duration(TimeSpan.FromMilliseconds(180)),
                    EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
                    {
                        EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut
                    },
                    EnableDependentAnimation = true
                };
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, scale);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, prop);
                sb.Children.Add(anim);
            }
            try { sb.Begin(); } catch { }
        }

        private void OnWysiwygBoxManipulated(object? sender, (string markType, string action, double dx, double dy) e)
        {
            if (_mode != EditorMode.Edit || _viewModel.SelectedClip == null) return;
            var op = _viewModel.SelectedClip as CinematicOperation;
            if (op == null) return;

            EnsureMarksNormalized(op);

            SpatialMark mark;
            if (e.markType == "Start") mark = op.StartMark;
            else if (e.markType == "Mid") mark = op.MidMark;
            else if (e.markType == "End") mark = op.EndMark;
            else return;

            if (mark == null) return;

            var transform = _playerControl.ActiveTransform;
            if (transform == null) return;

            double vpW = _playerControl.CanvasWidth > 0 ? _playerControl.CanvasWidth : 1920;
            double vpH = _playerControl.CanvasHeight > 0 ? _playerControl.CanvasHeight : 1080;

            // Must resolve identically to the rect the user is dragging (see UpdateWysiwygOverlay).
            // A drag converted in a different space than it was drawn in moves the mark somewhere
            // other than where the pointer went.
            double aspect = AspectOf(op, 0);
            if (aspect <= 0) return;

            double W, H;
            if (aspect >= vpW / vpH) { W = vpW; H = vpW / aspect; }
            else { H = vpH; W = vpH * aspect; }

            double videoAspect = W / H;
            double pipAspect = videoAspect * (op.PlacementWidth / op.PlacementHeight);

            double boxW = W;
            double boxH = H;
            if (pipAspect > videoAspect)
            {
                boxW = W;
                boxH = W / pipAspect;
            }
            else
            {
                boxH = H;
                boxW = H * pipAspect;
            }

            double Sc = transform.ScaleX;
            double txc = transform.TranslateX;
            double tyc = transform.TranslateY;

            double St = mark.Scale;
            // The whole of this method works in pane pixels; marks are fractions of the fit
            // (W x H), so convert in here and back out again on write.
            double txt = mark.X * W;
            double tyt = mark.Y * H;
            if (St <= 0) St = 1;

            if (e.action == "Translate")
            {
                mark.X -= (float)(e.dx / (Sc / St) / W);
                mark.Y -= (float)(e.dy / (Sc / St) / H);
            }
            else
            {
                double deltaW = 0;
                if (e.action == "TL" || e.action == "BL") deltaW = -e.dx;
                else if (e.action == "TR" || e.action == "BR") deltaW = e.dx;

                double currentWidth = boxW * (Sc / St);
                double newWidth = currentWidth + deltaW;
                if (newWidth < 50) newWidth = 50; 

                double newSt = boxW * Sc / newWidth;

                double cx = -txt * (Sc / St) + W / 2 + txc;
                double cy = -tyt * (Sc / St) + H / 2 + tyc;

                double dcx = 0;
                double dcy = 0;
                double deltaH = deltaW * (boxH / boxW);
                
                if (e.action == "TR") { dcy = -deltaH / 2; dcx = deltaW / 2; }
                else if (e.action == "TL") { dcy = -deltaH / 2; dcx = -deltaW / 2; }
                else if (e.action == "BR") { dcy = deltaH / 2; dcx = deltaW / 2; }
                else if (e.action == "BL") { dcy = deltaH / 2; dcx = -deltaW / 2; }

                cx += dcx;
                cy += dcy;

                mark.Scale = (float)newSt;
                mark.X = (float)(-(cx - W / 2 - txc) / (Sc / newSt) / W);
                mark.Y = (float)(-(cy - H / 2 - tyc) / (Sc / newSt) / H);
            }


            UpdateWysiwygOverlay();
        }

        private void OnOverlayBoxWheel(object? sender, (int slot, int delta) e)
        {
            if (_mode != EditorMode.Arrange) return;
            if (IsActivelyPlaying) return;   // same invariant: no resizing a live video surface
            var overlay = _activeOverlay[e.slot];
            if (overlay == null) return;
            // Wheel = uniform resize: scales both dimensions, preserving the box's current shape.
            double f = e.delta > 0 ? 1.08 : 1.0 / 1.08;
            overlay.PlacementWidth *= f;
            overlay.PlacementHeight *= f;
            ApplyOverlayBox(e.slot, overlay, false);
            _viewModel.RecordIfChanged();
}
    }
}
