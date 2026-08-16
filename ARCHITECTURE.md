# VideoDirector — Architecture & UI/UX Model

**Purpose**: This authoritative document serves as the single source of truth for the VideoDirector Non-Linear Editor (NLE) architecture **as it exists today**. Designed for human developers and AI assistants, it outlines core interaction laws, system topology, historical architectural achievements, and strict invariants.

**Scope boundary**: this document describes what the code *does*. All planned and in-progress work lives in [IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md). Nothing here is a proposal, and nothing there has landed unless its phase is marked ✅ Done. If the two disagree about current behaviour, **this document wins** and the plan needs updating.

Several statements below are marked ⚠ — they are accurate today but scheduled to change. Do not build new work that deepens a ⚠ invariant, and do not defend one against a change the plan calls for.

---

## 1. Product Overview & Core Mechanics

VideoDirector is a multi-track video sequencer and compositor built in **WinUI 3 / Windows App SDK** (mouse + keyboard primary; touch-compatible). It composes multiple video and image assets into a unified time-synchronized output.

### 4-Track Topology
The sequencer is bounded to 4 tracks (1 spine + 3 overlay tracks, enabling up to 3 simultaneous Picture-in-Picture layers):

> ⚠ **Scheduled to change** — Plan phase C2 makes all four tracks peers under a single `TimelineTrack` collection. "Gapless" becomes a per-track flag rather than a property of Track 1, and total duration becomes the max end across all tracks. Track 1 ships with the flag on, so default behaviour is unchanged.

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

> ⚠ **Scheduled to change** — two separate changes land here:
> * **Entry** (plan phase B1): single-clicking a clip will *select* it, not enter Edit. The inspector becomes available in Arrange. Edit becomes deliberate (double-click, Enter, or a button). Mode segregation itself is unaffected — selection simply stops being a mode trigger.
> * **Canvas input** (plan phase D2): direct content pan/zoom is replaced by directly manipulating the Start/Mid/End keyframe rectangles over the whole source frame. The wheel will zoom the *selected rectangle*.

---

## 3. UI Layout & Control Topography

* **Timeline Toolbar (`TrackDock` Header)**: A dedicated command bar directly above the timeline separating global actions from inspector panels. 
  * *Left Zone*: History (Undo/Redo) and Timeline Mode Tools (Snapping toggle, Ripple edit, Waveform display).
    * ⚠ Of these three, **only Snapping is functional**. `IsRippleEditEnabled` is written but never read anywhere, and the waveform is synthesised from the clip's hash rather than its audio. The Shuffle toggle on the transport pill has no handler at all. Plan phase A2 resolves all three.
  * *Right Zone*: View zoom/fit controls, Project Operations (Save, Load, Clear), and MP4 Export.
    * ⚠ The "fit" button resizes the *application window* to the video's aspect ratio and resets timeline zoom as a side effect — it is not a timeline-fit control despite its position and icon. Plan phase A2 splits it.
* **Proportional Timeline Dashboard (Bottom Dock)**: Hosts the time ruler, playhead, and 4 colored track lanes. Track labels ("Track 1", "Track 2", etc.) act as interactive load buttons that open file pickers (`LoadIntoTrack`) targeting that specific lane. Supports bidirectional cross-track drag-and-drop, ghost-follow dragging on Track 1, runtime magnetic snapping (8px threshold), and right-click context flyouts (`Duplicate`, `Remove`, `Split at playhead`, `Snapshot still`).

  > ⚠ **Scheduled to change** — Track 1 currently draws as the **top** lane while compositing at the **bottom** of the picture. Plan phase A3 flips lane order so the top lane is the topmost layer, matching the compositor and standard NLE convention, and consolidates the row geometry (today duplicated across six call sites) behind `RowYForTrack` / `TrackAtY`. Phase C3 replaces the single-button track label with a real header (Mute · Hide · Lock · Gapless + overflow) in a wider gutter.
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
2. **Strict Track Roles (Sequence vs. Layering)** ⚠: Track 1 (Spine) is strictly sequential and gapless. Tracks 2–4 (Overlays) are time-bounded layers that use `ResolveOverlaps()` to prevent overlapping clips on the same row.
   *Scheduled to change (plan phase C2)*: the sequence/layer distinction becomes a per-track `IsGapless` flag, not a role. What survives is the **one-active-clip-per-track** rule below, which is load-bearing for the player/surface mapping.
3. **One Active Clip Per Track**: Clips on a track never overlap in time, so at most one clip is active at any story time. This is what lets track *i* own exactly one player and one render surface. Simultaneity is expressed by using another track, never by stacking within one. Survives C2 unchanged.
4. **Z-Order Is Track Index**: Compositing order is determined solely by track number — Track 4 over Track 3 over Track 2 over Track 1. There is no per-clip z-override and none should be added. In the preview this is enforced by XAML declaration order in `DirectorPlayerControl.xaml`; when the spine becomes a generic visual (C2), the surface array must stay declared bottom-to-top.
5. **Modal Separation of Concern**: Editing handles, trim bars, and PiP bounding boxes must never leak into screening (PLAYBACK mode) or macro-structuring (ARRANGE mode).
6. **GPU Surface Preservation**: Never directly manipulate live `MediaPlayerElement` XAML properties per-frame in ways that corrupt DirectX swapchains; use dedicated transforms and delta-checked placement boxes. Corollary (§7A): never reshape a live video surface — Arrange shows a still proxy instead.
7. **Model Mutation Happens On Drop, Not During Drag**: a gesture must not change the model until it completes, and Esc must cancel it cleanly. A drag computes where the clip *would* land, redraws to show it, and commits exactly once on release (`CommitDrag`). Losing pointer capture cancels rather than half-applying.
8. **Commit Integrity**: Every architectural step must end in a green build and a clean git commit. Small, incremental steps ensure we are never more than one revert away from safety.

