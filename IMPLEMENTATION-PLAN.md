# VideoDirector — Implementation Plan

**Status**: active · **Last updated**: 2026-08-16

This document owns all *forward-looking* work. [ARCHITECTURE.md](ARCHITECTURE.md) describes the
system as it **is today**; this describes what it is becoming and why. If the two disagree about
current behaviour, ARCHITECTURE.md wins — and this file needs updating.

**For AI assistants**: nothing in this document describes code that exists yet unless its phase is
marked ✅ Done. Do not assume a phase has landed. Do not "helpfully" defend an invariant that this
plan schedules for removal — check the ⚠ markers in ARCHITECTURE.md §5 first.

---

## 1. The three problems this plan solves

**1. Track 1 is structurally different from every other track.** It is a gapless ordered list that
defines project duration and always renders full-frame; tracks 2–4 are freely-positioned layers with
placement boxes. Same clip type, two entirely different sets of rules. Every asymmetry downstream —
no placement on Track 1, no gaps, no transitions on overlays, a timeline that can't be extended
except via Track 1 — descends from this one split.

**2. Edit mode conflates "the live framing" with "the keyframe being authored".** One
`CompositeTransform` does both jobs, which is why scrubbing doesn't animate the framing, why the
three keyframe rectangles fly apart when you zoom, and why there is no way to navigate back to the
Mid or End framing once set.

**3. Constrained values are presented as independent ones.** Source in/out, speed and timeline
duration satisfy `Duration = (Out − In) ÷ Speed`, so only two can be free — but the inspector shows
five peer number boxes with invisible, asymmetric yield rules and no statement of the constraint.

---

## 2. Decisions already made

Settled 2026-08-16. Recorded so they are not re-litigated in a later session.

