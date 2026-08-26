# VideoDirector — Architecture, UI/UX Model & Roadmap

**Purpose**: This authoritative document serves as the single source of truth for the VideoDirector Non-Linear Editor (NLE) architecture. Designed for human developers and AI assistants, it outlines core interaction laws, system topology, historical architectural achievements, strict invariants, and strategic future work.

---

## 1. Product Overview & Core Mechanics

VideoDirector is a multi-track video sequencer and compositor built in **WinUI 3 / Windows App SDK** (mouse + keyboard primary; touch-compatible). It composes multiple video and image assets into a unified time-synchronized output.

### 6-Track Topology The sequencer is bounded to 6 tracks, of which a new project shows 3; Add and Remove act on the top of the stack, because a track index IS its identity and removing from the middle would renumber everything above it. They are **equal**: every track is a `TimelineTrack` holding an `ObservableCollection<CinematicOperation>`, and the engine addresses them generically. There is no spine and no overlay role — the earlier A-Roll / B-Roll split was removed with the unified track engine.

* **Compositing is by Z-order alone, and A HIGHER TRACK NUMBER WINS**: T1 renders at the bottom and T6 on top, each track drawn over the ones below it.

  Two things depend on this and will be silently wrong if it is ever inverted. Track visuals are inserted into their host in slot order, so later children draw over earlier ones. And `TryGetVisibleBorderRegion` scans **upward** from a slot — `for (int j = slot + 1; ...)` — because only a HIGHER track can cover this one; that is what decides whether a clip's border and frame are hidden, trimmed to a strip, or left whole.
* **Clips never overlap within a track**, so at most one clip per track is active at any story time. `ResolveOverlaps()` shifts siblings when a clip is moved or reordered.
* **Transitions are per-clip fades, not inter-clip effects.** `TransitionStyle` selects a fade in, a fade out, or both; the engine multiplies the ramp into the clip’s own opacity each frame. Setting a transition length moves `OpDuration` by the same delta, so the fade is extra time rather than material lost. A true crossfade is deliberately absent: it needs the outgoing and incoming clips on screen simultaneously, which one slot holding one clip cannot express.

* **The canvas is the composition space.**
 Every geometry read measures the canvas, not the player pane. Its size is the app window as it was when the project began (mode `Auto`), then held and persisted with the project; the pane only decides the scale it is drawn at. This is what stops the track dock, a window resize or full screen from changing an arrangement.

* **Gaps are allowed on every track.** `TimelineTrack.IsGapless` can force clips to sit perfectly adjacent — the old spine behaviour — but it is a per-track option and every track is currently created with it off.
* **Placement is normalised** (`PlacementCenterX/Y`, `PlacementWidth/Height` in 0..1 space) plus opacity, so any track can act as a Picture-in-Picture layer. Tracks differ only in where new clips *default* to sitting: track 1 defaults to full-screen centre; T2–T5 take the four corners; T6 sits in the centre on top.
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

### C. EDIT Mode (Red Badge) — NORMATIVE

**Purpose**: author one clip's Ken Burns move — a framing at Start, an optional Mid, and an End, interpolated over the clip's duration by the curve profile.

Entered by single-clicking a timeline clip or double-clicking a canvas PiP; exited via the mode badge on the playbar, the Done button, or Esc.

This section is **normative**. The rules below are requirements, not descriptions of current behaviour, and every one of them has been violated by a change that seemed locally reasonable. §5.9 states them as invariants; the failure modes are recorded in §4 so they are not rediscovered.

#### The four objects

| Object | What it is | May it change? |
|---|---|---|
| **Visible clip size window** | The output frame. Geometry, derived from the canvas and the clip's placement — placement is bypassed in Edit, so it is the video fit. | Only when the user navigates. Never as a result of zoom, a keyframe, or pressing anything. |
| **Source picture** | The clip's frame, positioned and scaled by the live framing. | Position and scale, yes. It is never cropped, hidden, dimmed or partially drawn. |
| **Marks** | Start / Mid / End, each a scale plus an offset. Data. | Only by a Set button or by dragging a rectangle. |
| **Live framing** | What is rendered right now. | Follows navigation while authoring, and the interpolation at the playhead during preview. |

#### The rules

