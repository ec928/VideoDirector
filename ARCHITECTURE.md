# VideoDirector — Architecture & UI/UX Model

**Purpose**: This authoritative document serves as the single source of truth for the VideoDirector Non-Linear Editor (NLE) architecture **as it exists today**. Designed for human developers and AI assistants, it outlines core interaction laws, system topology, historical architectural achievements, and strict invariants.

**Scope boundary**: this document describes what the code *does*. All planned and in-progress work lives in [IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md). Nothing here is a proposal, and nothing there has landed unless its phase is marked ✅ Done. If the two disagree about current behaviour, **this document wins** and the plan needs updating.

Several statements below are marked ⚠ — they are accurate today but scheduled to change. Do not build new work that deepens a ⚠ invariant, and do not defend one against a change the plan calls for.

---

## 1. Product Overview & Core Mechanics

VideoDirector is a multi-track video sequencer and compositor built in **WinUI 3 / Windows App SDK** (mouse + keyboard primary; touch-compatible). It composes multiple video and image assets into a unified time-synchronized output.

### 4-Track Topology
Four **peer** tracks (`DirectorViewModel.Tracks`, all `TimelineTrack`). There is no privileged "spine": every track holds the same clip type, carries a placement box, and obeys the same rules. Compositing order is track index (§5.4).

A clip's position is **always** its absolute `StartTime`. What differs between tracks is behaviour, expressed as flags:

* **`IsGapless`** - clips butt up end-to-end in list order; adding, removing or reordering reflows the rest, and start times are *derived* from order by `TimelineTrack.Normalize()`. This is what Track 1 used to be structurally. Ships on for `Tracks[0]` and off for the rest, so default behaviour matches the old app, but it can be switched off there or on anywhere.
* **`IsMuted` / `IsHidden` / `IsLocked`** - track-level overrides. Muted contributes no audio, hidden contributes no picture, locked rejects mutation but still selects and inspects. Muted and hidden are honoured by the live compositor; the exporter does not read them yet.

On a non-gapless track, gaps are legal and `ResolveOverlaps()` keeps clips from colliding. Every track is **strict** - its clips never overlap, so at most one is active at any story time (§5.3). A time with no clip on a track renders as background.

Project duration is the latest end across **all** tracks, not one track's span.

### Unified Clip & Ken Burns Model
Every clip on every track shares one schema (`CinematicOperation`). Any clip can carry an animated **Ken Burns** spatial motion - a smooth pan/zoom defined by Start, optional Mid, and End framing keyframes interpolated via easing curves. Stills (image files, or `PlaybackSpeed == 0`) advance master story time and Ken Burns animation by wall clock rather than media player timestamps.

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
* **Input Rules**: The canvas is the **framing editor**. The whole source frame is shown undistorted, and the Start / Mid / End keyframes are drawn as camera rectangles inside it — drag one to move it, its handles to resize (aspect locked to the frame), the wheel to zoom the selected one. Clicking a rectangle chooses which keyframe you are working on. The dim outside the selected rectangle shows what will actually be seen; a dashed path joins the keyframe centres; a red rectangle tracks the camera at the playhead, so scrubbing shows the real motion. A **Result view** toggle swaps the rectangles for what the viewer gets.
* **Entry is deliberate** - double-click a timeline clip, press Enter on a selection, double-tap a canvas PiP, or use the inspector's "Edit framing" button. **Selecting a clip does not enter Edit**: selection means "work on this clip" and shows it in the inspector, nothing more. Exit via the mode badge, Esc, or clicking the timeline, which exits *and* does its normal job in the same gesture.

---

## 3. UI Layout & Control Topography

* **Timeline Toolbar (`TrackDock` Header)**: A dedicated command bar directly above the timeline separating global actions from inspector panels. 
  * *Left Zone*: History (Undo/Redo) and the Snapping toggle (**N**).
  * *Right Zone*: timeline zoom readout, zoom in/out (**Ctrl+NumPad +/-**), fit-to-project (**Ctrl+0**), Save, Load, an overflow menu (resize window to video aspect; clear project), and MP4 Export. Every shortcut named in a tooltip here is actually bound - keep it that way.
* **Timeline Dashboard (Bottom Dock)**: The time ruler, playhead, and one lane per track, all on one shared px=seconds scale.
  * **Lanes run highest track at the top** (§5.4), so dragging a clip up a lane moves it up the compositing stack.
  * **Track headers** sit in a fixed 168px gutter outside the horizontal scroller: identity chip, name, **Mute / Hide / Lock / Sequence**, and an overflow menu (add clips, default placement for new clips, clear track).
  * The drawn span is content **plus runway**, never the content exactly, so a clip can always be dragged past the current end to extend the project.
  * Drags **preview and commit on drop** (§5.7); Esc cancels. External file drags highlight the destination lane. Right-click gives a lane-scoped menu whose clip actions disable when no clip is under the cursor.
  * Magnetic snapping (8px threshold), and Ctrl+drag scrubs from anywhere.
* **Inspector Panel & Telemetry HUD (Right Panel)**: Dedicated property editor displaying human-readable formatted timecodes (`00:00:00.00`), speed, transitions, the Start/Mid/End keyframe pickers, the Mid keyframe's position within the clip, and motion pacing. PiP coordinates and real-time operational readouts are cleanly consolidated into a compact Telemetry HUD for maximum workflow clarity.
* **Transport Pill (Bottom-Center Floating)**: Hosts core transport controls: Play/Pause, Previous/Next frame, range/trim sliders, global playback speed, loop toggle, and inspector docking controls.

---

## 4. Accomplished Improvements & Architectural Ledger

This chronological ledger records all established solutions and performance optimizations. **Do not re-propose or regress these items.**