| # | Decision | Choice |
|---|---|---|
| 1 | Ken Burns authoring model | Keyframe **rectangles are primary** — drag/resize boxes over the whole source frame. Mouse wheel still zooms the *selected rectangle*. Direct content pan/zoom is retired. |
| 2 | Speed / duration relationship | **Explicit pin control**, with fit-to-fill as a selectable mode, plus a **still mode that suspends the constraint** so duration is authored freely (this is today's implicit `speed = 0` behaviour, made explicit). |
| 3 | Keyframe count | Keep **Start / Mid / End**. Mid gains a real, draggable time instead of being hard-wired to 50%. Not going to N keyframes. |
| 4 | Ordering | **Decouple selection from Edit mode first**, so the inspector is usable in Arrange before the framing UI is built on top of it. |
| 5 | Timeline row order | **Flip it** — Track 4 top row, Track 1 bottom row, matching compositing order and every other NLE. |
| 6 | Track header scope | **Mute · Hide · Lock · Gapless + `⋯` overflow.** Not a full mixer strip; more than the current single load button. |

### Decided during implementation

| Decision | Choice | Phase |
|---|---|---|
| Waveform display | **Removed.** It was `Math.Sin` of the clip's hash, not audio — fake data you could trim against. Real waveforms can return later as an actual feature. Reverting is one commit. | A2 |
| Snapping shortcut | **N**, because S was already Split and the magnet tooltip wrongly claimed S. | A2 |
| Adding a clip | **Selects** it rather than diving into Edit, matching the reasoning `AddOverlayAsync` already applied to overlays. | B1 |
| Leaving Edit | **Keeps** the selection. It had to clear it before, or selecting would immediately re-enter Edit. | B1 |

### Still open

Decide when the owning phase starts, not before:

- **Do Mute / Hide affect export, or preview only?** Recommendation: honoured in export — the
  alternative surprises people at render time. (Phase C3)
- **Does Lock block selection, or only mutation?** Recommendation: mutation only, so a locked clip
  can still be inspected. (Phase C3)
- **Framing migration tolerance**: identity marks convert exactly; non-identity marks from existing
  projects convert best-effort and may need a visual check. Confirm that is acceptable. (Phase D1)
- **Should clicking a spine clip during playback still jump playback to it?** Preserved as-is in B1
  to limit blast radius, but it made more sense when selecting a clip meant "work on this clip".
  Now that selection is just selection, it may be a non-sequitur.
- **Test strategy.** There is no test project. Most of this code is WinUI-coupled and awkward to
  unit test, but the pure helpers (row geometry, the retime constraint solver in D4, mark
  conversion in D1) are testable and are exactly where silent regressions would hide. Needs a
  decision on framework and how far to go before the end-to-end regression pass.

---

## 3. Phase map

```
A1  Remove Record                    ─┐
A2  Toolbar cleanup                   ├─ independent, any order, no dependencies
A3  Row geometry + flip              ─┘

B1  Selection ≠ Edit mode             ─── first behavioural change (decision 4)
      │
      ├─ C1  Timeline runway + density
      │        │
      │        └─ C2  Unify track collections  ◄── the structural block
      │                 │
      │                 └─ C3  Real track headers
      │
      ├─ C4  Drag & drop correctness        (needs A3)
      │
      ├─ D1  Normalised framing coordinates
      │        └─ D2  Framing canvas
      │                 └─ D3  Mid keyframe timing
      │
      ├─ D4  Retime block                   (independent of D1–D3)
      │
      └─ D5  Placement presets + Reset      (needs C2)
```

`D4` can be pulled forward at any point — it is independent of the framing work and addresses the
day-to-day confusion of problem 3.

Every phase must end in a green build and a clean commit (ARCHITECTURE.md §5.5).

---

## Group A — Cleanup

Pure subtraction. No new behaviour, no dependencies, safe to do at any time.

### A1 — Remove Record ✅ **Done**

**Why.** `StartRecordingMotion` captures the transform every frame into `RecordedPath`, then
`DistillRecordedPath` discards all of it and keeps **first, middle, last**, forcing
`CurveProfile.DirectorsArc` (`Models/VideoPlaybackEngine.cs:1353-1374`). The output is therefore
identical in kind to pressing Start / Mid / End — only less precise, because you are aiming a moving
camera instead of composing a still frame. It also hijacks master playback speed (forces 0.5×,
restores after), is disabled for anything but Track 1, and `RecordedPath` has no `[JsonIgnore]`, so
hundreds of dead per-frame keyframes are serialised into every project file.

**Changes.**
- Delete from `Models/VideoPlaybackEngine.cs`: `StartRecordingMotion`, `StopRecordingMotion`,
  `RecordMotion_Rendering`, `DistillRecordedPath`, `_recordStartTime`, and the
  `CompositionTarget.Rendering -= RecordMotion_Rendering` lines.
- Delete `Models/TransformKeyframe.cs` and `CinematicOperation.RecordedPath` (plus its clear in
  `Reset()`).
- Delete `DirectorViewModel.IsRecordingMotion`.
- Delete from `Views/VideoDirectorControl`: `RecordButton`, `RecordIcon`, `RecordButton_Click`,
  `_preRecordSpeed`, the `IsRecordingMotion` branch of `ViewModel_PropertyChanged`, the
  `EditModeBadge` "Live Recording" border (its only visibility binding is `IsRecordingMotion`), and
  the `IsRecordingMotion` check in `InactivityTimer_Tick`.
- Add a `SchemaVersion` field to `ProjectData` (default 1; files without it read as 1) so later
  phases have a migration hook.

**Done when.** Builds clean, projects round-trip, no Record UI, ~150 lines removed.

---

### A2 — Toolbar cleanup ✅ **Done**

**Why.** Several controls in the timeline toolbar do not do what they claim, and one does two
unrelated things.

- **Shuffle** (`Views/VideoDirectorControl.xaml:315-317`) has no handler at all.
- **Ripple** writes `IsRippleEditEnabled` (`ViewModels/DirectorViewModel.cs:286`) which **nothing
  ever reads**. The behaviour returns as a per-track property in C3.
- **Waveform** draws `Math.Sin(i * 1.3 + clip.GetHashCode())`
  (`Views/VideoDirectorControl.xaml.cs:482`) — decorative fiction you could mistakenly trim against.
- **"Fit window"** (`FitWindow_Click`, `Views/VideoDirectorControl.xaml.cs:1426-1497`) resizes the
  *application window* to the video aspect **and** resets timeline zoom as a side effect, using a
  FullScreen icon positioned beside the timeline zoom buttons. It reads as "zoom timeline to fit".
- **Clear** is an icon-only `Delete` glyph two buttons from Load. It confirms, but a project-wiping
  action should not sit in the routine file-ops run.
- Tooltips promise shortcuts that were never registered: Magnet "(S)" — S is actually Split — plus
  Ripple "(R)", Waveform "(W)", and zoom "Ctrl + −/+".

**Changes.** Remove Shuffle and the global Ripple toggle. Resolve the waveform (see open decisions).
Split "Fit window" into a real *fit timeline to project* control beside the zoom buttons, and move
window-sizing out of the timeline toolbar. Move Clear behind an overflow. Register the shortcuts the
tooltips already promise (`Ctrl +/-`, `Home`, `End`) or strip the hints. Add a zoom-level readout
and a scroll-to-playhead control.

**Done when.** Every visible control does what its tooltip says, and no tooltip names a shortcut
that isn't bound.

---

### A3 — Row geometry + flip ✅ **Done**

**Why.** Track 1 draws at the **top** of the timeline but composites at the **bottom** of the
picture; Track 4 draws at the bottom and composites on top. Dragging a clip up a lane moves it
*down* the compositing stack. This was defensible while Track 1 was "the spine, the special one" —
after C2 it is not, and it contradicts every other NLE.

The row y-math is currently duplicated across six sites, each independently computing
`RowOvY + ti * RowPitch` or its inverse: `BuildTimelineBar`, `BuildTrackLabels`, `HitClip`,
`MoveOverlayTo`, `TrackNameAt`, `OverlaySection_Drop`. Consolidating them is what makes the flip a
one-line change instead of six chances to introduce drift.

**Changes.**
- Introduce `RowYForTrack(int trackIndex)` and `TrackAtY(double y)` as the single source of row
  geometry; convert all six call sites.
- Flip the mapping: Track 4 top, Track 1 bottom.
- Close the 2px dead zone between the spine row and the overlay rows (`RowSpineY = 16`,
  `BlockH = 16`, `RowOvY = 34` — `Views/VideoDirectorControl.xaml.cs:121`), so hit rows match drawn
  rows exactly.

**Done when.** Dragging a clip up moves it up the compositing stack, and nothing outside the helper
pair knows where a row lives.

---

## Group B — Shell

### B1 — Selection ≠ Edit mode ✅ **Done**

**Why.** `SelectClip` calls `BeginEdit` whenever playback is stopped
(`Views/VideoDirectorControl.xaml.cs:958-976`). Clicking a clip to check its duration swaps the
screen to that one clip full-frame, dims the timeline, and changes what every mouse gesture means.
The inspector is bound to `IsStoryboardVisible` (Edit **or** pinned), so a clip's properties cannot
be seen without leaving the composite view. And the first click back on the timeline *only* exits
Edit and does nothing else (`Views/VideoDirectorControl.xaml.cs:548`), so every edit costs a wasted
click to escape.

This is the single largest usability problem in the app, it is cheap to fix, and it changes the
shell that the Group D framing UI will live inside — hence decision 4.

**Changes.**
- `SelectClip` sets the selection and nothing else.
- Edit becomes deliberate: double-click a timeline block, `Enter` on a selection, canvas double-tap
  (already wired via `PlayerControl.EditRequested`), or an "Edit framing" button in the inspector.
- `IsStoryboardVisible` becomes `HasSelection || pinned || editing`.
- Remove the click-only-exits-Edit rule — a click should exit Edit **and** do its normal job.
- Selected clips get brighter frame chrome on the canvas (the `OverlayRender.Still` path already
  draws a frame; vary it by selection).

**Done when.** You can click a clip, read and change its numbers, and never leave the composite view.

---

## Group C — Track model

### C1 — Timeline runway + density ⬜

**Why.** Two things block C2:

- **The project cannot be extended.** `MoveOverlayTo` clamps a clip's start to `total - dur`
  (`Views/VideoDirectorControl.xaml.cs:1000`), and the bar spans exactly `TotalStoryDuration`
  because `_timelinePxPerSec = w / total`. Today you extend a project by adding to Track 1. Once
  Track 1 stops defining duration there is no privileged way to make the project longer, and every
  track hits a hard wall at the current end.
- **An empty project renders as nothing.** `BuildTimelineBar` returns early when `total <= 0`
  (`Views/VideoDirectorControl.xaml.cs:146`) — no ruler, no lanes, no bands. Labels draw (they are
  built before the return), so a fresh project is four floating buttons beside a blank grey strip.

Density is the third issue: 16px blocks at 18px pitch cannot carry thumbnails, and the header
controls C3 adds will not fit. Lanes are separated by 2px and a very faint tint
(`TrackPalette.At(color, 0x1E)` — `Views/VideoDirectorControl.xaml.cs:272`), so they read as one
striped mass.

**Changes.**
- Add empty runway past the last clip (last end + 20%, floor ~30s) and drop the `total - dur` clamp.
- Render empty projects and empty lanes: ruler at a default span, labelled droppable lanes, visible
  bands.
- Track-height setting — compact ~22px / normal ~40px / tall ~64px with thumbnails — default normal.
- Strengthen lane banding above the current alpha.

**Optional, high overlap**: switch to a real pixels-per-second scale with cursor-anchored zoom.
Today the scale is proportional (`w / total`), so adding a clip rescales the entire timeline and a
long project makes every clip a sliver. If this is ever going to happen, here is the cheapest place.

**Done when.** A brand-new project shows four labelled empty lanes and a ruler, and a clip can be
dragged past the current project end on any track.

---

### C2 — Unify track collections ⬜

The structural block. Everything else in Group C and `D5` depends on it.

**Why.** The spine lives in `ObservableCollection<CinematicOperation> TimelineNodes`; tracks 2–4
live in `ObservableCollection<OverlayTrack>`, each with its own `Clips`
(`ViewModels/DirectorViewModel.cs:22-28`). Same clip type, two containers, two rule sets. Already
logged as owed work in ARCHITECTURE.md §6D.

**Changes, in dependency order.**

1. **One collection.** `ObservableCollection<TimelineTrack>`, each with `Clips` and behaviour flags.
   Track 1 becomes `tracks[0]`. Keep the migration pattern already used for the legacy
   `OverlayClips` list; bump `SchemaVersion`.
2. **Absolute time everywhere.** Every clip gets a real `StartTime`. "Gapless spine" demotes from a
   structural law to a per-track *magnetic* flag (surfaced in C3). `TotalStoryDuration` becomes max
   end across all tracks.
3. **A background state.** The sharpest functional consequence of gaps on Track 1: today the bottom
   player always has something on it. With gaps you need an explicit "no clip active at *t* on this
   track" → render black. Cheap to build, but it must exist **before** gaps are legal.
4. **Placement for every clip.** Track 1 stops being implicitly full-frame and gets the same
   box-grid + clip-geometry + content-transform structure as an overlay, defaulting to
   `(1, 1, 0.5, 0.5)`. Practically: promote the spine to a fourth generic `OverlayVisual` at the
   bottom of the z-stack.
5. **Transitions.** The hard part. Crossfades need two surfaces per track. Giving every track an A/B
   pair means 8 `MediaPlayer` instances, which is likely too heavy given the stutter work recorded
   in ARCHITECTURE.md §4. **Plan for on-demand**: allocate the second surface only for tracks that
   actually carry a transition.
6. **Still-proxy path for Track 1**, so the §7A invariant ("never manipulate a live video surface")
   holds uniformly and Track 1 becomes reshapable in Arrange. This also partially addresses
   ARCHITECTURE.md §6A.
7. **Input.** `HitTestOverlaySlot` currently returns −1 for "not on a PiP"; that becomes track
   index 0.
8. **Selection.** Collapse `SelectedTimelineNode` / `SelectedOverlay` into one `SelectedClip` plus
   its owning track. `IsTrack1Selected` / `IsOverlaySelected` gating disappears from the inspector —
   Speed, Transition, Size and Opacity all become universal.
9. **Export.** Black base + N layers, or track 0 stays the base clip list with the same placement
   math applied.

**Z-order.** No work needed in the preview: stacking is XAML declaration order — spine, then
`OverlayGrid1` (T2), `OverlayGrid2` (T3), `OverlayGrid3` (T4)
(`Views/DirectorPlayerControl.xaml:33,56,73`). T4 over T3 over T2 over T1 already holds purely from
track index, and `HitTestOverlaySlot` walks topmost-down to match. The A/B `Canvas.SetZIndex` calls
during transitions only reorder the two spine players inside their own nested Grid, so they cannot
disturb overlay stacking. **Preserve this ordering when the spine becomes a generic visual — declare
the array bottom-to-top.**

⚠ **Verify before trusting**: export z-order. `VideoExporter` appends one `MediaOverlayLayer` per
track in track order (`Models/VideoExporter.cs:96-118`). Confirm `MediaOverlayLayer` list order maps
to the same stacking direction as the preview.

**Done when.** A clip behaves identically on any track; a project made only of Track 3 clips plays;
Track 1 can have gaps and a placement box.

---

### C3 — Real track headers ⬜

Depends on C2. This is where the per-clip/per-track QoL controls live.

**Why.** A track header is currently a single 18px button in a 58px gutter
(`Views/VideoDirectorControl.xaml:445`, `AddTrackLabel`) whose only action is opening a file picker.
There is no way to silence a layer without editing every clip on it, no way to hide a layer while
previewing, and no protection against dragging the wrong thing.

**Changes.**
- Gutter to ~140px. Header = colour chip + track name, then **Mute · Hide · Lock · Gapless**, then a
  `⋯` overflow carrying rename, track default placement, clear track, and the load-file action that
  currently *is* the entire header.
- `TimelineTrack` gains `IsMuted` / `IsHidden` / `IsLocked` / `IsGapless`.
- Engine: skip hidden tracks in `EvaluateOverlays`; force volume 0 on muted ones; locked tracks
  reject drag, trim and delete.
- **Track default placement** — `OverlayTrack.DefaultCenterX/Y` already exists with no UI and no
  default *size*. Add "new clips on this track land as: full frame / corner PiP".
- Preserve `TrackPalette` throughout. Colour is the correlation key between a timeline row and its
  picture in the composite, and it is the strongest thing in the current UI.

**Compatibility default**: ship Track 1 with `IsGapless = true` and tracks 2–4 with it off. The app
then behaves as it does today out of the box, but nothing is structural — gapless can be turned off
on Track 1 or on for Track 3.

**Done when.** Every track can be muted, hidden, locked and switched between gapless and free
placement, from its own header.

---

### C4 — Drag & drop correctness ⬜

Needs A3. Independent of C2.

**Why.**
- **Cross-track drags mutate the model mid-gesture.** `TimelineBar_PointerMoved` removes a clip from
  one collection and inserts it into another *while the drag is still in progress*
  (`Views/VideoDirectorControl.xaml.cs:584-603`), re-resolving overlaps and rewriting `StartTime`
  each time. A vertical wobble permanently reshuffles the model. The within-spine reorder on the
  same code path does it correctly — ghost preview, commit on release.
- **There is no way to cancel a drag.** No Esc handling anywhere.
- **The context menu is a dead end on empty lanes.** Right-clicking bare lane space opens the flyout
  with Split / Snapshot / Duplicate / Remove all looking live, but `_contextClip` is null so every
  handler silently does nothing (`Views/VideoDirectorControl.xaml.cs:696-714`).
- **No drop-target highlight** for external file drags — only a text caption in the drag UI. The
  ruler is currently a Track 1 drop zone (`toSpine = drop.Y < RowOvY`), which is arbitrary once
  tracks are peers.
- **The playhead is only grabbable in the 14px ruler strip.**

**Changes.** Ghost-preview every drag including cross-track; single commit on drop; Esc cancels.
Highlight the destination lane during external drags; make the ruler reject drops or route to the
nearest lane. Give bare lane space its own context menu and disable clip-scoped items when no clip
is hit. Make the playhead grabbable beyond the ruler strip.

**Done when.** No drag can change the model until it is dropped, and Esc always backs out cleanly.

---

## Group D — Clip editing

### D1 — Normalised framing coordinates ⬜

Must land before D2 — the rectangle geometry depends on it.

**Why.** `SpatialMark.X/Y` are raw `CompositeTransform.TranslateX/TranslateY` values in DIPs,
captured at whatever size the player happened to be
(`Views/VideoDirectorControl.xaml.cs:1152-1183`). Resize the window and every clip's framing shifts.
Worse, overlays carry a compensating fudge — `ApplyMarksAtProgress` multiplies the translate by
`PlacementWidth/Height` (`Models/VideoPlaybackEngine.cs:1801`) — so **resizing a PiP box silently
changes its Ken Burns framing**.

**Changes.**
- `SpatialMark(Scale, X, Y)`-in-pixels becomes `{ Zoom, CenterX, CenterY }` in source-frame
  fractions. The camera rect is `frame ÷ Zoom` centred at `(CenterX, CenterY)`.
- Two helpers — `MarkToTransform(mark, surfaceW, surfaceH)` and `TransformToMark(...)` — become the
  only place surface size enters the math. Delete the `panScaleX/panScaleY` fudge.
- Fix the hardcoded `16.0/9.0` in the overlay crop-box aspect
  (`Models/VideoPlaybackEngine.cs:1029`), which is wrong for any non-16:9 output.
- Migration at the next `SchemaVersion`: identity marks (scale 1, x/y 0 — nearly all of them) pass
  through untouched; non-identity marks convert best-effort against the saved window size.

**Done when.** Resizing the window no longer shifts any clip's framing, and resizing a PiP box no
longer shifts its Ken Burns.

---

### D2 — The framing canvas ⬜

The main event. Implements decision 1.

**Why.** In Edit mode, `PlayerControl.ActiveTransform` is simultaneously the live framing *and* the
keyframe being authored. Everything confusing about Edit mode falls out of that conflation:

- **Scrubbing does not animate the framing.** `SeekActiveOperation`
  (`Models/VideoPlaybackEngine.cs:154-168`) moves the player and forces a frame to decode, but never
  calls `ApplyMarksAtProgress`. Scrub to 60% and the picture is still framed however you last
  dragged it. The only way to see the motion is to press Play and watch the loop.
- **You cannot navigate to the Mid or End framing.** `CurrentEditTarget` exists in the ViewModel with
  a change event and a dispatcher hop, and has **no UI binding anywhere** (zero matches across all
  XAML). It is permanently `Start`.
- **The three rectangles fly apart.** `DrawRect` positions each mark relative to the *current*
  transform (`Models/VideoPlaybackEngine.cs:1065-1068`), so zooming in to set End sends the Start
  rect off-canvas. You never see the three framings in a stable relationship — the one thing needed
  to judge a Ken Burns move.
- **And they are not touchable.** `WysiwygCanvas` is `IsHitTestVisible="False"`
  (`Views/DirectorPlayerControl.xaml:90`). Pure decoration.

**Changes.** Replace `WysiwygCanvas` with a hit-testable control:

- Edit mode shows the clip's **whole source frame**, fit and dimmed.
- Start / Mid / End draw as aspect-locked rectangles — aspect from the viewport for Track 1, from
  the PiP box for overlay clips — each drag-to-move with 8 resize handles, clamped inside the frame,
  snapping to edges and centre.
- A faint motion path joins the rect centres so the move is legible at a glance.
- Clicking a rect selects it. **This is what finally gives `CurrentEditTarget` a UI binding**, and
  it is how you navigate to the Mid/End framing.
- Wheel zooms the selected rect (decision 1).
- `SeekActiveOperation` starts calling `ApplyMarksAtProgress` in Edit, so scrubbing animates the
  real framing, with an interpolated ghost rect on the canvas.
- A toggle flips between *framing view* (boxes over the whole frame) and *result view* (what the
  viewer actually sees).

**Done when.** A complete Ken Burns move can be authored without pressing Play, and scrubbing shows
the true framing at every point.

---

### D3 — Mid keyframe timing ⬜

**Why.** Mid is hard-wired to the midpoint, both in the interpolation split
(`ApplyMarksAtProgress`) and in the Edit seek (`EnterEditMode`). It is cleared by **right-clicking
the Mid button** (`ClearMid_RightTapped`) — undiscoverable. And the easing names are invented:
"Linear / Bezier / DirectorsArc" are linear, ease-in-out-quad and ease-out-cubic; nobody can predict
what "DirectorsArc" does.

**Changes.** Add normalised `MidTime` (default 0.5); split interpolation there instead of at 0.5;
`EnterEditMode` seeks there. Draggable Mid marker on the clip scrubber and on the motion path.
Visible delete affordance replacing the right-click. Rename curves to Linear / Ease In / Ease Out /
Ease In-Out with a small curve glyph.

**Done when.** Mid can sit anywhere in the clip and reads correctly while scrubbing.

---

### D4 — Retime block ⬜

Independent of D1–D3. Implements decision 2.

**Why.** `Duration = (Out − In) ÷ Speed` — only two of the three can be free. The inspector shows
five peer `NumberBox`es with invisible, asymmetric yield rules:

| You edit | What silently moves | Held fixed |
|---|---|---|
| Speed | Duration | In, Out |
| Duration | **Out** | In, Speed |
| In or Out | Duration | Speed |

Nothing on screen states the constraint or marks which value is derived. "Duration (Source /
Trimmed)" sits above a *disabled spinner* next to an editable one, so it reads as five independent
numbers that mysteriously move each other. A legitimate third case — *fill this 10-second slot with
this source, whatever speed that takes* — is unreachable.

