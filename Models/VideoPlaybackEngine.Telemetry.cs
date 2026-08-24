using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using VideoDirector.ViewModels;
using Microsoft.UI.Dispatching;

// VideoPlaybackEngine - the two HUDs. Every value is READ BACK from the live visual tree rather than recomputed, so it reports what the app is doing, not what it intends to.

namespace VideoDirector.Models
{
    public partial class VideoPlaybackEngine
    {
        private DateTime _lastTelemetryUpdate = DateTime.MinValue;
private void UpdateTelemetryOverlay(bool isEditMode = false)
        {
            if (_viewModel.IsTelemetryVisible)
            {
                var activeTransform = _playerControl.ActiveTransform;
                _playerControl.TelemetryOverlay.Visibility = Microsoft.UI.Xaml.Visibility.Visible;

                var currentActivePlayer = isEditMode ? _overlayPlayer[0] : null;
                var activeOp = isEditMode ? _viewModel.SelectedClip as CinematicOperation : null;

                string currentFileName = activeOp != null ? System.IO.Path.GetFileName(activeOp.FilePath) : "";
                
                var currentStoryTime = _viewModel.CurrentStoryTime;
                var clipEndTime = activeOp != null ? (activeOp.VideoStartTime + activeOp.OpDuration) : TimeSpan.Zero;
                _playerControl.TelemetryStoryTime.Text = $"Timeline  : {currentStoryTime:hh\\:mm\\:ss\\.ff} / {_viewModel.TotalStoryTime:hh\\:mm\\:ss\\.ff}";
                
                if (currentActivePlayer?.PlaybackSession != null)
                {
                    _playerControl.TelemetryClipTime.Text = $"Clip Time : {currentActivePlayer.PlaybackSession.Position:hh\\:mm\\:ss\\.ff} / {clipEndTime:hh\\:mm\\:ss\\.ff} [{currentFileName}]";
                    uint nw = currentActivePlayer.PlaybackSession.NaturalVideoWidth;
                    uint nh = currentActivePlayer.PlaybackSession.NaturalVideoHeight;
                    if (activeOp != null && (_viewModel.IsOverlaySelected || _isEditingOverlay || activeOp.PlacementWidth < 1.0 || activeOp.PlacementHeight < 1.0))
                    {
                        _playerControl.TelemetryVideoSize.Text = $"PiP Size  : W:{activeOp.PlacementWidth * 100:F1}% H:{activeOp.PlacementHeight * 100:F1}% (Res: {nw}x{nh})";
                    }
                    else
                    {
                        _playerControl.TelemetryVideoSize.Text = $"Resolution: {nw}x{nh} px (100% Full Frame)";
                    }
                    _playerControl.TelemetryVideoSize.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                }
                else
                {
                    _playerControl.TelemetryVideoSize.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                }
                
                WriteGeometryTelemetry();

                if (activeTransform != null) {
                    _playerControl.TelemetryOperationInfo.Text = $"Zoom/Pan  : Z:{activeTransform.ScaleX:F2} X:{activeTransform.TranslateX:F0} Y:{activeTransform.TranslateY:F0}";
                }
                
                if (activeOp != null && activeOp.StartMark != null && activeOp.EndMark != null
                    && TryGetMarkSpace(activeOp, out double W, out double H)) {
                    // The video FIT, not the whole pane: that is the space marks live in, and the
                    // boxes reported here are meant to match the ones the editor draws.
                    EnsureMarksNormalized(activeOp);

                    // ...and "match" means the PiP-shaped crop window, not the full frame. This
                    // reported W:1212 for a box the editor drew at W:409, which reads as a framing
                    // that overhangs the picture when it does not. Same derivation as
                    // UpdateWysiwygOverlay, so the two now agree.
                    double videoAspectT = W / H;
                    double pipAspectT = videoAspectT * (activeOp.PlacementWidth / activeOp.PlacementHeight);
                    double bwT = pipAspectT > videoAspectT ? W : H * pipAspectT;
                    double bhT = pipAspectT > videoAspectT ? W / pipAspectT : H;

                    double Sc = activeTransform != null ? activeTransform.ScaleX : 1.0;
                    double txc = activeTransform != null ? activeTransform.TranslateX : 0.0;
                    double tyc = activeTransform != null ? activeTransform.TranslateY : 0.0;

                    double St_s = activeOp.StartMark.Scale;
                    double txt_s = activeOp.StartMark.X * W;
                    double tyt_s = activeOp.StartMark.Y * H;
                    double startLeft = (-bwT / 2 - txt_s) * (Sc / St_s) + W / 2 + txc;
                    double startTop = (-bhT / 2 - tyt_s) * (Sc / St_s) + H / 2 + tyc;
                    double startWidth = bwT * (Sc / St_s);
                    double startHeight = bhT * (Sc / St_s);

                    double St_e = activeOp.EndMark.Scale;
                    double txt_e = activeOp.EndMark.X * W;
                    double tyt_e = activeOp.EndMark.Y * H;
                    double endLeft = (-bwT / 2 - txt_e) * (Sc / St_e) + W / 2 + txc;
                    double endTop = (-bhT / 2 - tyt_e) * (Sc / St_e) + H / 2 + tyc;
                    double endWidth = bwT * (Sc / St_e);
                    double endHeight = bhT * (Sc / St_e);

                    _playerControl.TelemetryStartMarkInfo.Text = $"Start Box : L:{startLeft:F0} T:{startTop:F0} W:{startWidth:F0} H:{startHeight:F0} (Z:{activeOp.StartMark.Scale:F2})";
                    
                    if (activeOp.MidMark != null) {
                        double St_m = activeOp.MidMark.Scale;
                        double txt_m = activeOp.MidMark.X * W;
                        double tyt_m = activeOp.MidMark.Y * H;
                        double midLeft = (-bwT / 2 - txt_m) * (Sc / St_m) + W / 2 + txc;
                        double midTop = (-bhT / 2 - tyt_m) * (Sc / St_m) + H / 2 + tyc;
                        double midWidth = bwT * (Sc / St_m);
                        double midHeight = bhT * (Sc / St_m);
                        _playerControl.TelemetryMidMarkInfo.Text   = $"MidBox   : L:{midLeft:F0} T:{midTop:F0} W:{midWidth:F0} H:{midHeight:F0} (Z:{activeOp.MidMark.Scale:F2})";
                        _playerControl.TelemetryMidMarkInfo.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                    } else {
                        _playerControl.TelemetryMidMarkInfo.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    }

                    _playerControl.TelemetryEndMarkInfo.Text   = $"End Box   : L:{endLeft:F0} T:{endTop:F0} W:{endWidth:F0} H:{endHeight:F0} (Z:{activeOp.EndMark.Scale:F2})";
                    _playerControl.TelemetryStartMarkInfo.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                    _playerControl.TelemetryEndMarkInfo.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                }
                else {
                    _playerControl.TelemetryStartMarkInfo.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    _playerControl.TelemetryMidMarkInfo.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    _playerControl.TelemetryEndMarkInfo.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                }
            }
            else
            {
                _playerControl.TelemetryOverlay.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            }
        }

