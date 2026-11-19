# Captures editor: what to use, adapt or build

Developer note. Written from a read of the code (`ui/src/components/captures/*`, `ExoLauncher/MainWindow.Capture.cs`,
`capture/README.md`, `capture/src/main.cpp`) and three leads. Nothing below was run on a recording on this PC.

## Where the editor is

- Viewer and editor: `CapturesSheet.tsx` `Detail`. A `<video>` in the page, a `TrimBar` (in and out handles, click to seek,
  keyboard), speed 0.5/1/2, loop, frame step (`,` `.`), and four saves through `capture.edit`.
- Host: `CaptureEditAsync` passes `trim` (lossless, no re-encode) or `export` (`discord`, `youtube`, `vertical`) to the
  Exo Capture add-on; `shrink` re-encodes a finished recording and proves it. Everything stays on this PC.
- Snips: `SnipEditor.tsx`, a canvas with marks (this is where arrows and text already live, for pictures).
- Exo's page has no video library and no encoder; every encode is the add-on's, on the GPU's encoder, off the page.

## Replay memory and available time

The host sizes legacy `configure.params.replaySeconds` to the smaller of the player's choice and a window estimated at
128 Mb/s, using one eighth of physical RAM (128–1,536 MiB, or 512 MiB if unknown). It keeps the existing 30-second
minimum and never changes quality or the saved preference. This bounds the requested time, not the actual memory used.

An add-on with a real byte cap must advertise `ready.data.replayMemoryBudget: true`. For that run, the host sends
`configure.params.replayMaxBytes` alongside the player's requested `replaySeconds`. The add-on must enforce both across
encoded replay audio/video and in-flight save snapshots, discarding whole keyframe groups without reducing quality.
While buffering it can report `replayRetainedMs` in state snapshots (actual playable time from the oldest saveable
keyframe, zero when none), and must refresh it after eviction or a source change. The host never infers that time from
the configured limit. The native implementation and GPU testing are not part of the host change.

## Leads, judged

| Lead | What it is (as read) | Verdict |
| --- | --- | --- |
| fframes (github.com/dmtrKovalenko/fframes) | Rust, MIT. SVG scenes, links FFmpeg's libav directly, Skia on GPU (Vulkan on Windows), a wasm browser editor, caption presets (Highlight, Karaoke, Reveal, Word) driven by a JSON transcript. Windows build needs a prebuilt shared FFmpeg 9 and LLVM. | **Idea only, no code.** MIT permits it, but it is a code-to-video renderer: FFmpeg plus Skia is tens of MB and a second GPU renderer next to the game. Exo already has an encoder (the add-on's Media Foundation path) and a page that draws. What is worth taking is its caption model: a transcript of words with start and end times, a style that highlights the current one. That is about 60 lines of React. |
| Remotion's timeline editor | Not read: x.com needs a login and was not fetched. Remotion itself is React plus headless Chrome plus FFmpeg, source-available with a paid company license beyond small teams (terms page not read in full). | **No.** A Chromium-per-render pipeline and a license that does not fit a public product. Its timeline idea (a thumbnail strip, a playhead you drag) is common and was built here from scratch. |
| @hdrgameanalysis's note | Not read (same wall). As told: Claude edits video with a voice-over and arrows over game recordings. | **Not an Exo feature.** It is an agent driving ffmpeg. Exo's agents are not in the product. The useful half (arrows over a game) is the existing snip marks, extended in time. |

## Pieces

| Piece | Cost now | Verdict |
| --- | --- | --- |
| Thumbnail strip and drag scrub on the trim bar | One hidden muted `<video>`, 9 to 20 seeks, no dependency, no host change. | **Built** (below). |
| Scene jumps | Needs cut detection: decode and diff frames. At strip density (a frame per 1 to 3 s) cuts are too coarse; real detection belongs in the add-on, which already decodes. | **Later**, add-on `scenes` method returning cut times, then `;` and `'` keys. Not in this slice. |
| Clip nesting (compositions) | A second timeline model, a renderer, and exports from it. The add-on trims one file losslessly; joining is the near win. | **Later, small:** `concat` of two clips with the same codec and size is a lossless mux in the add-on. Nesting of clips in clips: **no**. |
| Animated subtitles from the clip's voice | Needs speech to text. | **No.** Removed in 3.24.4: dictation and clip captions are gone. |
| Arrows and annotations | `SnipEditor` already has the marks. On video they need time spans and a burn-in on export (add-on: draw before encode). | **Later**, if burn-in is ever wanted for another feature; they would share it. |
| One-press highlight reel per game session | Needs the session's clips (the host knows game and time), an order, and `concat`. | **Built** (below). A fade between clips needs a re-encode of the whole reel, so it is not cheap: left out. |
| Shareable output | Already: For Discord, Full quality, Vertical, Send to Discord, Copy. | **Done.** A link needs hosting, which Exo does not run. |

