using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.IO;
using System.Text.Json;
using WinRT.Interop;

namespace VideoDirector
{
    public sealed partial class MainWindow : Window
    {
        public static MainWindow Instance { get; private set; } = null!;

        /// <summary>The editor control, so startup arguments can drive it.</summary>
        public Views.VideoDirectorControl Director => DirectorControl;
        private readonly AppWindow _appWindow;
        public new AppWindow AppWindow => _appWindow;

        private sealed class AppSettings
        {
            public int Version { get; set; } = 1;
            public int WindowWidth { get; set; } = -1;
            public int WindowHeight { get; set; } = -1;
            public int WindowX { get; set; } = -1;
            public int WindowY { get; set; } = -1;

            // Which display a performance takes over. -1 is "wherever the window already is".
            // Persisted because it is exactly the setting you configure once, before an event,
            // and would not think to set again after a restart.
            public int PresentDisplayIndex { get; set; } = -1;

            // User preference to draw clip frames in full, without occlusion.
            public bool AlwaysShowFullFrames { get; set; } = false;
        }

        private sealed class OldModernSettings
        {
            public int DirectorWindowWidth { get; set; } = -1;
            public int DirectorWindowHeight { get; set; } = -1;
            public int DirectorWindowX { get; set; } = -1;
            public int DirectorWindowY { get; set; } = -1;
        }

        private readonly string _settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VideoDirector", "settings.json");

        private AppSettings _currentSettings = new AppSettings();

        public MainWindow()
        {
            Instance = this;
            this.InitializeComponent();

            this.Title = "VideoDirector";
            if (this.Content is FrameworkElement fe) fe.RequestedTheme = Microsoft.UI.Xaml.ElementTheme.Dark;
            this.SystemBackdrop = new MicaBackdrop();

            var hwnd = WindowNative.GetWindowHandle(this);
            var appWindowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(appWindowId);

            this.ExtendsContentIntoTitleBar = true;
            this.SetTitleBar(DragZone);

            ConfigureWindow();

            // Closed fires when the window is already going away - too late to keep anything. The
            // AppWindow.Closing event is the one that can still be cancelled.
            _appWindow.Closing += AppWindow_Closing;

            this.Closed += (s, e) =>
            {
                Instance = null!;
                SaveAllSettings();
            };
        }

        // Set once the user has answered the prompt, so the second pass straight through.
        private bool _closeConfirmed;

        private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (_closeConfirmed) return;

            var control = DirectorControl;
            if (control == null || !control.HasUnsavedChanges) return;

            // Cancel BEFORE the first await. The event args are only honoured while the handler is
            // still running synchronously; awaiting first lets the close through and the dialog
            // would then be asked of a window that no longer exists.
            args.Cancel = true;

            var choice = await control.ConfirmUnsavedAsync();
            if (choice == Views.UnsavedChoice.Cancel) return;
            if (choice == Views.UnsavedChoice.Save && !await control.SaveProjectAsync()) return;

            _closeConfirmed = true;
            this.Close();
        }

        private void RootGrid_PointerMoved(object? sender, PointerRoutedEventArgs e)
        {
            if (DragZone == null) return;
            var position = e.GetCurrentPoint(RootGrid).Position;
            if (position.Y <= 45) DragZone.Opacity = 1.0;
            else DragZone.Opacity = 0.0;
        }

        private void ConfigureWindow()
        {
            AppSettings settings = new AppSettings();

            if (File.Exists(_settingsPath))
            {
                try
                {
                    string json = File.ReadAllText(_settingsPath);
                    var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                    if (loaded != null) settings = loaded;
                    PresentDisplayIndex = settings.PresentDisplayIndex;
                    AlwaysShowFullFrames = settings.AlwaysShowFullFrames;
                }
                catch { }
            }
            else
            {
                // Try inheriting from old ModernImageViewer settings if first run
                try
                {
                    string oldPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "ModernImageViewer", "settings.json");
                    if (File.Exists(oldPath))
                    {
                        var old = JsonSerializer.Deserialize<OldModernSettings>(File.ReadAllText(oldPath));
                        if (old != null && old.DirectorWindowWidth != -1 && old.DirectorWindowHeight != -1)
                        {
                            settings.WindowWidth = old.DirectorWindowWidth;
                            settings.WindowHeight = old.DirectorWindowHeight;
                            settings.WindowX = old.DirectorWindowX;
                            settings.WindowY = old.DirectorWindowY;
                        }
                    }
                }
                catch { }
            }

            if (settings.WindowWidth == -1 || settings.WindowHeight == -1)
            {
                try
                {
                    var displayArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
                    int targetHeight = (int)(displayArea.WorkArea.Height * 0.9375);
                    int targetWidth = (int)(targetHeight * (16.0 / 9.0));
                    settings.WindowWidth = targetWidth;
                    settings.WindowHeight = targetHeight;
                }
                catch
                {
                    settings.WindowWidth = 1600;
                    settings.WindowHeight = 900;
                }
            }

            _currentSettings = settings;

            int w = settings.WindowWidth < 300 ? 1600 : settings.WindowWidth;
            int h = settings.WindowHeight < 300 ? 900 : settings.WindowHeight;