        // ==================== Geometry HUD ====================
        //
        // The four numbers that decide what you actually see: where the box is on screen, where the
        // playhead is, what the motion transform is doing, and which part of the SOURCE frame that
        // combination ends up sampling. The last one is the point - it is the only line that tells
        // you whether black on screen is a framing you authored or a bug, and it is what took this
        // long to work out the first time round.
        //
        // Throttled to ~10Hz and skipped entirely when the HUD is hidden, so it costs nothing in
        // the render loop. Every value is read from the live visual tree rather than recomputed, so
        // it reports what the app IS doing, not what it intends to do.
        private DateTime _lastGeometryUpdate = DateTime.MinValue;

        private static string Secs(double s) => $"{s:00.00}";

        private void WriteGeometryTelemetry()
        {
            var line = _playerControl.TelemetryGeometry;
            if (line == null) return;

            if (!_viewModel.IsTelemetryVisible)
            {
                line.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                return;
            }

            var now = DateTime.Now;
            if ((now - _lastGeometryUpdate).TotalMilliseconds < 100) return;
            _lastGeometryUpdate = now;

            var sb = new System.Text.StringBuilder();

            for (int slot = 0; slot < MaxOverlayTracks; slot++)
            {
                var op = _activeOverlay[slot];
                if (op == null) continue;

                var vis = _playerControl.OverlayVisuals[slot];
                bool still = vis.Still != null && vis.Still.Visibility == Microsoft.UI.Xaml.Visibility.Visible;
                var surface = still ? (Microsoft.UI.Xaml.FrameworkElement)vis.Still
                                    : (Microsoft.UI.Xaml.FrameworkElement)vis.Video;
                var t = still ? vis.StillTransform : vis.Transform;

                double aspect = AspectOf(op, slot);
                if (aspect <= 0 || !TryGetMarkSpace(op, out double fitW, out double fitH))
                {
                    sb.AppendLine($"T{slot + 1}  waiting for source size");
                    continue;
                }

                bool editMode = _mode == EditorMode.Edit;
                double vpW = _playerControl.CanvasWidth, vpH = _playerControl.CanvasHeight;

                // Same functions the compositor uses, not a parallel copy - a readout that
                // recomputes its own geometry can agree with itself while disagreeing with what was
                // drawn, which is precisely how a HUD ends up lying.
                var box = ClipGeometry.Box(fitW, fitH, vpW, vpH,
                                           op.PlacementWidth, op.PlacementHeight,
                                           op.PlacementCenterX, op.PlacementCenterY, editMode);
                double boxW = box.W, boxH = box.H, left = box.X, top = box.Y;

                double contentW = surface != null && !double.IsNaN(surface.Width) ? surface.Width : boxW;
                double contentH = surface != null && !double.IsNaN(surface.Height) ? surface.Height : boxH;

                double S = t?.ScaleX ?? 1, tx = t?.TranslateX ?? 0, ty = t?.TranslateY ?? 0;
                if (S <= 0) S = 1;

                // Source pixel dimensions, so the sampled region reads in the units the footage is
                // actually in rather than in pane pixels.
                if (!TryGetSourcePixelSize(slot, op, out double srcW, out double srcH))
                {
                    srcH = 1080; srcW = 1080 * aspect;
                }

                // The visible window expressed on the source frame. The content surface holds the
                // WHOLE frame drawn at contentW x contentH, so scaling that ratio converts a
                // pane-pixel window into source pixels.
                var seen = ClipGeometry.SampledSource(contentW, contentH, boxW, boxH, S, tx, ty, srcW, srcH);
                double x0 = seen.X, x1 = seen.Right, y0 = seen.Y, y1 = seen.Bottom;

                var over = new System.Collections.Generic.List<string>();
                if (x0 < -0.5) over.Add($"{-x0 * (boxW / (x1 - x0)):F0}px left");
                if (y0 < -0.5) over.Add($"{-y0 * (boxH / (y1 - y0)):F0}px top");
                if (x1 > srcW + 0.5) over.Add($"{(x1 - srcW) * (boxW / (x1 - x0)):F0}px right");
                if (y1 > srcH + 0.5) over.Add($"{(y1 - srcH) * (boxH / (y1 - y0)):F0}px bottom");

                double into = Math.Max(0, (_viewModel.CurrentStoryTime - op.StartTime).TotalSeconds);
                double dur = op.OpDuration.TotalSeconds;
                var srcPos = op.VideoStartTime + TimeSpan.FromSeconds(into * Math.Max(0, op.PlaybackSpeed));

                sb.AppendLine($"T{slot + 1} {(still ? "still" : "video")}  {System.IO.Path.GetFileName(op.FilePath)}");
                sb.AppendLine($"   time    {Secs(_viewModel.CurrentStoryTime.TotalSeconds)}s of {Secs(_viewModel.TotalStoryDuration.TotalSeconds)}s" +
                              $"   into clip {Secs(into)}s of {Secs(dur)}s" +
                              $"   source {srcPos:hh\\:mm\\:ss\\.ff}");
                sb.AppendLine($"   box     ({left:F0},{top:F0}) to ({left + boxW:F0},{top + boxH:F0})   {boxW:F0} x {boxH:F0}" +
                              $"   pane {vpW:F0} x {vpH:F0}");
                sb.AppendLine($"   motion  zoom {S:F2}x   pan {tx:+0;-0;0},{ty:+0;-0;0}   surface {contentW:F0} x {contentH:F0}");
                sb.AppendLine($"   showing source x {x0:F0}..{x1:F0} of {srcW:F0}   y {y0:F0}..{y1:F0} of {srcH:F0}" +
                              (over.Count == 0 ? "   (all inside)" : "   BLACK: " + string.Join(", ", over)));
            }

            if (sb.Length == 0)
            {
                line.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                return;
            }

            line.Text = sb.ToString().TrimEnd();
            line.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        }
    }
}
