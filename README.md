# 🎬 VideoDirector

A multi-track video sequencer and compositor for Windows. Assembles video and image assets into a
time-synchronised composite, with animated pan/zoom on any clip and up to three simultaneous
picture-in-picture layers.

Built as a companion to [ModernImageViewer](https://github.com/ec928/ModernImageViewer), and runs
standalone.

## ✨ Key Features

**4-Track Timeline** — One spine track (A-roll) plays gaplessly end-to-end and defines project
duration. Three overlay tracks (B-roll) composite freely-positioned picture-in-picture layers over
it, with automatic collision resolution when clips are reordered.

**Ken Burns Motion** — Any clip, video or still, can carry an animated pan/zoom defined by start,
optional mid, and end framing keyframes interpolated with easing curves.

**Three-Mode Interaction** — Playback, Arrange, and Edit modes strictly segregate what mouse input
means, so canvas manipulation never collides with timeline scrubbing or review playback.

**Stills as First-Class Clips** — Images sit on the spine with a set duration and advance story time
by wall clock rather than media timestamps, so mixed photo/video sequences stay in sync.

**Transitions & Speed** — Optional crossfade and dip-to-black between spine clips, plus variable
playback speed including full stills.

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

**Windows SmartScreen**: this is an unsigned indie build, so Defender may warn on first launch. Choose
**More info → Run anyway**. That is normal for software not signed with a paid code-signing
certificate; the full source is in this repository if you would rather build it yourself.

## 🔨 Building from Source

Requires the .NET 8 SDK and the Windows App SDK workload (Visual Studio 2022, "Windows application
development").

```
git clone https://github.com/ec928/VideoDirector.git
cd VideoDirector
dotnet build -c Release -p:Platform=x64
```

To produce a portable build, use the publish profile:

```
dotnet publish -p:PublishProfile=FolderProfile -p:Platform=x64
```

`FolderProfile.pubxml` publishes self-contained, loose-file, ReadyToRun output. Note it writes to an
absolute `PublishDir` — change that to a path of your own before publishing.

> Deliberately **not** published as a single file. Single-file self-extracts the entire runtime to
> `%TEMP%` on the first launch after every publish, which measurably slows cold start.

## 📋 Status

Active development. The timeline, compositor, Ken Burns model, and mode system are implemented and in
use; export is in progress. Expect rough edges.

## 💬 Feedback

Bugs, performance problems, and feature requests are welcome in the
[Issues](https://github.com/ec928/VideoDirector/issues) tab.
