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

// VideoDirectorControl - what is on screen: the performance chrome, the media pre-flight, the dock inset, where the transport lives, and the canvas size.

namespace VideoDirector.Views
{
    public sealed partial class VideoDirectorControl : UserControl
    {
        // CINEMATIC + PLAYING is the performance, and only that combination changes anything.
        //
        // Going in: the chrome goes at once rather than after a timeout - nobody wants the first
        // seconds of a performance framed by an editor - and the timeline collapses so that moving
        // the mouse brings back the playbar alone rather than the whole track manager.
        //
        // Coming out: whatever the timeline was set to before is handed back. Cinematic on its own,
        // and playback on its own, are both left exactly as they were.
        private bool _inCinematicPlayback;
        private bool _dockOpenBeforePerformance = true;

        private void ApplyCinematicPlaybackChrome()
        {
            if (ViewModel == null) return;

            bool performing = ViewModel.IsCinematicMode && ViewModel.IsPlaying;
            if (performing == _inCinematicPlayback) return;
            _inCinematicPlayback = performing;

            if (performing)
            {
                _dockOpenBeforePerformance = ViewModel.IsTrackDockOpen;
                ViewModel.IsTrackDockOpen = false;
                ViewModel.IsControlsVisible = false;

                // Ignore the pointer events the full-screen transition generates on its way in.
                _chromeWakeBlockedUntil = DateTime.UtcNow.AddMilliseconds(600);
            }
            else
            {
                ViewModel.IsTrackDockOpen = _dockOpenBeforePerformance;
                ViewModel.IsControlsVisible = true;
            }
        }

        // Says plainly which sources are missing, and nothing at all when they are all fine.
        //
        // Called on opening a project and on arming cinematic - the two moments where finding out
        // early is worth a dialog, and long before an audience is looking at a black rectangle.
        private async System.Threading.Tasks.Task ReportMissingMediaAsync(string lead)
        {
            if (ViewModel == null) return;

            var missing = ViewModel.MissingSources();
            if (missing.Count == 0) return;

            var list = new StackPanel { Spacing = 4 };
            list.Children.Add(new TextBlock { Text = lead, TextWrapping = TextWrapping.Wrap });

            const int show = 8;
            foreach (var clip in missing.Take(show))
            {
                list.Children.Add(new TextBlock
                {
                    Text = "\u2022  " + clip.FileName,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                });
                ToolTipService.SetToolTip(list.Children[list.Children.Count - 1], clip.FilePath);
            }

            if (missing.Count > show)
                list.Children.Add(new TextBlock
                {
                    Text = "and " + (missing.Count - show) + " more",
                    FontSize = 12,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                });

            var dialog = new ContentDialog
            {
                Title = missing.Count == 1 ? "1 clip is missing its file" : missing.Count + " clips are missing their files",
                Content = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 320 },
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };

            try { await dialog.ShowAsync(); } catch { }
        }

        // Tell the player how much of its bottom edge the dock is covering, so the canvas can fit
        // into what is left rather than running underneath it. Zero when the dock is not showing.
        private void UpdateChromeInset()
        {
            if (PlayerControl == null || TrackDock == null) return;

            double inset = TrackDock.Visibility == Visibility.Visible ? TrackDock.ActualHeight : 0;
            if (Math.Abs(PlayerControl.BottomChromeInset - inset) < 0.5) return;

            PlayerControl.BottomChromeInset = inset;
            PlayerControl.UpdateCanvasLayout();
        }

        // What XAML gave the pill before anything moved it. Application.Current.Resources is not
        // theme-aware here and hands back the LIGHT brush, which is why the floating pill came out
        // white with washed-out controls.
        private Microsoft.UI.Xaml.Media.Brush _pillFloatBrush;

        // Move the transport between the dock toolbar and floating over the canvas.
        //
        // Docked is normal: the column is centred under the canvas and costs no picture. Floating
        // is for cinematic, where there is no dock - and there it keeps the pill chrome, because it
        // is drawn straight onto video.
        private void SetTransportDocked(bool docked)
        {
            _pillFloatBrush ??= FloatingPill?.Background;

            if (FloatingPill == null || TransportHost == null || ShellGrid == null) return;

            var wanted = docked ? (Panel)TransportHost : ShellGrid;
            if (ReferenceEquals(FloatingPill.Parent, wanted)) return;

            if (FloatingPill.Parent is Panel old) old.Children.Remove(FloatingPill);

            wanted.Children.Add(FloatingPill);

            // Column 0 of the transport grid, right-aligned: playback runs UP TO the centre line where
            // the panel toggle sits, and the edit controls start from it.
            Grid.SetColumn(FloatingPill, 0);

            if (docked)
            {
                // A row of controls, not a floating object: pill chrome inside a bordered dock is a
                // second border around the same thing.
                FloatingPill.Background = null;
                FloatingPill.BorderThickness = new Thickness(0);
                FloatingPill.CornerRadius = new CornerRadius(0);
                FloatingPill.Padding = new Thickness(0);
                FloatingPill.Margin = new Thickness(0);
                FloatingPill.VerticalAlignment = VerticalAlignment.Center;
                FloatingPill.HorizontalAlignment = HorizontalAlignment.Right;
            }
            else
            {
                FloatingPill.Background = _pillFloatBrush;
                FloatingPill.BorderThickness = new Thickness(1);
                FloatingPill.CornerRadius = new CornerRadius(16);
                FloatingPill.Padding = new Thickness(12, 8, 12, 8);
                FloatingPill.Margin = new Thickness(0, 0, 0, 32);
                FloatingPill.VerticalAlignment = VerticalAlignment.Bottom;
                FloatingPill.HorizontalAlignment = HorizontalAlignment.Center;
            }
        }