## Costs that decide

- Lean native app: no new runtime. Every verdict above that adds a dependency is a no, or an opt-in model download.
- While gaming: strip decoding is one small video decode in Exo's page, only while the Captures sheet is open. Nothing
  here runs from the add-on during a game. Any scene analysis would have to wait for the game to end.
- Public product: the add-on is source-available under its own usage terms (the `capture/LICENSE` header reads "Exo Capture
  usage terms"; the owner settled it on 2026-11-10: Exo's own usage terms, with the OBS game-capture hook staying GPL-2.0 and separate).
  The strip changed nothing in `capture/`; a release that ships a `capture/` change pushes the tree first.

## Built: timeline strip and scrubbing

- `ui/src/lib/timeline.ts`: pointer to time, strip frame count, frame times (mid-slice, never the black first and last
  instants), centered cover crop. Pure; tested in `ui/test/timeline.test.ts` (`npm test`, also in `npm run check`).
- `TimelineStrip.tsx`: a second, muted video seeks one frame per cell and draws it to a canvas under the trim bar. It reads
  the clip with `crossOrigin=anonymous` (the capture host already answers the shell's CORS reads for the snip editor), waits
  for a visible page, stops when the clip changes, keeps six finished strips, and leaves the bar plain if the clip will not
  decode. A clip's frames never leave the page.
- `TrimBar`: dragging anywhere off the handles now scrubs (it used to seek once); what the trim drops is dimmed over the
  frames and the kept part is outlined. Bar height 28 to 44px.

Unverified: the strip on a real capture from `captures.exo-launcher.local` (H.264, HEVC, AV1 in the installed WebView2;
an AV1 clip on a PC without the AV1 decoder extension will leave the bar plain); its cost beside a running game; the
bar after a window resize (frames are laid out at the width first measured and stretch until the clip is reopened).

## Built: highlight reel (`concat`)

- Page: **Make reel N** on a game's group (its newest sitting: clips at most 3 hours apart, trims, exports and earlier reels left
  out; the starred ones when two or more are starred) and on **Select** (the clips picked). `lib/reel.ts` chooses (tested in
  `ui/test/reel.test.ts`); `lib/capture.ts` keeps the state (`useReel`) so a reel goes on and is heard with the sheet closed (a
  notice offers it), and a finished one opens in the viewer, which has the usual actions.
- Host: `Services/CaptureReel.cs` (pure, `ExoLauncher.Tests/CaptureReelTests`): order by time made, name "Game reel yyyy-MM-dd" in
  the game's folder (`Reels` for several games), copy when every clip's signature is equal, else re-encode at the largest size,
  never HDR that differs, room on the drive, one at a time. `MainWindow.CaptureReel.cs` runs it: refused during a game or a
  recording (the page offers **Wait** for a game: the reel starts when the game ends, 10 s after), stopped by a game starting.
- Add-on (`capture/`, `mux.cpp`): `concat.facts`, `concat`, `concat.cancel`, events `concat` (percent) and `saved` (kind `concat`).
  `ConcatCopy` writes each clip's own packets one after another (a clip starts on its first key frame, its sound is cut where its picture
  ends, none of it re-encoded); `ConcatReencode` decodes and encodes H.264 and AAC (the mix track only), each picture fitted with black bars.
  Written to `.part` and renamed, below-normal priority, the clips only read. Self-test: `capture/src/concat_selftest.cpp`.
- Found on the way, fixed: Windows' MP4 source lists sound tracks from the last to the first, so `trim` put a clip's sound tracks in
  the reverse order and `export` (For Discord, Vertical) took the last, the microphone, instead of the mix. Both take the file's order now.
- `capture/` changed (`mux.cpp`, `mux.h`, `main.cpp`, `concat_selftest.cpp`, `CMakeLists.txt`, `build.ps1`): nothing is published: Exo Capture's source is not public since 3.18.0 (capture/LICENSE).
- Unverified: a reel of real Exo Capture clips (HEVC, AV1, B-frames after Make smaller) and its cost beside a running game; a fade.