> **These were rewritten on 2026-08-25 after the first version was found to contradict itself and the code.**
> The original nine were written *during* the argument that produced them, so they encoded positions from
> different points in the session and were never reconciled once the design settled. Rule 9 said "No clamp"
> while the shipped code clamps to contact. Rule 8 said only two things write a framing while four do. Rule 5
> said the wheel writes nothing while rule 4 said it drives the framing transform. Rules 6 and 7 answered
> "does the picture move when I press Set?" in opposite directions.
>
> That is worse than having no specification. A reviewer reading the old rule 6 filed a correct defect with a
> remedy that would have removed the Set-jumps-to-Mid behaviour, citing chapter and verse. **A contradictory
> spec does not produce vague reviews, it produces confident wrong fixes.** What follows is written against
> the code as it actually stands.

**THE ONE DISTINCTION EVERYTHING ELSE RESTS ON.** There is a difference between the LIVE FRAMING - what is on
screen right now - and a MARK, which is stored on the clip and survives the session. Wheel and drag change the
live framing continuously and store nothing. Set copies the live framing into a mark. Missing this distinction
is what made the earlier rules contradict each other, and what cost the most time.

**1. The picture is always whole.** It is never clipped to the output frame in Edit. Pan far enough and it sits mostly outside the frame, and stays entirely visible while it does. A frame edge that eats the picture breaks the core requirement; this was called *the invisible barrier*.

**2. The zoom rectangles are overlay, nothing more.** They indicate where each mark sits. Drawing one, selecting one, or looking at one changes nothing about the clip.

**3. Full opacity outside the frame — no ghosting.** A clip has THREE frames across its lifetime and none outranks the others. Dimming what falls outside one lets a single keyframe dictate how the picture reads for all three. The same objection rules out filling the frame with a colour to make its edge legible: it degrades the picture to mark a boundary.

**4. Exactly one multiplier.** What is rendered is `source x liveFraming`. The wheel drives that same transform, inside a window that does not change size. A canvas zoom layered on top is a SECOND multiplier and is forbidden: it makes Set either capture nothing or crop to twice what was on screen, depending on which value Set reads. Both were shipped and both were wrong.

**5. Wheel and drag are navigation in the sense that matters: they STORE nothing.** They move the live framing, which is how you choose a framing at all — but nothing survives the gesture. Turning a wheel can never alter a saved mark. *(The old wording said they "write nothing", which contradicted rule 4. They write the live framing and store no mark.)*

**6. Set copies the live framing into a mark. It does not reframe the picture.** Because the mark equals what was already rendered, the framing itself is unchanged by the press. `BeginEdit` must NOT be called from a Set handler: it re-enters Edit, re-runs the placement bypass, and snaps a 30% overlay's window to full frame — which is rule 10.

**7. Set then SELECTS the mark it wrote, and selection switches the displayed state.** So pressing Mid does jump the picture to the Mid state, and End to the End state. This is essential, not incidental. It does not contradict rule 6: the framing is not being altered, the playhead is moving to where that mark applies. `SeekForMark` is the mechanism and belongs in the Set handlers. *(The old rules 6 and 7 answered this both ways; removing the seek to satisfy rule 6 was tried and rejected.)*

**8. Exactly four things move the live framing:** the wheel, dragging the picture, dragging a rectangle's tab, and dragging its corner handles. Exactly two things write a MARK: a Set button, and a rectangle drag. *(The old rule said two writers total and omitted the wheel and the picture drag, which is simply false.)*

**9. The boundary is CONTACT, and it is a hard stop.** A framing may sit past the edge of the source — that is how a push-in from off-frame or an end on a corner is authored — and travel stops when the picture's trailing edge meets the frame's far edge, `(content x scale + box) / 2`. It may not travel beyond that, because a picture with no contact has nothing on screen pointing at it.

  Do NOT use coverage, `(content x scale - box) / 2`, which is `ClipGeometry.Allowance`. It is exactly 0 at scale 1, so the picture cannot be moved at all, and it forbids a framing that starts outside the picture. That was shipped, and it read as the drag being dead. *(The old rule 9 said "No clamp" outright. That was the position before boundaries were adopted and it was never updated.)*

**10. The visible clip window changes size only for navigation.** Not for a keyframe, not for pressing Set, not for a mode transition. The placement box is bypassed in Edit so the window is the video fit; anything that re-runs that bypass mid-session resizes the window and is a defect.

