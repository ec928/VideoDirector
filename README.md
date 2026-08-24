# 🎬 VideoDirector

A multi-track video sequencer and compositor for Windows. Assembles video and image assets into a time-synchronised composite, with animated pan/zoom on any clip and up to five simultaneous picture-in-picture layers.

Built as a companion to [ModernImageViewer](https://github.com/ec928/ModernImageViewer), and runs standalone.

## ✨ Key Features

**Six-Track Timeline** — Up to six video/image tracks composited freely, added and removed as a project needs. There is no artificial distinction between a "spine" and "overlays" — every track is treated equally, subject to Z-order. Picture-in-picture layers can be positioned anywhere, with automatic collision resolution when clips are reordered.

**Ken Burns Motion & Zoom** — Any clip, video or still, can carry an animated pan/zoom defined by start, optional mid, and end framing keyframes interpolated with easing curves.

**Fades** — Fade a clip in from black, out to black, or both. A transition **adds** to the clip’s length rather than trimming it, and the timeline shades the fade portion with a hairline at the boundary so the picture and the fade are told apart at a glance.

**Canvas Sizes** — Auto (the app window as it was when the project began, then held), four presets — 1920×1080, 3840×2160, 2.39:1 and 9:16 — or a custom size. Saved with the project.

**Present on Any Display** — Pick which screen cinematic playback takes over; the window moves there before going full screen and returns to its desk afterwards.

**Media Pre-Flight** — Opening a project, and arming cinematic, both report which clips can no longer find their source files. A project is a list of paths into a media library, so that is the failure worth catching early.

**Borders** — Any clip on any track can be styled with a border, from the video canvas or the timeline right-click menu. Includes various edge styles (Solid, Soft rounded edges, and dashed FilmStrip perforations) with selectable colors and thickness.

**Fixed Canvas** — The composition has its own frame rather than borrowing whatever the window happens to be. Hiding a panel, resizing the window or presenting full screen changes only the scale you view the arrangement at, never the arrangement. Pan with the middle mouse button and zoom with the wheel in Arrange; middle-click returns to fit.

**Three-Mode Interaction** — Playback, Arrange, and Edit modes strictly segregate what mouse input means, so canvas manipulation never collides with timeline scrubbing or review playback.

**Timeline Loop Region** — Drag horizontally across the timeline time ruler to visually select a specific section of the project to loop continuously during playback. Clicking the ruler without dragging clears it.

**Stills as First-Class Clips** — Images sit on the timeline with a set duration and advance story time by wall clock rather than media timestamps, so mixed photo/video sequences stay in sync.

**Variable Speed** — Per-clip playback speed, down to a full freeze-frame.

**Trim & Sync Tools** — Interactive edge trimming, cross-track magnetic snapping (8px threshold), and optional audio waveforms on timeline clips.

**Project Management** — JSON-based project save/load, infinite Undo/Redo history, and direct MP4 export using the Windows Media Foundation compositor.

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

## 📋 Status

Active development. The timeline, compositor, Ken Burns model, canvas and mode system are implemented and in daily use. Export writes 1080p MP4, but motion, per-clip speed and fades are preview-only and are not yet baked into the render — the app itself is the delivery mechanism. Expect rough edges.

## 💬 Feedback

Bugs, performance problems, and feature requests are welcome in the [Issues](https://github.com/ec928/VideoDirector/issues) tab.
