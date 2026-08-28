using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VideoDirector.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using VideoDirector.Models;
using Microsoft.UI.Xaml.Input;

// VideoDirectorControl - state, construction, and the shell: the chrome auto-hide, the cinematic presenter and which display it takes over, and what happens on load.

namespace VideoDirector.Views
{
    public sealed partial class VideoDirectorControl : UserControl
    {
        public DirectorViewModel ViewModel { get; } = new DirectorViewModel();
        private VideoPlaybackEngine _playbackEngine;
        private DispatcherTimer _inactivityTimer;

        // Proportional timeline bar (§7E/F): px-per-second scale + the playhead line & handle.
        private double _timelinePxPerSec;
        private double _timelineZoomFactor = 1.0;
        private Microsoft.UI.Xaml.Shapes.Rectangle _playhead;
        private Microsoft.UI.Xaml.Shapes.Polygon _playheadKnob;
        private TextBlock _playheadTime;
        private Border _playheadBadge;
        private Microsoft.UI.Xaml.Shapes.Rectangle _loopRegionHighlight;
        private double _timelineLoopStartX;
        // Pointer state: ruler = scrub; clip row tap = select; clip row drag = move/reorder.
        private Windows.Foundation.Point _timelinePressPoint;
        private bool _timelinePressed;
        private bool _timelineScrubbing;
        private bool _timelineMovingClip;
        private CinematicOperation _dragClip;
        private bool _dragIsSpine;
        private double _dragGrabOffsetSec;
        private double _dragCursorX;      // live cursor x, for the spine ghost
        private int _dragInsertIndex;     // where the ghost would drop
        private Windows.Foundation.Point _lastHoverPoint;  // for the context menu's target
        private int _lastActiveSignature = -1;             // playback spotlight refresh guard
        private DispatcherTimer _pulseTimer;
        private double _pulsePhase = 0;
        private readonly Dictionary<CinematicOperation, List<UIElement>> _clipBlockElements = new();

        public VideoDirectorControl()
        {
            this.InitializeComponent();
            this.DataContext = ViewModel;

            _inactivityTimer = new DispatcherTimer();
            _inactivityTimer.Interval = TimeSpan.FromSeconds(5);
            _inactivityTimer.Tick += InactivityTimer_Tick;

            _pulseTimer = new DispatcherTimer();
            _pulseTimer.Interval = TimeSpan.FromMilliseconds(50);
            _pulseTimer.Tick += PulseTimer_Tick;
            
            this.PointerMoved += VideoDirectorControl_PointerMoved;

            // Wire up the engine once the control loads
            this.Loaded += VideoDirectorControl_Loaded;
        }

        // Chrome wakes on REAL pointer movement only.
        //
        // Two things fire this without the mouse going anywhere: the window changing to full screen
        // shifts the layout under a stationary cursor, and the pointer sitting still can still raise
        // events. Either was enough to bring the playbar straight back the instant a performance
        // started - which looked like the hide never happened.
        private Windows.Foundation.Point _lastPointerPos;
        private DateTime _chromeWakeBlockedUntil = DateTime.MinValue;

        private void VideoDirectorControl_PointerMoved(object? sender, PointerRoutedEventArgs e)
        {
            var p = e.GetCurrentPoint(this).Position;
            double dx = p.X - _lastPointerPos.X, dy = p.Y - _lastPointerPos.Y;
            _lastPointerPos = p;

            // A take is rolling: do not wake anything, and do not start the timer either. Not a
            // longer timeout - nothing at all. Whatever the window shows is what lands in the file,
            // and a playbar summoned by a nudge of the mouse would be in it for good.
            if (ViewModel.IsRecording) return;

            // A layout shift can move the pointer a long way in control coordinates without the mouse
            // moving at all, so the settling period after entering a performance is ignored outright.
            if (DateTime.UtcNow < _chromeWakeBlockedUntil) return;
            if (Math.Abs(dx) < 2 && Math.Abs(dy) < 2) return;

            _inactivityTimer.Stop();
            ViewModel.IsControlsVisible = true;
            _inactivityTimer.Start();
        }

