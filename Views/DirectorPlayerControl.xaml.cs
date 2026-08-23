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

    // What grabbing the PiP box does: move it, or resize it from a specific edge/corner.
    // Determined at pointer-press from where in the box the cursor is (interior = move,
    // near an edge = one-dimension resize, near a corner = two-dimension resize).
    public enum BoxGrab { Move, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

    // One upper track's render surfaces. Pre-declared and bounded (3); the engine addresses them
    // generically by track index via OverlayVisuals[i] — no per-track code paths (§7B).
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
    }

    public sealed partial class DirectorPlayerControl : UserControl
    {
        private bool _isDragging = false;
        private Point _lastPointerPosition;
        private int _dragSlot = -1;
        private BoxGrab _dragGrab;

        // Index 0..2 == overlay track 0..2. Built once from the pre-declared XAML units.
        public OverlayVisual[] OverlayVisuals { get; private set; }

        // How close (px) to an edge counts as grabbing that edge for a resize.
        private const double HandleThreshold = 20.0;

        public event EventHandler ViewportTransformChanged;
        public Microsoft.UI.Xaml.Media.CompositeTransform ActiveTransform { get; set; }

        // PiP manipulation events (Arrange mode). Raised from the full-screen InputLayer
        public event EventHandler<(int slot, BoxGrab grab, double dx, double dy)> OverlayBoxDragged;
        public event EventHandler<(string markType, string action, double dx, double dy)> WysiwygBoxManipulated;
        public event EventHandler<string> WysiwygBoxGrabbed;

        // Raised when the wheel turns while a framing rectangle is selected. The engine owns the
        // resize maths, so the control only reports the gesture.
        public event EventHandler<int>? SelectedMarkWheel;

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

        public PlayerInputMode InputMode { get; set; } = PlayerInputMode.Content;

        public DirectorPlayerControl()
        {
            this.InitializeComponent();

            OverlayVisuals = new[]
            {
                new OverlayVisual { Grid = TrackGrid0, Video = TrackPlayer0, Still = TrackImage0, StillTransform = StillTransform0, Transform = TrackTransform0, Frame = TrackFrame0 },
                new OverlayVisual { Grid = TrackGrid1, Video = TrackPlayer1, Still = TrackImage1, StillTransform = StillTransform1, Transform = TrackTransform1, Frame = TrackFrame1 },
                new OverlayVisual { Grid = TrackGrid2, Video = TrackPlayer2, Still = TrackImage2, StillTransform = StillTransform2, Transform = TrackTransform2, Frame = TrackFrame2 },
                new OverlayVisual { Grid = TrackGrid3, Video = TrackPlayer3, Still = TrackImage3, StillTransform = StillTransform3, Transform = TrackTransform3, Frame = TrackFrame3 },
            };

            for (int i = 0; i < OverlayVisuals.Length; i++)
                StyleOverlayFrame(OverlayVisuals[i].Frame, i == 0 ? TrackPalette.Spine : TrackPalette.Overlay(i - 1), "T" + (i + 1));
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
            for (int i = OverlayVisuals.Length - 1; i >= 0; i--)
                if (IsInsideBox(OverlayVisuals[i].Grid, p)) return i;
            return -1;
        }

        private static bool IsInsideBox(Grid g, Point p)
        {
            if (g == null || g.Opacity <= 0.01 || double.IsNaN(g.Width) || g.Width <= 0 || g.Height <= 0) return false;
            double left = g.Margin.Left, top = g.Margin.Top;
            return p.X >= left && p.X <= left + g.Width && p.Y >= top && p.Y <= top + g.Height;
        }

        // Classify where in the box the cursor is: near an edge/corner (resize) or interior (move).
        private BoxGrab ClassifyGrab(int slot, Point p)
        {
            var g = OverlayVisuals[slot].Grid;
            double relX = p.X - g.Margin.Left;
            double relY = p.Y - g.Margin.Top;
            // Keep the threshold below half the box so a tiny box still has a movable interior.
            double t = Math.Min(HandleThreshold, Math.Min(g.Width, g.Height) / 3.0);
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
            var p = e.GetCurrentPoint(InputLayer).Position;

            if (InputMode == PlayerInputMode.ArrangePips)
            {
                _dragSlot = HitTestOverlaySlot(p);
                if (_dragSlot < 0) return; // clicked empty canvas
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
            if (!_isDragging) return;

            var p = e.GetCurrentPoint(InputLayer).Position;
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
            ViewportTransformChanged?.Invoke(this, EventArgs.Empty);
        }

        private int _contextSlot = -1;

        public event EventHandler<int>? ContextMenuOpening;

        private void InputLayer_RightTapped(object? sender, RightTappedRoutedEventArgs e)
        {
            if (InputMode != PlayerInputMode.ArrangePips) return;

            var p = e.GetPosition(InputLayer);
            _contextSlot = HitTestOverlaySlot(p);
            
            if (_contextSlot >= 0)
            {
                OverlayBoxPointerPressed?.Invoke(this, _contextSlot);
                ContextMenuOpening?.Invoke(this, _contextSlot);
                PipContextMenu.ShowAt(InputLayer, new FlyoutShowOptions { Position = p });
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
            _isDragging = false;
            _dragSlot = -1;
            InputLayer.ReleasePointerCapture(e.Pointer);
        }

        private void InputLayer_PointerCanceled(object? sender, PointerRoutedEventArgs e)
        {
            _isDragging = false;
            _dragSlot = -1;
            InputLayer.ReleasePointerCapture(e.Pointer);
        }

        private void InputLayer_PointerWheelChanged(object? sender, PointerRoutedEventArgs e)
        {
            var pt = e.GetCurrentPoint(InputLayer);
            int delta = pt.Properties.MouseWheelDelta;

            if (InputMode == PlayerInputMode.ArrangePips)
            {
                int slot = HitTestOverlaySlot(pt.Position);
                if (slot >= 0) OverlayBoxWheel?.Invoke(this, (slot, delta));
                return;
            }

            // A selected framing rectangle takes the wheel: resizing it IS the zoom for that
            // keyframe, and resizing the live view underneath at the same time would fight it.
            // Deselect (click empty canvas) and the wheel returns to zooming the view.
            if (IsMarkSelected)
            {
                SelectedMarkWheel?.Invoke(this, delta);
                return;
            }

            if (ActiveTransform == null) return;
            double zoomFactor = delta > 0 ? 1.1 : (1.0 / 1.1);
            double newScale = Math.Clamp(ActiveTransform.ScaleX * zoomFactor, 0.1, 10.0);
            ActiveTransform.ScaleX = newScale;
            ActiveTransform.ScaleY = newScale;
            ViewportTransformChanged?.Invoke(this, EventArgs.Empty);
        }

        private void InputLayer_DoubleTapped(object? sender, DoubleTappedRoutedEventArgs e)
        {
            if (InputMode == PlayerInputMode.ArrangePips)
            {
                // Edit whatever is under the cursor: an overlay PiP, or Track 1 if not on one.
                EditRequested?.Invoke(this, HitTestOverlaySlot(e.GetPosition(InputLayer)));
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





