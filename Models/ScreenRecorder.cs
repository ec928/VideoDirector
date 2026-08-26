using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace VideoDirector.Models
{
    /// <summary>
    /// Records the app's own window to an MP4 — the export that actually works.
    /// </summary>
    /// <remarks>
    /// WHY THIS EXISTS INSTEAD OF A RENDERER. Export through MediaComposition can carry cuts,
    /// timing and PiP placement and nothing else: Ken Burns, fades, speed and borders all need
    /// per-frame work, and that path is closed (invariant 6). It also refused real projects
    /// outright — a source whose width is odd is rejected as an overlay layer.
    ///
    /// Recording the performance sidesteps every bit of that by never touching the source files.
    /// It photographs what the compositor already drew, so motion, fades, speed, borders and
    /// crop-fill come out right for the same reason they look right on screen. There is no second
    /// definition of the geometry to drift.
    ///
    /// TWO THINGS THAT ARE NOT OPTIONAL:
    ///
    ///   The frame clock. Capture is CHANGE-driven — a window that is not redrawing produces no
    ///   frames at all. So the encoder runs on its own clock and re-sends the last frame when
    ///   nothing new arrived. Forwarding frames as they turn up would delete every held shot.
    ///
    ///   The vertical flip. Media Foundation reads an uncompressed BGRA buffer bottom-up while
    ///   GetPixelBytes hands back top-down rows, so without this the whole recording is mirrored.
    ///   Done as a draw transform rather than by reversing rows on the CPU: free on the GPU.
    /// </remarks>
    public sealed class ScreenRecorder : IDisposable
    {
        public static bool IsSupported
        {
            get { try { return GraphicsCaptureSession.IsSupported(); } catch { return false; } }
        }

        public sealed class Result
        {
            public bool Success { get; init; }
            public string Message { get; init; } = string.Empty;
            public int FramesEncoded { get; init; }
            public int FramesRepeated { get; init; }
            public TimeSpan Duration { get; init; }
        }

        private readonly object _gate = new object();
        private CanvasDevice _device;
        private CanvasRenderTarget _scaled;
        private Direct3D11CaptureFramePool _pool;
        private GraphicsCaptureSession _session;
        private byte[] _bufA, _bufB, _latest;
        private byte[][] _encodePool;
        private int _encodeSlot;
        private bool _useA = true;
        private int _outWidth, _outHeight;
        private Windows.Foundation.Rect _srcRect;
        private volatile bool _stopRequested;
        private int _captured, _encoded, _repeats;

        /// <summary>Frames actually delivered by the capture pipeline. Diagnostic only.</summary>
        public int FramesCaptured => Volatile.Read(ref _captured);

        /// <summary>Ask the recorder to finish. The take ends at the next frame boundary.</summary>
        public void RequestStop() => _stopRequested = true;

        /// <summary>
        /// Record <paramref name="hwnd"/> until RequestStop, or until maxSeconds elapses.
        /// Returns when the file is written and closed.
        /// </summary>
        public async Task<Result> RecordAsync(IntPtr hwnd, StorageFile output,
                                              int targetWidth = 1920, int targetHeight = 0, int fps = 30, int maxSeconds = 3600)
        {
            if (!IsSupported) return new Result { Message = "Screen capture is not available on this system." };

            GraphicsCaptureItem item;
            try { item = CreateItemForWindow(hwnd); }
            catch (Exception ex) { return new Result { Message = "Could not start capture: " + ex.Message }; }

            double capW = Math.Max(1, item.Size.Width);
            double capH = Math.Max(1, item.Size.Height);

            _outWidth = Align16(targetWidth);
            _outHeight = targetHeight > 0
                ? Align16(targetHeight)
                : Align16((int)Math.Round(_outWidth * capH / capW));

            // Do not upscale past the captured window: a 4K canvas viewed in a 1080p window
            // has no 4K pixels to photograph.
            double fit = Math.Min(1.0, Math.Min(capW / _outWidth, capH / _outHeight));
            if (fit < 1.0)
            {
                _outWidth = Align16((int)Math.Round(_outWidth * fit));
                _outHeight = Align16((int)Math.Round(_outHeight * fit));
            }

            // Centre-crop the captured window to the output aspect so a 9:16 canvas in a
            // landscape window is not squeezed, and pasteboard around a 2.39:1 canvas is dropped.
            double outAspect = (double)_outWidth / _outHeight;
            double capAspect = capW / capH;
            if (capAspect > outAspect)
            {
                double srcW = capH * outAspect;
                _srcRect = new Windows.Foundation.Rect((capW - srcW) / 2, 0, srcW, capH);
            }
            else
            {
                double srcH = capW / outAspect;
                _srcRect = new Windows.Foundation.Rect(0, (capH - srcH) / 2, capW, srcH);
            }

            _device = CanvasDevice.GetSharedDevice();
            _scaled = new CanvasRenderTarget(_device, _outWidth, _outHeight, 96);
            int byteCount = _outWidth * _outHeight * 4;
            _bufA = new byte[byteCount];
            _bufB = new byte[byteCount];
            _encodePool = new byte[8][];
            for (int i = 0; i < _encodePool.Length; i++) _encodePool[i] = new byte[byteCount];
            _encodeSlot = 0;

            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            _pool.FrameArrived += OnFrameArrived;
            _session = _pool.CreateCaptureSession(item);

            // The pointer must never reach the file. This is the API half; the chrome it would
            // otherwise summon is handled by the recording lock in ChromeRules.
            try { _session.IsCursorCaptureEnabled = false; } catch { }
            _session.StartCapture();

            // Don't open the file on grey: wait briefly for the first real frame.
            for (int i = 0; i < 40 && FramesCaptured == 0; i++) await Task.Delay(50);

            var clock = Stopwatch.StartNew();
            try
            {
                await EncodeAsync(output, fps, maxSeconds);
            }
            catch (Exception ex)
            {
                return new Result { Message = "Recording failed: " + ex.Message, Duration = clock.Elapsed };
            }
            finally
            {
                Dispose();
            }

            return new Result
            {
                Success = true,
                Message = output.Path,
                FramesEncoded = _encoded,
                FramesRepeated = _repeats,
                Duration = clock.Elapsed
            };
        }

        private void OnFrameArrived(Direct3D11CaptureFramePool pool, object _)
        {
            using (var frame = pool.TryGetNextFrame())
            {
                if (frame == null) return;
                try
                {
                    using (var bmp = CanvasBitmap.CreateFromDirect3D11Surface(_device, frame.Surface))
                    using (var ds = _scaled.CreateDrawingSession())
                    {
                        ds.Clear(Microsoft.UI.Colors.Black);
                        ds.Transform = Matrix3x2.CreateScale(1, -1) * Matrix3x2.CreateTranslation(0, _outHeight);
                        ds.DrawImage(bmp,
                            new Windows.Foundation.Rect(0, 0, _outWidth, _outHeight),
                            _srcRect);
                    }

                    // Two capture buffers, alternating: one being filled while the other is copied
                    // into an encode-pool slot. Allocating a new array per frame reached 1.9GB on a
                    // 26 second take; wrapping the capture buffer itself let FrameArrived overwrite
                    // a sample the transcoder still held.
                    var target = _useA ? _bufA : _bufB;
                    _scaled.GetPixelBytes(target.AsBuffer());
                    lock (_gate) { _latest = target; _useA = !_useA; }
                    Interlocked.Increment(ref _captured);
                }
                catch
                {
                    // A frame lost to a resize or a device blip is not worth ending a take over.
                }
            }
        }

        private async Task EncodeAsync(StorageFile output, int fps, int maxSeconds)
        {
            var props = VideoEncodingProperties.CreateUncompressed(
                MediaEncodingSubtypes.Bgra8, (uint)_outWidth, (uint)_outHeight);

            var mss = new MediaStreamSource(new VideoStreamDescriptor(props))
            {
                BufferTime = TimeSpan.Zero,
                Duration = TimeSpan.FromSeconds(maxSeconds)
            };

            int limit = maxSeconds * fps;
            int index = 0;
            var frameDuration = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / fps);
            byte[] lastSent = null;
            var pace = Stopwatch.StartNew();

            mss.Starting += (s, e) => e.Request.SetActualStartPosition(TimeSpan.Zero);
            mss.SampleRequested += (s, e) =>
            {
                if (_stopRequested || index >= limit) { e.Request.Sample = null; return; }

                // Paced to the wall clock: the window plays at 1x, so pulling faster would only
                // duplicate frames and finish before the performance does.
                var due = TimeSpan.FromTicks(frameDuration.Ticks * index);
                var wait = due - pace.Elapsed;
                if (wait > TimeSpan.Zero) Thread.Sleep(wait);

                byte[] src;
                lock (_gate) src = _latest;

                byte[] dest = _encodePool[_encodeSlot];
                _encodeSlot = (_encodeSlot + 1) % _encodePool.Length;

                if (src == null)
                {
                    if (lastSent != null) Buffer.BlockCopy(lastSent, 0, dest, 0, dest.Length);
                    else Array.Clear(dest, 0, dest.Length);
                    Interlocked.Increment(ref _repeats);
                }
                else
                {
                    Buffer.BlockCopy(src, 0, dest, 0, dest.Length);
                    if (ReferenceEquals(src, lastSent)) Interlocked.Increment(ref _repeats);
                    lastSent = src;
                }

                var sample = MediaStreamSample.CreateFromBuffer(dest.AsBuffer(), due);
                sample.Duration = frameDuration;
                e.Request.Sample = sample;
                index++;
                Interlocked.Increment(ref _encoded);
            };

            var profile = CreateSizedMp4(_outWidth, _outHeight, fps);

            using (var stream = await output.OpenAsync(FileAccessMode.ReadWrite))
            {
                var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
                var prep = await transcoder.PrepareMediaStreamSourceTranscodeAsync(mss, stream, profile);
                if (!prep.CanTranscode)
                    throw new InvalidOperationException("the encoder refused: " + prep.FailureReason);
                await prep.TranscodeAsync();
            }
        }

        public void Dispose()
        {
            try { _session?.Dispose(); } catch { }
            try { if (_pool != null) { _pool.FrameArrived -= OnFrameArrived; _pool.Dispose(); } } catch { }
            try { _scaled?.Dispose(); } catch { }
            _session = null; _pool = null; _scaled = null;
            _bufA = _bufB = _latest = null;
            _encodePool = null;
        }

        /// <summary>
        /// MP4 profile whose Width/Height are the ones we asked for, not a quality preset.
        /// </summary>
        /// <remarks>
        /// CreateMp4(HD1080p) is 1920×1080. Writing profile.Video.Width under CsWinRT mutates a
        /// copy, so the encoder kept the preset. A 2752×1158 canvas therefore exported as exactly
        /// 1920×1080. Build the H.264 properties, assign the whole Video object back, and throw
        /// if a read-back still disagrees.
        /// </remarks>
        internal static MediaEncodingProfile CreateSizedMp4(int width, int height, int fps = 30)
        {
            width = Align16(width);
            height = Align16(height);
            fps = Math.Max(1, fps);

            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
            var video = profile.Video;
            video.Width = (uint)width;
            video.Height = (uint)height;
            video.FrameRate.Numerator = (uint)fps;
            video.FrameRate.Denominator = 1;
            video.PixelAspectRatio.Numerator = 1;
            video.PixelAspectRatio.Denominator = 1;
            ulong pixels = (ulong)width * (ulong)height;
            video.Bitrate = (uint)Math.Clamp(
                pixels * 8_000_000UL / (1920UL * 1080UL), 2_000_000UL, 40_000_000UL);
            profile.Video = video;

            if (profile.Video == null
                || profile.Video.Width != (uint)width
                || profile.Video.Height != (uint)height)
            {
                throw new InvalidOperationException(
                    "encoder profile is "
                    + (profile.Video == null ? "empty" : profile.Video.Width + "x" + profile.Video.Height)
                    + ", wanted " + width + "x" + height);
            }
            return profile;
        }

        internal static int Align16(int n)
        {
            if (n < 16) n = 16;
            return (n + 8) / 16 * 16;
        }

        // ---- IGraphicsCaptureItemInterop, through the vtable ------------------------------------
        //
        // The tidy routes do not work: CsWinRT will not cast an activation factory's
        // IObjectReference to a ComImport interface, and neither will Marshal.GetObjectForIUnknown
        // on its ThisPtr. One call, made once per take.
        private delegate int CreateForWindowFn(IntPtr thisPtr, IntPtr hwnd, ref Guid iid, out IntPtr result);

        private static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
        {
            var interopIid = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
            var objRef = WinRT.ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");

            int hr = Marshal.QueryInterface(objRef.ThisPtr, ref interopIid, out IntPtr interop);
            if (hr != 0) throw new InvalidOperationException("capture interop unavailable (0x" + hr.ToString("X8") + ")");

            try
            {
                IntPtr vtbl = Marshal.PtrToStructure<IntPtr>(interop);
                IntPtr slot = Marshal.ReadIntPtr(vtbl, 3 * IntPtr.Size);   // 0-2 are IUnknown
                var create = Marshal.GetDelegateForFunctionPointer<CreateForWindowFn>(slot);

                // The WinRT IID of IGraphicsCaptureItem. NOT typeof(...).GUID — under CsWinRT that
                // returns the .NET type's GUID and CreateForWindow answers E_NOINTERFACE.
                var itemIid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
                hr = create(interop, hwnd, ref itemIid, out IntPtr itemPtr);
                if (hr != 0) throw new InvalidOperationException("CreateForWindow failed (0x" + hr.ToString("X8") + ")");

                return WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr);
            }
            finally { Marshal.Release(interop); }
        }
    }
}
