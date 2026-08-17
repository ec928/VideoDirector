



 Context: Document created because I told the AI to fix one and only one bug (the zoom issue) and it spent 25+ minutes doing fuck all except hallucinate previous recently fixed bugs and investigate them and waste time.

Purpose: So the AI doesn't immediately foget.

AI Fixes & Changelog Record

This document serves as a hardcoded memory record to prevent the AI agent from forgetting recent fixes due to context window truncation or checkpoint resets.

## Recent Fixes Applied

### 1. Audio Stutter during Ken Burns Zoom on Track 1 (Resolved)
- **Files Modified:** `Models/VideoPlaybackEngine.cs`
- **Issue:** Audio stuttered noticeably on Track 1 when a clip used Ken Burns zoom. The heavy UI thread overhead of animating the video frame caused the internal `CurrentStoryTime` master clock to run slightly ahead of or behind the actual hardware video decoder. This triggered the engine's drift correction system, which forced the `MediaPlayer` to seek repeatedly to catch up with the UI clock. 
- **Fix Applied:** Modified `PlaybackTimer_Tick` so that when Track 1 is actively playing a video, `CurrentStoryTime` is driven directly by the `MediaPlayer.PlaybackSession.Position` rather than the UI tick interval. Track 1 now acts as the master hardware clock, meaning it never drifts from itself, completely eliminating the stutter-inducing seeks.

### 2. Track 1 WYSIWYG Zoom Box Aspect Ratio (Resolved)
- **Files Modified:** `Models/VideoPlaybackEngine.cs`
- **Issue:** The dashed cyan WYSIWYG zoom boxes for Track 1 clips were blindly defaulting to an assumption of full-screen (16:9).
- **Fix Applied:** Removed the `_viewModel.IsOverlaySelected` condition inside `UpdateWysiwygOverlay()`. Now, Track 1 properly respects the placement and shape calculations of the PiP engine, aligning the zoom framing with the actual shape of the player window in Arrange mode.

### 2. WYSIWYG Video Preview Zoom Controls Regression (Resolved)
- **Files Modified:** `Models/VideoPlaybackEngine.cs`
- **Issue:** The mouse wheel zoom and pan functionality in the Edit Mode preview was broken. During a previous refactoring of `VideoPlaybackEngine.cs`, the line `_playerControl.ActiveTransform = transform;` inside `EnterEditMode()` was accidentally deleted. Without this, `ActiveTransform` remained null, and `DirectorPlayerControl` silently dropped all mouse wheel events.
- **Fix Applied:** Reinstated `_playerControl.ActiveTransform = transform;` inside `VideoPlaybackEngine.EnterEditMode()` to map the UI input layer back to the clip's transform data model. Mouse wheel zooming and panning in the preview now works perfectly again.

### 2. Timeline Zoom Controls Regression (Resolved)
- **Files Modified:** `Views/VideoDirectorControl.xaml.cs`
- **Issue:** Small clips were auto-fitting to the full timeline width, hard-capping the zoom factor at 1.0, preventing users from zooming out. Additionally, native mouse scroll wheel zooming over the timeline was broken.
- **Fix Applied:** Reinstated the 30-second minimum track width logic (`Math.Max(30.0, ...)`) and hooked up `TimelineScroll_PointerWheelChanged` to natively intercept and apply zoom math from the scroll wheel.

## Recent Fixes Applied

### 2. Audio/Video Playback Stuttering Regression (Resolved)
- **Files Modified:** `Models/VideoPlaybackEngine.cs`
- **Issue:** When a clip first loaded, Windows took ~500ms to open it. During that time, the internal story clock kept ticking forward. Once loaded, the drift correction immediately forced the player to seek 0.5s forward, causing an audio skip/stutter.
- **Fix Applied:** Added logic in `PlaybackTimer_Tick` to check `MediaPlaybackState`. If any active player is `Buffering` or `Opening`, the internal clock is stalled. This ensures every clip plays flawlessly from 0:00.

### 3. Track 1 (Spine) Gap Support (Resolved)
- **Files Modified:** `Models/TimelineTrack.cs`, pointer/drag events.
- **Issue:** Track 1 previously rejected gaps by forcing clips to append to the end of the previous clip mathematically, overriding the user's mouse X coordinate.
- **Fix Applied:** Stripped out the legacy snapping logic. Track 1 now uses the exact same free-form drag positioning math as overlay tracks, respecting exactly where a clip is dropped.

### 4. T1 Color and Selection Visuals (Resolved)
- **Issue:** T1 was the wrong color, and the selection highlight was sticking to the last edited clip instead of clearing when deselected. The video frame border was also not obvious enough to indicate selection.
- **Fix Applied:** These visual issues were fully resolved in a previous session prior to the zoom bug. 

---
*Note: Do not re-investigate ANY of these issues. They have been verified and pushed to the codebase.*
