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

// VideoDirectorControl - the rest of the commands: add and remove tracks, export, the mode badge, entering and leaving Edit, the accelerators, undo and redo.

namespace VideoDirector.Views
{
    public sealed partial class VideoDirectorControl : UserControl
    {
        // Add and Remove act on the TOP of the stack, never the middle. The timeline bar and the
        // composite both rebuild off Tracks.CollectionChanged (wired in the constructor), so
        // neither handler needs to refresh anything itself.
        private void AddTrack_Click(object? sender, RoutedEventArgs e)
        {
            ViewModel.AddTopTrack();
        }

        private async void RemoveTrack_Click(object? sender, RoutedEventArgs e)
        {
            if (!ViewModel.CanRemoveTrack) return;

            // Removing a populated track destroys those clips. Undo gets them back, but that is
            // not obvious in the moment, so say what is about to go and how many.
            int clips = ViewModel.TopTrackClipCount;
            if (clips > 0)
            {
                var dialog = new ContentDialog
                {
                    Title = "Remove Track " + ViewModel.Tracks.Count + "?",
                    Content = clips == 1
                        ? "That track has 1 clip on it, which will be removed with it. You can undo with Ctrl+Z."
                        : "That track has " + clips + " clips on it, which will be removed with it. You can undo with Ctrl+Z.",
                    PrimaryButtonText = "Remove",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = this.XamlRoot
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            }

            ViewModel.RemoveTopTrack();
        }

        private async void Export_Click(object? sender, RoutedEventArgs e)
        {
            bool hasClips = false;
            if (ViewModel.Tracks.Count > 0)
            {
                foreach (var track in ViewModel.Tracks)
                    if (track.Clips.Count > 0) { hasClips = true; break; }
            }

            if (!hasClips)
            {
                await ShowExportMessage("Nothing to export", "Add at least one clip first.");
                return;
            }

            // Say what the file will be missing BEFORE the wait, not after it. Export renders
            // through MediaComposition, which cannot carry per-frame work - see the measurement
            // in VideoExporter. Silent when this particular project loses nothing.
            var lost = Models.VideoExporter.WhatIsNotBaked(ViewModel.Tracks);
            if (lost.Count > 0 && !await ConfirmExportLossAsync(lost)) return;

            var savePicker = new FileSavePicker();
            var window = MainWindow.Instance;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hwnd);
            savePicker.SuggestedStartLocation = PickerLocationId.VideosLibrary;
            savePicker.FileTypeChoices.Add("MP4 Video", new List<string>() { ".mp4" });
            savePicker.SuggestedFileName = "Export";

            StorageFile file = await savePicker.PickSaveFileAsync();
            if (file == null) return;

            var bar = new Microsoft.UI.Xaml.Controls.ProgressBar { Minimum = 0, Maximum = 100, Value = 0, Width = 320 };
            var status = new TextBlock { Text = "Rendering the composite (spine + overlays) — this can take a while for long clips." };
            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(status);
            panel.Children.Add(bar);
            var progressDialog = new ContentDialog
            {
                Title = "Exporting video",
                Content = panel,
                XamlRoot = this.XamlRoot
            };

            var exporter = new Models.VideoExporter();
            var progress = new Progress<double>(p => bar.Value = p);

            _ = progressDialog.ShowAsync(); // non-blocking; hidden when the render finishes
            var result = await exporter.ExportAsync(ViewModel.Tracks, file, progress,
                                                    ViewModel.CanvasWidth, ViewModel.CanvasHeight);
            progressDialog.Hide();

            switch (result.Outcome)
            {
                case Models.VideoExporter.ExportOutcome.Success:
                    var msg = $"Saved to:\n{result.Message}";
                    if (result.SkippedFiles.Count > 0)
                        msg += $"\n\nSkipped {result.SkippedFiles.Count} clip(s) with missing files:\n• " + string.Join("\n• ", result.SkippedFiles);
                    await ShowExportMessage("Export complete", msg);
                    break;
                case Models.VideoExporter.ExportOutcome.NothingToRender:
                    await ShowExportMessage("Nothing to export", result.Message);
                    break;
                default:
                    await ShowExportMessage("Export failed", result.Message);
                    break;
            }
        }

