# VideoDirector 0.9.0 — review notes

Full-codebase review, 2026-08-26. Progress is tracked in [REVIEW.md](REVIEW.md).

## Summary

VideoDirector 0.9.0 is a serious WinUI 3 multi-track compositor: Ken Burns geometry, mode segregation, chrome rules, and the ScreenRecorder export path are clearly the product of measured failures, not guesses. The live compositor looks sound; the dominant risks are export (playhead not rewound, looping left on, fade duration counted twice, audio ignoring clip speed) and a handful of Edit/Arrange holes that the architecture already named and the code still has. Geometry and ChromeRules are the best-tested parts of the tree; almost none of the defects below are reachable from those unit tests.

ARCHITECTURE.md §4/§6 items marked SETTLED/DONE/FIXED were not re-proposed.

## Issues

See REVIEW.md for live status. Original findings:

1. **bug** Export starts from the playhead, not 0
2. **bug** Looping stays on during recording
3. **bug** Fade length counted twice in `TotalStoryTime`
4. **bug** Set Start/Mid/End calls `BeginEdit`
5. **bug** `ResumePlayback` drops per-clip speed
6. **bug** `CloneClip` omits stream flags — duplicated audio-only clips paint black
7. **bug** `HasModifications` ignores `MidMark`
8. **bug** Canvas PiP drag/resize skips undo and `IsLocked`
9. **bug** Track 1 hit-test selects the previous clip in a gap
10. **bug** Export audio ignores clip speed
11. **bug** ScreenRecorder reuses encode buffers without a copy
12. **bug** NumberBox `FractionDigits` is hardcoded to 0
13. **bug** `StampPlacementDefaults` never runs on load
14. **suggestion** `TryGetMarkSpace` always uses slot 0 for aspect
15. **suggestion** `MediaFailed` stays attached after `OnOpened`
16. **suggestion** Recording captures the window at width 1920, not the project canvas
17. **suggestion** ARCHITECTURE §2.C rule 9 still says “no clamp”; code uses contact
18. **suggestion** Doc/label drift (4 lanes, spine/overlay, stub recorder)
19. **suggestion** Unused `VideoExporter` / `ConfirmRecordAsync`
20. **suggestion** `UnhandledException` swallows every failure
21. **nit** Dead restart params / `CurrentPlayingOperation` never assigned
22. **nit** `ClampFraming` still documents `Allowance`

## Corrections after the spec rewrite

- **Issue 4:** drop `BeginEdit` only. Keep `SeekForMark` — Set Mid/End is supposed to jump the picture to that mark. The window-resize is the defect.
- **Issue 22:** declined. Allowance remarks on `ClampFraming` are contrast, not leftovers.
- **§2.C** was rewritten against the code (contact, live framing vs mark). Issue 17 is closed by that rewrite.

## What landed

- Recording is a closed path: rewind to 0, disable looping, wait until playback has started, size/crop to the canvas, delete the silent temp file.
- Fade duration: `OpDuration` already includes the fade; `TotalStoryTime` and timeline wedges agree.
- Set no longer calls `BeginEdit`; `SeekForMark` stays.
- Canvas arrange gestures record on pointer-up and honour `IsLocked`.
- `CloneClip` copies stream flags; `AddFilesAsync` fires `ClipAdded`.
- Export audio at non-1x is skipped and reported.
- `StampPlacementDefaults` on load.