        // Whether the pointer is over the transport, worked out from its BOUNDS rather than latched
        // from enter/exit events.
        //
        // The latch broke when the transport moved into the dock: hiding the chrome while the pointer
        // was over it meant PointerExited never fired, the flag stayed true for the rest of the
        // session, and the timer could never hide anything again. Geometry cannot get stuck.
        private bool PointerIsOverTransport()
        {
            if (FloatingPill == null) return false;
            if (FloatingPill.Visibility != Visibility.Visible) return false;
            if (FloatingPill.ActualWidth <= 0 || FloatingPill.ActualHeight <= 0) return false;

            try
            {
                var origin = FloatingPill.TransformToVisual(this)
                                         .TransformPoint(new Windows.Foundation.Point(0, 0));
                return _lastPointerPos.X >= origin.X
                    && _lastPointerPos.X <= origin.X + FloatingPill.ActualWidth
                    && _lastPointerPos.Y >= origin.Y
                    && _lastPointerPos.Y <= origin.Y + FloatingPill.ActualHeight;
            }
            catch { return false; }
        }


        private void InactivityTimer_Tick(object? sender, object e)
        {
            _inactivityTimer.Stop();

            // Nothing to hide during a take - the chrome is already locked away.
            if (ViewModel.IsRecording) return;

            // PLAYBACK is what hides the chrome, in either mode. Cinematic on its own is just a
            // full-screen window: toggling it should do nothing but toggle it, because a paused
            // frame you are still working on is not a performance.
            if (!PointerIsOverTransport() && ViewModel.IsPlaying)
            {
                ViewModel.IsControlsVisible = false;
            }
        }

        // Full screen belongs to the PERFORMANCE, not to arming it.
        //
        // Cinematic on its own is a choice about how the next playback will be presented; taking the
        // window full screen while nothing is rolling just puts the editor in a bigger window with
        // nothing gained. Full screen therefore follows cinematic AND playing, and drops back the
        // moment either ends.
        //
        // Guarded because the presenter call throws if the window is mid-teardown, and a failed
        // toggle must not take the app down with it.

        // The window a performance plays in when it belongs on another display. Null whenever no
        // performance is running there. The EDITOR window is never touched to make this happen.
        private Window _presentationWindow;
        private DirectorPlayerControl _presentationPlayer;

        // Where the editor was before it went full screen ON ITS OWN DISPLAY. Only used for the
        // no-target case; a performance on another display leaves the editor entirely alone.
        private Windows.Graphics.PointInt32? _restorePosition;
        private Windows.Graphics.SizeInt32? _restoreSize;

        private void ApplyCinematicPresenter(bool cinematic)
        {
            try
            {
                var appWindow = MainWindow.Instance?.AppWindow;
                if (appWindow == null) return;

                bool wantPerformance = cinematic && ViewModel != null && ViewModel.IsPlaying;
                bool performing = _presentationWindow != null
                    || appWindow.Presenter?.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen;
                if (wantPerformance == performing) return;

                var target = wantPerformance ? ChosenDisplay() : null;

                if (wantPerformance && target != null)
                {
                    // A CHOSEN DISPLAY GETS ITS OWN WINDOW. Sending the editor there worked, but
                    // the app disappeared off the desk for the length of the performance and could
                    // not be dismissed without finding it first. A second window leaves the editor
                    // exactly where it is, still showing the timeline.
                    OpenPresentationWindow(target);
                }
                else if (wantPerformance)
                {
                    // No display chosen: the performance is here, so this window becomes it.
                    _restorePosition = appWindow.Position;
                    _restoreSize = appWindow.Size;
                    appWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
                }
                else
                {
                    ClosePresentationWindow();

                    if (appWindow.Presenter?.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen)
                    {
                        appWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.Overlapped);
                        if (_restoreSize is Windows.Graphics.SizeInt32 sz) appWindow.Resize(sz);
                        if (_restorePosition is Windows.Graphics.PointInt32 pt) appWindow.Move(pt);
                    }
                    _restorePosition = null;
                    _restoreSize = null;
                }
            }
            catch { }
        }

