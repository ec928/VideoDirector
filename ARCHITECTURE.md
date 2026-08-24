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

### 🗂️ The Two Big Files Split Into Partials
* **`VideoPlaybackEngine` → 8 files, `VideoDirectorControl` → 10.** Both had passed 2,900 lines, which is past the point where you can hold a file in your head or find the second place something is done. Split by what the code is *for*, not by line count: the engine into transport, telemetry, marks, composite, layout, motion, edit mode and commands; the control into shell, timeline drawing, timeline input, clip commands, snapping, playback, about, project, chrome and commands.
* **Purely mechanical, and checked as such.** Nothing was renamed, reordered or rewritten — every member moved verbatim into a `partial`. Verified three ways rather than trusted: the splitter asserts a byte-exact round trip of the class body before writing anything; a separate pass compares the multiset of non-blank member lines before and against all the new files, so a line lost or duplicated shows up as a diff; and the app was launched, not just compiled. All field initialisers are constants or `new()` with no cross-field dependency, so the one hazard partials do carry — initialiser order becoming file-order dependent — does not apply.
* **Comments Reunited With Their Code**: a pile of five comment blocks had drifted 100–280 lines above the methods they described, stranded by earlier edits, so they documented whatever happened to follow them. `ApplyCanvasSize`, `SetTransportDocked`, `UpdateChromeInset` and `AddTrack_Click` have their own notes back.

### 🎬 Export: What It Can Carry, Stated Before The Wait
* **The Question Was Settled By Measurement.** A three-way spike rendered the same five seconds with no effects, with the system transform effect, and with a custom managed one. Baseline succeeded at 4.8MB; both effect paths failed, and a no-op transform effect was enough to kill the render. That is the whole reason motion, fades and speed are absent — not an unfinished TODO.
* **Output Follows The Canvas.** The render was pinned at 1920×1080 while a project canvas can be 4K, 2.39:1, 9:16 or anything typed, so a vertical project was squeezed into landscape by a renderer told the wrong shape. The profile now carries the real frame size, and overlay positions resolve into it.

  **CORRECTION — this entry previously claimed the exporter was "verified end to end". That was false.** The harness rendered a project written by hand to match the exporter's own assumptions: one mp4, one overlay track, no gaps, even dimensions. It was never once run against the saved projects in `Tests/`, and **every one of those fails**, as they always had. Two independent causes, both measured afterwards: a source with an odd width or height (one sample is 1918×804) is refused as an overlay layer by the Windows compositor; and `composition.Clips` is a sequence with no per-clip start time, so gaps on track 1 are deleted and overlays keep absolute delays, desynchronising everything after the first gap.

  The lesson is the one this project keeps relearning, one level up from "a green build proves nothing": **a passing test against a fixture you wrote yourself proves nothing either.** Export verification must run the projects in `Tests/`, which is what anyone actually loads.
* **The Warning Is Computed, Not Boilerplate.** `WhatIsNotBaked()` walks the actual clips and names only what THIS project loses, shown before the file picker. A project with no motion and no fades gets no warning at all. It also names the alternative that loses nothing: cinematic playback is the finished piece, so recording it captures what a render cannot.