        // What the render will drop, and the one thing that does not drop anything.
        //
        // The alternative is not a consolation prize: cinematic playback IS the finished piece,
        // motion and fades and speed included, so recording it captures everything a render
        // cannot. Worth saying plainly at the moment someone is deciding.
        private async Task<bool> ConfirmExportLossAsync(System.Collections.Generic.List<string> lost)
        {
            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(new TextBlock
            {
                Text = "Export renders through the Windows media compositor, which cannot carry "
                     + "per-frame work. This project uses:",
                TextWrapping = TextWrapping.Wrap
            });

            var bullets = new StackPanel { Spacing = 4, Margin = new Thickness(8, 0, 0, 0) };
            foreach (var item in lost)
                bullets.Children.Add(new TextBlock { Text = "\u2022  " + item, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(bullets);

            panel.Children.Add(new TextBlock
            {
                Text = "Everything else is baked: cuts and trims, clip order and timing, "
                     + "picture-in-picture position, size and opacity, the audio mix, and the "
                     + "canvas size.",
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(new TextBlock
            {
                Text = "To keep all of it, play the project in cinematic mode and screen-record "
                     + "that instead \u2014 what you see there is the finished piece.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.8
            });

            var dialog = new ContentDialog
            {
                Title = "Some of this will not survive the render",
                Content = panel,
                PrimaryButtonText = "Export anyway",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };

            try { return await dialog.ShowAsync() == ContentDialogResult.Primary; }
            catch { return false; }
        }

        private async Task ShowExportMessage(string title, string message)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
        }





        private void ResetClip_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedClip != null)
            {
                ViewModel.SelectedClip.Reset();
                _playbackEngine?.UpdateWysiwygOverlay();
            }
        }


        private void PulseTimer_Tick(object? sender, object e)
        {
            _pulsePhase += 0.15;
            if (ModeBadgeButton != null)
            {
                // Smooth sine wave oscillation between opacity 0.55 and 1.0
                ModeBadgeButton.Opacity = 0.775 + 0.225 * Math.Sin(_pulsePhase);
            }
        }

        // The badge is a two-way switch, not an exit. It left Edit but could not enter it, so the
        // same control did something in one mode and nothing in the other.
        //
        // Entering needs a clip to edit, so with nothing selected it stays inert - and the badge is
        // disabled in that case rather than looking broken. Playback is left alone: switching modes
        // mid-roll is not what a mode badge is for.
        private void ModeBadge_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel.IsPlaying) return;

            if (ViewModel.IsEditMode)
            {
                ExitEditMode();
                return;
            }

            if (ViewModel.SelectedClip is CinematicOperation clip && !clip.IsLocked)
                _playbackEngine?.BeginEdit(clip, ViewModel.CurrentEditTarget);
        }