---

## 6. Owed Work

**All planned work now lives in [IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md)**, which carries the
phase map, the decisions already settled, and the evidence behind each one. Add new forward-looking
work there, not here.

Two long-standing items from the previous roadmap are **not** covered by any phase in that plan and
are recorded here so they are not lost:

### A. Overlay Scrubbing Seeks (Medium Priority)
* **Problem**: During rapid timeline scrubbing, overlay tracks display a static frame rather than seeking live to the scrubbed timestamp (only Track 1 seeks live during scrub).
* **Target Solution**: Extend the live scrubbing seek loop to evaluate and seek active overlay media players per-frame during scrub gestures.

### B. Edge Trimming On The Timeline (Medium Priority)
* **Problem**: No track supports dragging a clip's edges on the timeline — there is no edge grab in `HitClip`/`PointerMoved`. Trimming requires entering Edit mode or typing numbers, and **Ripple** vs **Roll** semantics are undefined and unimplemented.
* **Target Solution**: Define explicit rules for Ripple Trimming (extending an edge pushes downstream clips on that track) versus Roll Trimming (extending an edge consumes gap space or overwrites), then implement edge grabs uniformly across all tracks.

### Previously listed, now superseded
* *Live PiP Video Reshaping* — largely solved by the §7A still-proxy path (see §4). Extending the same treatment to Track 1 is plan phase C2 step 6.
* *Deep Data Schema Unification* — this is plan phase C2.

---

## 7. Compositing & Playback Laws

The source refers to these by letter (`(§7A)`, `(§7B)`, `§7E/F`, …) at the points where they are
enforced. They are the rules that earlier rewrites converged on after repeated failures, so each one
records a specific trap. Breaking one tends to reproduce the original bug rather than a new one.

### 7A. Render Mode Is Set Explicitly, Never Inferred
An upper-track clip renders in exactly one of three modes, chosen in one place only —
`SetOverlayRender` (`Models/VideoPlaybackEngine.cs`):

* **Hidden** — nothing on screen; the `MediaPlayer` is detached from the element.
* **Still** — a plain bitmap (the clip's thumbnail). **No `MediaPlayer` is attached**, so there is no
  video surface at all: nothing that can blank, green, or composite over the arrange chrome when the
  box is moved or reshaped.
* **Video** — the live `MediaPlayerElement`, used for playback and full-screen content editing.

Two corollaries the code depends on:
* **Arrange is a video-free path.** Showing a still must not go through the video-activation
  pipeline. It used to, which is why a still only appeared *after* playing, and why reshaping could
  re-attach a surface and go black.
* **Never reshape a live video surface.** `ApplyOverlayBox` does geometry *only*. Deciding
  still-vs-video used to live there and silently never fired. Arrange-mode drag and wheel handlers
  bail while `IsActivelyPlaying`. Paused counts as Arrange.

### 7B. Tracks Are Data, Not Branches
Track *i* owns `_overlayPlayer[i]` and the pre-declared surface `OverlayVisuals[i]`, and every
per-track operation is one loop body indexed by track. There are no per-track code paths, so adding
a track is a data change rather than a new branch. This is only sound because of invariant §5.3
(one active clip per track) — that is what lets one track map to one player and one surface.

### 7C. One Story-Time Authority
`CurrentStoryTime` is the single value the trackbar, scrubber and overlay evaluation all read, so
they agree at transition boundaries. It is advanced continuously in the render loop from the active
player's real decode position — **not** stepped at clip boundaries — because overlay drift
correction compares against it every frame, and a stale target makes the correction fight the
overlay's real playback (the original overlay-stutter bug).

Transitions are **additive**: a spine clip occupies `OpDuration + TransitionDuration` of story time.
Per-clip speed divides into story time — a clip at 2× contributes half as much story time as video
watched. ⚠ Total length is currently defined by the spine alone; plan phase C2 changes this to the
max end across all tracks.

### 7E/F. The Timeline Is One Shared Time Axis
The ruler, the spine row and the overlay rows are all drawn on a single px-per-second scale, with
`x = 0` at time 0, so vertical alignment across lanes is literal. Track labels sit in a fixed-width
gutter **outside** the horizontal `ScrollViewer`, so they stay put while the lanes scroll. The
playhead is drawn full-height across every lane.

The pointer model follows standard NLE convention: the top ruler scrubs, clip rows drag clips, a tap
in a row selects, and empty row space scrubs. ⚠ The scale is currently *proportional*
(`_timelinePxPerSec = width / totalDuration`) rather than a true px-per-second, and lane order is
inverted relative to compositing — see plan phases C1 and A3.

### 7G. Static Composite Seek
Scrubbing shows the composite at story-time *t* **without playing**: the spine frame (main player
seeked to the right clip and offset, marks applied at the corresponding progress) plus whichever
overlays are active then. A drag on the timeline means "navigate the whole thing", so it drops out
of Edit into Arrange first. Scrubbing while paused means "take over" — it ends the playback loop and
settles into Arrange at the scrubbed point, rather than leaving a suspended loop alive.
