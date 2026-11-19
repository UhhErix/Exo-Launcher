# Exo Lab

A repeatable performance meter for the installed Exo. Every performance change is accepted or reverted on a before
and an after report from it, measured on the same PC with the same script. Node 22+, PowerShell 7, no packages.

```
node tools/lab/run.mjs --label before                 # restart Exo with a loopback CDP port, measure, put Exo back
node tools/lab/run.mjs --label after
node tools/lab/run.mjs --compare lab-reports/<before> lab-reports/<after>
```

| Option | Default | What |
| --- | --- | --- |
| `--label <text>` | `unlabeled` | Names the report (`before`, `after`, `baseline`). |
| `--rounds <n>` | 3 | Rounds over every room; each metric is the median of its rounds. |
| `--rooms <a,b>` | all six | Library, Store, Discord, Music, PC, Devices. A room without a Bar tab is skipped and says why. |
| `--presses <n>` | 5 | Presses per room for the press latency (median). |
| `--warmup <s>` | 15 (0 with `--attach`) | Rest after Exo starts, before the first room. |
| `--attach` | off | Measure an Exo already running with CDP on `--port`; no restart, no close. |
| `--port <n>` | 9339 | The CDP port (loopback only). |
| `--etw` | off | Present statistics from PresentMon as well, if it is here (below). |
| `--out <dir>` | `lab-reports/<yyyyMMdd-HHmmss>` | Where the report goes. |
| `--compare <A> <B>` | | Compares two reports (folders or `report.json` files); exit 1 when B is worse on a key metric. |

The default run (six rooms, three rounds) takes about six minutes, most of it the 15 s each room rests for memory and
idle CPU.

## What it does

1. Refuses to start while a game runs (Exo's `Local\ExoLauncher.GameRunning` event, and the page's playing card) or
   during a voice call (UDP endpoints held by Exo), the same checks as `tools/e2e`. A game that starts mid-run stops it
   there, leaving Exo alone.
2. Saves the files a run may change (`tools/e2e/lib/exo.mjs` › `GUARDED`), closes Exo normally (never a kill), and
   starts it with `--remote-debugging-port` in its own environment only. At the end it closes Exo, puts the files back
   byte for byte, and starts Exo again normally.
3. Runs itself at Idle priority. Its PowerShell sampler (`proc.ps1`) inherits Idle and sets it again; Exo is started at
   Normal (the report records all three). It never focuses, clicks or moves a real window: every press and wheel is a
   CDP `Input` event, which needs no focus. If Exo is not the foreground window the report notes it, because Chromium
   slows frames for a window that is covered or minimized; keep Exo visible during a run.
4. Per room and round: the motion, the presses, 5 s of rest, then the memory and heap, then 10 s of idle CPU.

The `tools/e2e` harness does the work: `lib/exo.mjs` (start, close, guard), `lib/kit.mjs` (rooms by their Bar tab),
`lib/page.mjs` (the frame sampler), `lib/win.mjs` (window state, the call check), `cdp.mjs` (the DevTools client).

## Metrics

Lower is better for every metric; `frame.count` and `*.procs` only describe the run.