*Historical note*: entries below predate the track unification (plan phase C2) and describe the app in the vocabulary of that time — "spine" for track 0, "overlay" for the rest. The problems they solved and the solutions they record still stand; only the naming has moved on.

### 🧱 Track Unification (plan phases A1–C4)
* **One track model**: `TimelineTrack` x4, no privileged spine. Position is always absolute `StartTime`; gapless/free is a per-track flag. Duration is the max end across all tracks, so a project made only of upper-track clips is playable and visible.
* **Lane order matches compositing**: highest track draws in the top lane. Row geometry lives in one tested `TimelineGeometry` rather than seven inlined copies.
* **Selection is not a mode**: one `SelectedClip`; the inspector follows it; Edit is entered deliberately.
* **Drags preview and commit on drop**, with Esc to cancel (§5.7).
* **Real track headers**: Mute / Hide / Lock / Sequence + overflow.
* **Placement for every track**: track 0 renders inside a `BaseBox` and can be a PiP like any other; geometry is the shared, tested `PlacementBox`.
* **Placement is authorable**: full-frame / fill / corner presets and numeric fields in the inspector, on every track. Fill can exceed the aspect-fit size, which is what covering a differently-shaped output requires.
* **Retiming is explicit**: source window, speed and duration satisfy `Duration = (Out − In) ÷ Speed`, and a per-clip `RetimeMode` says which one the app derives (`RetimeSolver`, pure and tested). "Still" is a named mode rather than a hidden meaning of `speed == 0`.
* **Framing is normalised and directly editable**: marks are zoom + centre in source-frame terms (`Framing`), and the Edit canvas draws them as draggable camera rectangles over the whole frame (`FramingRects`). Both are pure and unit-tested.
* **Regression suite**: `Tests/` (xunit) plus `Tests/run-ui-smoke.ps1` for UI Automation checks against the running app.

### 🎬 Timeline & Track Behavior Unification (pre-C2)
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
2. **Track Behaviour Is A Flag, Not A Role**: sequential-and-gapless versus freely-placed is `TimelineTrack.IsGapless`, settable per track, not a property of which track it is. On a gapless track, start times are derived from clip order by `Normalize()`; on a free one the user places them. Either way, position is read as `StartTime` and no consumer needs to know which kind of track it is looking at.
3. **One Active Clip Per Track**: Clips on a track never overlap in time, so at most one clip is active at any story time. This is what lets track *i* own exactly one player and one render surface. Simultaneity is expressed by using another track, never by stacking within one. Survives C2 unchanged.
4. **Z-Order Is Track Index**: Compositing order is determined solely by track number — Track 4 over Track 3 over Track 2 over Track 1. There is no per-clip z-override and none should be added. In the preview this is enforced by XAML declaration order in `DirectorPlayerControl.xaml` (track 0's `BaseBox` first, then the overlay units bottom-to-top); keep that order when touching that file. In the export, track 0 is the base clip list and each higher track becomes a `MediaOverlayLayer` added in track order.
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
* **Problem**: During rapid timeline scrubbing, upper tracks display a static frame rather than seeking live to the scrubbed timestamp (only track 0 seeks live during scrub).
* **Target Solution**: Extend the live scrubbing seek loop to evaluate and seek active overlay media players per-frame during scrub gestures.

### B. Edge Trimming On The Timeline (Medium Priority)
* **Problem**: No track supports dragging a clip's edges on the timeline — there is no edge grab in `HitClip`/`PointerMoved`. Trimming requires entering Edit mode or typing numbers, and **Ripple** vs **Roll** semantics are undefined and unimplemented.
* **Target Solution**: Define explicit rules for Ripple Trimming (extending an edge pushes downstream clips on that track) versus Roll Trimming (extending an edge consumes gap space or overwrites), then implement edge grabs uniformly across all tracks.

### Previously listed, now superseded
* *Live PiP Video Reshaping* — solved by the §7A still-proxy path (see §4).
* *Deep Data Schema Unification* — done in plan phase C2.
* *Trimming parity across tracks* — the behavioural half is done (every track follows the same rules); the missing part is on-timeline edge grabs, recorded as B above.

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

Transitions are **additive**: on a gapless track a clip occupies `OpDuration + TransitionDuration`
of story time, which is what `Normalize()` walks when deriving start times. Per-clip speed divides
into story time — a clip at 2× contributes half as much story time as video watched. Total length is
the max end across **all** tracks.

### 7E/F. The Timeline Is One Shared Time Axis
Every lane is drawn on a single px-per-second scale with `x = 0` at time 0, so vertical alignment
across lanes is literal. Track headers sit in a fixed-width gutter **outside** the horizontal
`ScrollViewer`, so they stay put while the lanes scroll. The playhead is drawn full-height across
every lane. All track-to-y mapping goes through `TimelineGeometry` — pure, WinUI-free and
unit-tested, because a lane-mapping error is invisible in review and obvious only on screen.

The pointer model follows standard NLE convention: the ruler (or Ctrl+drag anywhere) scrubs, lanes
drag clips, a press in a lane selects, and empty lane space scrubs. ⚠ The scale is *proportional*
(`_timelinePxPerSec = width / extent`) rather than a true px-per-second with cursor-anchored zoom —
see plan phase C1's optional note.

### 7G. Static Composite Seek
Scrubbing shows the composite at story-time *t* **without playing**: track 0's frame (main player
seeked to the right clip and offset, marks applied at the corresponding progress) plus whichever
upper-track clips are active then. A drag on the timeline means "navigate the whole thing", so it drops out
of Edit into Arrange first. Scrubbing while paused means "take over" — it ends the playback loop and
settles into Arrange at the scrubbed point, rather than leaving a suspended loop alive.