### 🧹 Housekeeping That Was Load-Bearing
* **One `bin`, Not Three.** `bin\x64\Debug` had quietly grown into a second full 170MB output tree, with 95MB of matching intermediates. The SDK defaults to `bin\$(Configuration)\` only while the platform is `AnyCPU`; the `.slnx` maps every solution platform to x64, so every IDE build went somewhere the publish pipeline never looks. That is the "tested a stale binary" failure waiting to happen, and this project has already lost an afternoon to one. `OutputPath` is now pinned so no platform can fork it, and the README table that documented the wrong two folders was corrected. `obj` is left to split by platform: it is disposable, and separate intermediates are the honest thing for a project that lists three.
* **Dropping A Clip No Longer Edits The One Before It.** Adding to a gapless Track 1 gave the previous clip a one-second `Crossfade`. Both halves were wrong: Crossfade is not implemented, so it rendered as nothing; and once transitions became additive in 0.7.0 the assignment silently grew that clip by a second on every drop. A transition is a choice made in the inspector, not something a drop makes on your behalf.
* **Documentation Reconciled With The Code.** The README claimed ripple editing (no such code), crossfade (disabled in the UI as unavailable), three PiP layers (five), and dual-player crossfade playback (one player per track into a fixed canvas). Claims are now checked against the source rather than inherited.

### 🎥 Export Became A Recording
* **The Renderer Already Existed.** Every attempt to make `MediaComposition` carry Ken Burns, fades or speed failed at the API (invariant 6), and it refused most real projects outright. So export stopped trying to re-derive the picture and started photographing the one the compositor already draws: `ScreenRecorder` captures the app window during a chrome-free full-screen playback. Motion, fades, speed, borders and crop-fill all survive for the same reason they look right on screen — there is no second definition of the geometry to drift.
* **Two Things That Are Not Optional.** Capture is CHANGE-driven: a window that is not redrawing produces no frames at all, so the encoder runs on its own clock and re-sends the last frame. Without that, every held shot vanishes. And Media Foundation reads an uncompressed BGRA buffer bottom-up while `GetPixelBytes` hands back top-down rows, so the frame is drawn flipped — the first recording came out mirrored.
* **Audio Is Mixed, Not Captured.** Lining up two independently clocked captures is a worse problem than mixing sound we already have. `BackgroundAudioTrack` carries delay, volume and trim WITHOUT a video overlay layer — which matters, since the overlay video path is exactly where MediaComposition refuses real projects. It will not accept a video file, so each audible clip is rendered to a small M4A first (0.4s for a twelve-second segment).

### 🔊 Sound-Only Clips
* **A Third Kind Of Clip.** Alongside video and stills there is now sound: an mp3, or any container holding audio with no picture. Detected rather than inferred from the extension, because an `.mp4` can hold audio alone. The two media APIs are strictly complementary about it — `MediaClip` refuses an audio-only file, `BackgroundAudioTrack` refuses one with video — so anything that picks the wrong one throws, which is what makes the distinction load-bearing.
* **`OverlayRender.Sound`, And Why The Other Three Would Not Do.** `Hidden` releases the slot and stops the audio. `Video` attaches a surface with no picture in it, and a `MediaPlayerElement` with nothing to draw paints BLACK — on an upper track that blots out everything beneath. Sound keeps the player running and stands down every visual: no box, no border, no frame, no transform.
* **The Detach That Silences.** `DetachOverlayVideo` pauses the player, and render mode is applied every frame, so using it for sound paused and resumed an mp3 about sixty times a second. It played, technically; it sounded like a fault. `DetachVideoSurfaceOnly` exists for this and nothing else. Drift correction is also far more reluctant for sound (750ms rather than 10): a seek in a picture is a frame you may not notice, a seek in audio is an audible click.
* **The UI Says What A Clip Is And What It Has.** Stills and audio clips carry their own symbols. *Muted by you* is red; *no sound to give* is dimmed — an image, a file with no audio track, or a frozen frame, which has an audio stream but no time passing to play it. Volume is forced to 0 and disabled where there is nothing to hear. For a sound-only clip the inspector hides framing, borders, fades and opacity, keeping only timing and volume: a control that moves and changes nothing is the same fault as a live volume slider on a silent clip, pointing the other way.

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
6. **Export Cannot Do Per-Frame Work, And This Is Measured** (`Models/VideoExporter.cs`): `MediaComposition` offers exactly one hook for per-frame rendering — a video effect on the clip — and it does not work here. A custom managed `IBasicVideoEffect` fails to activate at all (`0x80040154`, a managed type is not WinRT-activatable from an unpackaged process), and the SYSTEM-provided `VideoTransformEffectDefinition` breaks the render (`0xC00DA7FC`) even when constructed with nothing set. The baseline render succeeds, which is what makes this attributable rather than a guess. So Ken Burns, fades, speed, borders and crop-fill are blocked on the API, not on effort: obtaining them means a second renderer, not more lines in the exporter. **Do not re-open this by adding an effect and hoping.** What the exporter can and cannot carry is reported to the user before the render, from the clips actually in the project.
7. **Commit Integrity**: Every architectural step must end in a green build and a clean git commit. Small, incremental steps ensure we are never more than one revert away from safety.
8. **Mode Rules Are Pure Functions, Defined Once (`Models/ChromeRules.cs`)**: What a mode means — what is on screen, what is reachable — is arithmetic over seven booleans (cinematic, playing, edit, controls visible, dock open, inspector open, has selection), and it lives in one WinUI-free static class that `DirectorViewModel` delegates to. This is load-bearing history, not tidiness: cinematic mode was tested in five separate places, so each fix caught one caller and left the rest — arming it took the window full screen, disabled canvas zoom and pan, hid the inspector in Edit, and collapsed the track dock, none of which it should ever have done. **Cinematic changes exactly one thing: `IsPerforming = cinematic && playing`.** Never test the cinematic flag alone, and never inline one of these expressions at a call site — that is precisely how the rule forks again.

---

## 6. Active Roadmap & Technical Debt (Owed Work)

These items represent deferred technical debt and strategic improvements to be tackled in priority order:

### A. Live PiP Video Reshaping — **DONE** Resizing a live video surface on the canvas no longer corrupts the swapchain. Confirmed in use rather than by inspection; the surviving safeguards are §5.4 (never manipulate `MediaPlayerElement` properties per-frame) and §5.5 (surfaces live in an unconstrained `Canvas`).

### B. Scrubbing Seeks on Upper Tracks — **DONE** Every active track seeks live during a scrub, not just Track 1.

### C. Trimming Edge Mechanics — **SETTLED, and the gap closed** The rule was never missing, only unwritten and unevenly applied: `IsGapless` decides it. A gapless track **ripples** (change a clip’s length and the ones after it move, because a gapless track is defined by having no gaps); a free track allows gaps and clips are only ever pushed forward to stop an overlap, never pulled back.

The bug was that `ResolveOverlaps()` ran after a drag and after add/remove but **not after a trim**, which changes `OpDuration` just as surely (`OpDuration == (VideoEnd - VideoStart) / Speed`). Shortening a clip therefore opened a hole in a track that is not supposed to have any, and it stayed until some later edit happened to re-lay the track. The ripple now hangs off the clip-property change in `DirectorViewModel`, not off the trim handler, so every route to a length change is covered at once — the scrubber, a duration typed into the inspector, a speed change, adding a fade — with a re-entrancy guard, since `ResolveOverlaps` writes `StartTime` and that comes straight back.

**Roll trimming** — dragging the shared boundary between two adjacent clips so one grows as the other shrinks — is a separate gesture that does not exist here, and is not owed until someone misses it.

### D. Deep Data Schema Unification — **DONE** Track 1 (`TimelineNodes`) and tracks 2–4 (`OverlayTracks`) used to be distinct collection wrappers. They are now one `ObservableCollection<TimelineTrack>`, and `OverlayTrack.cs` became `TimelineTrack.cs`. No `TrackRole` discriminator was needed in the end: the roles were dropped rather than modelled, which is why tracks are interchangeable above.

The old `TimelineNodes` and `OverlayTracks` names survive only in the project-file deserialiser, so that projects saved before the change still load.