| Metric | Definition |
| --- | --- |
| `frame.p50/p95/p99/max` | Gaps (ms) between `requestAnimationFrame` callbacks in the page, recorded while Exo changes into the room (a CDP click on its Bar tab) and then while the room's main scroller (the largest visible vertical scroller; Exo Mouse's frame on Devices) takes 16 wheel notches down and 16 back up. |
| `frame.dropped` | Frames the screen showed with no new frame from the page in that recording: for each gap, `round(gap / refresh) - 1`. The refresh is the page's own idle frame interval, measured at the start (`refresh.measuredHz`, beside the GPU's reported rate). |
| `nav.ms` | From the Bar tab click until the room is current (polled every 100 ms, so coarse). |
| `press.frameMs` | A CDP press and release on the current room's Bar tab (harmless: the room is already open). From the `pointerdown` event's `timeStamp` to the next `requestAnimationFrame` callback after it: input delay plus the wait for the frame that can show the press. |
| `press.changeMs` | From `pointerdown` to the frame after the page's first DOM change, reported only when most presses changed the DOM (pressing the current tab usually does not, and an unrelated change would be timed otherwise). |
| `mem.<group>.privateMB` / `wsMB` | Private bytes and working set of the app's process tree after 5 s at rest, grouped: `host` (ExoLauncher.exe), `browser` (the WebView2 process without `--type=`), `gpu` (`--type=gpu-process`), `renderer` (`--type=renderer`), `utility` (`--type=utility` and the crash handler), `other` (other children, such as Exo Capture), `total`. Working sets overlap (shared pages); private bytes do not. |
| `heap.usedMB` / `totalMB` | The page's JS heap (CDP `Performance.getMetrics`). |
| `cpu.host` / `cpu.webview` / `cpu.other` | CPU time over the 10 s idle window as % of one core: the host, the whole WebView2 tree, and other children. |
| `cs.host` / `cs.webview` | Context switches per second over the same window, summed per thread (read with `NtQuerySystemInformation`; a thread that exits in the window is left out). |

The `all.*` rows are the median over rooms of each room's median; they are what the verdict reads.

## The acceptance rule

- Same PC, same script (rounds, rooms, presses, meter version), Idle priority, no focus, Exo visible and left alone.
  `--compare` warns when the machine, app, script or meter version differ.
- Measure `before` on the build without the batch and `after` on the installed build with it, then compare.
- Key metrics: `all.frame.p95`, `all.frame.dropped`, `all.press.frameMs`, `all.mem.gpu.privateMB`,
  `all.mem.renderer.privateMB`, `all.mem.host.privateMB`, `all.cpu.host`, `all.cpu.webview`.
- A change inside the noise floor is `same`: under 5% or 0.5 ms for times, 5% or 5 MB for memory, 10% or 0.3 points
  for CPU, 10% or one frame for dropped frames, 10% or 50/s for context switches.
- A batch worse on any key metric is reverted (the verdict says `WORSE`, exit code 1). Better on some and worse on none
  is accepted. All `same` means the batch needs another reason to stay. When a result looks like noise, run both
  sides again rather than arguing with it.

## PresentMon (`--etw`)

There is no PresentMon or WPR on this PC, and the meter downloads nothing. With `--etw` it looks for `PresentMon*.exe`
in `EXO_PRESENTMON` (a file path) or `tools/lab/bin/`; if found, each room's motion is also captured by PresentMon's own
timed capture (`--timed 8 --terminate_after_timed`, never stopped by force), filtered to the app's own processes, and
the present intervals (p50/p95/p99/max) go in the room's `etw`. PresentMon needs an elevated prompt for ETW. Without
it the report says `skipped: PresentMon not found`.

## The report

`lab-reports/<yyyyMMdd-HHmmss>/` (gitignored): `report.json` (every round's raw numbers, medians, phase timings, the
build's version and commit from ExoLauncher.exe's ProductVersion, GPU, CPU, memory, Windows build, measured and
reported refresh, window size, priorities, notes) and `summary.md`. Paths are written relative to `%USERPROFILE%` and
no command line leaves the sampler. `guard/` holds the saved settings files (local only).

## How other apps adopt it

The core takes an app by its executable, CDP port and page: the process tree is found from the executable, the page by
a part of its URL, and the memory, CPU, frame and press measurements do not depend on Exo.

```
node tools/lab/run.mjs --app <name> --exe <path to its exe> --port <port> --page <url part> --attach [--press "<css selector>"]
```

- Start the app with its own loopback CDP port first (for a WebView2 app, `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=<port>`
  in its environment). Another app is measured attached only: restarting it, its guarded files and its game and call
  checks are Exo's and stay in `lib/exo.mjs`.
- It is measured as one `page` room: the wheel over its main scroller, presses on `--press` (skipped without it), and
  the same memory and idle CPU.
- When an app needs rooms of its own, add its profile beside `exo` in `run.mjs` (`app.rooms` and how to reach each one),
  as Exo's uses `tools/e2e/lib/kit.mjs`.
