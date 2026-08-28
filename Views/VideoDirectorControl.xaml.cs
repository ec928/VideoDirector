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
        // Where the window was before a performance took it full screen, so it can be put back.
        private Windows.Graphics.PointInt32? _restorePosition;
        private Windows.Graphics.SizeInt32? _restoreSize;

        private void ApplyCinematicPresenter(bool cinematic)
        {
            try
            {
                var appWindow = MainWindow.Instance?.AppWindow;
                if (appWindow == null) return;

                bool wantFullScreen = cinematic && ViewModel != null && ViewModel.IsPlaying;
                bool isFullScreen = appWindow.Presenter?.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen;
                if (wantFullScreen == isFullScreen) return;

                if (wantFullScreen)
                {
                    _restorePosition = appWindow.Position;
                    _restoreSize = appWindow.Size;

                    // Move onto the chosen display FIRST. Full screen takes whichever display the
                    // window is on, so the move has to happen before the presenter changes.
                    var target = ChosenDisplay();
                    if (target != null)
                        appWindow.Move(new Windows.Graphics.PointInt32(
                            target.WorkArea.X + 8, target.WorkArea.Y + 8));

                    appWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
                }
                else
                {
                    appWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.Overlapped);

                    // Back to the desk it came from, not wherever full screen left it.
                    if (_restoreSize is Windows.Graphics.SizeInt32 sz) appWindow.Resize(sz);
                    if (_restorePosition is Windows.Graphics.PointInt32 pt) appWindow.Move(pt);
                    _restorePosition = null;
                    _restoreSize = null;
                }
            }
            catch { }
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
        private void PresentDisplay_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not int idx || ViewModel == null) return;

            ViewModel.PresentDisplayIndex = idx;
            if (MainWindow.Instance != null) MainWindow.Instance.PresentDisplayIndex = idx;
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
                    PlayerControl.SetCinematicView(ViewModel.IsCinematicMode && ViewModel.IsPlaying);
                    ApplyCinematicPlaybackChrome();
                    ApplyCinematicPresenter(ViewModel.IsCinematicMode);
                }

                // Cinematic goes further: it locks the view to the whole canvas as well.
                if (ev.PropertyName == nameof(ViewModel.IsCinematicMode))
                {
                    // The view lock belongs to the PERFORMANCE, like full screen and the chrome.
                    // Arming cinematic on its own must leave zoom and pan alone.
                    PlayerControl.SetCinematicView(ViewModel.IsCinematicMode && ViewModel.IsPlaying);
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