        // Push the project's canvas size into the player, initialising it from the window the
        // first time. Auto deliberately does NOT re-read the window afterwards: "the size it began
        // at" is the whole point, and re-reading would put the drift straight back.
        //
        // Auto means "the size of the window the project began at". A project with nothing on it
        // has not begun, so while it is EMPTY the canvas keeps following the window.
        //
        // That is not just semantics - it is the fix for the canvas reading 107% on a fresh start.
        // Capturing once at Loaded took whatever the pane measured mid-startup, before the window
        // had been restored to its saved size, and the canvas then stayed about a dock-height short
        // of the pane for the rest of the session. Following until there is content to protect
        // means the size that sticks is the one you actually started working at.
        private void ApplyCanvasSize()
        {
            if (ViewModel == null || PlayerControl == null) return;

            double w = PlayerControl.ActualWidth;
            double h = PlayerControl.ActualHeight;
            if (w <= 0 || h <= 0) return;   // not laid out yet; a later SizeChanged will come back
            bool following = ViewModel.CanvasSizeMode == ViewModels.CanvasSizeMode.Auto
                             && ViewModel.IsEmptyProject;

            if (following)
            {
                ViewModel.CanvasWidth = w;
                ViewModel.CanvasHeight = h;
            }
            else
            {
                ViewModel.InitialiseCanvasIfUnset(w, h);
            }

            if (!ViewModel.HasCanvasSize) return;

            PlayerControl.SetCanvasSize(ViewModel.CanvasWidth, ViewModel.CanvasHeight);
            PlayerControl.UpdateCanvasLayout();
            _playbackEngine?.RefreshComposite();
        }

        private void CanvasMode_Click(object? sender, RoutedEventArgs e)
        {
            ViewModel.CanvasSizeMode = ViewModels.CanvasSizeMode.Auto;

            // Auto follows the window only while the project is empty, so on a project with content
            // this keeps whatever size it already had rather than snapping it to the window - which
            // would move every clip at the moment you picked the mode.
            ApplyCanvasSize();
        }

        private void CanvasPreset_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not string tag) return;

            var parts = tag.Split('x');
            if (parts.Length != 2) return;
            if (!double.TryParse(parts[0], out double w) || !double.TryParse(parts[1], out double h)) return;

            SetCustomCanvas(w, h);
        }

        private async void CanvasCustom_Click(object? sender, RoutedEventArgs e)
        {
            var wBox = new NumberBox
            {
                Header = "Width",  Minimum = 16, Maximum = 16384, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                Value = ViewModel.CanvasWidth  > 0 ? Math.Round(ViewModel.CanvasWidth)  : 1920
            };
            var hBox = new NumberBox
            {
                Header = "Height", Minimum = 16, Maximum = 16384, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                Value = ViewModel.CanvasHeight > 0 ? Math.Round(ViewModel.CanvasHeight) : 1080
            };

            var panel = new StackPanel { Spacing = 12, Width = 260 };
            panel.Children.Add(new TextBlock
            {
                Text = "The composition's own size. Everything is measured against it; the window only "
                     + "decides how big it looks.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
            panel.Children.Add(wBox);
            panel.Children.Add(hBox);

            var dialog = new ContentDialog
            {
                Title = "Canvas size",
                Content = panel,
                PrimaryButtonText = "Set",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (double.IsNaN(wBox.Value) || double.IsNaN(hBox.Value)) return;

            SetCustomCanvas(Math.Round(wBox.Value), Math.Round(hBox.Value));
        }

        private void SetCustomCanvas(double w, double h)
        {
            if (w <= 0 || h <= 0) return;

            ViewModel.CanvasSizeMode = ViewModels.CanvasSizeMode.Custom;
            ViewModel.CanvasWidth = w;
            ViewModel.CanvasHeight = h;

            PlayerControl.SetCanvasSize(w, h);
            PlayerControl.UpdateCanvasLayout();
            _playbackEngine?.RefreshComposite();
            ViewModel.RecordIfChanged();
        }

        // The tick has to be read off the project, not left wherever the last click put it.
        private void CanvasMenu_Opening(object? sender, object e)
        {
            if (ViewModel == null) return;

            bool auto = ViewModel.CanvasSizeMode == ViewModels.CanvasSizeMode.Auto;
            int w = (int)Math.Round(ViewModel.CanvasWidth);
            int h = (int)Math.Round(ViewModel.CanvasHeight);

            if (CanvasAutoItem != null)  CanvasAutoItem.IsChecked  = auto;
            if (CanvasHdItem != null)    CanvasHdItem.IsChecked    = !auto && w == 1920 && h == 1080;
            if (CanvasUhdItem != null)   CanvasUhdItem.IsChecked   = !auto && w == 3840 && h == 2160;
            if (CanvasScopeItem != null) CanvasScopeItem.IsChecked = !auto && w == 2560 && h == 1072;
            if (CanvasVertItem != null)  CanvasVertItem.IsChecked  = !auto && w == 1080 && h == 1920;

            if (CanvasCustomItem != null)
                CanvasCustomItem.IsChecked = !auto
                    && !(CanvasHdItem?.IsChecked ?? false)
                    && !(CanvasUhdItem?.IsChecked ?? false)
                    && !(CanvasScopeItem?.IsChecked ?? false)
                    && !(CanvasVertItem?.IsChecked ?? false);
        }
    }
}
