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

        // EXPORT = RECORD THE PERFORMANCE.
        //
        // The old export rendered through MediaComposition, and it never worked on a real project:
        // it could not carry Ken Burns, fades, speed or borders (invariant 6), and it refused any
        // source with an odd width outright. Recording plays the project and photographs what the
        // compositor draws, so everything that is right on screen is right in the file, and the
        // source files are never touched.
        //
        // The cost is honest and stated up front: it runs in real time.
        private async void Export_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel == null || _recorder != null) return;

            bool hasClips = false;
            foreach (var track in ViewModel.Tracks)
                if (track.Clips.Count > 0) { hasClips = true; break; }

            if (!hasClips)
            {
                await ShowExportMessage("Nothing to export", "Add at least one clip first.");
                return;
            }

            if (!Models.ScreenRecorder.IsSupported)
            {
                await ShowExportMessage("Cannot record",
                    "Windows screen capture is not available on this system.");
                return;
            }

            var total = ViewModel.TotalStoryTime;
            if (total <= TimeSpan.Zero) total = TimeSpan.FromSeconds(10);

            // Straight to the picker. There was a "are you sure" dialog here and it earned nothing:
            // choosing a filename IS the confirmation, and the recording is interruptible with Esc.
            // A prompt in front of an action the user just asked for is a click you have to make.
            var savePicker = new FileSavePicker();
            var window = MainWindow.Instance;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hwnd);
            savePicker.SuggestedStartLocation = PickerLocationId.VideosLibrary;
            savePicker.FileTypeChoices.Add("MP4 Video", new List<string>() { ".mp4" });
            savePicker.SuggestedFileName = string.IsNullOrWhiteSpace(ViewModel.ProjectName) ? "Recording" : ViewModel.ProjectName;

            StorageFile file = await savePicker.PickSaveFileAsync();
            if (file == null) return;

            await RunRecordingAsync(file, hwnd, total);
        }

        private Models.ScreenRecorder _recorder;

        /// <summary>
        /// Record the loaded project straight to a path, with no dialog and no picker.
        /// </summary>
        /// <remarks>
        /// Runs the SAME RunRecordingAsync the button runs. Reached only from the command line
        /// (--record), so the shipping code path can be exercised end to end without a human
        /// clicking through a file picker. The alternative is testing a copy of the logic, which
        /// is how the old exporter came to be certified while broken.
        /// </remarks>
        public async Task RecordToPathAsync(string outputPath)
        {
            var folder = await StorageFolder.GetFolderFromPathAsync(System.IO.Path.GetDirectoryName(outputPath));
            var file = await folder.CreateFileAsync(System.IO.Path.GetFileName(outputPath),
                                                    CreationCollisionOption.ReplaceExisting);
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance);

            var total = ViewModel.TotalStoryTime;
            if (total <= TimeSpan.Zero) total = TimeSpan.FromSeconds(10);

            await RunRecordingAsync(file, hwnd, total);
        }
        private bool _telemetryBeforeRecording;

        // Play the project from the top, full screen, with nothing on it, and record that.
        private async Task RunRecordingAsync(StorageFile file, IntPtr hwnd, TimeSpan total)
        {
            _telemetryBeforeRecording = ViewModel.IsTelemetryVisible;

            // The HUD is drawn INTO the window, so capture takes it too - it showed up in the
            // first recording as an FPS/GPU readout across the top corner. The chrome lock cannot
            // reach it because it is not chrome, it is content.
            ViewModel.IsTelemetryVisible = false;

            ViewModel.IsRecording = true;          // locks the chrome; see ChromeRules
            ViewModel.IsCinematicMode = true;
            ViewModel.IsControlsVisible = false;

            // A moment for the window to reach full screen before the first frame is kept.
            await Task.Delay(700);
            _playbackEngine?.StopPlayback();
            await (_playbackEngine?.StartPlaybackAsync(0) ?? Task.CompletedTask);

            _recorder = new Models.ScreenRecorder();

            // Stop when the project ends. Esc stops early through StopRecording.
            var guard = DispatcherQueue.CreateTimer();
            guard.Interval = TimeSpan.FromMilliseconds(250);
            guard.Tick += (s, e) =>
            {
                if (ViewModel.IsRecording && ViewModel.CurrentStoryTime < total && ViewModel.IsPlaying) return;
                guard.Stop();
                _recorder?.RequestStop();
            };
            guard.Start();

            // Record the picture to a scratch file first. The sound is mixed from the source files
            // and laid on afterwards - capture takes pixels only, and lining up two independently
            // clocked captures is a worse problem than mixing the audio we already have.
            // Plain temp folder, not ApplicationData.Current - that one needs package identity
            // and this app is unpackaged, so touching it throws.
            var tempFolder = await StorageFolder.GetFolderFromPathAsync(System.IO.Path.GetTempPath());
            var temp = await tempFolder.CreateFileAsync("videodirector-silent.mp4", CreationCollisionOption.ReplaceExisting);

            var result = await _recorder.RecordAsync(hwnd, temp,
                targetWidth: 1920, fps: 30,
                maxSeconds: (int)Math.Ceiling(total.TotalSeconds) + 5);

            if (result.Success)
            {
                var audio = await Models.PerformanceAudio.MuxAsync(
                    temp, ViewModel.Tracks, file, ViewModel.CanvasWidth, ViewModel.CanvasHeight);

                if (!audio.Success)
                {
                    // No sound to add, or the mix would not render. Keep the picture rather than
                    // losing the take: copy the silent recording to where the user asked for it.
                    await temp.CopyAndReplaceAsync(file);
                }
            }

            guard.Stop();
            _recorder = null;

            StopRecording();                        // releases the lock, stops playback
            ViewModel.IsCinematicMode = false;
            ViewModel.IsTelemetryVisible = _telemetryBeforeRecording;

            // Success says so in the title bar and then gets out of the way. A modal OK after
            // something you asked for and watched happen is a click for nothing - and you already
            // chose where the file goes, so there is nothing to tell you that you do not know.
            // Failure still stops you, because that IS news.
            if (!result.Success)
            {
                await ShowExportMessage("Recording failed", result.Message);
                return;
            }

            ShowBanner("Recording saved",
                       System.IO.Path.GetFileName(result.Message) + "  \u2022  "
                       + result.Duration.TotalSeconds.ToString("F0") + "s, "
                       + result.FramesEncoded + " frames",
                       InfoBarSeverity.Success);
        }

        /// <summary>
        /// Tell the user something finished, without making them dismiss it.
        /// </summary>
        /// <remarks>
        /// There was a modal OK here, and it was a click for nothing after an action you asked for
        /// and watched happen. Replacing it with a window-title change went too far the other way:
        /// coming out of full-screen playback nobody looks at the title bar, so the recording
        /// finished and said nothing at all. A banner inside the window is seen, needs no action,
        /// and takes itself away.
        /// </remarks>
        private void ShowBanner(string title, string message, InfoBarSeverity severity)
        {
            if (StatusBanner == null) return;

            StatusBanner.Title = title;
            StatusBanner.Message = message;
            StatusBanner.Severity = severity;
            StatusBanner.IsOpen = true;

            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(12);
            timer.IsRepeating = false;
            timer.Tick += (s, e) => { try { StatusBanner.IsOpen = false; } catch { } };
            timer.Start();
        }

        private async Task<bool> ConfirmRecordAsync(TimeSpan total)
        {
            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(new TextBlock
            {
                Text = "The project will play full screen from the beginning and be recorded as it "
                     + "goes. Everything you see is captured \u2014 motion, fades, speed, borders and "
                     + "picture-in-picture.",
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(new TextBlock
            {
                Text = "This happens in real time, so it will take about "
                     + Math.Ceiling(total.TotalSeconds) + " seconds. Press Esc to stop early.",
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(new TextBlock
            {
                Text = "No sound yet \u2014 the recording is silent.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.8
            });

            var dialog = new ContentDialog
            {
                Title = "Record this project",
                Content = panel,
                PrimaryButtonText = "Record",
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
            // A rolling take is the outermost state of all, and Esc is the ONLY way out of it:
            // deliberately not any key, so a stray press cannot truncate one. Stopping the take
            // does not also drop cinematic - a second Esc does that, same as always.
            if (ViewModel.IsRecording) { StopRecording(); args.Handled = true; return; }

            // Cinematic next: it is the outermost state otherwise, and it is the one with no
            // visible way out once the pill has faded. Esc must always get you back to the editor.
            if (ViewModel.IsCinematicMode) { ViewModel.IsCinematicMode = false; args.Handled = true; return; }
            if (ViewModel.IsEditMode) { ExitEditMode(); args.Handled = true; return; }

            // Outermost first, innermost last: with nothing else to leave, Esc drops the selection
            // and with it the inspector. A keyboard route matters here because the panel covers the
            // right of the canvas and the only other way out is finding empty timeline to click.
            if (ViewModel.SelectedClip != null) { ViewModel.SelectedClip = null; args.Handled = true; }
        }

        /// <summary>
        /// End the take. Stops playback with it, because a recording of the editor reappearing is
        /// not something anyone wants at the end of their file.
        /// </summary>
        /// <remarks>
        /// The recorder itself is not built yet; this is the state and the way out of it, which is
        /// the half that has to be right before any frames are written. Setting IsRecording false
        /// releases the chrome lock in ChromeRules.
        /// </remarks>
        private void StopRecording()
        {
            if (ViewModel == null || !ViewModel.IsRecording) return;

            ViewModel.IsRecording = false;
            _recorder?.RequestStop();          // ends the take at the next frame boundary
            if (ViewModel.IsPlaying) _playbackEngine?.StopPlayback();

            // The chrome was locked away, not hidden by the timer, so put it back deliberately
            // rather than waiting for a mouse move to do it.
            ViewModel.IsControlsVisible = true;
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