            // A saved position is only worth honouring if it is still somewhere the user can SEE.
            // Restoring it blind is how the app strands itself: present onto a display that is not
            // a real monitor (an HDMI audio sink enumerates as one, 720x480 at some far corner of
            // the desktop), close while the window is still there, and every launch afterwards puts
            // the window back onto the invisible screen. There is no way out from inside the app,
            // because the app is what is invisible. Verified against a real stranded settings.json:
            // WindowX/Y 3416,1440 against a DISPLAY2 at exactly 3416,1440, 720x480.
            bool leftOnPresentationDisplay =
                IsOnPresentationDisplay(settings.WindowX, settings.WindowY, settings.PresentDisplayIndex);

            // The SIZE came from that display too - 576x384 is a 720x480 screen at 125% - so a
            // window rescued by position alone still opens as a postage stamp. Geometry left behind
            // by a performance is discarded whole, not in halves.
            if (leftOnPresentationDisplay) { w = 1600; h = 900; }

            _appWindow.Resize(new Windows.Graphics.SizeInt32(w, h));

            if (settings.WindowX != -1 && settings.WindowY != -1 && !leftOnPresentationDisplay &&
                IsPositionVisible(settings.WindowX, settings.WindowY, w, h))
                _appWindow.Move(new Windows.Graphics.PointInt32(settings.WindowX, settings.WindowY));
        }

        /// <summary>Is a window at this rect landing somewhere the user can actually see?</summary>
        /// <remarks>
        /// Tests the window's TITLE BAR strip rather than the whole rect, because that is what has
        /// to be reachable to drag the window anywhere else - a window whose body overlaps a monitor
        /// but whose bar does not is still unusable. Requires a real overlap, not a touching edge.
        /// Any failure to enumerate answers "yes": refusing to restore a position is a far smaller
        /// harm than refusing to start, and the fallback below is a sane one either way.
        /// </remarks>
        /// <summary>Is this saved desk position actually a performance left behind on the presentation display?</summary>
        /// <remarks>
        /// It should never be. The desk position is where the user WORKS, and a performance always
        /// restores it on the way out - unless the app was closed mid-performance, in which case the
        /// position saved is the presentation display's. If that display is one the user cannot see
        /// (an HDMI audio sink enumerates as a 720x480 monitor), every later launch restores the
        /// window onto it and the app is unreachable, with nothing on screen to fix it with.
        ///
        /// So a saved position sitting on the chosen presentation display is treated as leftover and
        /// discarded. The cost when wrong - someone who presents to the same monitor they work on -
        /// is one window opening at the default position. The cost of trusting it is an app that
        /// cannot be recovered without hand-editing settings.json.
        /// </remarks>
        private static bool IsOnPresentationDisplay(int x, int y, int presentIndex)
        {
            if (presentIndex < 0) return false;
            try
            {
                var all = DisplayArea.FindAll();
                if (presentIndex >= all.Count) return false;
                var b = all[presentIndex].OuterBounds;
                return x >= b.X && x < b.X + b.Width && y >= b.Y && y < b.Y + b.Height;
            }
            catch { return false; }
        }

        private static bool IsPositionVisible(int x, int y, int width, int height)
        {
            try
            {
                const int barHeight = 32;      // enough of the top edge to grab
                const int minOverlap = 120;    // and enough of it to aim at

                foreach (var area in DisplayArea.FindAll())
                {
                    var b = area.WorkArea;
                    int ox = Math.Min(x + width, b.X + b.Width) - Math.Max(x, b.X);
                    int oy = Math.Min(y + barHeight, b.Y + b.Height) - Math.Max(y, b.Y);
                    if (ox >= minOverlap && oy > 0) return true;
                }
                return false;
            }
            catch { return true; }
        }

        /// <summary>Which display a performance takes over. Saved with the window settings.</summary>
        public int PresentDisplayIndex { get; set; } = -1;

        /// <summary>User preference to always show full clip frames instead of occluding them behind higher tracks.</summary>
        public bool AlwaysShowFullFrames { get; set; } = false;

        private void SaveAllSettings()
        {
            try
            {
                if (_appWindow == null) return;
                if (_appWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Minimized) return;

                // While full screen the window's position and size belong to the PRESENTATION
                // display, not to the desk the user works at. Saving them sends the next launch to
                // wherever the performance happened to be - and if that was a display they cannot
                // see, the app never comes back. Keep whatever was saved last; the restore path in
                // ApplyCinematicPresenter puts the real geometry back on exit anyway.
                bool presenting = _appWindow.Presenter?.Kind == AppWindowPresenterKind.FullScreen;
                if (!presenting)
                {
                    _currentSettings.WindowWidth = _appWindow.Size.Width;
                    _currentSettings.WindowHeight = _appWindow.Size.Height;
                    _currentSettings.WindowX = _appWindow.Position.X;
                    _currentSettings.WindowY = _appWindow.Position.Y;
                }
                _currentSettings.PresentDisplayIndex = PresentDisplayIndex;
                _currentSettings.AlwaysShowFullFrames = AlwaysShowFullFrames;

                string json = JsonSerializer.Serialize(_currentSettings, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                string? dir = Path.GetDirectoryName(_settingsPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(_settingsPath, json);
            }
            catch { }
        }
    }
}