        // Whether a performance belongs on some OTHER display. When it does, this window is not
        // the performance and must not dress like one: locking the editor to a fit canvas with its
        // chrome gone would leave it looking broken, since the picture has moved to the other
        // window and this canvas has nothing to draw. Read from the setting rather than from the
        // presentation window, so the answer does not depend on which runs first.
        private bool PerformanceGoesElsewhere => ChosenDisplay() != null;
        /// <summary>Opens a bare full-screen window on the target display and renders the performance into it.</summary>
        /// <remarks>
        /// Nothing is stripped to get "just the picture": a DirectorPlayerControl in a cinematic
        /// view already gates off frames, badges, marks and the canvas edge. Telling it what it is
        /// is enough - the composite, with every clip's own borders and motion, is what remains.
        /// </remarks>
        private void OpenPresentationWindow(Microsoft.UI.Windowing.DisplayArea target)
        {
            if (_presentationWindow != null || target == null || _playbackEngine == null) return;

            _presentationPlayer = new DirectorPlayerControl();
            _presentationWindow = new Window
            {
                Title = "VideoDirector - performance",
                Content = new Grid
                {
                    // Black, because the canvas rarely fills a display of a different shape and
                    // whatever surrounds it is part of the performance.
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black),
                    Children = { _presentationPlayer }
                }
            };

            // Closing it by any route ends the performance rather than leaving a headless one
            // running with its picture nowhere.
            _presentationWindow.Closed += (s, e) =>
            {
                if (ViewModel != null && ViewModel.IsCinematicMode) ViewModel.IsCinematicMode = false;
            };

            var b = target.OuterBounds;
            _presentationWindow.AppWindow.MoveAndResize(
                new Windows.Graphics.RectInt32(b.X, b.Y, b.Width, b.Height));
            _presentationWindow.AppWindow.SetPresenter(
                Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
            _presentationWindow.Activate();

            _presentationPlayer.SetCanvasEdgeVisible(false);
            _presentationPlayer.SetPlaybackView(true);
            _presentationPlayer.SetCinematicView(true);

            _playbackEngine.RetargetTo(_presentationPlayer);

            ShowPerformanceElsewhereNotice(target);
        }

        // Names the display, because "another display" is not much help when there are three and
        // one of them is an audio device pretending to be a monitor.
        private void ShowPerformanceElsewhereNotice(Microsoft.UI.Windowing.DisplayArea target)
        {
            if (PerformanceElsewhereNotice == null) return;

            int shown = (ViewModel?.PresentDisplayIndex ?? -1) + 1;
            if (PerformanceElsewhereText != null && shown > 0)
                PerformanceElsewhereText.Text = "Performing on Display " + shown;

            PerformanceElsewhereNotice.Visibility = Visibility.Visible;
        }
        private void ClosePresentationWindow()
        {
            if (_presentationWindow == null) return;

            var window = _presentationWindow;
            _presentationWindow = null;      // cleared first: Closed re-enters this method
            _presentationPlayer = null;

            // The picture comes home BEFORE the window holding it goes away, or the surfaces are
            // torn down while the players are still attached to them.
            try { _playbackEngine?.RetargetTo(PlayerControl); } catch { }
            if (PerformanceElsewhereNotice != null)
                PerformanceElsewhereNotice.Visibility = Visibility.Collapsed;
            try { window.Close(); } catch { }
        }
        // Null means "leave the window where it is".
        private Microsoft.UI.Windowing.DisplayArea ChosenDisplay()
        {
            int want = ViewModel?.PresentDisplayIndex ?? -1;
            if (want < 0) return null;

            try
            {
                var all = Microsoft.UI.Windowing.DisplayArea.FindAll();
                return want < all.Count ? all[want] : null;
            }
            catch { return null; }
        }

