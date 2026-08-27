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
            public bool AlwaysShowFullFrames { get; set; } = true;
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

            _appWindow.Resize(new Windows.Graphics.SizeInt32(w, h));

            if (settings.WindowX != -1 && settings.WindowY != -1)
                _appWindow.Move(new Windows.Graphics.PointInt32(settings.WindowX, settings.WindowY));
        }

        /// <summary>Which display a performance takes over. Saved with the window settings.</summary>
        public int PresentDisplayIndex { get; set; } = -1;

        /// <summary>User preference to always show full clip frames instead of occluding them behind higher tracks.</summary>
        public bool AlwaysShowFullFrames { get; set; } = true;

        private void SaveAllSettings()
        {
            try
            {
                if (_appWindow == null) return;
                if (_appWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Minimized) return;

                _currentSettings.WindowWidth = _appWindow.Size.Width;
                _currentSettings.WindowHeight = _appWindow.Size.Height;
                _currentSettings.WindowX = _appWindow.Position.X;
                _currentSettings.WindowY = _appWindow.Position.Y;
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

