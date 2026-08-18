# 🎬 VideoDirector

A multi-track video sequencer and compositor for Windows. Assembles video and image assets into a time-synchronised composite, with animated pan/zoom on any clip and up to three simultaneous picture-in-picture layers.

Built as a companion to [ModernImageViewer](https://github.com/ec928/ModernImageViewer), and runs standalone.

## ✨ Key Features

**4-Track Timeline** — Up to four video/image tracks composited freely. There is no artificial distinction between a "spine" and "overlays"—every track is treated equally, subject to Z-order. Picture-in-picture layers can be positioned anywhere, with automatic collision resolution when clips are reordered.

**Ken Burns Motion & Zoom** — Any clip, video or still, can carry an animated pan/zoom defined by start, optional mid, and end framing keyframes interpolated with easing curves.

**Custom PiP Borders** — Picture-in-picture clips can be styled with custom borders directly from the video canvas or timeline right-click menus. Includes various edge styles (Solid, Soft rounded edges, and dashed FilmStrip perforations) with selectable colors and thickness.

**Three-Mode Interaction** — Playback, Arrange, and Edit modes strictly segregate what mouse input means, so canvas manipulation never collides with timeline scrubbing or review playback.

**Timeline Loop Region** — Drag horizontally across the timeline time ruler to visually select a specific section of the project to loop continuously during playback. Clicking the ruler without dragging clears it.

**Stills as First-Class Clips** — Images sit on the timeline with a set duration and advance story time by wall clock rather than media timestamps, so mixed photo/video sequences stay in sync.

**Transitions & Speed** — Optional crossfade and dip-to-black between clips, plus variable playback speed including full stills.

## 🛠 Technical Stack

| | |
|---|---|
| Framework | C# / .NET 8 |
| UI | WinUI 3 (Windows App SDK) |
| Playback | `MediaPlayer` / `MediaPlayerElement` with dual-player crossfade |
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
| `bin\x64\Debug\` | `dotnet build -p:Platform=x64` | the test build |
| `bin\x64\Release\` | `publish.bat` | self-contained, loose-file, ReadyToRun — ship this |

Release *is* the publish output. `dotnet publish` must build before it copies, and that build lands in `bin\x64\Release` regardless, so pointing the published files anywhere else only leaves a half-built Release folder beside the real one.

`publish.bat "D:\somewhere"` publishes elsewhere; add `nosmoke` to skip the launch check. The `FolderProfile.pubxml` used by the Visual Studio Publish button targets the same folder.

> Deliberately **not** published as a single file. Single-file self-extracts the entire runtime to `%TEMP%` on the first launch after every publish, which measurably slows cold start.

## 📋 Status

Active development. The timeline, compositor, Ken Burns model, and mode system are implemented and in use; export is in progress. Expect rough edges.

## 💬 Feedback

Bugs, performance problems, and feature requests are welcome in the [Issues](https://github.com/ec928/VideoDirector/issues) tab.
