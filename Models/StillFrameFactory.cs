using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Editing;
using Windows.Storage;

namespace VideoDirector.Models
{
    // Bakes the frame a still clip is frozen on into a real bitmap, at the source's native
    // resolution.
    //
    // Why it matters: a still used to be rendered by parking a MediaPlayerElement on one frame,
    // and the Ken Burns transform sat on that element. A MediaPlayerElement rasterises into a
    // swapchain sized to its LAYOUT size, and a RenderTransform is applied by the compositor
    // afterwards — so zooming a 1080p source inside a 700px box magnifies ~700px of surviving
    // detail, not 1080p of it. There is nothing left for the resampler to interpolate against, so
    // a sub-pixel-per-frame push-in ratchets a whole pixel at a time instead of creeping.
    //
    // Decoding the frame into a bitmap instead keeps every source texel addressable: the
    // compositor samples the full 1920x1080 surface through the whole ramp, downsampling rather
    // than upsampling, and the sub-pixel motion resolves.
    //
    // Deliberately no DecodePixelWidth — capping the decode at the display size would rebuild the
    // exact problem this exists to remove.
    internal static class StillFrameFactory
    {
        private static readonly string[] ImageExtensions =
            { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff" };

        // Returns null if the source can't be read or decoded; the caller keeps the video surface
        // as its fallback rather than showing nothing.
        public static async Task<BitmapImage> ExtractAsync(string filePath, TimeSpan position)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return null;

            StorageFile file;
            try { file = await StorageFile.GetFileFromPathAsync(filePath); }
            catch { return null; }

            var ext = System.IO.Path.GetExtension(filePath);
            bool isImage = Array.Exists(ImageExtensions, e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));

            // An image clip has no frame to seek to — decode the file itself. This also takes
            // image stills off the MediaSource path entirely, which Media Foundation could not
            // reliably open for a .jpg in the first place.
            if (isImage) return await DecodeFileAsync(file);

            return await ExtractVideoFrameAsync(file, position);
        }

        private static async Task<BitmapImage> DecodeFileAsync(StorageFile file)
        {
            try
            {
                using var stream = await file.OpenReadAsync();
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                return bitmap;
            }
            catch { return null; }
        }

        private static async Task<BitmapImage> ExtractVideoFrameAsync(StorageFile file, TimeSpan position)
        {
            try
            {
                var clip = await MediaClip.CreateFromFileAsync(file);

                // Native encoded size, so the bake is a copy of the frame rather than a resize of it.
                var props = clip.GetVideoEncodingProperties();
                int w = (int)(props?.Width ?? 0);
                int h = (int)(props?.Height ?? 0);
                if (w <= 0 || h <= 0) { w = 1920; h = 1080; }

                var at = position < TimeSpan.Zero ? TimeSpan.Zero : position;
                if (clip.OriginalDuration > TimeSpan.Zero && at >= clip.OriginalDuration)
                {
                    at = clip.OriginalDuration - TimeSpan.FromMilliseconds(100);
                    if (at < TimeSpan.Zero) at = TimeSpan.Zero;
                }

                var composition = new MediaComposition();
                composition.Clips.Add(clip);

                using var stream = await composition.GetThumbnailAsync(
                    at, w, h, VideoFramePrecision.NearestFrame);
                if (stream == null) return null;

                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                return bitmap;
            }
            catch { return null; }
        }
    }
}