        // Built when the menu opens, so settings stay fresh.
        private void PreferencesFlyout_Opening(object? sender, object e)
        {
            if (PreferencesFlyout == null || ViewModel == null) return;

            PreferencesFlyout.Items.Clear();

            // 1. Clip Frames Toggle
            var framesToggle = new ToggleMenuFlyoutItem
            {
                Text = "Always show clip frames",
                IsChecked = MainWindow.Instance != null && MainWindow.Instance.AlwaysShowFullFrames
            };
            framesToggle.Click += (s, args) =>
            {
                if (MainWindow.Instance != null)
                {
                    MainWindow.Instance.AlwaysShowFullFrames = framesToggle.IsChecked;
                    _playbackEngine?.Invalidate(); // forces a render refresh
                }
            };
            PreferencesFlyout.Items.Add(framesToggle);

            // 2. Display Selector
            IReadOnlyList<Microsoft.UI.Windowing.DisplayArea> all = null;
            try { all = Microsoft.UI.Windowing.DisplayArea.FindAll(); } catch { }

            if (all != null && all.Count > 1)
            {
                PreferencesFlyout.Items.Add(new MenuFlyoutSeparator());
                
                var header = new MenuFlyoutItem { Text = "Presentation Display", IsEnabled = false };
                PreferencesFlyout.Items.Add(header);

                var current = new RadioMenuFlyoutItem
                {
                    Text = "Current display",
                    GroupName = "PresentDisplay",
                    IsChecked = ViewModel.PresentDisplayIndex < 0,
                    Tag = -1
                };
                current.Click += PresentDisplay_Click;
                PreferencesFlyout.Items.Add(current);

                for (int i = 0; i < all.Count; i++)
                {
                    var b = all[i].OuterBounds;
                    var item = new RadioMenuFlyoutItem
                    {
                        Text = "Display " + (i + 1) + "  (" + b.Width + " x " + b.Height + ")",
                        GroupName = "PresentDisplay",
                        IsChecked = ViewModel.PresentDisplayIndex == i,
                        Tag = i
                    };
                    item.Click += PresentDisplay_Click;
                    PreferencesFlyout.Items.Add(item);
                }
            }
        }

