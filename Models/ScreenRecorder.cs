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
        private bool _useA = true;
        private int _outWidth, _outHeight;
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
                                              int targetWidth = 1920, int fps = 30, int maxSeconds = 3600)
        {
            if (!IsSupported) return new Result { Message = "Screen capture is not available on this system." };

            GraphicsCaptureItem item;
            try { item = CreateItemForWindow(hwnd); }
            catch (Exception ex) { return new Result { Message = "Could not start capture: " + ex.Message }; }

            _outWidth = Math.Max(2, (int)Math.Round(targetWidth / 2.0) * 2);
            _outHeight = (int)Math.Round(_outWidth * (double)item.Size.Height / Math.Max(1, item.Size.Width));
            if (_outHeight % 2 != 0) _outHeight++;
            if (_outHeight < 2) _outHeight = 2;

            _device = CanvasDevice.GetSharedDevice();
            _scaled = new CanvasRenderTarget(_device, _outWidth, _outHeight, 96);
            int byteCount = _outWidth * _outHeight * 4;
            _bufA = new byte[byteCount];
            _bufB = new byte[byteCount];

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
                        ds.DrawImage(bmp, new Windows.Foundation.Rect(0, 0, _outWidth, _outHeight));
                    }

                    // Two buffers, alternating: one being filled while the other is encoded. The
                    // first version allocated per frame and reached 1.9GB on a 26 second take.
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

                byte[] buf;
                lock (_gate) buf = _latest;

                if (buf == null) buf = lastSent ?? new byte[_outWidth * _outHeight * 4];
                else if (ReferenceEquals(buf, lastSent)) Interlocked.Increment(ref _repeats);
                lastSent = buf;

                var sample = MediaStreamSample.CreateFromBuffer(buf.AsBuffer(), due);
                sample.Duration = frameDuration;
                e.Request.Sample = sample;
                index++;
                Interlocked.Increment(ref _encoded);
            };

            var profile = MediaEncodingProfile.CreateMp4(
                _outHeight >= 1000 ? VideoEncodingQuality.HD1080p : VideoEncodingQuality.HD720p);
            profile.Video.Width = (uint)_outWidth;
            profile.Video.Height = (uint)_outHeight;
            profile.Video.FrameRate.Numerator = (uint)fps;
            profile.Video.FrameRate.Denominator = 1;

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