Worst of all, **`speed = 0` is a hidden mode switch**. It converts a video into a freeze-frame
(`Models/CinematicOperation.cs:34-43`) and flips Duration from *derived* to *independently authored*
(`Models/CinematicOperation.cs:226-235`). Same field, two meanings, no signal. Relatedly, Speed is
enabled for image clips, where it does nothing except divide the hold time — setting a photo to 2×
halves how long it is on screen.

**Changes.**
- A `RetimeMode` on the clip, with all four setters routed through one `Reconcile(changedField,
  mode)` method instead of four independent yield rules:
  - **Hold source** (default) — In/Out and Speed authored, Duration derived. Today's behaviour.
  - **Fit to fill** — In/Out and Duration authored, Speed derived.
  - **Still** — constraint suspended: Duration authored freely, Speed hidden, frame position is the
    In point. This is exactly today's `speed = 0` path, named and made explicit. Images enter it
    automatically.
- Inspector shows the equation, visually marks the derived field, and offers the three modes as a
  segmented control.
- Honest labels: "Source In/Out", "Timeline Duration", source length as plain text rather than a
  disabled spinner.

**Done when.** No field moves another without the UI saying why, and setting a still's duration does
not require knowing that `speed = 0` is a secret mode.

---

### D5 — Placement presets + Reset ⬜