#### The mechanism — what a rebuild actually needs

The rules above say what Edit mode must do. They are not sufficient to rebuild it; this is.

**Geometry chain** (`Models/ClipGeometry.cs`, pure and unit-tested):

* `Fit(aspect, vpW, vpH)` — the source contained in the canvas, preserving aspect. This is the **scale-1 reference**: a mark of 1.0 means exactly this.
* `Box(...)` — the output frame. In Edit, placement is **bypassed** (`editMode ? 1.0 : placeW`), so the box IS the fit. In Arrange it is the PiP rectangle, and the video crop-fills it.
* `Content(boxW, boxH, aspect)` — the smallest surface at the source aspect that fully covers the box. Always `>=` the box; the surplus is what a pan consumes.
* `Allowance(contentW, contentH, boxW, boxH, scale)` = `((contentW*scale - boxW)/2, (contentH*scale - boxH)/2)` — how far a framing may travel before the box stops being covered. Used by the Ken Burns replay to bound a mark. **It is NOT the interactive limit** — that is contact, rule 9. Coverage is 0 at scale 1 and was shipped as the interactive clamp by mistake, which read as the drag being dead.

**Element tree and where the transform lives.** The grid is box-sized and carries `Margin = (left, top)`. Inside it the surfaces sit in an unconstrained `Canvas`, sized to `contentW x contentH`, offset by `(-padX, -padY)` where `pad = (content - box)/2`, so the oversized surface is centred on the box by hand. `RenderTransformOrigin` is `0.5,0.5` and the `RenderTransform` is the framing. **The Canvas is load-bearing**: a sizing parent layout-clips an overflowing child BEFORE `RenderTransform` runs, which silently discards the surplus a pan needs (§5.5). Debug asserts guard it.

**Marks are normalised.** `SpatialMark` is `(Scale, X, Y)` with `X`/`Y` as fractions of the fit, so reopening a project at a different window size reproduces the framing instead of shifting it. `CaptureMark` divides by `fitW`/`fitH`; `EnsureMarksNormalized` converts legacy clips stored in raw pane pixels, on first draw rather than at load, because the pane size is not trustworthy until then. `MidMark` is nullable — absent means a two-point Start->End move.

**Rectangles are drawn at `Sc/St`** — the live framing scale over the mark's own — plus the live translation:

```
left  = (-boxW/2 - txt) * (Sc/St) + W/2 + txc + (vpW - W)/2
width = boxW * (Sc/St)
```

so a mark at the live framing draws exactly on the box edge, and one at a wider framing draws outside it. `CaptureMark` is the inverse of this and must stay so: if the two disagree, a rectangle placed visibly inside the picture writes a mark outside it.

**Aspect has one source of truth.** `AspectOf` prefers the clip's persisted `SourceAspect` and falls back to the live `_overlayAspect`; it returns 0 for genuinely unknown and callers must hold off rather than assume 16:9. Three separate fallbacks once disagreed, and on a 2.39:1 source the rects were drawn 34% out.

**Preview.** The live framing follows `ApplyMarksAtProgress` at the playhead. `SeekForMark` moves the playhead to a mark's time — this is what implements rule 7. `BeginEdit` must NOT be called from a Set handler: it re-enters edit mode and re-runs the placement bypass, which resizes the window and violates the window invariant.

**Lifecycle.** `EnterEditMode` isolates the clip into slot 0 and releases every other slot, seeds the transform from the mark being edited, and applies the box with `editMode: true`. `editMode` is derived from engine state — `_mode == EditorMode.Edit && ReferenceEquals(overlay, _editClip)` — never passed as a literal, because the per-frame path once hardcoded `false` and overwrote the box every frame.

**Input map in Edit.** Wheel magnifies the framing inside the fixed window. Left-drag on the picture pans the framing. A rectangle's tab moves that mark, its corner handles resize it, and both mark their events handled so they never reach the input layer. Right-click the Mid button clears it.

**Hit-testing.** `InputLayer` owns all pointer input; every render surface is `IsHitTestVisible = false`. **A Grid with any Background becomes hit-testable** and will swallow events meant for `InputLayer` — this killed drag and wheel in both modes when a background was put on `CanvasHost`. The pasteboard grey therefore lives on the control and `RootLayer` only.

