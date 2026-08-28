# 🎬 VideoDirector

**Cinematic Collage & Motion Slideshow for Video, Stills & Sound.**

Arrange stills, video and sound anywhere on a fixed canvas, give any of them animated pan and zoom, and present the result full screen or export it. Six equal tracks, any of which can be a picture-in-picture.

Built as a companion to [ModernImageViewer](https://github.com/ec928/ModernImageViewer), and runs standalone.

## ✨ Key Features

**Six-Track Timeline** — Up to six video/image tracks composited freely, added and removed as a project needs. There is no artificial distinction between a "spine" and "overlays" — every track is treated equally, subject to Z-order. Picture-in-picture layers can be positioned anywhere, with automatic collision resolution when clips are reordered.

**Ken Burns Motion & Zoom** — Any clip, video or still, can carry an animated pan/zoom defined by start, optional mid, and end framing keyframes interpolated with easing curves.

**Fades** — Fade a clip in from black, out to black, or both. A transition **adds** to the clip’s length rather than trimming it, and the timeline shades the fade portion with a hairline at the boundary so the picture and the fade are told apart at a glance.

**Canvas Sizes** — Auto (the app window as it was when the project began, then held), four presets — 1920×1080, 3840×2160, 2.39:1 and 9:16 — or a custom size. Saved with the project.

**Present on Any Display** — Pick which screen a cinematic performance plays on. The performance opens its own bare full-screen window there; the editor stays exactly where it is, still showing the timeline, and says where the picture went. Choosing a display puts a confirmation ON that display and reverts after 10 seconds if nobody accepts it — nothing can tell a monitor from an HDMI audio sink, which reports itself as an ordinary screen, so the only real test is whether someone sitting there can act on it.

**Preferences** — A settings flyout on the toolbar. *Always show clip frames* keeps every track's frame drawn in full rather than occluding it behind the clips above, which makes a crowded arrangement easier to read while placing it.

**Media Pre-Flight** — Opening a project, and arming cinematic, both report which clips can no longer find their source files. A project is a list of paths into a media library, so that is the failure worth catching early.

**Borders** — Any clip on any track can be styled with a border, from the video canvas or the timeline right-click menu. Includes various edge styles (Solid, Soft rounded edges, and dashed FilmStrip perforations) with selectable colors and thickness.

**Fixed Canvas** — The composition has its own frame rather than borrowing whatever the window happens to be. Hiding a panel, resizing the window or presenting full screen changes only the scale you view the arrangement at, never the arrangement. Pan with the middle mouse button and zoom with the wheel in Arrange; middle-click returns to fit.

**Three-Mode Interaction** — Playback, Arrange, and Edit modes strictly segregate what mouse input means, so canvas manipulation never collides with timeline scrubbing or review playback.

**Timeline Loop Region** — Drag horizontally across the timeline time ruler to visually select a specific section of the project to loop continuously during playback. Clicking the ruler without dragging clears it.

**Stills as First-Class Clips** — Images sit on the timeline with a set duration and advance story time by wall clock rather than media timestamps, so mixed photo/video sequences stay in sync.

**Variable Speed** — Per-clip playback speed, down to a full freeze-frame.

**Audio Clips** — Drop in an `.mp3`, `.m4a`, `.wav`, `.flac` or any other sound file and it behaves like a clip: trim it, place it in time, set its level. A sound-only clip draws nothing, so it never covers the tracks beneath it, and the inspector shows only what applies to it — timing and volume. Video containers holding only audio are detected too.

**Says What A Clip Is, And What It Has** — The timeline marks stills and audio clips with their own symbols, and distinguishes *muted by you* (red) from *no sound to give* (dimmed) — an image, a file with no audio track, or a frozen frame. Volume is disabled and dimmed where there is nothing to hear, rather than offering a control that moves and changes nothing.

**Trim & Sync Tools** — Interactive edge trimming, cross-track magnetic snapping (8px threshold), and optional audio waveforms on timeline clips.

**Project Management** — JSON-based project save/load and unlimited Undo/Redo history.

**Export to MP4** — Press Export and the project plays full screen, chrome-free, while being recorded. Because it captures what the compositor actually draws, **everything survives**: Ken Burns motion, fades, per-clip speed, borders, picture-in-picture. Sound is mixed from the sources and laid on afterwards. It runs in real time — a two-minute project takes two minutes — and Esc stops a take early.

> The previous export rendered through the Windows compositor and could carry none of that; worse, it refused most real projects outright. See ARCHITECTURE.md for the measurements that closed off that route.

## 🛠 Technical Stack

| | |
|---|---|
| Framework | C# / .NET 8 |
| UI | WinUI 3 (Windows App SDK) |
| Playback | One `MediaPlayer` per track, composited live into a fixed canvas |
| Deployment | Self-contained, portable, x64 |

See [ARCHITECTURE.md](ARCHITECTURE.md) for the full system topology, interaction laws, and invariants.

## 📦 Installation (Portable)

Distributed as a self-contained portable app — no installation or registry changes.

1. Download the latest `.zip` from the [Releases](https://github.com/ec928/VideoDirector/releases) page.
2. Extract the folder anywhere.
3. Run `VideoDirector.exe`.

**Windows SmartScreen**: this is an unsigned indie build, so Defender may warn on first launch. Choose **More info → Run anyway**. That is normal for software not signed with a paid code-signing certificate; the full source is in this repository if you would rather build it yourself.

## 🔨 Building from Source

Requires the .NET 8 SDK and the Windows App SDK workload (Visual Studio 2022, "Windows application development").

```
git clone https://github.com/ec928/VideoDirector.git
cd VideoDirector
dotnet build -p:Platform=x64
```

To produce the portable, shippable build:

```
publish.bat
```

There are two builds and two folders, and nothing is written anywhere else:

| Folder | Produced by | What it is |
|---|---|---|
| `bin\Debug\` | `dotnet build` | the test build |
| `bin\Release\` | `publish.bat` | self-contained, loose-file, ReadyToRun — ship this |

Release *is* the publish output. `dotnet publish` must build before it copies, and that build lands in `bin\Release` regardless, so pointing the published files anywhere else only leaves a half-built Release folder beside the real one.

Neither path carries a `$(Platform)` segment, deliberately. The SDK only defaults to `bin\$(Configuration)\` while the platform is `AnyCPU`; name a platform and it becomes `bin\$(Platform)\$(Configuration)\`. The solution maps every platform to x64, so building from the IDE otherwise grows a second full tree beside the real one — which is how you end up testing a stale binary. `OutputPath` is pinned in the csproj so that cannot happen.

`publish.bat "D:\somewhere"` publishes elsewhere; add `nosmoke` to skip the launch check. The `FolderProfile.pubxml` used by the Visual Studio Publish button targets the same folder.

> Deliberately **not** published as a single file. Single-file self-extracts the entire runtime to `%TEMP%` on the first launch after every publish, which measurably slows cold start.

## 🔬 Playback Instrumentation

Playback timing problems are not diagnosable by reading code — the costs live in the media pipeline, not
in the call graph. The engine can trace what the UI thread actually did, gated behind an environment
variable so it costs nothing when off.

```
set VD_TRACE=1
set VD_TRACE_MS=45000
VideoDirector.exe --play
```

| Variable | Meaning |
|---|---|
| `VD_TRACE` | any non-empty value enables tracing |
| `VD_TRACE_MS` | how long to record before writing the log (default 25000) |

The log is written to `%TEMP%d-trace.log`, tab-separated as `ms`, `tick`, `gapMs`, `event`. A row with a
`gapMs` is one frame of the render loop; the events between two ticks are what happened during that frame.
Recorded events include `LOOP wrap to 0`, `PRELOAD`/`PRIMED`, `OPEN`/`OPENED`, `REACTIVATE`, `DRIFT-SEEK`,
`SEEK-DONE`, GC collections with heap size, and bytes allocated per 60 frames.

`DRIFT-SEEK` and `REACTIVATE` carry `vol=`. **This matters more than anything else in the log:** overlays
default to `Volume = 0`, so only the slot carrying the audio bed can produce an audible stutter. A silent
slot seeking hard is not the bug you are chasing — that mistake cost two wrong fixes (see §4 of
`ARCHITECTURE.md`).

`trace-startup.ps1` runs the whole thing and summarises it:

```
.	race-startup.ps1 [project] -Seconds 45
```

It launches with `--play`, stops the app, and reports preload cost, tick count, median and worst frame gap,
frames lost at 60Hz, loop wraps, collections, and every stall over 20ms with the events near it. Preload and
the first post-preload tick are excluded from the frame-drop figures, because neither is a dropped frame.

> Frame gaps and audible stutter are **different measurements**. A uniform frame gap cannot cause an
> intermittent stutter; when the two disagree, the frame gap is not what is being heard.

## 📋 Status

Active development. The timeline, compositor, Ken Burns model, canvas and mode system are implemented and in daily use. Export records the performance and keeps everything you see; it is silent only in the sense that it takes as long as the project runs. Expect rough edges.

## 💬 Feedback

Bugs, performance problems, and feature requests are welcome in the [Issues](https://github.com/ec928/VideoDirector/issues) tab.
