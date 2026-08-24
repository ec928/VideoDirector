# VideoDirector — Architecture, UI/UX Model & Roadmap

**Purpose**: This authoritative document serves as the single source of truth for the VideoDirector Non-Linear Editor (NLE) architecture. Designed for human developers and AI assistants, it outlines core interaction laws, system topology, historical architectural achievements, strict invariants, and strategic future work.

---

## 1. Product Overview & Core Mechanics

VideoDirector is a multi-track video sequencer and compositor built in **WinUI 3 / Windows App SDK** (mouse + keyboard primary; touch-compatible). It composes multiple video and image assets into a unified time-synchronized output.

### 6-Track Topology The sequencer is bounded to 6 tracks, of which a new project shows 3; Add and Remove act on the top of the stack, because a track index IS its identity and removing from the middle would renumber everything above it. They are **equal**: every track is a `TimelineTrack` holding an `ObservableCollection<CinematicOperation>`, and the engine addresses them generically. There is no spine and no overlay role — the earlier A-Roll / B-Roll split was removed with the unified track engine.

* **Compositing is by Z-order alone**: track 0 renders at the bottom, track 3 at the top.
* **Clips never overlap within a track**, so at most one clip per track is active at any story time. `ResolveOverlaps()` shifts siblings when a clip is moved or reordered.
* **Transitions are per-clip fades, not inter-clip effects.** `TransitionStyle` selects a fade in, a fade out, or both; the engine multiplies the ramp into the clip’s own opacity each frame. Setting a transition length moves `OpDuration` by the same delta, so the fade is extra time rather than material lost. A true crossfade is deliberately absent: it needs the outgoing and incoming clips on screen simultaneously, which one slot holding one clip cannot express.

* **The canvas is the composition space.**
 Every geometry read measures the canvas, not the player pane. Its size is the app window as it was when the project began (mode `Auto`), then held and persisted with the project; the pane only decides the scale it is drawn at. This is what stops the track dock, a window resize or full screen from changing an arrangement.

* **Gaps are allowed on every track.** `TimelineTrack.IsGapless` can force clips to sit perfectly adjacent — the old spine behaviour — but it is a per-track option and every track is currently created with it off.
* **Placement is normalised** (`PlacementCenterX/Y`, `PlacementWidth/Height` in 0..1 space) plus opacity, so any track can act as a Picture-in-Picture layer. Tracks differ only in where new clips *default* to sitting: track 1 defaults to full-screen centre, tracks 2–4 to smaller corner placements.
* **Transitions and speed** (crossfade, dip to black, variable rate including speed 0 for stills held over a set time) apply per clip, not per track role.

### Unified Clip & Ken Burns Model Every clip on every track shares a single data schema (`CinematicOperation`). Any clip can carry an animated **Ken Burns** spatial motion—a smooth pan/zoom defined by Start, optional Mid, and End framing keyframes interpolated via easing curves. Still images advance master story time and Ken Burns spatial animation via wall-clock time rather than media player timestamps.

---

## 2. UI/UX Interaction Model — Three Strict Modes

To prevent interaction collisions and UI clutter, VideoDirector enforces a non-negotiable **Mode Segregation Law**: *The active mode alone dictates how user input is interpreted. No interaction may belong to more than one mode, and no alternate parallel pathways should be introduced.*

### A. PLAYBACK Mode (Green Badge)
* **Purpose**: Screening and review.
* **Behavior**: The entire composite plays back across all tracks in real time.
* **Input Rules**: Canvas direct-manipulation and clip-specific trim controls are strictly disabled to prevent accidental modifications during screening.

### B. ARRANGE Mode (Cyan Badge)
* **Purpose**: Macro-structuring and spatial layout.
* **Behavior**: The composite is paused at the current playhead position.
* **Input Rules**: Users scrub the timeline, reorder clips across tracks, and directly manipulate PiP bounding boxes on the canvas (left-click drag to move, edge/corner drag to resize, mouse wheel to scale).

### C. EDIT Mode (Red Badge)
* **Purpose**: Micro-framing and motion design.
* **Behavior**: Isolates and displays **one clip full-frame** on a clean canvas without redundant outer borders.
* **Input Rules**: Canvas manipulation adjusts the clip's internal Ken Burns content framing (drag to pan content, mouse wheel to zoom content). Entered by single-clicking a timeline clip or double-clicking a canvas PiP; cleanly exited via the interactive mode badge on the playbar, the Done button, or pressing Esc.

