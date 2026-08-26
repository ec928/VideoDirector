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

// VideoPlaybackEngine - Ken Burns marks and the WYSIWYG rectangles: one aspect resolver, mark capture, normalisation, and the on-screen handles.

namespace VideoDirector.Models
{
    public partial class VideoPlaybackEngine
    {
        // ==================== Mark coordinate space ====================
        //
        // A mark's X/Y are fractions of the video's FIT rectangle — the area the video occupies in
        // the player pane at Scale 1, which is exactly the box Edit mode frames against. Every
        // read multiplies by this; every write divides by it. Keeping the conversion in one place
        // is what makes a mark mean the same thing at any window size.
        // Box layout vs mark space are deliberately different policies, not a leftover fork.
        //
        // AspectOf may fall back to the live slot cache: a slightly wrong *box* is cosmetic and
        // corrects next evaluation. TryGetMarkSpace will not: a wrong *mark* is saved. The 34%
        // defect was three fallbacks disagreeing; mark paths now wait on SourceAspect rather than
        // borrow another clip's cache. Returns 0 for genuinely unknown — callers must hold off
        // rather than be handed a plausible-looking lie.
        private double AspectOf(CinematicOperation op, int slot)
        {
            double aspect = op?.SourceAspect ?? 0;
            if (aspect <= 0 && slot >= 0 && slot < MaxOverlayTracks) aspect = _overlayAspect[slot];
            return aspect > 0 ? aspect : 0;
        }

        public bool TryGetMarkSpace(CinematicOperation op, out double fitW, out double fitH)
        {
            fitW = 0; fitH = 0;

            double vpW = _playerControl.CanvasWidth;
            double vpH = _playerControl.CanvasHeight;
            if (vpW <= 0 || vpH <= 0) return false;

            // No 16:9 guess. Reporting false lets the caller hold off for a frame; inventing an
            // aspect produced a fit rect that disagreed with the one the surface was sized to, and
            // marks interpreted in the wrong space are exactly how framing lands off-picture.
            // SourceAspect only. Slot 0's cached aspect belongs to whichever clip last occupied
            // that slot, which is the wrong space for a mark on any other track (or a clip whose
            // decoder has not opened yet). 0 means wait a frame.
            double aspect = op?.SourceAspect ?? 0;
            if (aspect <= 0) return false;

            var fit = ClipGeometry.Fit(aspect, vpW, vpH);
            fitW = fit.W; fitH = fit.H;
            return true;
        }

        // Turn the live edit transform into a mark. The transform is in pane pixels; the mark is
        // stored normalised, so reopening the project at a different window size reproduces the
        // framing rather than shifting it.
        public SpatialMark CaptureMark(CinematicOperation op, Microsoft.UI.Xaml.Media.CompositeTransform t)
        {
            if (t == null) return new SpatialMark(1f, 0, 0);

            EnsureMarksNormalized(op);

            // RECORDS WHAT IS ON SCREEN. The wheel magnifies this very transform inside a fixed
            // window, so storing it makes Set a visual no-op: the framing saved is the framing
            // already rendered. Reading the canvas view instead added a second magnification and
            // pressing Set visibly cropped the clip.
            if (!TryGetMarkSpace(op, out double fitW, out double fitH) || fitW <= 0 || fitH <= 0)
                return new SpatialMark((float)t.ScaleX, 0, 0);

            return new SpatialMark((float)t.ScaleX,
                                   (float)(t.TranslateX / fitW),
                                   (float)(t.TranslateY / fitH));

        }

        // Convert a legacy clip's marks from raw pane pixels to fractions of the fit.
        //
        // Done here — on first draw — rather than at load, because this is the first moment the
        // pane size is known for certain; at load the control may not have been measured yet, and
        // normalising against a zero-width pane would destroy the marks. Idempotent and cheap: one
        // bool test once the clip has been converted.
        //
        // The conversion itself is lossless: dividing by the fit here and multiplying by the same
        // fit at render round-trips exactly, so normalising costs nothing.
        //
        // It does NOT follow that a legacy project renders unchanged. Translate used to be scaled
        // per-axis by (PlacementWidth, PlacementHeight) and is now scaled uniformly by
        // max(width, height) — see KenBurnsMotion.PanScale. On a square or wide PiP those agree; on
        // a TALL one they do not, and such clips will reframe. That is the point: the old result
        // did not match what the editor drew.
        public void EnsureMarksNormalized(CinematicOperation op)
        {
            if (op == null || !op.MarksAreLegacyPixels) return;
            if (!TryGetMarkSpace(op, out double fitW, out double fitH) || fitW <= 0 || fitH <= 0) return;

            Norm(op.StartMark);
            Norm(op.MidMark);
            Norm(op.EndMark);
            op.MarksAreLegacyPixels = false;

            void Norm(SpatialMark m)
            {
                if (m == null) return;
                m.X = (float)(m.X / fitW);
                m.Y = (float)(m.Y / fitH);
            }
        }

        // Sweep every clip, not just the ones currently on screen. EnsureMarksNormalized alone
        // converts a clip the first time it is drawn, which leaves a project that is loaded and
        // immediately saved holding pixel marks under a schema that promises fractions. Called on
        // load and again before save, both points where the pane is certain to be measured.
        public void NormalizeAllMarks(System.Collections.Generic.IEnumerable<TimelineTrack> tracks)
        {
            if (tracks == null) return;
            foreach (var track in tracks)
            {
                if (track?.Clips == null) continue;
                foreach (var clip in track.Clips)
                {
                    EnsureMarksNormalized(clip);

                }
            }
        }

