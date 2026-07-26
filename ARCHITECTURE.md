# VideoDirector — Architecture, UI/UX Model & Roadmap

**Path**: `c:\Users\chan_\OneDrive\Apps\ModernImageViewer\VideoDirector\ARCHITECTURE.md`  
**Purpose**: This authoritative document serves as the single source of truth for the VideoDirector Non-Linear Editor (NLE) architecture within ModernImageViewer. Designed for human developers and AI assistants, it outlines core interaction laws, system topology, historical architectural achievements, strict invariants, and strategic future work.

---

## 1. Product Overview & Core Mechanics

VideoDirector is a multi-track video sequencer and compositor built in **WinUI 3 / Windows App SDK** (mouse + keyboard primary; touch-compatible). It composes multiple video and image assets into a unified time-synchronized output.

### 4-Track Topology
The sequencer is bounded to 4 tracks (1 spine + 3 overlay tracks, enabling up to 3 simultaneous Picture-in-Picture layers):
* **Track 1 — The Spine (A-Roll)**: A gapless row of clips played sequentially end-to-end. It defines the total duration of the project. Supports optional additive transitions (crossfade, dip to black) between clips, and variable playback speed (including speed 0 for still images rendered over a set time).
* **Tracks 2, 3 & 4 — Overlays (B-Roll / PiP)**: Freely positioned time-bounded clips (gaps allowed, one active clip per track at any given timestamp). Composited over Track 1 as Picture-in-Picture (PiP) windows. Each clip maintains normalized position and dimension coordinates (`PlacementCenterX/Y`, `PlacementWidth/Height` in 0..1 space) and opacity. When reordering occurs on an overlay track, `ResolveOverlaps()` dynamically shifts sibling clips sequentially to prevent stacking collisions.

### Unified Clip & Ken Burns Model
Every clip across spine and overlay tracks shares a unified data schema (`CinematicOperation`). Any clip can carry an animated **Ken Burns** spatial motion—a smooth pan/zoom defined by Start, optional Mid, and End framing keyframes interpolated via easing curves. Still images on Track 1 advance master story time and Ken Burns spatial animation via wall-clock time rather than media player timestamps.

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
* **Proportional Timeline Dashboard (Bottom Dock)**: Hosts the time ruler, playhead, and 4 colored track lanes. Track labels ("Track 1", "Track 2", etc.) act as interactive load buttons that open file pickers (`LoadIntoTrack`) targeting that specific lane. Supports bidirectional cross-track drag-and-drop, ghost-follow dragging on Track 1, runtime magnetic snapping (8px threshold), and right-click context flyouts (`Duplicate`, `Remove`, `Split at playhead`, `Snapshot still`).
* **Inspector Panel & Telemetry HUD (Right Panel)**: Dedicated property editor displaying human-readable formatted timecodes (`00:00:00.00`), speed, transitions, Ken Burns keyframe capture buttons (Start/Mid/End), and easing profiles. PiP coordinates and real-time operational readouts are cleanly consolidated into a compact Telemetry HUD for maximum workflow clarity.
* **Transport Pill (Bottom-Center Floating)**: Hosts core transport controls: Play/Pause, Previous/Next frame, range/trim sliders, global playback speed, loop toggle, and inspector docking controls.

---

## 4. Accomplished Improvements & Architectural Ledger

This chronological ledger records all established solutions and performance optimizations. **Do not re-propose or regress these items.**

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

### 🚀 Playback Synchronization & GPU Performance (`95cd10a`, `a3adb0c`)
* **Wall-Clock Time Synchronization for Still Ken Burns**: Updated `UpdateSpatial` and `CompositionTarget_Rendering` so still images with Ken Burns on Track 1 advance master story time via real wall-clock time rather than remaining frozen at `MediaPlayer.Position = 0:00`. This completely eliminated continuous drift-correction seek-jumping and audio/video stuttering on overlay tracks (Tracks 2–4).
* **Per-Frame UI Layout & GPU Composition Optimization**: Guarded overlay bounding box layout adjustments (`grid.Margin = ...` in `ApplyOverlayBox`) and `CompositeTransform` property assignments (`ApplyMarksAtProgress`) against redundant per-frame overwrites (`Math.Abs(...) > 0.0001` and `Margin.Left != left`). This eliminates 60 FPS unnecessary Measure/Arrange XAML layout passes and prevents dirtying DirectComposition visual trees when transforms and bounding boxes are static.
* **Canvas Edit Mode Visual Cleanliness**: Removed the redundant thick outer accent border (`<Border BorderThickness="3" ... />`) around the video canvas during Edit mode in `VideoDirectorControl.xaml`. Edit mode visual indicators are now cleanly confined to the inspector panel header and the interactive WYSIWYG crop/motion overlays directly on the video.

---

## 5. Core Architectural Invariants & Laws

1. **Holistic Design over Piecemeal Hacks**: Never apply localized fixes that break the overarching NLE mental model. All tracks must follow consistent interaction laws.
2. **Strict Track Roles (Sequence vs. Layering)**: Track 1 (Spine) is strictly sequential and gapless. Tracks 2–4 (Overlays) are time-bounded layers that use `ResolveOverlaps()` to prevent overlapping clips on the same row.
3. **Modal Separation of Concern**: Editing handles, trim bars, and PiP bounding boxes must never leak into screening (PLAYBACK mode) or macro-structuring (ARRANGE mode).
4. **GPU Surface Preservation**: Never directly manipulate live `MediaPlayerElement` XAML properties per-frame in ways that corrupt DirectX swapchains; use dedicated transforms and delta-checked placement boxes.
5. **Commit Integrity**: Every architectural step must end in a green build and a clean git commit. Small, incremental steps ensure we are never more than one revert away from safety.

---

## 6. Active Roadmap & Technical Debt (Owed Work)

These items represent deferred technical debt and strategic improvements to be tackled in priority order:

### A. Live PiP Video Reshaping (High Priority)
* **Problem**: Reshaping or resizing a live/paused `MediaPlayerElement` video surface directly on the canvas can cause DirectX swapchain corruption (green flashing, blanking, or handle occlusion).
* **Target Solution**: Rebuild the PiP compositing layer so that Arrange mode manipulation uses a lightweight, aspect-correct proxy bitmap or swapchain-decoupled surface during active resizing, switching back to live video rendering upon gesture completion.

### B. Overlay Scrubbing Seeks (Medium Priority)
* **Problem**: During rapid timeline scrubbing, overlay tracks currently display a static frame rather than seeking live to the scrubbed timestamp (only Track 1 seeks live during scrub).
* **Target Solution**: Extend the live scrubbing seek loop to evaluate and seek active overlay media players per-frame during scrub gestures.

### C. Trimming Edge Mechanics Across Overlay Tracks (Medium Priority)
* **Problem**: Edge trimming on Tracks 2–4 needs formal behavioral parity with Track 1.
* **Target Solution**: Define and implement explicit rules for **Ripple Trimming** (extending an overlay clip edge pushes downstream clips on that track) versus **Roll Trimming** (extending an edge consumes empty gap space or overwrites).

### D. Deep Data Schema Unification (Long-Term Architecture)
* **Problem**: Track 1 (`TimelineNodes`) and Tracks 2–4 (`OverlayTracks`) currently use distinct collection wrappers.
* **Target Solution**: Refactor the underlying data models so that all tracks share a single unified `ObservableCollection<TimelineTrack>` schema, differentiated only by compositing attributes (`TrackRole.Spine` vs `TrackRole.Overlay`).