---

## 3. UI Layout & Control Topography

* **Timeline Toolbar (`TrackDock` Header)**: A dedicated command bar directly above the timeline separating global actions from inspector panels.
  * *Left Zone*: History (Undo/Redo) and Timeline Mode Tools (Snapping toggle, Ripple edit, Waveform display).
  * *Right Zone*: View zoom/fit controls, Project Operations (Save, Load, Clear), and MP4 Export.
* **Proportional Timeline Dashboard (Bottom Dock)**: Hosts the time ruler, playhead, and 4 colored track lanes. Track labels ("Track 1", "Track 2", etc.) act as interactive load buttons that open file pickers (`LoadIntoTrack`) targeting that specific lane. Supports bidirectional cross-track drag-and-drop, ghost-follow dragging on Track 1, runtime magnetic snapping (8px threshold), and right-click context flyouts (`Duplicate`, `Remove`, `Split at playhead`, `Snapshot still`), and visual drag-to-loop regions on the time ruler.
* **Inspector Panel & Telemetry HUD (Right Panel)**: Dedicated property editor displaying human-readable formatted timecodes (`00:00:00.00`), speed, transitions, Ken Burns keyframe capture buttons (Start/Mid/End), and easing profiles. PiP coordinates and real-time operational readouts are cleanly consolidated into a compact Telemetry HUD for maximum workflow clarity.
* **Transport Pill (Bottom-Center Floating)**: Hosts core transport controls: Play/Pause, Previous/Next frame, range/trim sliders, global playback speed, loop toggle, and inspector docking controls.

---

## 4. Accomplished Improvements & Architectural Ledger

This chronological ledger records all established solutions and performance optimizations. **Do not re-propose or regress these items.**

> Entries below predate the unified track engine and still use the old vocabulary — "spine" for track 1 and "overlay" for tracks 2–4. Those roles no longer exist (see §1); the wording is left as written so the history stays accurate to when each change was made.

### 🎬 Timeline & Track Behavior Unification
* **Consolidated Timeline Toolbar**: Re-homed global operations (Save/Load/Export/Undo) into a dedicated toolbar above the timeline dock, de-cluttering inspector panels.
* **Clickable Track Labels**: Enabled direct asset loading into specific track lanes via clickable track labels.
* **Dynamic Overlap Resolution (`ResolveOverlaps`)**: Replaced rigid slot clamping on Tracks 2–4 with dynamic overlap resolution, automatically shifting sibling clips when reordering occurs.
* **Cross-Track Transfers & Snapping**: Implemented seamless vertical drag-and-drop between spine and overlay tracks, ghost-follow dragging on Track 1, and 8px magnetic snapping across all clip edges and playheads.
* **Context Flyouts**: Re-homed right-click menus (`Duplicate`, `Remove`, `Split`, `Snapshot`) to open cleanly without triggering left-click selection or canvas rebuilds.

### ✂️ Canvas Ergonomics & Modal Polish
* **Mode-Specific Playbar & One-Click Exit**: Confined trimming controls strictly to Edit mode. Colored mode badges (Red=Edit, Green=Play, Cyan=Arrange) act as interactive buttons; clicking the Edit badge instantly returns to Arrange mode.
* **Trim "Messy Blob" Resolution**: Fixed scaling math when trimming short clips from long source videos, preventing trim controls from collapsing into unusable blobs.
* **Compact Inspector & Telemetry HUD (`c45c45c`)**: Re-homed PiP size coordinates and operational readouts into a clean Telemetry HUD, streamlining the Inspector UI.