Needs C2 (placement must exist on every track first).

**Why.** There is **no** numeric or preset control for a PiP box anywhere. Canvas drag and wheel are
the only way to set one — the inspector's "Transform & Overlay" expander contains just Start / Mid /
End and Record (`Views/VideoDirectorControl.xaml:117-133`), and PiP size lives read-only in the
Telemetry HUD, itself gated on pinning the panel.

The existing Reset is incomplete and mis-gated. `CinematicOperation.Reset()`
(`Models/CinematicOperation.cs:442`) does **not** clear `PlacementWidth/Height/CenterX/CenterY`,
`Opacity` or `Volume`, and never restores `VideoEndTime` to `SourceDuration`, so a trimmed clip
stays trimmed. `HasModifications` (`Models/CinematicOperation.cs:54-62`) ignores those same fields —
so after moving and resizing a PiP, **the Reset button is disabled on a heavily formatted clip**.

**Changes.**
- Placement presets on the clip: **Full frame · Fill · corner/centre positions**. Surface them in the
  inspector *and* the timeline right-click flyout.
  - `PlacementWidth/Height` are fractions of the **aspect-fit rect**, not the viewport
    (`Models/VideoPlaybackEngine.cs:1635-1650`), so "Full frame" is exactly `(1, 1, 0.5, 0.5)` — no
    aspect math needed.
  - ⚠ Both are clamped to max 1.0, so a true screen-**fill** on a clip whose aspect differs from the
    viewport is currently unreachable. Raise the clamp or add an explicit fill mode.