        private void PlaybarSplit_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedClip != null)
            {
                bool isSpine = ViewModel.SelectedOverlay == null;
                SplitClip(ViewModel.SelectedClip, isSpine);
            }
        }

        private void ExitToArrange_Click(object? sender, RoutedEventArgs e) => ExitEditMode();

        // Double-tap the image (Arrange) to edit the clip under the cursor: an overlay PiP, or the
        // Track 1 clip at the playhead if the tap wasn't on a PiP. Routes through SelectClip so
        // there's one entry path (selection + enter-edit) shared with the timeline.
        private void PlayerControl_EditRequested(object? sender, int slot)
        {
            if (ViewModel.IsPlaying) return;
            if (slot >= 0)
            {
                var clip = _playbackEngine?.GetActiveOverlay(slot);
                if (clip != null)
                {
                    SelectClip(clip, isSpine: false);
                    _playbackEngine?.BeginEdit(clip, ViewModel.CurrentEditTarget);
                }
            }
            else if (ViewModel.Tracks.Count > 0 && ViewModel.Tracks[0].Clips.Count > 0)
            {
                int idx = ViewModel.GetTimelineIndexForStoryTime(ViewModel.CurrentStoryTime);
                if (idx >= 0 && idx < ViewModel.Tracks[0].Clips.Count)
                {
                    var clip = ViewModel.Tracks[0].Clips[idx];
                    SelectClip(clip, isSpine: true);
                    _playbackEngine?.BeginEdit(clip, ViewModel.CurrentEditTarget);
                }
            }
        }

        private void ExitEditMode()
        {
            if (!ViewModel.IsEditMode) return;

            if (ViewModel.SelectedOverlay != null && ViewModel.Tracks.Count > 1)
            {
                for (int i = 1; i < ViewModel.Tracks.Count; i++)
                {
                    var track = ViewModel.Tracks[i];
                    if (track.Clips.Contains(ViewModel.SelectedOverlay))
                    {
                        track.ResolveOverlaps();
                        break;
                    }
                }
            }

            // Clear the selection so we don't immediately re-enter Edit, then return to Arrange.
            ViewModel.SelectedTimelineNode = null;
            ViewModel.SelectedOverlay = null;
            _playbackEngine?.ExitToArrange();
            // An edit session (trim/speed/framing changes) collapses into one undo step here.
            ViewModel.RecordIfChanged();
            BuildTimelineBar();
        }

        private void EscapeAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
                                               Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            // Cinematic first: it is the outermost state, and it is the one with no visible way
            // out once the pill has faded. Esc must always be able to get you back to the editor.
            if (ViewModel.IsCinematicMode) { ViewModel.IsCinematicMode = false; args.Handled = true; return; }
            if (ViewModel.IsEditMode) { ExitEditMode(); args.Handled = true; return; }

            // Outermost first, innermost last: with nothing else to leave, Esc drops the selection
            // and with it the inspector. A keyboard route matters here because the panel covers the
            // right of the canvas and the only other way out is finding empty timeline to click.
            if (ViewModel.SelectedClip != null) { ViewModel.SelectedClip = null; args.Handled = true; }
        }

        // Space = play/pause. Ignored while typing so it doesn't hijack text entry.
        private void PlayPauseAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
                                                  Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            if (IsTextInputFocused()) return;
            args.Handled = true;
            PlayPause_Click(this, null);
        }

        // Delete = remove the selected clip (never while typing or during playback). If the clip is
        // being edited, drop back to Arrange first so we don't linger in Edit on a deleted clip.
        private void DeleteAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
                                               Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            if (IsTextInputFocused() || ViewModel.IsPlaying || ViewModel.SelectedClip == null) return;
            args.Handled = true;
            var clip = ViewModel.SelectedClip;
            bool isSpine = ViewModel.IsTrack1Selected;
            if (ViewModel.IsEditMode) ExitEditMode();
            RemoveClip(clip, isSpine);
        }

        private void LeftAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
                                             Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            if (IsTextInputFocused()) return;
            args.Handled = true;
            StepFrame(-1);
        }

        private void RightAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
                                              Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            if (IsTextInputFocused()) return;
            args.Handled = true;
            StepFrame(1);
        }

        private void SplitAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
                                              Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            if (IsTextInputFocused() || !ViewModel.HasSelection) return;
            args.Handled = true;
            PlaybarSplit_Click(this, null);
        }

        // A NumberBox hosts an inner TextBox, so a focused TextBox means the user is typing —
        // in which case Space/Delete/Ctrl+Z must reach the field, not trigger a shortcut.
        private bool IsTextInputFocused()
            => FocusManager.GetFocusedElement(this.XamlRoot) is TextBox;

        private void UndoAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
                                             Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            if (IsTextInputFocused()) return; // let Ctrl+Z undo text in a field
            args.Handled = true;
            ApplyHistory(ViewModel.Undo);
        }

        private void RedoAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
                                             Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            if (IsTextInputFocused()) return;
            args.Handled = true;
            ApplyHistory(ViewModel.Redo);
        }

        private void Undo_Click(object? sender, RoutedEventArgs e) => ApplyHistory(ViewModel.Undo);
        private void Redo_Click(object? sender, RoutedEventArgs e) => ApplyHistory(ViewModel.Redo);

        // Undo/redo swap the whole clip collection, so any engine references to the old clips (edit
        // target, playing op) go stale. Settle the engine into a clean Arrange first, apply the
        // history step, then rebuild the timeline and composite from the restored state.
        private void ApplyHistory(Action historyOp)
        {
            if (ViewModel.IsPlaying) _playbackEngine?.StopPlayback();
            if (ViewModel.IsEditMode) _playbackEngine?.ExitToArrange();
            historyOp();
            BuildTimelineBar();
            _playbackEngine?.RefreshComposite();
        }





        private void OverlaySection_DragOver(object? sender, DragEventArgs e)
        {
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                // Name the row being hovered, so it's obvious where the drop will land.
                e.DragUIOverride.Caption = "Add to " + TrackNameAt(e.GetPosition(TimelineBar).Y);
                e.Handled = true;
            }
        }

        // Which track a given y in the timeline belongs to: the Track 1 row (and the ruler above
        // it) is the spine; the rows below are the overlay tracks.
        private string TrackNameAt(double y)
        {
            // The Count check matters at a single track: there is no row below the spine, so any
            // y belongs to Track 1. Without it this named a Track 2 that does not exist.
            if (y < RowOvY || ViewModel.Tracks.Count < 2) return "Track 1";
            int i = Math.Clamp((int)((y - RowOvY) / RowPitch), 0, ViewModel.Tracks.Count - 2);
            return "Track " + (i + 2);
        }

        // Drop a video/image onto the timeline strip to add it. Which row you drop on decides the
        // track (Track 1 row = spine, lower rows = that overlay track); the drop x sets the start
        // time (falls back to the playhead if the scale isn't ready).
        private async void OverlaySection_Drop(object? sender, DragEventArgs e)
        {
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                e.Handled = true;
                var drop = e.GetPosition(TimelineBar);
                TimeSpan startTime = _timelinePxPerSec > 0
                    ? TimeSpan.FromSeconds(Math.Max(0, drop.X / _timelinePxPerSec))
                    : ViewModel.CurrentStoryTime;

                // The row you drop on decides the destination: the Track 1 row (and the ruler
                // above it) adds to the spine; the rows below add to that overlay track.
                bool toSpine = drop.Y < RowOvY;
                int trackIndex = toSpine ? 0 : (int)((drop.Y - RowOvY) / RowPitch) + 1;
                trackIndex = Math.Clamp(trackIndex, 0, Math.Max(0, ViewModel.Tracks.Count - 1));

                var items = await e.DataView.GetStorageItemsAsync();
                foreach (var item in items)
                {
                    if (item is Windows.Storage.StorageFile file && IsSupportedMedia(file.FileType))
                    {
                        await ViewModel.AddOverlayAsync(item.Path, startTime, trackIndex);
                    }
                }

                // Open the newest clip for editing
                var track = ViewModel.Tracks[trackIndex];
                if (track.Clips.Count > 0)
                {
                    if (ViewModel.IsPlaying) _playbackEngine?.StopPlayback();
                    SelectClip(track.Clips[^1], isSpine: trackIndex == 0);
                }
            }
        }
    }
}