### 🎞️ Ken Burns Framing Geometry (`066e715`)
* **Layout Clip Preceded the Transform**: A clip with a Ken Burns pan rendered as a narrow strip of picture against black. The geometry was correct throughout — at the failing frame the pan was 518px against an allowance of ±546, i.e. fully covered — but the frame-sized surface lived in a box-sized grid, so WinUI cropped it to the box *before* the transform panned it, leaving `556 − 518 = 38px`. Edit mode was unaffected because there box == content, so nothing overflowed. Fixed by moving the surfaces into a `Canvas` (see §5.5). **Do not "simplify" that Canvas away.**
* **One Aspect Resolver (`AspectOf`)**: Three call sites derived the fit rectangle from different inputs, and two fell back to 16:9 without consulting the clip — so on a 2.39:1 source the WYSIWYG rects were drawn and dragged against a fit 34% out, and a rect dropped visibly *inside* the picture wrote a mark *outside* it. `op.SourceAspect` leads (it is persisted, so the box can be built before any decoder opens); 0 means genuinely unknown and callers must handle it rather than be handed a plausible lie.
* **Geometry Extracted to `ClipGeometry`**: fit → box → content → motion → sampled-source is now pure arithmetic with no WinUI types, used by the compositor, the telemetry HUD and the tests alike. The HUD previously recomputed its own copy and could agree with itself while disagreeing with what was drawn.
* **Headless Regression Suite (`VideoDirector.Tests`)**: 14 tests over that chain, split into invariants (any clip, any pane) and golden values pinned to a frozen project fixture. Note the limit honestly: these would **not** have caught the defect above, because the arithmetic was right — the `Debug.Assert` on the surface's parent is what guards that class of fault.
* **Geometry HUD**: The telemetry overlay reports the box on screen, the motion transform, and the region of the *source frame* being sampled, with any overrun quantified. That last line is the one that distinguishes "black you authored" from "black that is a bug"; throttled to ~10Hz and skipped entirely when hidden.

### 🧪 Mode Rules Made Testable (`ChromeRules`)
* **One Definition Per Rule**: `IsPerforming`, `IsEditorChromeVisible`, `IsTrackDockVisible`, `IsTrackDockReopenVisible`, `IsInspectorVisible` and `CanToggleEditMode` moved into `Models/ChromeRules.cs` as pure static functions; the view-model properties are now one-line delegations. Nothing about the behaviour changed — what changed is that there is exactly one place to change it.
* **The Test That Would Have Caught The Whole Class**: `ArmingCinematicWhileStoppedChangesNothing` sweeps all 128 combinations of the seven flags and asserts that, with `playing == false`, every rule returns the same answer whether cinematic is on or off. That one property covers the four separate cinematic regressions this project shipped. Verified by mutation — reverting `IsTrackDockVisible` to test `cinematic` alone fails the suite.
* **Honest Limit**: these rules say what *should* be on screen. A binding that ignores the property, or a code path that sets `Visibility` directly, is still invisible to the suite — which is why `ApplyCanvasChrome` is the single writer of `CanvasEdge.Visibility` rather than one writer among several.

### 🚀 Playback Synchronization & GPU Performance (`95cd10a`, `a3adb0c`)
* **Wall-Clock Time Synchronization for Still Ken Burns**: Updated `UpdateSpatial` and `CompositionTarget_Rendering` so still images with Ken Burns on Track 1 advance master story time via real wall-clock time rather than remaining frozen at `MediaPlayer.Position = 0:00`. This completely eliminated continuous drift-correction seek-jumping and audio/video stuttering on overlay tracks (Tracks 2–4).
* **Per-Frame UI Layout & GPU Composition Optimization**: Guarded overlay bounding box layout adjustments (`grid.Margin = ...` in `ApplyOverlayBox`) and `CompositeTransform` property assignments (`ApplyMarksAtProgress`) against redundant per-frame overwrites (`Math.Abs(...) > 0.0001` and `Margin.Left != left`). This eliminates 60 FPS unnecessary Measure/Arrange XAML layout passes and prevents dirtying DirectComposition visual trees when transforms and bounding boxes are static.
* **Canvas Edit Mode Visual Cleanliness**: Removed the redundant thick outer accent border (`<Border BorderThickness="3" ... />`) around the video canvas during Edit mode in `VideoDirectorControl.xaml`. Edit mode visual indicators are now cleanly confined to the inspector panel header and the interactive WYSIWYG crop/motion overlays directly on the video.
* **Zero-Speed Keyframe Interpolation**: Ensured Ken Burns animations for speed-0 clips (stills) scale and pan gracefully via real-time rendering fallback when the hardware media position remains static at 0.
* **Timeline Loop Region**: Implemented a drag-to-loop visual region directly on the time ruler that automatically constrains CurrentStoryTime bounds during active playback without interfering with modal edit bounds.

---

## 5. Core Architectural Invariants & Laws