**Diagnosis.** The telemetry HUD (`VideoPlaybackEngine.Telemetry.cs`) reports box, surface, motion, the source range visible, black on each edge, the grid's laid-out size against the wanted box, and the canvas host/transform. When geometry is in doubt, read it rather than infer from a screenshot — see §4.

---

## 3. UI Layout & Control Topography

* **Timeline Toolbar (`TrackDock` Header)**: A dedicated command bar directly above the timeline separating global actions from inspector panels.
  * *Left Zone*: History (Undo/Redo) and Timeline Mode Tools (Snapping toggle, Ripple edit, Waveform display).
  * *Right Zone*: View zoom/fit controls, Project Operations (Save, Load, Clear), and MP4 Export.
* **Proportional Timeline Dashboard (Bottom Dock)**: Hosts the time ruler, playhead, and up to six coloured track lanes (three on a new project). Track labels ("Track 1", "Track 2", etc.) act as interactive load buttons that open file pickers (`LoadIntoTrack`) targeting that specific lane. Supports bidirectional cross-track drag-and-drop, ghost-follow dragging on Track 1, runtime magnetic snapping (8px threshold), and right-click context flyouts (`Duplicate`, `Remove`, `Split at playhead`, `Snapshot still`), and visual drag-to-loop regions on the time ruler.
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

### 🧭 One Boundary Rule, In Both Modes (`0.9.0`)

Edit and Arrange now obey the same law, stated once in each place: **a thing may travel until its edge meets the boundary, and no further.** The picture may leave its frame, a clip may leave the canvas, and neither can be lost.

That replaced four different limits. Edit clamped the framing to COVERAGE — `(content x scale - box) / 2`, which is exactly 0 at scale 1, so the picture could not be moved at all and a push-in from off-frame was unauthorable. Arrange had three: the grid snap clamped the centre to the canvas, a size preset kept the whole clip inside it, and a drag clamped nothing. Underneath all of that, `PlacementCenterX/Y` clamped to `[0, 1]` in the MODEL, which held half of every clip on canvas no matter what the engine computed.

Contact — `(content x scale + box) / 2`, or `-w/2 .. 1 + w/2` for placement — permits everything an author needs and forbids only losing something. Verified against the running app rather than asserted: in Edit, at zoom 2.81 the limit is 5243 and the pan stopped at exactly -5243; in Arrange, all four clips landed on their computed corner limits to four decimals.

The two supporting changes were dropping the clips that made the boundary necessary in the first place. `grid.Clip` no longer crops in Edit, so the picture is whole while you frame it, and `CanvasHost` no longer layout-clips its children, so a clip past the canvas draws whole. Both were sizing parents cutting content before a transform could use it — the same trap invariant §5.5 already recorded one level down.

The recurring cause across all of it: **two owners with different rules for one value, and the one that knows least wins because it runs last.**

### 🖼️ Chrome That Can Be Found (`0.9.0`)

Clip frames moved into a host above every picture, at `Canvas.ZIndex` 20. A frame used to live inside its own clip's grid, so its z-layer was the clip's and any higher track drew over it — and `Canvas.ZIndex` only sorts SIBLINGS, so no z-order tweak inside the clip could ever have fixed it. They are driven from the same call site as the borders, with the same box values and the same visible region, so geometry and visibility are set together by one owner.

Two occlusion faults surfaced there and were fixed for borders as well: a small opaque clip in the MIDDLE of a larger one covered none of its border yet trimmed it to one edge strip, and a clip covered outright kept its chrome because the containment case fell out of arithmetic that a later gate skipped.

The canvas is now a black surface against a lighter pasteboard, and its outline never renders thinner than a pixel — it lives inside the scaled canvas, so a plain 1px stroke was drawn at 0.25px when zoomed out to 25%, fading precisely when the boundary mattered most.

### 🪟 Edit Mode Framing — Five Regressions And What Measured Them

A long session of changes to the Edit-mode wheel, `CaptureMark` and the layout produced five distinct regressions. Each is recorded with the evidence that identified it, because in every case reasoning from screenshots produced a confident wrong answer and one number settled it.