- Numeric placement fields in the inspector.
- Split Reset into **Reset framing/motion** (marks, curve) and **Reset placement** (box, opacity) —
  resetting a trim is a different intent from resetting a Ken Burns move, and one button conflating
  all three gets used and then undone.
- Extend `HasModifications` to cover placement, opacity and volume.

**Done when.** A PiP can be set to full frame or a corner in one click, and Reset actually clears
what the user changed.

---

## 4. Known issues not scheduled here

Real problems found during the audit that no phase above addresses. Listed so they are not lost.

- **Preview ≠ export.** Per-clip speed, Ken Burns motion and transitions are all absent from the
  export, and PiPs are stretched rather than crop-filled (`Models/VideoExporter.cs:17-20`). The
  app's entire value is WYSIWYG composition and the last step does not honour it. Long-term that is
  one compositor, not two; short-term the export dialog should state plainly which features will not
  be baked — it currently says nothing.
- **No project safety net.** No dirty-state indicator, no filename in the title bar, no
  unsaved-changes prompt on close, no autosave, no recent-projects list, no `.json` file
  association. Closing the window discards everything silently.
- **Silent failure on load.** `catch { }` in `AddFilesAsync`, `AddOverlayAsync` and both thumbnail
  loaders (`ViewModels/DirectorViewModel.cs:425` and nearby). A missing or unreadable file becomes a
  black 10-second clip with no message; you only find out at export.
