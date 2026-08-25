using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace VideoDirector.Views
{
    // The single switch that decides what all pointer input does. Set by the engine from the
    // editor mode — nothing else influences input routing (strict mode segregation).
    public enum PlayerInputMode
    {
        Content,     // Edit mode: drag = pan the clip's content, wheel = zoom it.
        ArrangePips  // Arrange mode: drag = move the PiP under the cursor, wheel = resize it.
    }

    // The answer to "you have unsaved work" on the way out.
    public enum UnsavedChoice { Save, Discard, Cancel }

    // What grabbing the PiP box does: move it, or resize it from a specific edge/corner.
    // Determined at pointer-press from where in the box the cursor is (interior = move,
    // near an edge = one-dimension resize, near a corner = two-dimension resize).
    public enum BoxGrab { Move, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

    // One track's render surfaces. Built in a loop, MaxTracks of them; the engine addresses
    // them generically by track index via OverlayVisuals[i] - no per-track code paths (7B).
    public sealed class OverlayVisual
    {
        public Microsoft.UI.Xaml.Controls.Grid Grid;
        public Microsoft.UI.Xaml.Controls.MediaPlayerElement Video;
        // The whole frame, drawn oversized by UniformToFill and clipped by the grid. The surplus
        // outside the box is the rest of the picture — which is what makes zooming out reveal more
        // of it rather than exposing background.
        public Microsoft.UI.Xaml.Controls.Image Still;
        public Microsoft.UI.Xaml.Media.CompositeTransform Transform;
        // The still surface's own transform. Separate from Transform (which belongs to the video
        // element) so the two surfaces never share state, and declared in XAML alongside
        // RenderTransformOrigin="0.5,0.5" so the pivot is RELATIVE to the element — an Image with
        // UniformToFill overflows its slot, and an absolute pivot would not be its centre.
        public Microsoft.UI.Xaml.Media.CompositeTransform StillTransform;
        public Microsoft.UI.Xaml.Controls.Grid Frame;
        // The clip border. Lives in BorderHost, not in Grid: above every track picture, because a
        // shape beneath a video surface is erased rather than blended. Being above everything, it
        // is the engine that decides when a higher opaque clip should hide it.
        public Microsoft.UI.Xaml.Shapes.Rectangle Border;

    }

    public sealed partial class DirectorPlayerControl : UserControl
    {
        private bool _isDragging = false;

        // Middle-button view drag. Panning is measured in PANE pixels - the translate it feeds is
        // applied outside the canvas scale, so canvas-space deltas would be wrong by the zoom.
        private bool _isPanning;
        private bool _panMoved;
        private Point _panLast;

        /// <summary>Set by the host so the wheel knows whether a clip owns it.</summary>
        public bool HasSelection { get; set; }

        /// <summary>The middle button clears the clip selection before it pans.</summary>
        public event EventHandler? DeselectRequested;
        private Point _lastPointerPosition;
        private int _dragSlot = -1;
        private BoxGrab _dragGrab;

        // Index i == track i. One entry per DirectorViewModel.MaxTracks, always, because the
        // array is sized from that constant rather than transcribed from XAML by hand.
        public OverlayVisual[] OverlayVisuals { get; private set; }

        // How close (px) to an edge counts as grabbing that edge for a resize.
        // How far INSIDE a box counts as grabbing its edge rather than its middle.
        private const double HandleThreshold = 24.0;

        // ...and how far OUTSIDE. Without this the band was inside-only, so aiming at a 2px dashed
        // line meant roughly half of every attempt landed outside the box and hit nothing at all.
        // The visible line now sits in the middle of the grab zone rather than at its edge.
        private const double GrabOutset = 10.0;

        public event EventHandler ViewportTransformChanged;
        public Microsoft.UI.Xaml.Media.CompositeTransform ActiveTransform { get; set; }

        // PiP manipulation events (Arrange mode). Raised from the full-screen InputLayer
        public event EventHandler<(int slot, BoxGrab grab, double dx, double dy)> OverlayBoxDragged;
        public event EventHandler<(string markType, string action, double dx, double dy)> WysiwygBoxManipulated;
        public event EventHandler<string> WysiwygBoxGrabbed;

        // Set by the engine. The control needs it only to decide where the wheel goes.
        public bool IsMarkSelected { get; set; }

        // Raised on a press that lands on empty canvas in content mode - the deselect gesture.
        public event EventHandler? CanvasCleared;
        public event EventHandler<(int slot, int delta)> OverlayBoxWheel;
        public event EventHandler<int>? OverlayBoxPointerPressed;

        // Double-tap the image to enter Edit for the clip under the cursor (slot = overlay track
        // index, or -1 = Track 1). In Edit mode a double-tap exits instead.
        public event EventHandler<int> EditRequested;
        public event EventHandler ExitEditRequested;

        // ---- The canvas ------------------------------------------------------------------
        //
        // Its size is the composition's size. The pane only decides how big it LOOKS.

        public double CanvasWidth  => CanvasHost.Width  > 0 ? CanvasHost.Width  : 1920;
        public double CanvasHeight => CanvasHost.Height > 0 ? CanvasHost.Height : 1080;

        // How much of the pane's bottom the track manager is covering.
        //
        // The pane deliberately spans the whole window so that showing or hiding the dock cannot
        // change the canvas - but that left the bottom of the canvas BEHIND the dock, with edit
        // rectangles and clip edges running under it.
        //
        // The fix is a VIEW adjustment, not a composition one: the canvas keeps its size, and the
        // fit is computed against the part of the pane you can actually see, then nudged up to
        // centre in it. Toggling the dock changes the scale you view the arrangement at and never
        // the arrangement.
        public double BottomChromeInset { get; set; }

        private double _canvasZoom = 1.0;

        // Two different presentations, with different rules.
        //
        // PLAYBACK dims the chrome and leaves the view alone: zoom and pan are useful while
        // something is running, and middle-click puts the view back in one action.
        //
        // CINEMATIC is the performance. The whole canvas, fit, no pan, and the view controls inert -
        // there is no reason to be zoomed into a corner of a piece you are showing someone, and no
        // opportunity to notice and fix it while it happens. The working view is handed back on the
        // way out.
        private bool _isPlaybackView;
        private bool _isCinematicView;
        private bool _isEditView;
        private double _savedZoom = 1.0, _savedPanX, _savedPanY;
        private double _canvasPanX, _canvasPanY;

        public double CanvasZoom => _canvasZoom;

        private double _canvasFit = 1.0;

        /// <summary>The view as a framing: what the pane is currently looking at.</summary>
        /// <remarks>
        /// ZOOM IS PASSIVE. It magnifies the view and alters nothing, so the Set buttons read it at
        /// the moment they are pressed instead of the wheel writing the clip framing as it turns.
        /// Pan arrives in pane pixels; marks live in canvas units, hence the divide by the fit.
        /// </remarks>
        public bool TryGetViewFraming(out double zoom, out double panX, out double panY)
        {
            zoom = _canvasZoom;
            panX = 0; panY = 0;
            if (_canvasFit <= 0 || double.IsNaN(_canvasFit) || double.IsInfinity(_canvasFit)) return false;
            panX = _canvasPanX / _canvasFit;
            panY = _canvasPanY / _canvasFit;
            return true;
        }
        public bool IsCanvasViewDefault => _canvasZoom == 1.0 && _canvasPanX == 0 && _canvasPanY == 0;

        /// <summary>Raised when the canvas size changes, so the composite can be re-laid out.</summary>
        public event EventHandler CanvasSizeChanged;

        public void SetCanvasSize(double w, double h)
        {
            if (w <= 0 || h <= 0) return;
            if (CanvasHost.Width == w && CanvasHost.Height == h) return;

            CanvasHost.Width = w;
            CanvasHost.Height = h;

            // Once now, and once more after the layout pass this resize triggers. Computing the fit
            // against a stale ActualWidth is what left it wrong for the rest of the session, because
            // nothing recomputed it afterwards.
            UpdateCanvasLayout();
            DispatcherQueue?.TryEnqueue(UpdateCanvasLayout);

            CanvasSizeChanged?.Invoke(this, EventArgs.Empty);
        }

        // Fit the canvas to the pane, then apply zoom and pan on top. Fit is recomputed on every
        // pane resize; the arrangement inside the canvas never moves, only this transform does.
        public void UpdateCanvasLayout()
        {
            if (RootLayer == null || CanvasTransform == null) return;

            // THIS control's size, not RootLayer's. The host sets the canvas from PlayerControl's
            // ActualWidth/Height, and the fit has to divide by the very same number or the two
            // disagree - which is exactly how a canvas that had just been set to the pane size still
            // came out at 107%: set from one measurement, divided by another taken a layout pass
            // apart.
            double paneW = ActualWidth, paneH = ActualHeight;
            if (paneW <= 0 || paneH <= 0) return;

            double visibleH = Math.Max(1, paneH - BottomChromeInset);
            double fit = Math.Min(paneW / CanvasWidth, visibleH / CanvasHeight);
            if (fit <= 0 || double.IsNaN(fit) || double.IsInfinity(fit)) fit = 1;

            _canvasFit = fit;
            double effective = fit * _canvasZoom;

            CanvasTransform.ScaleX = effective;
            CanvasTransform.ScaleY = effective;
            CanvasTransform.TranslateX = _canvasPanX;
            // Up by half the covered strip, so the canvas centres in what is visible rather than in
            // the pane - otherwise it would sit half behind the dock.
            CanvasTransform.TranslateY = _canvasPanY - BottomChromeInset / 2;

            UpdateCanvasLabel(effective);

            // Keyframe tabs carry the inverse, so their text is never drawn at a fractional scale.
            double inv = effective > 0 ? 1.0 / effective : 1.0;
            if (WysiwygStartTabScale != null) { WysiwygStartTabScale.ScaleX = inv; WysiwygStartTabScale.ScaleY = inv; }
            if (WysiwygMidTabScale != null)   { WysiwygMidTabScale.ScaleX = inv;   WysiwygMidTabScale.ScaleY = inv; }
            if (WysiwygEndTabScale != null)   { WysiwygEndTabScale.ScaleX = inv;   WysiwygEndTabScale.ScaleY = inv; }
        }

        // The canvas outline belongs to Arrange. In Edit a single clip is being framed against the
        // Start/Mid/End rectangles, and a canvas outline there has nothing to say.

        /// <summary>Called per frame by the engine. Records the condition; ApplyCanvasChrome owns
        /// what actually happens to the element.</summary>
        public void SetCanvasEdgeVisible(bool visible)
        {
            bool edit = !visible;
            if (_isEditView == edit) return;

            _isEditView = edit;
            ApplyCanvasChrome();
        }

        // Takes the scale it is meant to display, rather than reading a field that has to be kept
        // in step. The field version silently printed its own initialiser - 100%, always - once a
        // refactor dropped the line that assigned it, so the readout sat next to a transform it had
        // no connection to.
        private void UpdateCanvasLabel(double effectiveScale)
        {
            if (CanvasLabel == null) return;
            CanvasLabel.Text = "Canvas  " + Math.Round(effectiveScale * 100).ToString("0") + "%";
        }

        /// <summary>
        /// Playback: take the canvas chrome out of shot. The view is untouched.
        /// </summary>
        public void SetPlaybackView(bool playing)
        {
            if (_isPlaybackView == playing) return;
            _isPlaybackView = playing;
            ApplyCanvasChrome();
        }

        /// <summary>
        /// Cinematic: the whole canvas at fit, no pan, view controls inert. The working view is
        /// saved on the way in and restored on the way out.
        /// </summary>
        public void SetCinematicView(bool cinematic)
        {
            if (_isCinematicView == cinematic) return;
            _isCinematicView = cinematic;

            if (cinematic)
            {
                _savedZoom = _canvasZoom; _savedPanX = _canvasPanX; _savedPanY = _canvasPanY;
                _canvasZoom = 1.0; _canvasPanX = 0; _canvasPanY = 0;
            }
            else
            {
                _canvasZoom = _savedZoom; _canvasPanX = _savedPanX; _canvasPanY = _savedPanY;
            }

            ApplyCanvasChrome();
            UpdateCanvasLayout();
        }

        // ONE OWNER FOR THE EDGE.
        //
        // Every condition that can hide it feeds this, and nothing else writes Visibility. The bug
        // this replaces: the engine set the edge visible from ApplyOverlayBox on EVERY FRAME while
        // playback set it hidden once, so playback lost - the chrome came straight back and the
        // label sat in the middle of the picture for the whole performance.
        //
        // HIDDEN, not dimmed. The edge stroke straddles the canvas boundary, so half of it lies over
        // the outermost pixels of any clip that reaches the edge - and a full-frame clip does. At low
        // opacity against black void it disappears; against a bright picture it reads as a line down
        // each side of the frame, which is precisely where it is least wanted.
        private void ApplyCanvasChrome()
        {
            if (CanvasEdge == null) return;

            bool show = !_isPlaybackView && !_isCinematicView && !_isEditView;
            var want = show ? Visibility.Visible : Visibility.Collapsed;
            if (CanvasEdge.Visibility != want) CanvasEdge.Visibility = want;
        }

        /// <summary>Back to fit-the-pane, centred. Bound to middle-click.</summary>
        /// <summary>Back to fit-the-pane, centred. Bound to middle-click.</summary>
        public void ResetCanvasView()
        {
            _canvasZoom = 1.0;
            _canvasPanX = 0;
            _canvasPanY = 0;
            UpdateCanvasLayout();
        }

        public void ZoomCanvas(double factor)
        {
            if (_isCinematicView) return;   // a performance is not a view to move around
            _canvasZoom = Math.Clamp(_canvasZoom * factor, 0.2, 8.0);
            UpdateCanvasLayout();
        }

        public void PanCanvas(double dx, double dy)
        {
            if (_isCinematicView) return;
            _canvasPanX += dx;
            _canvasPanY += dy;
            UpdateCanvasLayout();
        }

        // Setting this abandons any view drag in progress. Edit has no view controls, so a pan
        // that survived the mode change would keep swallowing pointer moves that Edit needs.
        // The geometry the framing is allowed to move within, published by the engine each time it
        // lays the edit box out. Held here because the pan and the wheel both happen in this class
        // and both have to respect it - clamping in one place and not the other just moves the
        // problem to the other gesture.
        public double FramingContentW, FramingContentH, FramingBoxW, FramingBoxH;

        /// <summary>
        /// Hold the framing inside its box: the picture may not be pushed past the edge.
        /// </summary>
        /// <remarks>
        /// The box is clipped (ApplyOverlayBox sets grid.Clip), so without this a drag could carry
        /// the picture off the edge and leave black behind it - a hard, unmarked wall that nothing
        /// on screen explained. Rather than draw the wall, do not let the picture reach it.
        ///
        /// ClipGeometry.Allowance is the same arithmetic the Ken Burns replay uses to decide how
        /// far a mark may travel, so the interactive limit and the animated one cannot disagree.
        /// A NEGATIVE allowance means the content is smaller than its box (zoomed below fit), where
        /// black is unavoidable and centred is the only sensible place to be.
        /// </remarks>
        private void ClampFraming()
        {
            if (ActiveTransform == null) return;
            if (FramingContentW <= 0 || FramingBoxW <= 0) return;

            double scale = ActiveTransform.ScaleX;
            var (ax, ay) = VideoDirector.Models.ClipGeometry.Allowance(
                FramingContentW, FramingContentH, FramingBoxW, FramingBoxH, scale);

            ActiveTransform.TranslateX = ax <= 0 ? 0 : Math.Clamp(ActiveTransform.TranslateX, -ax, ax);
            ActiveTransform.TranslateY = ay <= 0 ? 0 : Math.Clamp(ActiveTransform.TranslateY, -ay, ay);
        }

        private PlayerInputMode _inputMode = PlayerInputMode.Content;
        public PlayerInputMode InputMode
        {
            get => _inputMode;
            set { _inputMode = value; _isPanning = false; }
        }

        public DirectorPlayerControl()
        {
            this.InitializeComponent();

            RootLayer.SizeChanged += (s, e) => UpdateCanvasLayout();

            OverlayVisuals = new OverlayVisual[ViewModels.DirectorViewModel.MaxTracks];
            for (int i = 0; i < OverlayVisuals.Length; i++)
            {
                OverlayVisuals[i] = BuildOverlayVisual();
                // Insert at i so the tracks land beneath everything declared inside CanvasHost
                // and stack among themselves in track order.
                CanvasHost.Children.Insert(i, OverlayVisuals[i].Grid);

                // One border per slot, in slot order, so BorderHost.Children[i] is always slot i.
                BorderHost.Children.Add(OverlayVisuals[i].Border);
                StyleOverlayFrame(OverlayVisuals[i].Frame,
                                  i == 0 ? TrackPalette.Spine : TrackPalette.Overlay(i - 1),
                                  "T" + (i + 1));

            }
        }

        // One track's surfaces, matching what used to be four hand-copied XAML blocks. Every
        // property below was load-bearing:
        //   Opacity 0             - a slot shows nothing until the engine activates a clip on it
        //   IsHitTestVisible off  - InputLayer owns all pointer input
        //   Left/Top alignment    - the engine positions via Margin plus explicit Width/Height
        //   UniformToFill         - the surface overflows its box on purpose, and that surplus is
        //                           the only picture a Ken Burns pan has to move into; size it to
        //                           the box instead and every pan runs straight to black
        //   RenderTransformOrigin - 0.5,0.5 is RELATIVE to the element, which an overflowing
        //                           UniformToFill surface needs; an absolute pivot is not centre
        //
        // The frame's Rectangle must stay Children[0]: the engine finds it by that index to
        // colour it per track (SetOverlayRender), and StyleOverlayFrame appends the badge after.
        private static OverlayVisual BuildOverlayVisual()
        {
            var videoTransform = new Microsoft.UI.Xaml.Media.CompositeTransform();
            var video = new MediaPlayerElement
            {
                AreTransportControlsEnabled = false,
                Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = videoTransform
            };

            var stillTransform = new Microsoft.UI.Xaml.Media.CompositeTransform();
            var still = new Image
            {
                Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
                Visibility = Visibility.Collapsed,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = stillTransform
            };

            // A Canvas, because the engine places the surfaces with Canvas.Left/Top.
            var surfaces = new Canvas { IsHitTestVisible = false };
            surfaces.Children.Add(video);
            surfaces.Children.Add(still);

            // A Rectangle, not a Border, because a Border cannot draw dashes.
            var frame = new Grid { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            frame.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                // 3, not 2: this line is the resize surface, and it should look like something
                // you can take hold of rather than a hairline you have to aim at.
                StrokeThickness = 3,
                StrokeDashArray = new Microsoft.UI.Xaml.Media.DoubleCollection { 4, 4 },
                IsHitTestVisible = false
            });

            var border = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Fill = null,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };

            var grid = new Grid
            {
                Opacity = 0,
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            grid.Children.Add(surfaces);
            grid.Children.Add(frame);
            // NOTE: `border` is deliberately NOT added here. It goes into BorderHost, above every
            // track, because a shape underneath a video surface gets erased rather than blended.

            return new OverlayVisual
            {
                Grid = grid,
                Video = video,
                Still = still,
                Transform = videoTransform,
                StillTransform = stillTransform,
                Frame = frame,
                Border = border
            };
        }

        private static void StyleOverlayFrame(Microsoft.UI.Xaml.Controls.Grid frame, Windows.UI.Color color, string badgeText)
        {
            if (frame == null) return;
            var brush = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);

            if (frame.Children.Count > 0 && frame.Children[0] is Microsoft.UI.Xaml.Controls.Border border)
            {
                border.BorderBrush = brush;
                border.BorderThickness = new Microsoft.UI.Xaml.Thickness(2);
            }

            var badge = new Microsoft.UI.Xaml.Controls.Border
            {
                Background = brush,
                CornerRadius = new Microsoft.UI.Xaml.CornerRadius(3),
                Padding = new Microsoft.UI.Xaml.Thickness(5, 1, 5, 1),
                Margin = new Microsoft.UI.Xaml.Thickness(4),
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left,
                VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Top,
                Child = new Microsoft.UI.Xaml.Controls.TextBlock
                {
                    Text = badgeText,
                    FontSize = 10,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(TrackPalette.TextOn(color))
                }
            };
            frame.Children.Add(badge);
        }

        // Which PiP box (if any) is under the given InputLayer-space point; topmost track wins.
        // Returns a 0-based track index, or -1 for none. The overlay grids are positioned via
        // Margin + Width/Height in the same coordinate space as the full-screen InputLayer, so a
        // plain bounds test is valid.
        private int HitTestOverlaySlot(Point p)
        {
            // Topmost first, and each box is tested with its grab outset so the edge is catchable
            // from either side of the drawn line.
            for (int i = OverlayVisuals.Length - 1; i >= 0; i--)
                if (IsInsideBox(OverlayVisuals[i].Grid, p, GrabOutset)) return i;
            return -1;
        }

        private static bool IsInsideBox(Grid g, Point p, double outset = 0)
        {
            if (g == null || g.Opacity <= 0.01 || double.IsNaN(g.Width) || g.Width <= 0 || g.Height <= 0) return false;
            double left = g.Margin.Left - outset, top = g.Margin.Top - outset;
            double right = g.Margin.Left + g.Width + outset, bottom = g.Margin.Top + g.Height + outset;
            return p.X >= left && p.X <= right && p.Y >= top && p.Y <= bottom;
        }

        // Classify where in the box the cursor is: near an edge/corner (resize) or interior (move).
        private BoxGrab ClassifyGrab(int slot, Point p)
        {
            var g = OverlayVisuals[slot].Grid;
            double relX = p.X - g.Margin.Left;
            double relY = p.Y - g.Margin.Top;
            // Keep the threshold below half the box so a tiny box still has a movable interior.
            double t = Math.Min(HandleThreshold, Math.Min(g.Width, g.Height) / 3.0);

            // relX/relY can be NEGATIVE now: the hit test admits points just outside the edge, and
            // those are unambiguously a grab on that edge rather than a move.
            bool nearLeft = relX <= t, nearRight = relX >= g.Width - t;
            bool nearTop = relY <= t, nearBottom = relY >= g.Height - t;

            if (nearTop && nearLeft) return BoxGrab.TopLeft;
            if (nearTop && nearRight) return BoxGrab.TopRight;
            if (nearBottom && nearLeft) return BoxGrab.BottomLeft;
            if (nearBottom && nearRight) return BoxGrab.BottomRight;
            if (nearLeft) return BoxGrab.Left;
            if (nearRight) return BoxGrab.Right;
            if (nearTop) return BoxGrab.Top;
            if (nearBottom) return BoxGrab.Bottom;
            return BoxGrab.Move;
        }

        private void InputLayer_PointerPressed(object? sender, PointerRoutedEventArgs e)
        {
            // MIDDLE BUTTON DRIVES THE VIEW, never a clip - and it drops the selection first.
            //
            // ARRANGE ONLY. This block used to run before the mode check, so in Edit it cleared the
            // selected clip and started panning the canvas: the keyframe rectangles lost their
            // selection, the inspector emptied, and the whole mode came apart. Edit frames one clip
            // against the canvas and has its own meaning for drag and wheel - the view controls have
            // no business there.
            //
            // The wheel means "zoom the canvas" only while nothing is selected. Without clearing
            // here, the natural sequence - pan to somewhere, then scroll to zoom - would silently
            // resize whatever was still selected instead. That is a destructive accident you would
            // not notice, which is worth more than the mild annoyance of losing a selection.
            if (InputMode == PlayerInputMode.ArrangePips && !_isCinematicView &&
                e.GetCurrentPoint(RootLayer).Properties.IsMiddleButtonPressed)
            {
                DeselectRequested?.Invoke(this, EventArgs.Empty);
                _isPanning = true;
                _panMoved = false;
                _panLast = e.GetCurrentPoint(RootLayer).Position;
                InputLayer.CapturePointer(e.Pointer);
                e.Handled = true;
                return;
            }

            var p = e.GetCurrentPoint(CanvasHost).Position;

            if (InputMode == PlayerInputMode.ArrangePips)
            {
                _dragSlot = HitTestOverlaySlot(p);
                if (_dragSlot < 0)
                {
                    // Empty space drops the selection - the same outcome the middle button has, but
                    // without resetting the view, because a click on nothing says nothing about zoom.
                    DeselectRequested?.Invoke(this, EventArgs.Empty);
                    return;
                }
                OverlayBoxPointerPressed?.Invoke(this, _dragSlot);
                _dragGrab = ClassifyGrab(_dragSlot, p);
                _isDragging = true;
                _lastPointerPosition = p;
                InputLayer.CapturePointer(e.Pointer);
                return;
            }

            // Content mode: a press that reaches the input layer missed every rectangle (their
            // tabs and handles handle their own presses), so it is a deselect.
            CanvasCleared?.Invoke(this, EventArgs.Empty);

            _isDragging = true;
            _lastPointerPosition = p;
            InputLayer.CapturePointer(e.Pointer);
        }

        private void InputLayer_PointerMoved(object? sender, PointerRoutedEventArgs e)
        {
            if (_isPanning)
            {
                var q = e.GetCurrentPoint(RootLayer).Position;
                double dx = q.X - _panLast.X, dy = q.Y - _panLast.Y;
                if (Math.Abs(dx) > 0.5 || Math.Abs(dy) > 0.5) _panMoved = true;
                PanCanvas(dx, dy);
                _panLast = q;
                return;
            }
            if (!_isDragging) return;

            var p = e.GetCurrentPoint(CanvasHost).Position;
            var deltaX = p.X - _lastPointerPosition.X;
            var deltaY = p.Y - _lastPointerPosition.Y;
            _lastPointerPosition = p;

            if (InputMode == PlayerInputMode.ArrangePips)
            {
                if (_dragSlot >= 0) OverlayBoxDragged?.Invoke(this, (_dragSlot, _dragGrab, deltaX, deltaY));
                return;
            }

            if (ActiveTransform == null) return;
            ActiveTransform.TranslateX += deltaX;
            ActiveTransform.TranslateY += deltaY;
            ClampFraming();          // the picture stops at the edge instead of disappearing past it
            ViewportTransformChanged?.Invoke(this, EventArgs.Empty);
        }

        private int _contextSlot = -1;

        public event EventHandler<int>? ContextMenuOpening;

        private void InputLayer_RightTapped(object? sender, RightTappedRoutedEventArgs e)
        {
            if (InputMode != PlayerInputMode.ArrangePips) return;

            var p = e.GetPosition(CanvasHost);       // canvas space: for the hit test
            var paneP = e.GetPosition(InputLayer);   // pane space: for where the flyout opens
            _contextSlot = HitTestOverlaySlot(p);
            
            if (_contextSlot >= 0)
            {
                OverlayBoxPointerPressed?.Invoke(this, _contextSlot);
                ContextMenuOpening?.Invoke(this, _contextSlot);
                PipContextMenu.ShowAt(InputLayer, new FlyoutShowOptions { Position = paneP });
            }
        }

        public event EventHandler<int>? EditClipRequested;
        public event EventHandler<(int Slot, Models.BorderType Type)>? BorderTypeRequested;
        public event EventHandler<(int Slot, Windows.UI.Color Color)>? BorderColorRequested;
        public event EventHandler<(int Slot, double Thickness)>? BorderThicknessRequested;

        /// <summary>
        /// One handler for every size preset; the fraction rides on the menu item's Tag, so adding
        /// a size is a line of XAML rather than another event and another engine method.
        /// </summary>
        private void PipSize_Click(object? sender, RoutedEventArgs e)
        {
            if (_contextSlot < 0) return;
            var tag = (sender as FrameworkElement)?.Tag?.ToString();
            if (!string.IsNullOrEmpty(tag)) PipSizeRequested?.Invoke(this, (_contextSlot, tag));
        }

        // The preset travels as its tag rather than a number, because "fill" is not a fraction -
        // it depends on the window's shape, which only the engine knows.
        public event EventHandler<(int slot, string preset)>? PipSizeRequested;

        /// <summary>
        /// A layout applies to the whole composite, so unlike the size presets it carries no slot -
        /// the engine works out which clips are on screen.
        /// </summary>
        private void PipLayout_Click(object? sender, RoutedEventArgs e)
        {
            var tag = (sender as FrameworkElement)?.Tag?.ToString();
            if (!string.IsNullOrEmpty(tag)) LayoutRequested?.Invoke(this, tag);
        }

        public event EventHandler<string>? LayoutRequested;

        private void EditClip_Click(object? sender, RoutedEventArgs e)
        {
            if (_contextSlot >= 0) EditClipRequested?.Invoke(this, _contextSlot);
        }

        private void BorderType_Click(object? sender, RoutedEventArgs e)
        {
            if (_contextSlot >= 0 && sender is FrameworkElement fe && fe.Tag is string tag)
            {
                if (Enum.TryParse(tag, out Models.BorderType type))
                    BorderTypeRequested?.Invoke(this, (_contextSlot, type));
            }
        }

        private void BorderColor_Click(object? sender, RoutedEventArgs e)
        {
            if (_contextSlot >= 0 && sender is FrameworkElement fe && fe.Tag is string tag)
            {
                if (tag == "White") BorderColorRequested?.Invoke(this, (_contextSlot, Microsoft.UI.Colors.White));
                else if (tag == "Black") BorderColorRequested?.Invoke(this, (_contextSlot, Microsoft.UI.Colors.Black));
                else if (tag == "Red") BorderColorRequested?.Invoke(this, (_contextSlot, Microsoft.UI.Colors.Red));
                else if (tag == "Gold") BorderColorRequested?.Invoke(this, (_contextSlot, Microsoft.UI.Colors.Gold));
                else if (tag == "Blue") BorderColorRequested?.Invoke(this, (_contextSlot, Microsoft.UI.Colors.DodgerBlue));
                else if (tag == "Green") BorderColorRequested?.Invoke(this, (_contextSlot, Microsoft.UI.Colors.LimeGreen));
                else if (tag == "DarkGrey") BorderColorRequested?.Invoke(this, (_contextSlot, Microsoft.UI.Colors.DarkGray));
            }
        }

        private void BorderThickness_Click(object? sender, RoutedEventArgs e)
        {
            if (_contextSlot >= 0 && sender is FrameworkElement fe && fe.Tag is string tag)
            {
                if (double.TryParse(tag, out double t))
                    BorderThicknessRequested?.Invoke(this, (_contextSlot, t));
            }
        }

        public event EventHandler<int> HideRequested;
        public event EventHandler<int> LockRequested;

        private void PipHide_Click(object? sender, RoutedEventArgs e)
        {
            if (_contextSlot >= 0) HideRequested?.Invoke(this, _contextSlot);
        }

        private void PipLock_Click(object? sender, RoutedEventArgs e)
        {
            if (_contextSlot >= 0) LockRequested?.Invoke(this, _contextSlot);
        }
        public event EventHandler<(int slot, float opacity)> OpacityRequested;
        private void Opacity_Click(object? sender, RoutedEventArgs e)
        {
            if (_contextSlot >= 0 && sender is FrameworkElement fe && fe.Tag is string tag)
            {
                if (float.TryParse(tag, System.Globalization.CultureInfo.InvariantCulture, out float opacity))
                    OpacityRequested?.Invoke(this, (_contextSlot, opacity));
            }
        }
        // The Opacity items are RadioMenuFlyoutItems, which keep whatever was last clicked. With
        // nothing syncing them to the clip, the tick was a record of the last menu interaction in
        // this session rather than a reading of the clip under the cursor - so a clip at 100% could
        // show 25% ticked. The border menu had this method from the start; opacity never did.
        //
        // Nothing is ticked when the value came from the inspector slider and is not one of the
        // four presets. Showing the nearest preset would be a different lie.
        public void UpdateOpacityMenuState(double opacity)
        {
            const double eps = 0.001;
            PipOpacity100.IsChecked = Math.Abs(opacity - 1.00) < eps;
            PipOpacity75.IsChecked  = Math.Abs(opacity - 0.75) < eps;
            PipOpacity50.IsChecked  = Math.Abs(opacity - 0.50) < eps;
            PipOpacity25.IsChecked  = Math.Abs(opacity - 0.25) < eps;
        }

        public void UpdateBorderMenuState(Models.BorderType type, Windows.UI.Color color, double thickness)
        {
            PipBorderTypeNone.IsChecked = type == Models.BorderType.None;
            PipBorderTypeSolid.IsChecked = type == Models.BorderType.Solid;
            PipBorderTypeSoft.IsChecked = type == Models.BorderType.Soft;
            PipBorderTypeFilmStrip.IsChecked = type == Models.BorderType.FilmStrip;

            PipBorderColorWhite.IsChecked = color == Microsoft.UI.Colors.White;
            PipBorderColorBlack.IsChecked = color == Microsoft.UI.Colors.Black;
            PipBorderColorRed.IsChecked = color == Microsoft.UI.Colors.Red;
            PipBorderColorGold.IsChecked = color == Microsoft.UI.Colors.Gold;
            PipBorderColorBlue.IsChecked = color == Microsoft.UI.Colors.DodgerBlue;
            PipBorderColorGreen.IsChecked = color == Microsoft.UI.Colors.LimeGreen;
            PipBorderColorDarkGrey.IsChecked = color == Microsoft.UI.Colors.DarkGray;

            PipBorderThick2.IsChecked = thickness == 2;
            PipBorderThick4.IsChecked = thickness == 4;
            PipBorderThick8.IsChecked = thickness == 8;
        }

        private void InputLayer_PointerReleased(object? sender, PointerRoutedEventArgs e)
        {
            if (_isPanning)
            {
                _isPanning = false;
                InputLayer.ReleasePointerCapture(e.Pointer);
                // A middle CLICK - press and release without moving - is "put the view back".
                if (!_panMoved) ResetCanvasView();
                return;
            }
            _isDragging = false;
            _dragSlot = -1;
            InputLayer.ReleasePointerCapture(e.Pointer);
        }

        private void InputLayer_PointerCanceled(object? sender, PointerRoutedEventArgs e)
        {
            if (_isPanning)
            {
                _isPanning = false;
                InputLayer.ReleasePointerCapture(e.Pointer);
                // A middle CLICK - press and release without moving - is "put the view back".
                if (!_panMoved) ResetCanvasView();
                return;
            }
            _isDragging = false;
            _dragSlot = -1;
            InputLayer.ReleasePointerCapture(e.Pointer);
        }

        private void InputLayer_PointerWheelChanged(object? sender, PointerRoutedEventArgs e)
        {
            var pt = e.GetCurrentPoint(CanvasHost);
            int delta = pt.Properties.MouseWheelDelta;

            if (InputMode == PlayerInputMode.ArrangePips)
            {
                // Nothing selected: the wheel belongs to the view. Select a clip and it goes back
                // to resizing, which is why the middle button deselects before it pans.
                if (!HasSelection)
                {
                    ZoomCanvas(delta > 0 ? 1.1 : 1.0 / 1.1);
                    return;
                }

                int slot = HitTestOverlaySlot(pt.Position);
                if (slot >= 0) OverlayBoxWheel?.Invoke(this, (slot, delta));
                return;
            }

            // THE WHEEL IS PASSIVE. It magnifies the view and changes NOTHING about the clip.
            //
            // It used to write ActiveTransform, which IS the clip's framing, so every click was a
            // destructive edit: the picture reframed and the keyframe rectangles - drawn at Sc/St -
            // pulled away from the clip border as you scrolled. Zooming the canvas magnifies the
            // picture and all three rectangles together, so their alignment cannot change.
            //
            // The framing is written only by the Set buttons, which read this view, and by dragging
            // a rectangle. Both are deliberate acts; turning a wheel is not.
            ZoomCanvas(delta > 0 ? 1.1 : 1.0 / 1.1);
        }

        private void InputLayer_DoubleTapped(object? sender, DoubleTappedRoutedEventArgs e)
        {
            if (InputMode == PlayerInputMode.ArrangePips)
            {
                // Edit whatever is under the cursor - and nothing when there is nothing under it.
                // HitTestOverlaySlot returns -1 for a miss, which used to be forwarded and read by
                // the handler as "no PiP, so edit Track 1": double-clicking empty canvas to drop
                // focus opened an edit session on an unrelated clip instead.
                //
                // This does not affect a full-screen Track 1: its grid covers the pane, so a click
                // over its picture hit-tests to slot 0 and still opens. Only a genuine miss - the
                // letterbox bars, or a spot where no track has an active clip - is now inert.
                int slot = HitTestOverlaySlot(e.GetPosition(CanvasHost));
                if (slot >= 0) EditRequested?.Invoke(this, slot);
            }
            else
            {
                // Already editing one clip full-screen — double-tap exits.
                ExitEditRequested?.Invoke(this, EventArgs.Empty);
            }
            e.Handled = true;
        }

        private bool _isWysiwygDragging = false;
        private Point _wysiwygLastPos;
        private string _wysiwygTarget = "";
        private string _wysiwygAction = "";

        private void WysiwygTab_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var el = sender as FrameworkElement;
            if (el == null) return;
            _wysiwygTarget = el.Tag?.ToString() ?? "";
            _wysiwygAction = "Translate";
            _isWysiwygDragging = true;
            _wysiwygLastPos = e.GetCurrentPoint(WysiwygCanvas).Position;
            el.CapturePointer(e.Pointer);
            WysiwygBoxGrabbed?.Invoke(this, _wysiwygTarget);
            e.Handled = true;
        }

        private void WysiwygTab_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isWysiwygDragging || _wysiwygAction != "Translate") return;
            var pos = e.GetCurrentPoint(WysiwygCanvas).Position;
            double dx = pos.X - _wysiwygLastPos.X;
            double dy = pos.Y - _wysiwygLastPos.Y;
            _wysiwygLastPos = pos;
            WysiwygBoxManipulated?.Invoke(this, (_wysiwygTarget, _wysiwygAction, dx, dy));
            e.Handled = true;
        }

        private void WysiwygTab_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _isWysiwygDragging = false;
            (sender as FrameworkElement)?.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }

        private void WysiwygHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var el = sender as FrameworkElement;
            if (el == null) return;
            var tags = el.Tag?.ToString().Split(',');
            if (tags == null || tags.Length < 2) return;
            _wysiwygTarget = tags[0];
            _wysiwygAction = tags[1];
            _isWysiwygDragging = true;
            _wysiwygLastPos = e.GetCurrentPoint(WysiwygCanvas).Position;
            el.CapturePointer(e.Pointer);
            WysiwygBoxGrabbed?.Invoke(this, _wysiwygTarget);
            e.Handled = true;
        }

        private void WysiwygHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isWysiwygDragging || _wysiwygAction == "Translate") return;
            var pos = e.GetCurrentPoint(WysiwygCanvas).Position;
            double dx = pos.X - _wysiwygLastPos.X;
            double dy = pos.Y - _wysiwygLastPos.Y;
            _wysiwygLastPos = pos;
            WysiwygBoxManipulated?.Invoke(this, (_wysiwygTarget, _wysiwygAction, dx, dy));
            e.Handled = true;
        }

        private void WysiwygHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _isWysiwygDragging = false;
            (sender as FrameworkElement)?.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }
}





