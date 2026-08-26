# VideoDirector review — 0.9.0 follow-up

Source: full-codebase review (2026-08-26). Architecture §4/§6 items marked SETTLED/DONE/FIXED are out of scope.

Status: **done**, with two corrections from the spec rewrite that followed the review.

Corrections:
- **Issue 4** — the defect is `BeginEdit` resizing the Edit window, not the mark jump. Set keeps `SeekForMark` (picture jumps to the mark just written). `BeginEdit` is gone from those handlers.
- **Issue 22** — declined. `ClampFraming`'s Allowance remarks are deliberate contrast ("coverage is wrong here"), not leftover docs.
- **Issue 17 / §2.C** — spec was rewritten against the code (contact, live framing vs mark, four movers / two mark-writers). Not a code change.

## Bugs

- [x] **1** Export starts from the playhead, not 0
- [x] **2** Looping stays on during recording
- [x] **3** Fade length counted twice in `TotalStoryTime`
- [x] **4** Set Start/Mid/End calls `BeginEdit` (SeekForMark kept)
- [x] **5** `ResumePlayback` drops per-clip speed
- [x] **6** `CloneClip` omits `SourceHasVideo` / `SourceHasAudio`
- [x] **7** `HasModifications` ignores `MidMark`
- [x] **8** Canvas PiP drag/resize skips undo and `IsLocked`
- [x] **9** Track 1 hit-test selects the previous clip in a gap
- [x] **10** Export audio ignores clip speed (skip-and-warn)
- [x] **11** ScreenRecorder reuses encode buffers without a copy
- [x] **12** NumberBox `FractionDigits` is hardcoded to 0
- [x] **13** `StampPlacementDefaults` never runs on load

## Suggestions

- [x] **14** `TryGetMarkSpace` waits on `SourceAspect`; draw/drag/Set now share that space (`AspectOf` remains for box layout only)
- [x] **15** `MediaFailed` stays attached after `OnOpened`
- [x] **16** Recording sized to the canvas. Follow-up: CreateMp4(HD1080p) ignored Width/Height (CsWinRT copy); mux used the same preset. Both now assign Video back and throw if the size does not stick.

Follow-up from 0-Test7 export: T3's border drew over opaque T6 because a spanning-axis gate skipped any occluder that did not cover a full side (`Layout.cs`). Borders are now four edge strips; subtracting a cover from an edge is exact. Frames also stay hidden while recording.
- [x] **17** ARCHITECTURE §2.C rewritten (spec, not code)
- [x] **18** Doc/label drift: lanes, spine/overlay, stubbed canvas modes, “recorder not built”
- [x] **19** Unused `VideoExporter` / `ConfirmRecordAsync` removed
- [x] **20** `UnhandledException` surfaces a status banner

## Nits

- [x] **21** Dead restart params / `CurrentPlayingOperation` assigned / formatting
- [ ] **22** declined — see above

## Tests added

- [x] Fade: 10s clip + 1s fade ⇒ total 11s, fade inside that window
- [x] `HasModifications` is true when only Mid is set (`ClipRules.HasMarkModifications`)
- [x] Speed-changed clip: mux skip contract (`ClipRules.CanMixExportAudio`)
