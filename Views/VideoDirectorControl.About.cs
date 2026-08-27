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

// VideoDirectorControl - the About dialog and the version it reports.

namespace VideoDirector.Views
{
    public sealed partial class VideoDirectorControl : UserControl
    {
        /// <summary>
        /// About VideoDirector: what it is, which build you are running, and how to report a
        /// problem.
        ///
        /// The feedback route lives here now. It was a "?" on the transport pill, which is where
        /// people look for help with PLAYBACK - not for a way to file a bug - and it sat among the
        /// controls most likely to be hidden, since the pill auto-hides. About is where anyone
        /// looks for a version number and a contact route, and the toolbar never hides.
        ///
        /// Laid out to match TypoZen's About: an accent-coloured wordmark, the version beneath it
        /// in tabular figures, a one-line description, a rule, then short titled sections of
        /// bullets with the key term of each emphasised.
        /// </summary>
        private async void About_Click(object? sender, RoutedEventArgs e)
        {
            // ---- brand block (Sticky Header)
            var head = new StackPanel { Spacing = 0 };

            head.Children.Add(new TextBlock
            {
                Text = "VideoDirector",
                FontSize = 24,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                CharacterSpacing = -20,          // 1/1000 em; the -0.02em of the CSS
                Foreground = ThemeBrush("AccentTextFillColorPrimaryBrush", "TextFillColorPrimaryBrush")
            });
            head.Children.Add(new TextBlock
            {
                Text = "Version " + AppVersion(),
                FontSize = 12,
                Margin = new Thickness(0, 3, 0, 0),
                Foreground = ThemeBrush("TextFillColorSecondaryBrush", null)
            });
            head.Children.Add(new TextBlock
            {
                Text = "Cinematic Collage & Motion Slideshow for Video, Stills & Sound — arrange "
                     + "clips anywhere on the canvas, give any of them pan and zoom, and present "
                     + "the result.",
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = ThemeBrush("TextFillColorSecondaryBrush", null)
            });
            head.Children.Add(new Border
            {
                Height = 1,
                Margin = new Thickness(0, 14, 0, 0),
                Background = ThemeBrush("SurfaceStrokeColorDefaultBrush", null)
            });

            // ---- body (Scrollable)
            var body = new StackPanel { Spacing = 0, Margin = new Thickness(0, 0, 0, 14) };

            // ---- sections. Revised for clarity and punchiness.
            AddSection(body, "Compose", new[]
            {
                ("Six-track timeline", "— mix and match up to six independent layers of video, stills, and audio. Every track is treated equally, making picture-in-picture a breeze."),
                ("Fixed canvas resolution", "— work in standard formats (1080p, 4K, 2.39:1, 9:16) that never shift when you resize panels or present full screen."),
                ("Freeform placement", "— size, position, and blend clips anywhere on the canvas. Apply solid, soft, or film-strip borders to any element."),
                ("First-class still images", "— mix photo and video sequences seamlessly. Images act as standard clips with set durations that keep your project perfectly in sync.")
            });

            AddSection(body, "Motion", new[]
            {
                ("Ken Burns pan and zoom", "— easily add animated motion to any video or still image using start, mid, and end framing keyframes with smooth easing curves."),
                ("Direct canvas framing", "— what you see is what you get. Use your mouse wheel and drag directly on the canvas to frame your shots perfectly without diving into menus."),
                ("Source-resolution sampling", "— VideoDirector uses the full resolution of your original files, ensuring that slow zoom-ins remain crystal clear rather than pixelating.")
            });

            AddSection(body, "Edit & Present", new[]
            {
                ("Precision editing tools", "— trim, split, and retime clips with variable speed (including freeze-frames). Enjoy magnetic snapping, cross-track dragging, and unlimited undo/redo."),
                ("Cinematic playback", "— take over any display for a distraction-free presentation. The app remembers your preferred screen between sessions."),
                ("Flawless MP4 export", "— export exactly what you see. VideoDirector captures your live composite in real-time, guaranteeing that every fade, border, and motion effect survives intact.")
            });

            // ---- footer (Sticky)
            var foot = new StackPanel { Spacing = 0, Margin = new Thickness(0, 14, 0, 0) };
            foot.Children.Add(new Border
            {
                Height = 1,
                Margin = new Thickness(0, 0, 0, 14),
                Background = ThemeBrush("SurfaceStrokeColorDefaultBrush", null)
            });
            
            var links = new StackPanel { Spacing = 0 };
            var report = new HyperlinkButton { Content = "Report a problem or suggest a feature on GitHub", Padding = new Thickness(0) };
            report.Click += ReportProblem_Click;
            links.Children.Add(report);
            links.Children.Add(new HyperlinkButton
            {
                Content = "github.com/ec928/VideoDirector",
                NavigateUri = new Uri("https://github.com/ec928/VideoDirector"),
                Padding = new Thickness(0)
            });
            foot.Children.Add(links);

            foot.Children.Add(new TextBlock
            {
                Text = "MIT licensed. Portable and self-contained — no installer, no registry.",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
                Foreground = ThemeBrush("TextFillColorSecondaryBrush", null)
            });

            var layoutGrid = new Grid();
            layoutGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var scroll = new ScrollViewer
            {
                Content = body,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(0, 0, 16, 0), // Push content away from the scrollbar
                MaxHeight = 460                 // the panel scrolls rather than the window growing
            };
            Grid.SetRow(scroll, 0);
            layoutGrid.Children.Add(scroll);

            Grid.SetRow(foot, 1);
            layoutGrid.Children.Add(foot);

            var dialog = new ContentDialog
            {
                Title = head, // WinUI 3 correctly applies top margins and paddings for the Title property
                Content = layoutGrid,
                CloseButtonText = "Close",
                XamlRoot = this.XamlRoot            // required, or ContentDialog throws in WinUI 3
            };

            try { await dialog.ShowAsync(); }
            catch (Exception ex)
            {
                // Another dialog already open, or no XamlRoot yet. Never worth crashing over.
                System.Diagnostics.Debug.WriteLine($"[About] Could not show dialog: {ex.Message}");
            }
        }

