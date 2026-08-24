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

// VideoDirectorControl - the project itself: save, load, clear, and the unsaved-changes prompt.

namespace VideoDirector.Views
{
    public sealed partial class VideoDirectorControl : UserControl
    {
        private void Prev_Click(object? sender, RoutedEventArgs e)
        {
            StepFrame(-1);
        }

        private void Next_Click(object? sender, RoutedEventArgs e)
        {
            StepFrame(1);
        }


        private async void Save_Click(object? sender, RoutedEventArgs e) => await SaveProjectAsync();

        /// <summary>Does the project hold work that closing would destroy?</summary>
        public bool HasUnsavedChanges => ViewModel != null && ViewModel.HasUnsavedChanges;

        /// <summary>
        /// Save through the picker. Returns false if the user backed out, which the caller needs
        /// in order NOT to close the window on a save that never happened.
        /// </summary>
        public async Task<bool> SaveProjectAsync()
        {
            var savePicker = new FileSavePicker();
            var window = MainWindow.Instance;
            if (window == null) return false;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hwnd);

            savePicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            savePicker.FileTypeChoices.Add("Director Sequence", new List<string>() { ".json" });
            savePicker.SuggestedFileName = "NewSequence";

            StorageFile file = await savePicker.PickSaveFileAsync();
            if (file == null) return false;

            // Any clip still holding pre-normalisation pixel marks has to be converted before
            // the file claims the current schema.
            _playbackEngine?.NormalizeAllMarks(ViewModel.Tracks);
            await ViewModel.SaveAsync(file);
            ViewModel.MarkSaved();
            return true;
        }

        /// <summary>Save / Don't save / Cancel, asked on the way out.</summary>
        public async Task<UnsavedChoice> ConfirmUnsavedAsync()
        {
            var dialog = new ContentDialog
            {
                Title = "Save changes before closing?",
                Content = "This project has changes that have not been saved. "
                        + "Closing now loses them for good - undo does not survive a restart.",
                PrimaryButtonText = "Save",
                SecondaryButtonText = "Don't save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary) return UnsavedChoice.Save;
            if (result == ContentDialogResult.Secondary) return UnsavedChoice.Discard;
            return UnsavedChoice.Cancel;
        }

        // Applied after any project load: a loaded project brings its own canvas, and one saved
        // before the canvas existed brings zeros and gets initialised from the window here.
        private void AfterProjectLoaded()
        {
            ApplyCanvasSize();
        }

        /// <summary>
        /// Open a project by path and optionally start playing it, with no picker and no clicks.
        /// </summary>
        /// <remarks>
        /// Exists so the recorder can be measured against a REAL project rather than a fixture.
        /// Every claim about capture - does it keep up, does a held shot survive, does the file
        /// match the preview - needs the app playing something real, unattended, repeatably.
        /// Reached only from the command line (--play), never from the UI.
        /// </remarks>
        public async Task<bool> OpenAndPlayAsync(string path, bool play, bool cinematic)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                await ViewModel.LoadAsync(file);
                AfterProjectLoaded();
                _playbackEngine?.NormalizeAllMarks(ViewModel.Tracks);

                // Not awaited: it opens each source once to ask whether it has sound, and the
                // timeline should not wait on that. Projects saved before the flag existed default
                // to "has audio", so without this pass an old project keeps offering a live volume
                // control on a silent clip.
                _ = ViewModel.RefreshAudioCapabilityAsync();

                if (cinematic) ViewModel.IsCinematicMode = true;
                if (play) await (_playbackEngine?.StartPlaybackAsync(0) ?? Task.CompletedTask);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("OpenAndPlayAsync failed: " + ex.Message);
                return false;
            }
        }

        private async void Load_Click(object? sender, RoutedEventArgs e)
        {
            var openPicker = new FileOpenPicker();
            var window = MainWindow.Instance;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(openPicker, hwnd);

            openPicker.ViewMode = PickerViewMode.List;
            openPicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            openPicker.FileTypeFilter.Add(".json");

            StorageFile file = await openPicker.PickSingleFileAsync();
            if (file != null)
            {
                await ViewModel.LoadAsync(file);
                AfterProjectLoaded();   // the project brings its own canvas, or gets one from the window
                _ = ViewModel.RefreshAudioCapabilityAsync();   // which clips actually have sound
                await ReportMissingMediaAsync("This project refers to files that are not where it left them.");
                // Convert a pre-normalisation project up front rather than clip-by-clip on first
                // draw, so marks never sit in two conventions at once.
                _playbackEngine?.NormalizeAllMarks(ViewModel.Tracks);
                if (ViewModel.IsAutoPlayEnabled && ViewModel.Tracks.Count > 0 && ViewModel.Tracks[0].Clips.Count > 0)
                {
                    _ = _playbackEngine?.StartPlaybackAsync(0);
                }
            }
        }

        private async void Clear_Click(object? sender, RoutedEventArgs e)
        {
            bool hasContent = false;
            if (ViewModel.Tracks.Count > 0)
            {
                foreach (var t in ViewModel.Tracks)
                {
                    if (t.Clips.Count > 0) { hasContent = true; break; }
                }
            }

            if (hasContent)
            {
                var dialog = new ContentDialog
                {
                    Title = "Clear project?",
                    Content = "This removes every clip from all tracks. You can undo it with Ctrl+Z.",
                    PrimaryButtonText = "Clear",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = this.XamlRoot
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            }

            ViewModel.Clear();
        }
    }
}