1. **Holistic Design over Piecemeal Hacks**: Never apply localized fixes that break the overarching NLE mental model. All tracks must follow consistent interaction laws.
2. **Uniform Track Semantics**: All four tracks behave identically — time-bounded layers composited by Z-order, using `ResolveOverlaps()` to keep clips on a row from colliding. Nothing may reintroduce a privileged track: sequential, gapless behaviour is the per-track `IsGapless` option, not a role.
3. **Modal Separation of Concern**: Editing handles, trim bars, and PiP bounding boxes must never leak into screening (PLAYBACK mode) or macro-structuring (ARRANGE mode).
4. **GPU Surface Preservation**: Never directly manipulate live `MediaPlayerElement` XAML properties per-frame in ways that corrupt DirectX swapchains; use dedicated transforms and delta-checked placement boxes.
5. **Render Surfaces Live in an Unconstrained Parent**: The `MediaPlayerElement` and still `Image` of each track sit inside `TrackSurfaces{n}`, a `Canvas`, and are sized to the whole frame rather than to the visible box. This is not tidiness — it is load-bearing. WinUI issues a **layout clip** to any child that overflows its parent, and `RenderTransform` is applied *after* that clip, so a frame-sized surface inside a box-sized parent is cropped to the box **before** the Ken Burns pan can move into the surplus. Cropping must therefore happen at render time (`grid.Clip`, a mask) and never through a sizing parent (a constraint). Moving these surfaces back under a sized element, or restoring `HorizontalAlignment="Center"` in place of the explicit `Canvas.Left/Top`, silently reintroduces the black-edge defect. `ApplyOverlayBox` carries a `Debug.Assert` on the parent type for exactly this reason.
6. **Commit Integrity**: Every architectural step must end in a green build and a clean git commit. Small, incremental steps ensure we are never more than one revert away from safety.
7. **Mode Rules Are Pure Functions, Defined Once (`Models/ChromeRules.cs`)**: What a mode means — what is on screen, what is reachable — is arithmetic over seven booleans (cinematic, playing, edit, controls visible, dock open, inspector open, has selection), and it lives in one WinUI-free static class that `DirectorViewModel` delegates to. This is load-bearing history, not tidiness: cinematic mode was tested in five separate places, so each fix caught one caller and left the rest — arming it took the window full screen, disabled canvas zoom and pan, hid the inspector in Edit, and collapsed the track dock, none of which it should ever have done. **Cinematic changes exactly one thing: `IsPerforming = cinematic && playing`.** Never test the cinematic flag alone, and never inline one of these expressions at a call site — that is precisely how the rule forks again.

---

## 6. Active Roadmap & Technical Debt (Owed Work)

These items represent deferred technical debt and strategic improvements to be tackled in priority order:

### A. Live PiP Video Reshaping (High Priority)
* **Problem**: Reshaping or resizing a live/paused `MediaPlayerElement` video surface directly on the canvas can cause DirectX swapchain corruption (green flashing, blanking, or handle occlusion).
* **Target Solution**: Rebuild the PiP compositing layer so that Arrange mode manipulation uses a lightweight, aspect-correct proxy bitmap or swapchain-decoupled surface during active resizing, switching back to live video rendering upon gesture completion.

### B. Scrubbing Seeks on Upper Tracks (Medium Priority)
* **Problem**: During rapid timeline scrubbing, tracks above the bottom one currently display a static frame rather than seeking live to the scrubbed timestamp (only Track 1 seeks live during scrub).
* **Target Solution**: Extend the live scrubbing seek loop to evaluate and seek every active media player per-frame during scrub gestures, not just the bottom track's.

### C. Trimming Edge Mechanics Across Tracks (Medium Priority)
* **Problem**: Edge trimming needs consistent, formally defined behaviour on every track.
* **Target Solution**: Define and implement explicit rules for **Ripple Trimming** (extending a clip edge pushes downstream clips on that track) versus **Roll Trimming** (extending an edge consumes empty gap space or overwrites).

### D. Deep Data Schema Unification — **DONE** Track 1 (`TimelineNodes`) and tracks 2–4 (`OverlayTracks`) used to be distinct collection wrappers. They are now one `ObservableCollection<TimelineTrack>`, and `OverlayTrack.cs` became `TimelineTrack.cs`. No `TrackRole` discriminator was needed in the end: the roles were dropped rather than modelled, which is why tracks are interchangeable above.

The old `TimelineNodes` and `OverlayTracks` names survive only in the project-file deserialiser, so that projects saved before the change still load.



