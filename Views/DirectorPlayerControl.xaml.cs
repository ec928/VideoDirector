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
        public Microsoft.UI.Xaml.Controls.Image Still;
        public Microsoft.UI.Xaml.Media.CompositeTransform Transform;
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

        // PiP manipulation events (Arrange mode). Raised from the full-screen InputLayer — the
        // only reliable pointer catcher, since the PiP's MediaPlayerElement video surface does
        // not raise its own pointer events. Move and resize share one channel; the grab mode
        // tells the engine whether to translate the box or reshape it from an edge/corner.
        public event EventHandler<(int slot, BoxGrab grab, double dx, double dy)> OverlayBoxDragged;
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
                new OverlayVisual { Grid = TrackGrid0, Video = TrackPlayer0, Still = TrackImage0, Transform = TrackTransform0, Frame = TrackFrame0 },
                new OverlayVisual { Grid = TrackGrid1, Video = TrackPlayer1, Still = TrackImage1, Transform = TrackTransform1, Frame = TrackFrame1 },
                new OverlayVisual { Grid = TrackGrid2, Video = TrackPlayer2, Still = TrackImage2, Transform = TrackTransform2, Frame = TrackFrame2 },
                new OverlayVisual { Grid = TrackGrid3, Video = TrackPlayer3, Still = TrackImage3, Transform = TrackTransform3, Frame = TrackFrame3 },
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

        private void InputLayer_RightTapped(object? sender, RightTappedRoutedEventArgs e)
        {
            if (InputMode != PlayerInputMode.ArrangePips) return;

            var p = e.GetPosition(InputLayer);
            _contextSlot = HitTestOverlaySlot(p);
            
            if (_contextSlot >= 0)
            {
                PipContextMenu.ShowAt(InputLayer, new FlyoutShowOptions { Position = p });
            }
        }

        public event EventHandler<int>? MakeFullScreenRequested;
        public event EventHandler<int>? MakeWindowRequested;

        private void MakeFullScreen_Click(object? sender, RoutedEventArgs e)
        {
            if (_contextSlot >= 0) MakeFullScreenRequested?.Invoke(this, _contextSlot);
        }

        private void MakeWindow_Click(object? sender, RoutedEventArgs e)
        {
            if (_contextSlot >= 0) MakeWindowRequested?.Invoke(this, _contextSlot);
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
    }
}