- **No edge-trimming on the timeline.** There is no edge grab in `HitClip` / `PointerMoved` on any
  track, so trimming means entering Edit or typing numbers. Ripple and roll rules are unimplemented
  (ARCHITECTURE.md §6C).
- **Immediate-mode timeline.** `BuildTimelineBar` clears and re-creates every Canvas child on any
  change, which already caused one class of bug that had to be worked around — destroying the
  element a gesture started on kills the gesture (see the comments at
  `Views/VideoDirectorControl.xaml.cs:643-649`). Retained per-clip containers would remove a family
  of these.
- **Pill auto-hides after 5s of pointer stillness** (`Views/VideoDirectorControl.xaml.cs:49-51`),
  including mid-edit. Correct during playback, wrong while arranging.
- **Audio is one number per clip.** No master level, no mute toggle, no meters, no fades. Overlays
  default muted with no indication beyond a `0.00` in a NumberBox.
- **Overlay scrubbing** shows a static frame rather than seeking live (ARCHITECTURE.md §6B).

---

## 5. Maintaining this document

- Mark a phase ✅ Done the moment it lands, and move anything it invalidated out of
  "Known issues not scheduled here".
- When a phase removes something ARCHITECTURE.md §5 states as an invariant, update that invariant in
  the same commit and drop its ⚠ marker.
- Record new decisions in §2 with their date. Future sessions read that table before asking again.