**The per-frame layout hardcoded `editMode: false`.** Entering Edit called `ApplyOverlayBox(0, overlay, true)` and set the full-fit box; the motion path then called `ApplyOverlayBox(slot, overlay, false)` every frame and put the PiP placement box straight back. A 30% overlay was drawn into a 30% window at any zoom while every calculation used the full box. Found by adding a HUD line reporting the grid's `ActualWidth` against the box: *laid out grid 2752 x 1146, want 2752 x 1147* — correct — while the visible picture stayed the same size at `Z:0.96` and `Z:10.00`. **A window that does not change with zoom is a box, not a clip artifact.** `grid.Clip`, the standing suspect, was never involved. `editMode` is now derived from engine state, never passed as a literal.

**A canvas zoom was added as a second multiplier.** The wheel was changed from driving `ActiveTransform` to zooming the canvas, so nothing wrote the transform `CaptureMark` reads and every Set stored a full-frame mark. Making Set read the view instead produced the opposite failure: `motion zoom 2.14x` while the view was also at 2.14, cropping to twice what was on screen. See §2.C rule 4 — there is one multiplier.

**A framing clamp made the edges unreachable.** Added to stop black appearing when panning; black past the edge is an authorial choice, and the clamp also silently killed the left-drag gesture entirely, because at scale 1.0 the allowance is `(box x 1.0 - box) / 2 = 0` exactly.

**Resetting the canvas view inside a Set handler** kept the picture steady but shrank the on-screen window by the zoom factor, violating the one thing the window must never do.

**A `Background` on `CanvasHost`** made that Grid hit-testable, and it swallowed the pointer events `InputLayer` needs — killing drag and wheel in both Arrange and Edit. The pasteboard grey lives on the control and `RootLayer` only.

The lesson is procedural rather than technical: the telemetry HUD resolved in one screenshot what hours of inference from screenshots did not. When geometry is in question, add the number and read it.