        // NOTE: this deliberately does NOT move the window to prove the display is visible. That
        // was tried and it is a worse bug than the one it guards: moving the ONLY window to a
        // display nobody can see, to ask a question nobody can read, means the app has vanished -
        // and what a user does when the app vanishes is kill it. The geometry saved on the way out
        // is then the invisible display's, recorded while NOT full screen and while the display
        // choice is still the previous one, so every guard in ConfigureWindow misses it and the
        // strand is permanent. A safety check whose failure mode is the failure it prevents is not
        // a safety check. Presenting is made safe in MainWindow instead, where the geometry that
        // does the damage is written.
        private async void PresentDisplay_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not int idx || ViewModel == null) return;

            int previous = ViewModel.PresentDisplayIndex;
            ApplyPresentDisplay(idx);

            // "Current display" needs no warning: it is the one being looked at right now.
            if (idx < 0 || XamlRoot == null) return;

            // The question is asked ON THE DISPLAY BEING CHOSEN, which is the entire point: if it
            // cannot be read there, nobody clicks Keep, and the choice reverts. Asking on the
            // display the user is already looking at proves nothing at all.
            //
            // What makes this safe is that it is a SEPARATE window. The editor window never moves,
            // so no geometry of the app is ever written from an unseeable display, and a probe left
            // stranded on one simply times out and closes. The earlier attempt sent the ONE window
            // there, which is why an unanswered question turned into an unrecoverable app.
            if (!await ConfirmOnTargetDisplayAsync(idx))
                ApplyPresentDisplay(previous);
        }

        private void ApplyPresentDisplay(int idx)
        {
            if (ViewModel == null) return;
            ViewModel.PresentDisplayIndex = idx;
            if (MainWindow.Instance != null) MainWindow.Instance.PresentDisplayIndex = idx;
        }

        /// <summary>Puts a window on the candidate display and waits to be told someone can see it.</summary>
        /// <remarks>
        /// Nothing in the system can tell a monitor from an HDMI audio sink - both enumerate through
        /// DisplayArea.FindAll and the sink reports an ordinary 720x480 - so the only real test is
        /// whether a human sitting there can act on it. Silence is a NO: an unanswered probe means
        /// nobody was looking, which is exactly the condition being screened for.
        /// </remarks>
        private async System.Threading.Tasks.Task<bool> ConfirmOnTargetDisplayAsync(int idx)
        {
            const int seconds = 10;

            Microsoft.UI.Windowing.DisplayArea area = null;
            try
            {
                var all = Microsoft.UI.Windowing.DisplayArea.FindAll();
                if (idx < all.Count) area = all[idx];
            }
            catch { }
            if (area == null) return false;

            var done = new System.Threading.Tasks.TaskCompletionSource<bool>();
            var probe = new Window { Title = "VideoDirector - confirm display" };

            var heading = new TextBlock
            {
                Text = "Display " + (idx + 1) + " selected",
                FontSize = 22,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            var detail = new TextBlock
            {
                Text = "Performances will play here. The editor window stays where it is.",
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0)
            };
            var countdown = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Opacity = 0.7,
                Margin = new Thickness(0, 16, 0, 0)
            };
            var keep = new Button
            {
                Content = "Use this display",
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 20, 0, 0)
            };
            keep.Click += (s, e) => { done.TrySetResult(true); probe.Close(); };

            var panel = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(24)
            };
            panel.Children.Add(heading);
            panel.Children.Add(detail);
            panel.Children.Add(countdown);
            panel.Children.Add(keep);
            probe.Content = new Grid { Children = { panel } };

            // Closing by any route that is not the button counts as "not seen".
            probe.Closed += (s, e) => done.TrySetResult(false);

            int left = seconds;
            countdown.Text = "Reverting in " + left + "s";
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick += (s, e) =>
            {
                left--;
                if (left <= 0) { timer.Stop(); done.TrySetResult(false); try { probe.Close(); } catch { } }
                else countdown.Text = "Reverting in " + left + "s";
            };

            try
            {
                // Sized to the display rather than to a fixed guess: the case being screened for is
                // a 720x480 sink, and a window bigger than the screen would put the button off it.
                var wa = area.WorkArea;
                int w = Math.Min(520, (int)(wa.Width * 0.9));
                int h = Math.Min(300, (int)(wa.Height * 0.9));
                probe.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                    wa.X + (wa.Width - w) / 2, wa.Y + (wa.Height - h) / 2, w, h));

                if (probe.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter op)
                    op.IsAlwaysOnTop = true;

                probe.Activate();
                timer.Start();
                return await done.Task;
            }
            catch { return false; }
            finally
            {
                timer.Stop();
                try { probe.Close(); } catch { }
            }
        }

        private void VideoDirectorControl_Loaded(object? sender, RoutedEventArgs e)
        {
            _playbackEngine = new VideoPlaybackEngine(PlayerControl, ViewModel);
            PlayerControl.ViewportTransformChanged += PlayerControl_ViewportTransformChanged;
            PlayerControl.SizeChanged += PlayerControl_SizeChanged;
            PlayerControl.EditRequested += PlayerControl_EditRequested;

            // The wheel only zooms the canvas while nothing is selected, so the player has to know.
            ViewModel.PropertyChanged += (s, ev) =>
            {
                if (ev.PropertyName == nameof(ViewModel.HasSelection) ||
                    ev.PropertyName == nameof(ViewModel.SelectedClip))
                    PlayerControl.HasSelection = ViewModel.HasSelection;

                // Playback takes the view: fit the whole canvas, drop the chrome, ignore zoom and
                // pan until it stops.
                // Each chevron points the way its panel will go when clicked.
                if (ev.PropertyName == nameof(ViewModel.IsTrackDockOpen) && TrackDockTabIcon != null)
                    TrackDockTabIcon.Glyph = ViewModel.IsTrackDockOpen ? "\uE70D" : "\uE70E";

                if (ev.PropertyName == nameof(ViewModel.IsTrackDockOpen) ||
                    ev.PropertyName == nameof(ViewModel.IsTrackDockVisible) ||
                    ev.PropertyName == nameof(ViewModel.IsChromeVisible))
                    DispatcherQueue.TryEnqueue(UpdateChromeInset);

                if (ev.PropertyName == nameof(ViewModel.IsInspectorOpen) && InspectorTabIcon != null)
                    InspectorTabIcon.Glyph = ViewModel.IsInspectorOpen ? "\uE76C" : "\uE76B";

                // The taskbar and Alt-Tab should say which project too.
                if (ev.PropertyName == nameof(ViewModel.ProjectName) && MainWindow.Instance != null)
                    MainWindow.Instance.Title = "Video Director  -  " + ViewModel.ProjectName;

                if (ev.PropertyName == nameof(ViewModel.IsPlaying))
                {
                    PlayerControl.SetPlaybackView(ViewModel.IsPlaying);
                    PlayerControl.SetCinematicView(ViewModel.IsCinematicMode && ViewModel.IsPlaying && !PerformanceGoesElsewhere);
                    ApplyCinematicPlaybackChrome();
                    ApplyCinematicPresenter(ViewModel.IsCinematicMode);
                }

                // Cinematic goes further: it locks the view to the whole canvas as well.
                if (ev.PropertyName == nameof(ViewModel.IsCinematicMode))
                {
                    // The view lock belongs to the PERFORMANCE, like full screen and the chrome.
                    // Arming cinematic on its own must leave zoom and pan alone.
                    PlayerControl.SetCinematicView(ViewModel.IsCinematicMode && ViewModel.IsPlaying && !PerformanceGoesElsewhere);
                    ApplyCinematicPlaybackChrome();

                    // Arming cinematic is the deliberate "I am about to show this" moment, and the
                    // last one where a missing file is still cheap to find out about.
                    if (ViewModel.IsCinematicMode)
                        _ = ReportMissingMediaAsync("Some clips will not play.");
                }
            };
            PlayerControl.DeselectRequested += (s, ev) => ViewModel.SelectedClip = null;

            // The canvas has to exist before the first composite.
            //
            // CALLED, not subscribed. This method IS the Loaded handler, so "Loaded += ..." here
            // hooked an event that had already fired and could never fire again - which is why the
            // canvas stayed at its XAML default of 1920x1080 against a 2752-wide pane and the fit
            // read 107% for the whole session. The enqueued second call catches the case where the
            // pane has not reached its final size by the time Loaded runs.
            ApplyCanvasSize();
            DispatcherQueue.TryEnqueue(ApplyCanvasSize);
            PlayerControl.SizeChanged += (s, ev) => ApplyCanvasSize();
            SetTransportDocked(true);   // out of the picture by default

            // The display choice lives with the window settings, not the project: it describes the
            // room you are presenting in, not the piece.
            if (MainWindow.Instance != null)
                ViewModel.PresentDisplayIndex = MainWindow.Instance.PresentDisplayIndex;

            // The canvas fits the part of the pane the dock is not covering.
            TrackDock.SizeChanged += (s, ev) => UpdateChromeInset();
            UpdateChromeInset();
            PlayerControl.ExitEditRequested += (s, ev) => ExitEditMode();
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            ViewModel.EditTargetChanged += ViewModel_EditTargetChanged;

            ViewModel.Tracks.CollectionChanged += (s, ev) => { HookOverlayTrackClips(); BuildTimelineBar(); _playbackEngine?.RefreshComposite(); };
            ViewModel.ClipPropertyChanged += (s, ev) => { _playbackEngine?.RefreshComposite(); };
            // An image clip is a still with no frame to seek to - bake its bitmap now so the
            // first activation renders from it instead of failing a MediaSource open first.
            ViewModel.ClipAdded += (s, clip) => { if (clip is { IsStill: true }) _playbackEngine?.PrebakeStillFrame(clip); };
            HookOverlayTrackClips();
            BuildTimelineBar();
        }

        // Each track owns its own clip collection, so the timeline has to watch them all.
        private readonly System.Collections.Generic.HashSet<TimelineTrack> _hookedTracks = new();
        private void HookOverlayTrackClips()
        {
            foreach (var track in ViewModel.Tracks)
                if (_hookedTracks.Add(track))
                    track.Clips.CollectionChanged += (s, ev) => { BuildTimelineBar(); _playbackEngine?.RefreshComposite(); };
        }

        // The track that owns a given clip.
        private TimelineTrack TrackOf(CinematicOperation clip)
        {
            foreach (var track in ViewModel.Tracks)
                if (track.Clips.Contains(clip)) return track;
            return null;
        }

        private void AlwaysShowFramesToggle_Loaded(object sender, RoutedEventArgs e)
        {
            if (MainWindow.Instance != null && sender is Microsoft.UI.Xaml.Controls.ToggleSwitch ts)
            {
                ts.IsOn = MainWindow.Instance.AlwaysShowFullFrames;
            }
        }

        private void AlwaysShowFramesToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (MainWindow.Instance != null && sender is Microsoft.UI.Xaml.Controls.ToggleSwitch ts)
            {
                MainWindow.Instance.AlwaysShowFullFrames = ts.IsOn;
                _playbackEngine?.Invalidate(); // forces a render refresh
            }
        }
    }
}