        /// <summary>
        /// A titled group of bullets. Each bullet leads with its key term in the body colour and
        /// continues in the muted one, which is what gives the list its shape at a glance.
        /// </summary>
        private void AddSection(Panel host, string title, (string lead, string rest)[] points)
        {
            host.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 16, 0, 6)
            });

            foreach (var (lead, rest) in points)
            {
                var line = new TextBlock
                {
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(12, 0, 0, 5),
                    Foreground = ThemeBrush("TextFillColorSecondaryBrush", null)
                };
                line.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
                {
                    Text = "•  " + lead,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = ThemeBrush("TextFillColorPrimaryBrush", null)
                });
                line.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = " " + rest });
                host.Children.Add(line);
            }
        }

        /// <summary>
        /// A theme brush by key, falling back to a second key and then to null (inherit) rather
        /// than throwing. Resource keys differ between Windows App SDK versions, and an About box
        /// is not worth an unhandled exception.
        /// </summary>
        private static Microsoft.UI.Xaml.Media.Brush? ThemeBrush(string key, string? fallbackKey)
        {
            var res = Application.Current?.Resources;
            if (res == null) return null;
            if (res.TryGetValue(key, out var v) && v is Microsoft.UI.Xaml.Media.Brush b) return b;
            if (fallbackKey != null && res.TryGetValue(fallbackKey, out var v2)
                && v2 is Microsoft.UI.Xaml.Media.Brush b2) return b2;
            return null;
        }

        /// <summary>
        /// The informational version if the build stamped one, else the assembly version. Read from
        /// the assembly rather than hardcoded so it cannot drift from what was actually shipped.
        /// </summary>
        private static string AppVersion()
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var info = System.Reflection.CustomAttributeExtensions
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(asm)
                ?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                // Strip the +<commit> suffix the SDK appends when SourceLink is on.
                int plus = info.IndexOf('+');
                return plus > 0 ? info.Substring(0, plus) : info;
            }
            return asm.GetName().Version?.ToString() ?? "unknown";
        }

        /// <summary>
        /// Opens the project's issue tracker in the default browser. Most users reach the app
        /// through a Releases zip and never see the repository, so the feedback route has to be
        /// reachable from inside the app to be used at all.
        /// </summary>
        private void ReportProblem_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://github.com/ec928/VideoDirector/issues/new/choose",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                // Never let a missing browser association take the app down.
                System.Diagnostics.Debug.WriteLine($"[ReportProblem] Could not open issue tracker: {ex.Message}");
            }
        }
    }
}