### 🧹 Housekeeping That Was Load-Bearing
* **One `bin`, Not Three.** `bin\x64\Debug` had quietly grown into a second full 170MB output tree, with 95MB of matching intermediates. The SDK defaults to `bin\$(Configuration)\` only while the platform is `AnyCPU`; the `.slnx` maps every solution platform to x64, so every IDE build went somewhere the publish pipeline never looks. That is the "tested a stale binary" failure waiting to happen, and this project has already lost an afternoon to one. `OutputPath` is now pinned so no platform can fork it, and the README table that documented the wrong two folders was corrected. `obj` is left to split by platform: it is disposable, and separate intermediates are the honest thing for a project that lists three.
* **Dropping A Clip No Longer Edits The One Before It.** Adding to a gapless Track 1 gave the previous clip a one-second `Crossfade`. Both halves were wrong: Crossfade is not implemented, so it rendered as nothing; and once transitions became additive in 0.7.0 the assignment silently grew that clip by a second on every drop. A transition is a choice made in the inspector, not something a drop makes on your behalf.
* **Documentation Reconciled With The Code.** The README claimed ripple editing (no such code), crossfade (disabled in the UI as unavailable), three PiP layers (five), and dual-player crossfade playback (one player per track into a fixed canvas). Claims are now checked against the source rather than inherited.

### 🖱️ Canvas Input, Made Predictable
* **One Gesture, One Meaning.** The wheel used to be captured by a selected framing rectangle in Edit, so the same gesture zoomed either the picture or the keyframe depending on a selection you could forget you had made — and it did the less useful one, since you generally select a keyframe in order to look at it more closely. The wheel now always zooms the picture; marks resize by dragging their corners. The event, its subscription and its handler were removed rather than left unreachable: the corner-drag path writes Scale, X and Y together out of the geometry and never depended on the wheel handler’s ratio adjustment.
* **The Resize Edge Is Catchable.** The Arrange grab band was 20px measured INSIDE the box only, so aiming at a 2px dashed line put roughly half of every attempt outside the box, where the hit test returned nothing at all. It is now 24px inward plus a 10px outset, which centres the drawn line in the grab zone, and the line is drawn heavier because it IS the resize surface. Corner nodes were built and then removed: the overlay grid is clipped to exactly the box, so a node centred on a corner lost its outer half and rendered as a stub — and once that was fixed they were still wrong, because the whole edge is grabbable and a corner node implies otherwise. The comment in `SetOverlayRender` had said exactly that before any of it was written.
* **The Framing Stops At Its Own Edge — SUPERSEDED, see "One Boundary Rule" above.** This entry described clipping the picture to the video fit and clamping the drag against `ClipGeometry.Allowance`. Both were replaced: Edit no longer clips the picture at all, and the limit is contact rather than coverage. Kept only so the older wording is not mistaken for current design — coverage is 0 at scale 1 and makes the drag dead.

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
2. **Uniform Track Semantics**: All six tracks behave identically — time-bounded layers composited by Z-order, using `ResolveOverlaps()` to keep clips on a row from colliding. Nothing may reintroduce a privileged track: sequential, gapless behaviour is the per-track `IsGapless` option, not a role.
3. **Modal Separation of Concern**: Editing handles, trim bars, and PiP bounding boxes must never leak into screening (PLAYBACK mode) or macro-structuring (ARRANGE mode).
4. **GPU Surface Preservation**: Never directly manipulate live `MediaPlayerElement` XAML properties per-frame in ways that corrupt DirectX swapchains; use dedicated transforms and delta-checked placement boxes.
5. **Render Surfaces Live in an Unconstrained Parent**: The `MediaPlayerElement` and still `Image` of each track sit inside `TrackSurfaces{n}`, a `Canvas`, and are sized to the whole frame rather than to the visible box. This is not tidiness — it is load-bearing. WinUI issues a **layout clip** to any child that overflows its parent, and `RenderTransform` is applied *after* that clip, so a frame-sized surface inside a box-sized parent is cropped to the box **before** the Ken Burns pan can move into the surplus. Cropping must therefore happen at render time (`grid.Clip`, a mask) and never through a sizing parent (a constraint). Moving these surfaces back under a sized element, or restoring `HorizontalAlignment="Center"` in place of the explicit `Canvas.Left/Top`, silently reintroduces the black-edge defect. `ApplyOverlayBox` carries a `Debug.Assert` on the parent type for exactly this reason.
6. **Export Cannot Do Per-Frame Work, And This Is Measured**: `MediaComposition` offers exactly one hook for per-frame rendering — a video effect on the clip — and it does not work here. A custom managed `IBasicVideoEffect` fails to activate at all (`0x80040154`, a managed type is not WinRT-activatable from an unpackaged process), and the SYSTEM-provided `VideoTransformEffectDefinition` breaks the render (`0xC00DA7FC`) even when constructed with nothing set. The baseline render succeeds, which is what makes this attributable rather than a guess. So Ken Burns, fades, speed, borders and crop-fill are blocked on the API, not on effort: obtaining them means a second renderer, not more lines in an exporter. **Do not re-open this by adding an effect and hoping.** Visual export is `ScreenRecorder` photographing the compositor; `VideoExporter` was the measured-dead MediaComposition path and has been removed so it cannot be wired to the Export button by accident.
7. **Commit Integrity**: Every architectural step must end in a green build and a clean git commit. Small, incremental steps ensure we are never more than one revert away from safety.
8. **Mode Rules Are Pure Functions, Defined Once (`Models/ChromeRules.cs`)**: What a mode means — what is on screen, what is reachable — is arithmetic over seven booleans (cinematic, playing, edit, controls visible, dock open, inspector open, has selection), and it lives in one WinUI-free static class that `DirectorViewModel` delegates to. This is load-bearing history, not tidiness: cinematic mode was tested in five separate places, so each fix caught one caller and left the rest — arming it took the window full screen, disabled canvas zoom and pan, hid the inspector in Edit, and collapsed the track dock, none of which it should ever have done. **Cinematic changes exactly one thing: `IsPerforming = cinematic && playing`.** Never test the cinematic flag alone, and never inline one of these expressions at a call site — that is precisely how the rule forks again.

9. **Edit Mode Is Specified In §2.C, And The Spec Wins**: The picture is always whole, the rectangles are overlay only, there is exactly ONE multiplier in the render path, Set records what is rendered rather than changing it, and the visible clip size window moves for navigation and nothing else. Every one of those has been broken by a change that looked locally sensible — clipping the surface to the frame, adding a canvas zoom on top of the framing transform, resetting the view inside a Set handler, clamping the pan to COVERAGE rather than contact, and hardcoding `editMode: false` in the per-frame layout. Before touching the wheel, `CaptureMark`, `ApplyOverlayBox`, or `grid.Clip`, read §2.C and check the change against all ten rules. A fix that satisfies nine of them is a regression. The rules were rewritten on 2026-08-25 because the first version contradicted itself; if a rule ever disagrees with the code again, one of the two is a defect and the disagreement is the finding.

---

## 6. Active Roadmap & Technical Debt (Owed Work)

These items represent deferred technical debt and strategic improvements to be tackled in priority order:

### A. Live PiP Video Reshaping — **DONE** Resizing a live video surface on the canvas no longer corrupts the swapchain. Confirmed in use rather than by inspection; the surviving safeguards are §5.4 (never manipulate `MediaPlayerElement` properties per-frame) and §5.5 (surfaces live in an unconstrained `Canvas`).

### B. Scrubbing Seeks on Upper Tracks — **DONE** Every active track seeks live during a scrub, not just Track 1.

### C. Trimming Edge Mechanics — **SETTLED, and the gap closed** The rule was never missing, only unwritten and unevenly applied: `IsGapless` decides it. A gapless track **ripples** (change a clip’s length and the ones after it move, because a gapless track is defined by having no gaps); a free track allows gaps and clips are only ever pushed forward to stop an overlap, never pulled back.

The bug was that `ResolveOverlaps()` ran after a drag and after add/remove but **not after a trim**, which changes `OpDuration` just as surely (`OpDuration == (VideoEnd - VideoStart) / Speed`). Shortening a clip therefore opened a hole in a track that is not supposed to have any, and it stayed until some later edit happened to re-lay the track. The ripple now hangs off the clip-property change in `DirectorViewModel`, not off the trim handler, so every route to a length change is covered at once — the scrubber, a duration typed into the inspector, a speed change, adding a fade — with a re-entrancy guard, since `ResolveOverlaps` writes `StartTime` and that comes straight back.

**Roll trimming** — dragging the shared boundary between two adjacent clips so one grows as the other shrinks — is a separate gesture that does not exist here, and is not owed until someone misses it.

### E. Arrange Travel Stopped At Half A Clip — **FIXED, and the cause was a fourth clamp**

`ClipGeometry.ContactCentre` is the anchor rule: the centre runs `-w/2 .. 1 + w/2`, so a clip may rest its edge on the canvas boundary and go no further. It was applied at all three placement writers and the app still refused to let a clip past halfway.

**The cause was in the MODEL, not the engine.** `PlacementCenterX` and `PlacementCenterY` clamped to `[0, 1]` in their own setters. The centre is a fraction of the canvas, so pinning it to `1.0` holds the centre on the canvas edge — exactly half the clip on, every side, however far you drag. `ContactCentre` computed the right value and the setter discarded it.

Two reported symptoms, one clamp: dragging to an edge stopped at 50%, and pushing a CORNER stopped at "about 75%" — because 50% off the right and 50% off the top leave only the bottom-left quadrant on canvas, which is 25% on and 75% off. Corner drags also disguised it further, since the width lands and the centre is snapped back, so the clip appears to slide rather than resize.

**The limit cannot live in the model.** Contact depends on how wide the clip is on the canvas, which needs the fit and the canvas size, and the model knows neither. The setters now guard only against non-finite values — storage and sanity, not policy — and `ContactCentre` owns the rule. `ApplyOverlayBox` also applies it once per clip, because placement is deserialised straight into those properties and so bypasses every writer; without that, an old or hand-edited project could open with a clip out of reach.

The general lesson is the one that cost the most time this week: **two owners with different rules for one value, and the one that knows least wins because it runs last.** Before adding a guard, check whether something downstream already has an opinion.

### D. Deep Data Schema Unification — **DONE** Track 1 (`TimelineNodes`) and tracks 2–4 (`OverlayTracks`) used to be distinct collection wrappers. They are now one `ObservableCollection<TimelineTrack>`, and `OverlayTrack.cs` became `TimelineTrack.cs`. No `TrackRole` discriminator was needed in the end: the roles were dropped rather than modelled, which is why tracks are interchangeable above.

The old `TimelineNodes` and `OverlayTracks` names survive only in the project-file deserialiser, so that projects saved before the change still load.



