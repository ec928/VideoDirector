using Microsoft.UI.Xaml;
using System;
using System.Threading.Tasks;

namespace VideoDirector
{
    public partial class App : Application
    {
        private Window? _window;

        public App()
        {
            InitializeComponent();
            this.UnhandledException += App_UnhandledException;
        }

        private void App_UnhandledException(object? sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"[CRITICAL] Unhandled Exception: {e.Exception}");
            e.Handled = true;
            try
            {
                var director = MainWindow.Instance?.Director;
                if (director == null) return;
                string msg = e.Exception?.Message ?? "An unexpected error occurred.";
                director.DispatcherQueue.TryEnqueue(() => director.ReportUnexpectedError(msg));
            }
            catch { }
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            _window.Activate();

            // --play <project.json> [--cinematic]
            //
            // A way to put the app into a known, real state without a human clicking - the thing
            // that was missing when the exporter was "verified" against a project written to match
            // its own assumptions. Anything measured about playback or recording has to be measured
            // on a project someone actually saved.
            var argv = Environment.GetCommandLineArgs();
            string? project = null, record = null;
            bool cinematic = false, paused = false;
            for (int i = 1; i < argv.Length; i++)
            {
                if (string.Equals(argv[i], "--play", StringComparison.OrdinalIgnoreCase) && i + 1 < argv.Length)
                    project = argv[++i];
                else if (string.Equals(argv[i], "--record", StringComparison.OrdinalIgnoreCase) && i + 1 < argv.Length)
                    record = argv[++i];
                else if (string.Equals(argv[i], "--cinematic", StringComparison.OrdinalIgnoreCase))
                    cinematic = true;
                else if (string.Equals(argv[i], "--paused", StringComparison.OrdinalIgnoreCase))
                    paused = true;
            }

            if (project != null) StartProject(project, cinematic, record, paused);
        }

        // Deferred to the dispatcher: the control has to be loaded and sized before a project can
        // establish its canvas, and OnLaunched runs before that.
        private void StartProject(string project, bool cinematic, string? record = null, bool paused = false)
        {
            var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            var timer = queue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(1200);
            timer.IsRepeating = false;
            timer.Tick += async (s, e) =>
            {
                var director = MainWindow.Instance?.Director;
                if (director == null) return;

                // When recording, do not start playback here - RunRecordingAsync starts it itself,
                // from the top, once the window is full screen and the chrome is locked away.
                await director.OpenAndPlayAsync(project, play: record == null && !paused, cinematic: cinematic);

                if (record != null)
                {
                    await director.RecordToPathAsync(record);

                    // Let the recorder, its timers and the transcoder finish unwinding before the
                    // process goes. Exiting the instant the file is written raced them and took the
                    // app down with a stowed exception inside the WinUI messaging layer - after the
                    // recording was safely on disk, so it cost nothing but looked like a crash.
                    await Task.Delay(1500);
                    Application.Current.Exit();
                }
            };
            timer.Start();
        }
    }
}
