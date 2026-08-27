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

            // ---- sections. Claims kept to what the app actually does; see the export note.
            AddSection(body, "Compose", new[]
            {
                ("Up to six equal tracks", "composited by Z-order — no privileged “spine”, and any clip on any track can be a picture-in-picture. Add and remove them as a project needs."),
                ("A fixed canvas", "— the composition has its own frame rather than borrowing the window’s, so hiding a panel, resizing or presenting full screen changes only how big it looks."),
                ("Free placement", "with independent size, position and opacity, plus solid, soft or film-strip borders."),
                ("Clips may hang off the canvas", "as far as their edge meeting the boundary line, so a full bleed or an entry from off screen is authorable. They draw whole while you place them, and cannot be pushed somewhere unreachable."),
                ("Chrome that stays findable", "— every clip carries a dashed outline in its track colour above every picture, so a clip hidden behind a larger one can still be found. A fully opaque clip on a higher track hides it, exactly as it hides the picture."),
                ("Stills as first-class clips", "— images hold for a set duration and advance story time by wall clock, so mixed photo and video sequences stay in sync."),
                ("Audio clips", "- drop in an mp3 or any other sound file and trim, place and level it like any clip. It draws nothing, so it never covers the tracks beneath, and the inspector offers only what applies to it: timing and volume."),
                ("Fades", "in from black, out to black, or both, on any clip. A transition ADDS to the clip’s length rather than eating into it, and the timeline shades the part that is fade rather than picture."),
            });

            AddSection(body, "Motion", new[]
            {
                ("Ken Burns pan and zoom", "on any clip, video or still, from Start, optional Mid, and End framing keyframes with easing curves."),
                ("Frame it on the canvas", "— the wheel magnifies the clip inside a window that never changes size, and Set Start, Mid or End records the framing already on screen. Pressing Set cannot move the picture, because what it stores is what you were already looking at."),
                ("Shape a keyframe by dragging it", "— a rectangle’s tab moves that framing, its corners resize it. Selecting one never steals the wheel."),
                ("The picture is never cut off", "while you frame it. It may sit past the edge of its frame - that is how a push-in from off-frame is authored - and stops only when its edge reaches the boundary, so it can never be lost off screen."),
                ("Source-resolution stills", "so a slow push-in resamples real pixels instead of a flattened screen-sized copy."),
            });

            AddSection(body, "Edit", new[]
            {
                ("Three strict modes", "— Playback, Arrange and Edit — so canvas manipulation never collides with scrubbing or review."),
                ("Trim, split, retime", "with variable speed including freeze-frames, cross-track drag, 8px magnetic snapping and unlimited undo."),
                ("Loop a region", "by dragging across the time ruler."),
            });

            AddSection(body, "Screen and share", new[]
            {
                ("Cinematic mode", "— arm it, and playback takes over the whole screen with every trace of the editor gone. Move the mouse for the transport, stop playing and the editor returns as you left it."),
                ("Present on any display", "— choose which screen a performance takes over, remembered between sessions. The list is built when you open it, so a projector plugged in after launch needs no restart."),
                ("A check before you present", "— opening a project, and arming cinematic, both say plainly which clips can no longer find their files. A project is a list of paths, and that is the failure that actually bites."),
                ("Export to MP4", "by recording the performance. The project plays full screen with no chrome and is captured as it goes, so motion, fades, speed, borders and picture-in-picture all survive - it photographs what the compositor draws rather than trying to re-render it. Sound is mixed from the sources and laid on afterwards. Runs in real time; Esc stops a take early."),
            });

            // ---- footer (Sticky)
            var foot = new StackPanel { Spacing = 0 };
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
            layoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layoutGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid.SetRow(head, 0);
            layoutGrid.Children.Add(head);

            var scroll = new ScrollViewer
            {
                Content = body,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 460                 // the panel scrolls rather than the window growing
            };
            Grid.SetRow(scroll, 1);
            layoutGrid.Children.Add(scroll);

            Grid.SetRow(foot, 2);
            layoutGrid.Children.Add(foot);

            var dialog = new ContentDialog
            {
                // We use layoutGrid to manually control the header and footer, bypassing ContentDialog's Title styling
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