        public void UpdateWysiwygOverlay()
        {
            // The Ken Burns edit rectangles belong to Edit mode only, and to the CURRENT SUBJECT
            // (SelectedClip) whatever track it's on — not just Track 1. Keying this off
            // SelectedTimelineNode was why editing an overlay drew nothing. Mode is the authority
            // (during composite play _mode is Arrange, so the rects stay hidden).
            if (_mode != EditorMode.Edit || _viewModel.SelectedClip == null)
            {
                _playerControl.WysiwygCanvas.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                if (_viewModel.SelectedMark != null)
                {
                    _viewModel.SelectedMark = null;
                    _playerControl.IsMarkSelected = false;
                }
                return;
            }

            var op = _viewModel.SelectedClip as CinematicOperation;
            var transform = _playerControl.ActiveTransform;
            if (op == null || transform == null) return;

            EnsureMarksNormalized(op);

            _playerControl.WysiwygCanvas.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            UpdateTelemetryOverlay(true);

            // Same space as CaptureMark / rectangle drag. Hide rather than draw in a borrowed
            // slot aspect — an absent rect is obviously absent, a misplaced one looks authoritative.
            if (!TryGetMarkSpace(op, out double W, out double H) || W <= 0 || H <= 0)
            {
                _playerControl.WysiwygCanvas.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                return;
            }

            double vpW = _playerControl.CanvasWidth;
            double vpH = _playerControl.CanvasHeight;

            // The crop box aspect ratio depends on the video's intrinsic aspect ratio
            double videoAspect = W / H;
            double pipAspect = videoAspect * (op.PlacementWidth / op.PlacementHeight);

            double boxW = W;
            double boxH = H;

            // When Scale=1 (UniformToFill), the crop box fits the video on one axis.
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

            void DrawRect(Microsoft.UI.Xaml.FrameworkElement rect, SpatialMark targetMark, bool show)
            {
                if (!show || targetMark == null)
                {
                    rect.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    return;
                }

                rect.Visibility = Microsoft.UI.Xaml.Visibility.Visible;

                double Sc = transform.ScaleX;
                double txc = transform.TranslateX;
                double tyc = transform.TranslateY;

                double St = targetMark.Scale;
                // Marks are fractions of the fit (W x H here) — back to pixels to draw them.
                double txt = targetMark.X * W;
                double tyt = targetMark.Y * H;

                if (St <= 0) St = 1;

                // W/2 and H/2 place it relative to the video bounds. Add the centering offset (vpW - W)/2 to map to canvas.
                double currentLeft = (-boxW / 2 - txt) * (Sc / St) + W / 2 + txc + (vpW - W) / 2;
                double currentTop = (-boxH / 2 - tyt) * (Sc / St) + H / 2 + tyc + (vpH - H) / 2;
                double currentWidth = boxW * (Sc / St);
                double currentHeight = boxH * (Sc / St);

                Microsoft.UI.Xaml.Controls.Canvas.SetLeft(rect, currentLeft);
                Microsoft.UI.Xaml.Controls.Canvas.SetTop(rect, currentTop);
                rect.Width = Math.Max(0, currentWidth);
                rect.Height = Math.Max(0, currentHeight);
            }

            DrawRect(_playerControl.WysiwygStartRect, op.StartMark, true);
            DrawRect(_playerControl.WysiwygMidRect, op.MidMark, true);
            DrawRect(_playerControl.WysiwygEndRect, op.EndMark, true);

            // Selection styling. Solid and full strength for the selected keyframe, thin dashed and
            // faded for the rest — the colour coding still says WHICH keyframe each one is, so the
            // highlight only has to say which one the wheel and the inspector will act on.
            var sel = _viewModel.SelectedMark;
            StyleMarkRect(_playerControl.WysiwygStartRect, _playerControl.WysiwygStartFrame, sel == EditTarget.Start);
            StyleMarkRect(_playerControl.WysiwygMidRect, _playerControl.WysiwygMidFrame, sel == EditTarget.Mid);
            StyleMarkRect(_playerControl.WysiwygEndRect, _playerControl.WysiwygEndFrame, sel == EditTarget.End);
        }

        private static void StyleMarkRect(Microsoft.UI.Xaml.FrameworkElement rect,
                                          Microsoft.UI.Xaml.Shapes.Rectangle frame, bool selected)
        {
            if (rect != null)
            {
                // 0.42 was too faint to find against a bright picture in daylight, and it is not
                // the only thing marking an unselected rectangle: the dashes and the thinner stroke
                // already say which one the inspector is acting on, so the opacity does not have to
                // carry that distinction as well.
                double opacity = selected ? 1.0 : 0.75;
                if (Math.Abs(rect.Opacity - opacity) > 0.001) rect.Opacity = opacity;
            }
            if (frame == null) return;

            double thickness = selected ? 3.0 : 2.0;
            if (Math.Abs(frame.StrokeThickness - thickness) > 0.001) frame.StrokeThickness = thickness;

            // Solid for the selected one; the dashes are what make an unselected rectangle read as
            // a guide rather than as the thing being manipulated.
            bool dashed = frame.StrokeDashArray != null && frame.StrokeDashArray.Count > 0;
            if (selected && dashed) frame.StrokeDashArray = new Microsoft.UI.Xaml.Media.DoubleCollection();
            else if (!selected && !dashed) frame.StrokeDashArray = new Microsoft.UI.Xaml.Media.DoubleCollection { 4, 4 };
        }
    }
}
